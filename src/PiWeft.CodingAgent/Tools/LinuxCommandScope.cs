using System.Diagnostics;

namespace PiWeft.CodingAgent.Tools;

/// <summary>
/// Linux 上的命令容器：把一条命令放进 user manager 的 transient scope（cgroup）。
/// 命令自己 setsid、双重 fork，或被 reparent 到 init 的后代都还留在同一个 cgroup 里，
/// 按单元名杀一次就能收干净。没有 user systemd（容器、非 systemd 发行版）时返回 null，
/// 调用方退回 setsid + 进程组。
/// </summary>
internal sealed class LinuxCommandScope
{
	/// <summary>退出阶段每个单元最多等这么久：关应用不该被 systemd 交互拖成好几秒。</summary>
	public const int ShutdownTimeoutMs = 300;

	private const int ProbeTimeoutMs = 1_500;
	private const int KillTimeoutMs = 2_000;

	private static readonly Lazy<Toolchain?> Probe = new(ProbeToolchain, LazyThreadSafetyMode.ExecutionAndPublication);

	private readonly Toolchain _tools;

	private LinuxCommandScope(Toolchain tools, string name)
	{
		_tools = tools;
		Name = name;
		Unit = $"{name}.scope";
	}

	/// <summary>本机是否能用 scope 装命令。探测只做一次，失败后一直走进程组。</summary>
	public static bool IsAvailable => Probe.Value is not null;

	/// <summary>单元名的短形式，systemd-run 的 <c>--unit</c> 用它。</summary>
	public string Name { get; }

	/// <summary>scope 单元名，形如 <c>piweft-0123456789ab.scope</c>。systemctl 要带后缀才不会当成同名的 .service。</summary>
	public string Unit { get; }

	/// <summary>systemd-run 的绝对路径。</summary>
	public string FileName => _tools.SystemdRun;

	/// <summary>systemd-run 的参数前缀，后面接 shell 路径与命令。</summary>
	public IReadOnlyList<string> Arguments =>
	[
		"--user",
		"--scope",
		"--quiet",
		"--collect",
		$"--unit={Name}",
		"--",
	];

	/// <summary>
	/// systemd-run 的 scope 模式会把 argv 里的 <c>$$</c> 当成单元文件转义还原成一个 <c>$</c>
	/// （v262 实测，<c>$PATH</c> 这类不受影响）。命令最终由 bash 解析，所以要把 <c>$$</c>
	/// 加倍抵掉这一次还原，否则 <c>echo $$</c>、<c>trap ... $$</c> 会悄悄变成 <c>$</c>。
	/// 探测时确认过本机确实会还原，新版 systemd 不这么做时原样返回。
	/// </summary>
	public string EscapeCommand(string command) => EscapeCommand(command, _tools.CollapsesDoubleDollar);

	internal static string EscapeCommand(string command, bool collapsesDoubleDollar) => collapsesDoubleDollar
		? command.Replace("$$", "$$$$", StringComparison.Ordinal)
		: command;

	/// <summary>新建一个 scope 描述；本机不可用时返回 <c>null</c>。</summary>
	public static LinuxCommandScope? TryCreate()
	{
		var tools = Probe.Value;
		if (tools is null)
		{
			return null;
		}

		var suffix = Guid.NewGuid().ToString("N")[..12];
		return new LinuxCommandScope(tools, $"piweft-{suffix}");
	}

	/// <summary>
	/// 按单元名杀残留进程。单元已经为空（或命令退出后已被 <c>--collect</c> 收掉）时
	/// systemctl 返回非零，这不是失败信号，也不再重试：普通命令每条都多一次 systemctl 不划算，
	/// 真失败时调用方还有整树 kill。
	/// </summary>
	public static void KillUnit(string unit, int timeoutMs = KillTimeoutMs)
	{
		var tools = Probe.Value;
		if (tools is null)
		{
			return;
		}

		_ = Run(tools.Systemctl, ["--user", "kill", "--kill-who=all", "--signal=SIGKILL", unit], timeoutMs);
	}

	/// <summary>杀掉单元里残留的所有进程。</summary>
	public void Kill() => KillUnit(Unit);

	private static Toolchain? ProbeToolchain()
	{
		if (!OperatingSystem.IsLinux())
		{
			return null;
		}

		try
		{
			var systemdRun = ShellRunner.FindOnPath("systemd-run");
			var systemctl = ShellRunner.FindOnPath("systemctl");
			if (systemdRun is null || systemctl is null)
			{
				return null;
			}

			// 只查二进制不够：容器里 systemd-run 存在但连不上 user manager。用一次真实的
			// scope 调用验证整条链路，失败就不再尝试，避免每条命令都卡在启动失败上。
			// $0 是普通 argv，不经过 shell 展开，正好拿来验 $$ 还原。
			var probeUnit = $"piweft-probe-{Guid.NewGuid().ToString("N")[..12]}";
			var output = Run(
				systemdRun,
				["--user", "--scope", "--quiet", "--collect", $"--unit={probeUnit}", "--", "/bin/sh", "-c", "printf %s \"$0\"", "x$$y"],
				ProbeTimeoutMs);
			return output is null ? null : new Toolchain(systemdRun, systemctl, CollapsesDoubleDollar: output == "x$y");
		}
		catch (Exception)
		{
			// 探测路径也不能把第一个 bash 调用弄挂；失败就当本机没有 user systemd。
			return null;
		}
	}

	/// <summary>同步跑一个管理命令；超时就杀掉它。成功返回 stdout，否则 <c>null</c>，从不抛异常。</summary>
	private static string? Run(string fileName, ReadOnlySpan<string> arguments, int timeoutMs)
	{
		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = fileName,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			foreach (var argument in arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}

			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return null;
			}

			// 先挂上读任务再等退出：管理命令的输出很小，但管道写满会让它永远不退出。
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(timeoutMs))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (Exception)
				{
					// 已经被别的路径收掉。
				}

				return null;
			}

			var output = stdout.GetAwaiter().GetResult();
			_ = stderr.GetAwaiter().GetResult();
			return process.ExitCode == 0 ? output : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private sealed record Toolchain(string SystemdRun, string Systemctl, bool CollapsesDoubleDollar);
}
