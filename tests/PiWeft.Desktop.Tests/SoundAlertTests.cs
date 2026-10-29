using OldBit.Beep;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 提示音：设置归一化、事件冷却与去重、音量缩放。全部是纯逻辑，
/// 不启动音频设备、不写用户目录（缓存路径显式给临时目录）。
/// </summary>
[Collection("mewui-controls")]
public sealed class SoundAlertTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-sound-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly List<(SoundAlertPreset Preset, double Volume)> _played = [];
	private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
	private bool _focused;

	public SoundAlertTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
		SoundAlerts.Shared.Reset();
		GC.SuppressFinalize(this);
	}

	private SoundAlerts NewAlerts(SoundAlertSettings? settings = null)
	{
		var alerts = new SoundAlerts
		{
			SettingsProvider = () => settings ?? new SoundAlertSettings(),
			IsWindowFocused = () => _focused,
			Clock = () => _now,
			Player = (preset, volume) => _played.Add((preset, volume)),
		};
		return alerts;
	}

	[Fact]
	public void DefaultsMatchPideck()
	{
		var settings = new SoundAlertSettings();

		Assert.True(settings.Enabled);
		Assert.Equal(0.6, settings.Volume);
		Assert.False(settings.OnlyWhenUnfocused);
		Assert.True(settings.Done.Enabled);
		Assert.Equal(SoundAlertPreset.DoneChime, settings.Done.Preset);
		Assert.True(settings.Error.Enabled);
		Assert.Equal(SoundAlertPreset.ErrorBuzz, settings.Error.Preset);
		// 等待输入默认关闭：连续提问时一直响是 PiDeck 明确避免的行为。
		Assert.False(settings.Waiting.Enabled);
		Assert.Equal(SoundAlertPreset.WaitingPing, settings.Waiting.Preset);
	}

	[Fact]
	public void CompletedRunRingsOnceAndRespectsTheEightSecondCooldown()
	{
		var alerts = NewAlerts();
		alerts.Observe("s1", streaming: true, error: null);
		alerts.Observe("s1", streaming: false, error: null);

		Assert.Equal([(SoundAlertPreset.DoneChime, 0.6)], _played);

		// 状态兜底会重复报到「已完成」：8 秒内不再响。
		_now = _now.AddSeconds(3);
		alerts.Observe("s1", streaming: false, error: null);
		Assert.Single(_played);

		// 冷却过后，下一轮完成重新响。
		_now = _now.AddSeconds(10);
		alerts.Observe("s1", streaming: true, error: null);
		alerts.Observe("s1", streaming: false, error: null);
		Assert.Equal(2, _played.Count);
	}

	[Fact]
	public void GlobalCooldownLetsOnlyTheLatestCompletionRing()
	{
		var alerts = NewAlerts();
		alerts.Observe("a", streaming: true, error: null);
		alerts.Observe("b", streaming: true, error: null);
		alerts.Observe("a", streaming: false, error: null);
		// 同一毫秒里第二个会话也完成：全局 1.5 秒冷却把它拦下，避免叠音。
		alerts.Observe("b", streaming: false, error: null);

		Assert.Single(_played);
	}

	[Fact]
	public void ErrorRingsOnTheEdgeOnly()
	{
		var alerts = NewAlerts();
		// 首帧就带错误：用户刚发了一条必然失败的消息，仍然提醒。
		alerts.Observe("s1", streaming: false, error: "boom");
		Assert.Equal([(SoundAlertPreset.ErrorBuzz, 0.6)], _played);

		// 同一个错误重复上报不重复响。
		_now = _now.AddSeconds(5);
		alerts.Observe("s1", streaming: false, error: "boom");
		Assert.Single(_played);

		// 错误换了一条（新一轮失败）：再响。
		_now = _now.AddSeconds(5);
		alerts.Observe("s1", streaming: false, error: "disk full");
		Assert.Equal(2, _played.Count);
	}

	[Fact]
	public void ErrorDoesNotAlsoCountAsCompletion()
	{
		var alerts = NewAlerts();
		alerts.Observe("s1", streaming: true, error: null);
		alerts.Observe("s1", streaming: false, error: "boom");

		Assert.Equal([(SoundAlertPreset.ErrorBuzz, 0.6)], _played);
	}

	[Fact]
	public void WaitingRingsPerBatchAndResetsAfterTheBatchIsAnswered()
	{
		var alerts = NewAlerts(new SoundAlertSettings
		{
			Waiting = SoundAlertEventSettings.Default(SoundAlertKind.Waiting) with { Enabled = true },
		});

		alerts.RequestWaiting("s1", "r1");
		alerts.RequestWaiting("s1", "r2");
		Assert.Equal([(SoundAlertPreset.WaitingPing, 0.6)], _played);

		// 一批还没答完：仍在等待，不再重复响。
		alerts.CompleteWaiting("s1", "r1");
		alerts.RequestWaiting("s1", "r3");
		Assert.Single(_played);

		// 全部答完（清空）后，下一次新批重新提醒（隔开全局冷却，与真实节奏一致）。
		_now = _now.AddSeconds(3);
		alerts.CompleteWaiting("s1", "r2");
		alerts.CompleteWaiting("s1", "r3");
		alerts.RequestWaiting("s1", "r4");
		Assert.Equal(2, _played.Count);
	}

	[Fact]
	public void MasterAndPerEventSwitchesGateEverything()
	{
		var off = NewAlerts(new SoundAlertSettings { Enabled = false });
		off.Observe("s1", streaming: true, error: null);
		off.Observe("s1", streaming: false, error: null);
		off.RequestWaiting("s1", "r1");
		Assert.Empty(_played);

		var doneOnly = NewAlerts(new SoundAlertSettings
		{
			Error = SoundAlertEventSettings.Default(SoundAlertKind.Error) with { Enabled = false },
		});
		doneOnly.Observe("s1", streaming: false, error: "boom");
		Assert.Empty(_played);
	}

	[Fact]
	public void OnlyWhenUnfocusedStaysSilentWhileTheWindowIsActive()
	{
		var alerts = NewAlerts(new SoundAlertSettings { OnlyWhenUnfocused = true });

		_focused = true;
		alerts.Observe("s1", streaming: true, error: null);
		alerts.Observe("s1", streaming: false, error: null);
		Assert.Empty(_played);

		_focused = false;
		_now = _now.AddSeconds(10);
		alerts.Observe("s1", streaming: true, error: null);
		alerts.Observe("s1", streaming: false, error: null);
		Assert.Single(_played);
	}

	[Fact]
	public void SettingsRoundTripThroughDesktopJson()
	{
		var settings = new SoundAlertSettings
		{
			Enabled = false,
			Volume = 0.35,
			OnlyWhenUnfocused = true,
			Done = new SoundAlertEventSettings(false, SoundAlertPreset.DoneBell),
			Error = new SoundAlertEventSettings(true, SoundAlertPreset.ErrorAlert),
			Waiting = new SoundAlertEventSettings(true, SoundAlertPreset.WaitingKnock),
		};

		var json = SoundAlertJson.Write(settings);
		var parsed = SoundAlertJson.Parse(json);

		Assert.Equal(settings, parsed);
	}

	[Fact]
	public void BrokenSettingsFallBackToDefaultsInsteadOfGoingSilent()
	{
		// 手改坏的 JSON：类型不对、预设名不存在、音量越界。
		var parsed = SoundAlertJson.Parse(System.Text.Json.Nodes.JsonNode.Parse(
			"""{"enabled":"yes","volume":5,"done":{"enabled":true,"sound":"nope"},"error":{"enabled":false}}"""));

		Assert.True(parsed.Enabled);
		Assert.Equal(1, parsed.Volume);
		Assert.Equal(SoundAlertPreset.DoneChime, parsed.Done.Preset);
		Assert.False(parsed.Error.Enabled);
		Assert.Equal(SoundAlertPreset.ErrorBuzz, parsed.Error.Preset);

		// 预设名合法但不属于该类事件（done 用了 waiting 的音效）：回落该事件默认。
		var mismatched = SoundAlertJson.Parse(System.Text.Json.Nodes.JsonNode.Parse(
			"""{"done":{"enabled":true,"sound":"waiting-knock"}}"""));
		Assert.Equal(SoundAlertPreset.DoneChime, mismatched.Done.Preset);
	}

	[Fact]
	public void VolumeIsClampedAndRoundedToFivePercentSteps()
	{
		Assert.Equal(0.6, SoundPlayer.ClampVolume(double.NaN));
		Assert.Equal(1, SoundPlayer.ClampVolume(3));
		Assert.Equal(0, SoundPlayer.ClampVolume(-2));
		Assert.Equal(60, SoundPlayer.VolumePercent(0.6));
		Assert.Equal(35, SoundPlayer.VolumePercent(0.34));
		Assert.Equal(100, SoundPlayer.VolumePercent(1));
	}

	[Fact]
	public void EmbeddedPresetsExistAndCacheToTheAgentDirectory()
	{
		foreach (var preset in Enum.GetValues<SoundAlertPreset>())
		{
			var name = SoundPlayer.FileName(preset);
			using var stream = typeof(SoundPlayer).Assembly.GetManifestResourceStream(SoundPlayer.ResourceName(preset));
			Assert.NotNull(stream);
			Assert.True(stream!.Length > 8_000, $"{name} 太短，可能不是完整音效");

			var path = SoundPlayer.CachedPath(preset, _root);
			Assert.NotNull(path);
			Assert.True(File.Exists(path));
			Assert.Contains("sounds", path, StringComparison.Ordinal);
		}

		// 重复取：复用缓存，不再多写一份。
		var first = SoundPlayer.CachedPath(SoundAlertPreset.DoneChime, _root);
		var again = SoundPlayer.CachedPath(SoundAlertPreset.DoneChime, _root);
		Assert.Equal(first, again);

		// 回退路径（平台命令）才需要缩放副本：Beep 可用时缓存目录里只有原始 WAV。
		var quiet = SoundPlayer.ScaledPath(SoundAlertPreset.DoneChime, first!, 0.3);
		Assert.NotNull(quiet);
		Assert.NotEqual(first, quiet);
		Assert.True(Peak(quiet!) < Peak(first!));
	}

	[Fact]
	public void WavPresetsParseIntoPcmTheBeepBackendCanPlay()
	{
		foreach (var preset in Enum.GetValues<SoundAlertPreset>())
		{
			var path = SoundPlayer.CachedPath(preset, _root);
			Assert.NotNull(path);
			var audio = WavAudio.Parse(File.ReadAllBytes(path!));

			Assert.NotNull(audio);
			// PiDeck 的预设统一是 44.1 kHz / 单声道 / 16-bit；Beep 的构造参数直接来自这里。
			Assert.Equal(44100, audio!.SampleRate);
			Assert.Equal(1, audio.Channels);
			Assert.Equal(AudioFormat.Signed16BitIntegerLittleEndian, audio.Format);
			Assert.True(audio.Pcm.Length > 8_000);
			Assert.InRange(audio.Duration.TotalMilliseconds, 200, 1500);
		}
	}

	[Fact]
	public void BeepBackendPlaysThroughItsSilentDevice()
	{
		var path = SoundPlayer.CachedPath(SoundAlertPreset.DonePop, _root);
		Assert.NotNull(path);

		// 静音后端：验证「解析 → 构造 → 入队 → 停止」整条链，不碰真实声卡（无头测试必须有声音设备之外的路径）。
		Assert.True(SoundPlayer.TryPlayWithBeep(path!, 0.6, silent: true));
	}

	[Fact]
	public void NonPcmWavIsRejectedInsteadOfBeingMisreadAsPcm()
	{
		// 只有 RIFF 头、没有 data 块：解析必须返回 null，而不是拿半截数据去播。
		var broken = new byte[44];
		"RIFF"u8.CopyTo(broken);
		"WAVE"u8.CopyTo(broken.AsSpan(8));
		Assert.Null(WavAudio.Parse(broken));
		Assert.Null(WavAudio.Parse([1, 2, 3]));
	}

	/// <summary>WAV 里 16-bit 样本的绝对值峰值（用于验证音量缩放真的改了样本）。</summary>
	private static int Peak(string path)
	{
		var bytes = File.ReadAllBytes(path);
		var peak = 0;
		for (var offset = 44; offset + 1 < bytes.Length; offset += 2)
		{
			peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(bytes, offset)));
		}

		return peak;
	}
}
