using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 启动目录兜底：Finder/launchd 启动会把 cwd 丢成文件系统根，此时不能把 <c>/</c> 当成项目。
/// </summary>
public class StartupDirectoryTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-startup-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _home;
	private readonly string _project;
	private readonly string _binary;
	private readonly string _systemRoot = Path.GetPathRoot(Path.GetTempPath())!;

	public StartupDirectoryTests()
	{
		_home = Path.Combine(_root, "home");
		_project = Path.Combine(_root, "project");
		_binary = Path.Combine(_root, "app");
		foreach (var directory in new[] { _home, _project, _binary })
		{
			Directory.CreateDirectory(directory);
		}
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void KeepsATerminalWorkingDirectory() =>
		Assert.Equal(_project, StartupDirectory.Resolve(_project, _binary, launchedByShell: false, [], _home));

	[Fact]
	public void RestoresTheRecentProjectWhenStartedFromTheBinaryDirectory() =>
		Assert.Equal(_project, StartupDirectory.Resolve(_binary, _binary, launchedByShell: false, [_project], _home));

	[Fact]
	public void FallsBackToTheRecentProjectWhenAShellLaunchLosesTheWorkingDirectory() =>
		Assert.Equal(_project, StartupDirectory.Resolve(_systemRoot, _binary, launchedByShell: true, [_project], _home));

	[Fact]
	public void FallsBackToTheHomeDirectoryWithoutARecentProject() =>
		Assert.Equal(_home, StartupDirectory.Resolve(_systemRoot, _binary, launchedByShell: true, [], _home));

	[Fact]
	public void KeepsARootWorkingDirectoryForTerminalLaunches() =>
		Assert.Equal(_systemRoot, StartupDirectory.Resolve(_systemRoot, _binary, launchedByShell: false, [_project], _home));

	[Fact]
	public void IgnoresRecentProjectsThatNoLongerExist() =>
		Assert.Equal(_home, StartupDirectory.Resolve(_systemRoot, _binary, launchedByShell: true, [Path.Combine(_root, "gone")], _home));

	[Fact]
	public void RecognizesFileSystemRootsAndOrdinaryDirectories()
	{
		Assert.True(StartupDirectory.IsFileSystemRoot(_systemRoot));
		Assert.False(StartupDirectory.IsFileSystemRoot(_project));
	}

	[Theory]
	[InlineData(true, "dev.piweft.desktop", null, true)]
	[InlineData(true, null, "application.dev.piweft.desktop.123", true)]
	[InlineData(true, null, "0", false)]
	[InlineData(true, null, null, false)]
	[InlineData(false, "dev.piweft.desktop", null, false)]
	public void DetectsShellLaunchesFromTheMacOsMarkers(
		bool isMacOS,
		string? bundleIdentifier,
		string? xpcServiceName,
		bool expected) =>
		Assert.Equal(expected, StartupDirectory.IsShellLaunch(isMacOS, bundleIdentifier, xpcServiceName));
}
