using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>命令 Dock 的 shell 选择：偏好持久化、解析回退与参数。</summary>
public sealed class TerminalShellTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "pi-shell-" + Guid.NewGuid().ToString("N")[..8]);

	public TerminalShellTests() => Directory.CreateDirectory(_directory);

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	[Fact]
	public void PreferenceRoundTripsTheShell()
	{
		new DesktopPreference().WithTerminalShell("/bin/zsh").Save(_directory);

		Assert.Equal("/bin/zsh", DesktopPreference.Load(_directory).TerminalShell);

		// 空值不落盘：文件里看不到字段，读取时回到自动。
		new DesktopPreference().WithTerminalShell(string.Empty).Save(_directory);
		Assert.Equal(string.Empty, DesktopPreference.Load(_directory).TerminalShell);
	}

	[Fact]
	public void InvalidShellFallsBackToAnExistingOne()
	{
		var resolved = TerminalShells.Resolve("/definitely/not/a/shell");

		Assert.True(File.Exists(resolved), $"fallback shell should exist: {resolved}");
	}

	[Fact]
	public void ExplicitShellWinsWhenItExists()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
		{
			return;
		}

		Assert.Equal("/bin/sh", TerminalShells.Resolve("/bin/sh"));
		Assert.True(TerminalShells.IsExecutable("sh"));
		Assert.Contains("-i", TerminalShells.Arguments("/bin/sh"));
	}

	[Fact]
	public void NonPosixShellsStartWithoutTheInteractiveFlag()
	{
		Assert.Empty(TerminalShells.Arguments("/usr/bin/nu"));
	}

	[Fact]
	public void DetectIncludesTheSystemShell()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
		{
			return;
		}

		var paths = TerminalShells.Detect().Select(static option => option.Path).ToArray();

		Assert.Contains("/bin/sh", paths);
		Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());

		// /bin 与 /usr/bin 在 usrmerge 下指向同一文件，菜单不能出现两组 bash/sh/zsh。
		var real = paths.Select(TerminalShells.RealPath).ToArray();
		Assert.Equal(real.Length, real.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void EraseScrollbackLeavesAnEmptyScreen()
	{
		var screen = new TerminalScreen(40, 5);
		screen.Feed("old output\r\nmore output".AsSpan());
		Assert.True(screen.ScrollbackLines >= 0);

		screen.Feed("\u001b[3J\u001b[H".AsSpan());

		Assert.Equal(0, screen.ScrollbackLines);
		Assert.Equal(0, screen.ViewOffset);
		Assert.Equal(string.Empty, screen.GetText());
	}
}
