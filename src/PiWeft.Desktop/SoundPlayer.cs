using System.Diagnostics;
using Aprillz.MewUI;
using OldBit.Beep;
using PiWeft.Cli;
using ZLogger;

namespace PiWeft.Desktop;

/// <summary>内置预设音效：与 PiDeck 的 SOUND_ALERT_PRESETS 一一对应。</summary>
public enum SoundAlertPreset
{
	DoneChime,
	DoneBell,
	DonePop,
	ErrorBuzz,
	ErrorAlert,
	WaitingPing,
	WaitingKnock,
}

/// <summary>
/// 提示音播放器：把嵌入的 WAV 落到 <c>&lt;agentDir&gt;/sounds/</c> 缓存，按 PCM 交给
/// <see href="https://github.com/oldbit-com/Beep">OldBit.Beep</see> 播放
/// （Windows WASAPI、macOS AudioToolbox、Linux ALSA，都是系统框架，无随包原生库）。
///
/// 选择 Beep 而不是平台命令或更大的音频引擎：它只有 64 KB、纯 P/Invoke、
/// 三个平台的正式后端齐全，音量内建；PiDeck 那种「一个短提示音」的需求用不上
/// 引擎级的混音/录音能力。平台命令（paplay / afplay / winmm）保留为兜底：
/// Linux 缺 libasound、容器里没有音频服务、或 Beep 初始化失败时仍然有声音。
///
/// 设备生命周期：Beep 在构造时打开音频设备、<c>Stop</c> 时关闭。提示音频率很低，
/// 所以每次播放构造一个实例、播完即弃——空闲时不占音频设备，代价是每次
/// 10–50 ms 的开关设备延迟，对提示音完全够用。
/// </summary>
internal static partial class SoundPlayer
{
	/// <summary>出厂音量（与 PiDeck 一致）。</summary>
	internal const double DefaultVolume = 0.6;

	/// <summary>播放命令的探测结果：null 表示本机没有可用播放器（回退路径也走不通）。</summary>
	private static readonly Lazy<(string Command, string[] Args)?> Backend = new(DetectBackend);

	private static readonly string[] LinuxPlayers = ["paplay", "pw-play", "aplay"];

	/// <summary>预设对应的嵌入资源名（csproj 会把 Resources/sounds/*.wav 打进程序集）。</summary>
	internal static string ResourceName(SoundAlertPreset preset) => "PiWeft.Desktop.Resources.sounds." + FileName(preset);

	/// <summary>预设对应的文件名，与 PiDeck 的 SOUND_ALERT_PRESETS 一致。</summary>
	internal static string FileName(SoundAlertPreset preset) => preset switch
	{
		SoundAlertPreset.DoneChime => "done-chime.wav",
		SoundAlertPreset.DoneBell => "done-bell.wav",
		SoundAlertPreset.DonePop => "done-pop.wav",
		SoundAlertPreset.ErrorBuzz => "error-buzz.wav",
		SoundAlertPreset.ErrorAlert => "error-alert.wav",
		SoundAlertPreset.WaitingPing => "waiting-ping.wav",
		_ => "waiting-knock.wav",
	};

	/// <summary>同一时间只让一个提示音持有音频设备：连续触发时排队，而不是互相抢设备。</summary>
	private static readonly SemaphoreSlim Gate = new(1, 1);

	/// <summary>
	/// 播放指定预设；<paramref name="volume"/> 取 0–1。任何失败都不向调用方抛。
	///
	/// 播放要开关音频设备并等音效放完（最多 1 秒），所以在 UI 线程上只投递、不等待：
	/// 会话完成事件与设置页试听都从 UI 线程来，同步等会把界面冻住。
	/// </summary>
	public static void Play(SoundAlertPreset preset, double volume)
	{
		var level = ClampVolume(volume);
		if (level <= 0)
		{
			return;
		}

		if (Application.IsRunning && Application.Current.Dispatcher?.IsOnUIThread == true)
		{
			_ = Task.Run(() => PlayCore(preset, level));
			return;
		}

		PlayCore(preset, level);
	}

