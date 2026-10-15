using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// <summary>
/// stdio 服务器命令的解析与子进程 PATH 的组装。图形外壳启动（macOS Finder）只给最小 PATH，
/// 这里的合并顺序与解析结果决定了 <c>"command": "node"</c> 能不能起来。
/// </summary>
public class McpLaunchEnvironmentTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-launch-" + Guid.NewGuid().ToString("N")[..8]);

	public McpLaunchEnvironmentTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void MergePathKeepsTheFirstOccurrenceAndSkipsEmptyEntries()
	{
		var separator = Path.PathSeparator;
		var merged = McpLaunchEnvironment.MergePath([
			$"/first{separator}/shared",
			null,
			string.Empty,
			$"/shared{separator}/second{separator}",
		]);

		Assert.Equal($"/first{separator}/shared{separator}/second", merged);
	}

	[Fact]
	public void ResolveCommandFindsAnExecutableOnTheGivenPath()
	{
		var executable = Path.Combine(_root, "piweft-launch-probe");
		File.WriteAllText(executable, string.Empty);

		Assert.Equal(executable, McpLaunchEnvironment.ResolveCommand("piweft-launch-probe", _root));
		Assert.Null(McpLaunchEnvironment.ResolveCommand("piweft-launch-probe-missing", _root));
	}

	[Theory]
	[InlineData("./server")]
	[InlineData("/opt/tools/server")]
	[InlineData("../bin/server")]
	public void ResolveCommandLeavesCommandsThatCarryADirectoryAlone(string command)
	{
		// 相对路径的基准是子进程的工作目录，改写会破坏该语义。
		Assert.Equal(command, McpLaunchEnvironment.ResolveCommand(command, _root));
	}

	[Fact]
	public void WellKnownDirectoriesAreAbsoluteAndNotEmpty()
	{
		var directories = McpLaunchEnvironment.WellKnownDirectories().ToList();

		Assert.NotEmpty(directories);
		Assert.All(directories, static directory => Assert.True(Path.IsPathRooted(directory), directory));

		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (home.Length > 0)
		{
			Assert.Contains(Path.Combine(home, ".local", "bin"), directories);
		}

		if (OperatingSystem.IsMacOS())
		{
			Assert.Contains("/opt/homebrew/bin", directories);
		}
		else if (OperatingSystem.IsLinux())
		{
			Assert.Contains("/usr/local/bin", directories);
		}
	}

	[Fact]
	public void EffectivePathStartsWithTheEnvironmentPath()
	{
		var environment = Environment.GetEnvironmentVariable("PATH");
		Assert.False(string.IsNullOrEmpty(environment));

		Assert.StartsWith(environment!.Split(Path.PathSeparator)[0], McpLaunchEnvironment.EffectivePath);
	}

	[Fact]
	public async Task LoginShellPathIsEitherUsableOrEmpty()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var path = await McpLaunchEnvironment.ReadLoginShellPathAsync();

		// 探测是尽力而为的：失败按空串负缓存，成功则必须是单行 PATH。
		Assert.DoesNotContain('\n', path);
		if (path.Length > 0)
		{
			Assert.Contains(Path.PathSeparator, path);
		}
	}

	[Fact]
	public async Task StartingAnUnknownCommandReportsTheSearchedPath()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var config = new McpServerConfig { Name = "image-gen", Command = "piweft-missing-command-xyz" };

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => McpProcess.StartAsync(config, CancellationToken.None));

		Assert.Contains("piweft-missing-command-xyz", exception.Message);
		Assert.Contains("PATH searched", exception.Message);
	}

	[Fact]
	public async Task BareCommandNamesAreResolvedThroughTheEffectivePath()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// sh 一定位于系统目录且在这些平台上都在 PATH 里：解析结果必须是存在的绝对路径，
		// 否则 .NET 会用自己的查找规则，图形外壳下仍然 ENOENT。
		var (fileName, path) = await McpLaunchEnvironment.PrepareAsync("sh");

		Assert.True(Path.IsPathRooted(fileName), fileName);
		Assert.True(File.Exists(fileName), fileName);
		Assert.False(string.IsNullOrEmpty(path));
	}
}
