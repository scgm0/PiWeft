using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public sealed class ShellCommandNormalizerTests
{
	[Theory]
	[InlineData("echo hi > nul", "echo hi >/dev/null")]
	[InlineData("echo hi > NUL", "echo hi >/dev/null")]
	[InlineData("echo hi >> nul", "echo hi >>/dev/null")]
	[InlineData("cmd 2> nul", "cmd 2>/dev/null")]
	[InlineData("cmd 2>> nul", "cmd 2>>/dev/null")]
	[InlineData("cmd &> nul", "cmd &>/dev/null")]
	[InlineData("cmd >& nul", "cmd >&/dev/null")]
	[InlineData("echo > nul.txt", "echo > nul.txt")]
	[InlineData("echo > nul-backup", "echo > nul-backup")]
	[InlineData("echo \"> nul\"", "echo \"> nul\"")]
	[InlineData("echo '> nul'", "echo '> nul'")]
	[InlineData("echo \\> nul", "echo \\> nul")]
	public void RedirectsToNulBecomeDevNull(string command, string expected) =>
		Assert.Equal(expected, ShellCommandNormalizer.NormalizeNulRedirects(command));
}

public sealed class TrashGuardTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-trash-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
	private readonly string? _previousGuard = Environment.GetEnvironmentVariable("PIDECK_TRASH_GUARD");

	public TrashGuardTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previousXdg);
		Environment.SetEnvironmentVariable("PIDECK_TRASH_GUARD", _previousGuard);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void ExtractsLiteralTargetsFromBashDeletes()
	{
		Assert.Equal(["a b", "c"], TrashGuard.ExtractDeleteTargets("""rm -rf "a b" c""", powerShell: false));
		Assert.Equal(["x"], TrashGuard.ExtractDeleteTargets("sudo rm -f x", powerShell: false));
		Assert.Equal(["foo"], TrashGuard.ExtractDeleteTargets("rm foo > log.txt", powerShell: false));
		Assert.Equal(["-weird"], TrashGuard.ExtractDeleteTargets("rm -- -weird", powerShell: false));
		Assert.Equal(["echo ok", "rm -rf build"], TrashGuard.SplitShellSegments("echo ok; rm -rf build"));
		Assert.Equal(["build"], TrashGuard.ExtractDeleteTargets("rm -rf build", powerShell: false));
		Assert.Empty(TrashGuard.ExtractDeleteTargets("git clean -fdx", powerShell: false));
		Assert.Empty(TrashGuard.ExtractDeleteTargets("echo rm", powerShell: false));
	}

	[Fact]
	public void ExtractsPowerShellAndWindowsTargets()
	{
		Assert.Equal(
			[@"C:\a b", "D:\\dir"],
			TrashGuard.ExtractDeleteTargets("""Remove-Item -LiteralPath 'C:\a b' -Recurse -Force D:\dir""", powerShell: true));
		Assert.Equal(["ignored"], TrashGuard.ExtractDeleteTargets("""rd /s /q ignored""", powerShell: false));
	}

	[Fact]
	public void ExpandsTheLastGlobSegment()
	{
		var directory = Path.Combine(_root, "build");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "a.log"), "x");
		File.WriteAllText(Path.Combine(directory, "b.txt"), "x");

		var targets = TrashGuard.CollectTargets("rm build/*.log", _root, powerShell: false);
		Assert.Single(targets);
		Assert.EndsWith("a.log", targets[0]);

		Assert.Empty(TrashGuard.CollectTargets("rm build/**/*.log", _root, powerShell: false));
		Assert.Empty(TrashGuard.CollectTargets("rm build/*.md", _root, powerShell: false));
	}

	[Fact]
	public async Task LinuxBackupCopiesTargetsIntoTheXdgTrash()
	{
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		var data = Path.Combine(_root, "data");
		Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
		var work = Path.Combine(_root, "work");
		Directory.CreateDirectory(Path.Combine(work, "dir"));
		File.WriteAllText(Path.Combine(work, "dir", "inside.txt"), "inside");
		File.WriteAllText(Path.Combine(work, "one.txt"), "one");

		await TrashGuard.BackupAsync("bash", "rm -rf dir one.txt", work, CancellationToken.None);

		// 原始文件还在：guard 只备份，删除由原命令完成。
		Assert.True(File.Exists(Path.Combine(work, "one.txt")));
		Assert.True(File.Exists(Path.Combine(work, "dir", "inside.txt")));

		var trash = Path.Combine(data, "Trash");
		Assert.True(File.Exists(Path.Combine(trash, "files", "one.txt")));
		Assert.True(File.Exists(Path.Combine(trash, "files", "dir", "inside.txt")));

		var info = Directory.GetFiles(Path.Combine(trash, "info"), "*.trashinfo");
		Assert.Equal(2, info.Length);
		Assert.Contains(info, path => File.ReadAllText(path).Contains("Path=/", StringComparison.Ordinal));
	}

	[Fact]
	public async Task BackupCanBeDisabled()
	{
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		var data = Path.Combine(_root, "data");
		Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
		Environment.SetEnvironmentVariable("PIDECK_TRASH_GUARD", "off");
		var work = Path.Combine(_root, "work");
		Directory.CreateDirectory(work);
		File.WriteAllText(Path.Combine(work, "one.txt"), "one");

		await TrashGuard.BackupAsync("bash", "rm one.txt", work, CancellationToken.None);

		Assert.False(Directory.Exists(Path.Combine(data, "Trash")));
	}
}

/// 模型专用代理隔离会改进程环境变量，单独进串行集合。
[Collection("process-environment")]
public sealed class ShellProxyScopeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-proxy-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousHttp = Environment.GetEnvironmentVariable("HTTP_PROXY");
	private readonly string? _previousHttps = Environment.GetEnvironmentVariable("HTTPS_PROXY");
	private readonly string? _previousScope = Environment.GetEnvironmentVariable("PIDECK_PI_PROXY_SCOPE");

	public ShellProxyScopeTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("HTTP_PROXY", _previousHttp);
		Environment.SetEnvironmentVariable("HTTPS_PROXY", _previousHttps);
		Environment.SetEnvironmentVariable("PIDECK_PI_PROXY_SCOPE", _previousScope);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task ModelOnlyProxyIsRemovedFromAgentShells()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		Environment.SetEnvironmentVariable("HTTP_PROXY", "http://model-proxy.invalid:8080");
		Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://model-proxy.invalid:8080");
		Environment.SetEnvironmentVariable("PIDECK_PI_PROXY_SCOPE", "model-only");

		var hidden = await ShellRunner.RunAsync(
			"printf '%s' \"$HTTP_PROXY\"",
			_root,
			timeoutSeconds: 30,
			onOutput: null,
			CancellationToken.None);
		Assert.Equal(string.Empty, hidden.Output.Trim());

		Environment.SetEnvironmentVariable("PIDECK_PI_PROXY_SCOPE", null);
		var inherited = await ShellRunner.RunAsync(
			"printf '%s' \"$HTTP_PROXY\"",
			_root,
			timeoutSeconds: 30,
			onOutput: null,
			CancellationToken.None);
		Assert.Equal("http://model-proxy.invalid:8080", inherited.Output.Trim());
	}
}