	private static void PlayCore(SoundAlertPreset preset, double level)
	{
		// 有界等待：设备被挂起/驱动卡死时，后续提示音不能无限堆在线程池里。
		if (!Gate.Wait(TimeSpan.FromSeconds(3)))
		{
			AppLog.For<SoundAlerts>().ZLogWarning($"Sound alert skipped: previous playback still holds the device");
			return;
		}

		try
		{
			var path = CachedPath(preset);
			if (path is null)
			{
				return;
			}

			// 首选 Beep：设备可用时听感与音量都由它负责。
			if (TryPlayWithBeep(path, level))
			{
				return;
			}

			// 回退：Beep 起不来（缺 libasound、无音频服务、设备被独占）时用平台命令，
			// 音量通过缩放后的 WAV 副本实现——两条路径的音量语义保持一致。
			if (ScaledPath(preset, path, level) is { } scaled)
			{
				PlayFile(scaled);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
			or PlatformNotSupportedException or AudioPlayerException)
		{
			// 提示音是尽力而为：磁盘只读、没有音频设备、播放器退出都不能影响会话。
			AppLog.Failure(AppLog.For<SoundAlerts>(), "Play sound alert", ex);
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>音量给 0–1，非法值按 0.6 处理（与 PiDeck 的 clampSoundVolume 一致）。</summary>
	internal static double ClampVolume(double volume) =>
		double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : DefaultVolume;

	/// <summary>回调音量百分比（5% 步进）：回退路径用同一档位的缩放副本。</summary>
	internal static int VolumePercent(double volume) =>
		(int)Math.Round(ClampVolume(volume) * 20) * 5;

	/// <summary>
	/// 播放缓存路径：首次从嵌入资源落一份原始 WAV 到 <c>&lt;agentDir&gt;/sounds/</c>。
	/// 返回 null 表示嵌入资源缺失（构建裁剪掉了），此时静默不播。
	/// </summary>
	internal static string? CachedPath(SoundAlertPreset preset, string? agentDir = null)
	{
		var directory = Path.Combine(agentDir ?? CodingAgent.Session.SessionPaths.GetAgentDir(), "sounds");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, FileName(preset));
		if (File.Exists(path))
		{
			return path;
		}

		using var resource = typeof(SoundPlayer).Assembly.GetManifestResourceStream(ResourceName(preset));
		if (resource is null)
		{
			AppLog.For<SoundAlerts>().ZLogWarning($"Missing embedded sound: {ResourceName(preset)}");
			return null;
		}

		// 先写临时文件再改名：并发播放同一音效时不会读到写了一半的 WAV。
		var temporary = path + ".tmp";
		try
		{
			using (var file = File.Create(temporary))
			{
				resource.CopyTo(file);
			}

			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}

		return path;
	}

	/// <summary>
	/// 回退路径的缩放副本（5% 步进复用）。只在 Beep 不可用时生成，
	/// 所以正常情况下缓存目录里只有原始 WAV。
	/// </summary>
	internal static string? ScaledPath(SoundAlertPreset preset, string source, double volume)
	{
		var percent = VolumePercent(volume);
		if (percent >= 100)
		{
			return source;
		}

		var scaled = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + $"@{percent}.wav");
		if (File.Exists(scaled))
		{
			return scaled;
		}

		var temporary = scaled + ".tmp";
		try
		{
			ScaleWav(source, temporary, percent / 100.0);
			File.Move(temporary, scaled, overwrite: true);
			return scaled;
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}

	/// <summary>
	/// 16-bit PCM 采样缩放：只改 data 块里的样本，头部原样保留。
	/// 遇到非 16-bit / 非 PCM 的文件直接复制（宁可响得比设置响，也不要没声）。
	/// </summary>
	internal static void ScaleWav(string source, string destination, double factor)
	{
		var bytes = File.ReadAllBytes(source);
		var audio = WavAudio.Parse(bytes);
		if (audio is null)
		{
			File.WriteAllBytes(destination, bytes);
			return;
		}

		var span = bytes.AsSpan(audio.DataOffset, audio.DataLength);
		for (var offset = 0; offset + 1 < span.Length; offset += 2)
		{
			var value = (short)(BitConverter.ToInt16(span[offset..]) * factor);
			BitConverter.TryWriteBytes(span[offset..], value);
		}

		File.WriteAllBytes(destination, bytes);
	}

	/// <summary>
	/// 用 Beep 播一次。返回 false 表示本机没有可用设备/后端，调用方应回退到平台命令。
	/// <paramref name="silent"/> 为 true 时走 Beep 的静音后端（无头测试用：验证解析与调用链，不碰设备）。
	/// </summary>
	internal static bool TryPlayWithBeep(string path, double volume, bool silent = false)
	{
		var audio = WavAudio.Parse(File.ReadAllBytes(path));
		if (audio is null)
		{
			AppLog.For<SoundAlerts>().ZLogWarning($"Unsupported WAV preset: {path}");
			return false;
		}

		AudioPlayer? player = null;
		try
		{
			var options = silent ? new PlayerOptions { UseSilentAudioPlayer = true } : null;
			player = new AudioPlayer(audio.Format, audio.SampleRate, audio.Channels, options);
			if (!silent && !player.IsPlatformSupported)
			{
				return false;
			}

			// Beep 的音量是 0–100，内部按样本缩放，与我们回退路径的口径一致。
			player.Volume = Math.Max(1, (int)Math.Round(ClampVolume(volume) * 100));
			player.Start();
			player.EnqueueAsync(audio.Pcm).GetAwaiter().GetResult();
			if (!silent)
			{
				// 库没有「播完」回调：按 WAV 时长等一次，再 Stop 关设备。
				Thread.Sleep(audio.Duration + TimeSpan.FromMilliseconds(40));
			}

			return true;
		}
		catch (Exception ex) when (ex is AudioPlayerException or DllNotFoundException or InvalidOperationException
			or PlatformNotSupportedException)
		{
			// 没有音频设备 / 缺 libasound / WASAPI 初始化失败：留给平台命令回退。
			AppLog.For<SoundAlerts>().ZLogDebug($"Beep playback unavailable: {ex.Message}");
			return false;
		}
		finally
		{
			try
			{
				player?.Stop();
				player?.Dispose();
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				// 设备已经没了：没什么可收的。
			}
		}
	}

	private static void PlayFile(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			PlayWindows(path);
			return;
		}

		if (Backend.Value is not { } backend)
		{
			return;
		}

		var start = new ProcessStartInfo(backend.Command)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};
		foreach (var argument in backend.Args)
		{
			start.ArgumentList.Add(argument);
		}

		start.ArgumentList.Add(path);
		using var process = Process.Start(start);
		if (process is null)
		{
			return;
		}

		// 输出必须读干：aplay 之类的错误信息虽小，但子进程写不出去会一直等到播放结束。
		_ = process.StandardError.ReadToEndAsync();
		_ = process.StandardOutput.ReadToEndAsync();
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static void PlayWindows(string path)
	{
		// SND_FILENAME | SND_ASYNC | SND_NODEFAULT：异步播放、文件不存在时不播放系统提示音。
		const uint Flags = 0x00020000 | 0x0001 | 0x0002;
		if (!NativeMethods.PlaySound(path, IntPtr.Zero, Flags))
		{
			AppLog.For<SoundAlerts>().ZLogWarning($"winmm PlaySound failed: {path}");
		}
	}

	private static (string Command, string[] Args)? DetectBackend()
	{
		if (OperatingSystem.IsMacOS())
		{
			return ("afplay", []);
		}

		if (!OperatingSystem.IsLinux())
		{
			return null;
		}

		foreach (var player in LinuxPlayers)
		{
			if (FindOnPath(player) is null)
			{
				continue;
			}

			// paplay / pw-play / aplay 都直接吃文件路径。
			return (player, []);
		}

		AppLog.For<SoundAlerts>().ZLogWarning($"No audio player found (paplay/pw-play/aplay); sound alerts disabled");
		return null;
	}

	/// <summary>在 PATH 里找可执行文件（不启动它）。</summary>
	internal static string? FindOnPath(string command, string? path = null)
	{
		path ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			var candidate = Path.Combine(directory, command);
			if (File.Exists(candidate))
			{
				return candidate;
			}

			if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
			{
				return candidate + ".exe";
			}
		}

		return null;
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static partial class NativeMethods
	{
		[System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "PlaySoundW",
			StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
		[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
		internal static partial bool PlaySound(string sound, IntPtr module, uint flags);
	}
}

/// <summary>
/// 从 WAV 里取出 PCM 与格式：Beep 只吃裸样本，格式由构造参数给出。
/// 只认 PCM（format=1）与 ≤32-bit 的位深；PiDeck 的预设都是 44.1 kHz / 单声道 / 16-bit。
/// </summary>
internal sealed record WavAudio(AudioFormat Format, int SampleRate, int Channels, byte[] Pcm, TimeSpan Duration)
{
	/// <summary>data 块在原文件里的偏移与长度（回退路径要就地缩放样本）。</summary>
	public int DataOffset { get; init; }

	public int DataLength => Pcm.Length;

	/// <summary>解析 WAV；格式不认识时返回 null（调用方回退或跳过）。</summary>
	public static WavAudio? Parse(byte[] bytes)
	{
		if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
		{
			return null;
		}

		AudioFormat? format = null;
		int rate = 0;
		int channels = 0;
		int bits = 0;
		var position = 12;
		while (position + 8 <= bytes.Length)
		{
			var id = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
			var size = BitConverter.ToInt32(bytes, position + 4);
			// long 比较：size 是文件里的原始 32 位值，直接加 position 可能溢出成负数。
			if (size < 0 || position + 8L + size > bytes.Length)
			{
				return null;
			}

			if (id == "fmt ")
			{
				// WAVEFORMATEX 至少 16 字节；更短的 fmt 块说明文件本身坏了。
				if (size < 16)
				{
					return null;
				}

				var encoding = BitConverter.ToUInt16(bytes, position + 8);
				channels = BitConverter.ToUInt16(bytes, position + 10);
				rate = BitConverter.ToInt32(bytes, position + 12);
				bits = BitConverter.ToUInt16(bytes, position + 22);
				if (encoding != 1)
				{
					// 非 PCM（例如 IEEE float 的 fmt 标签 = 3）不猜，交给其它路径。
					return null;
				}

				format = bits switch
				{
					8 => AudioFormat.Unsigned8Bit,
					16 => AudioFormat.Signed16BitIntegerLittleEndian,
					32 => AudioFormat.Float32BitLittleEndian,
					_ => null,
				};
			}
			else if (id == "data")
			{
				if (format is null || channels <= 0 || rate <= 0)
				{
					return null;
				}

				var pcm = new byte[size];
				Array.Copy(bytes, position + 8, pcm, 0, size);
				var bytesPerFrame = (long)channels * (bits / 8);
				var frames = bytesPerFrame > 0 ? size / bytesPerFrame : 0;
				return new WavAudio(format.Value, rate, channels, pcm, TimeSpan.FromSeconds((double)frames / rate))
				{
					DataOffset = position + 8,
				};
			}

			position += 8 + size + (size & 1);
		}

		return null;
	}
}
