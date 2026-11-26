using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 会话目录里的仓库扫描：根仓库排首位，子仓库按相对路径排序，依赖目录与链接不跟进。
public sealed class GitRepositoriesTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitrepos-" + Guid.NewGuid().ToString("N")[..8]);

	public GitRepositoriesTests()
	{
		Directory.CreateDirectory(_root);
		InitRepository(_root);
		InitRepository(Path.Combine(_root, "packages", "lib"));
		InitRepository(Path.Combine(_root, "apps", "web"));
		// 依赖目录里的仓库不算用户的仓库。
		InitRepository(Path.Combine(_root, "node_modules", "dep"));
		// submodule 形态：.git 是文件而不是目录。
		var submodule = Path.Combine(_root, "vendor-lib");
		Directory.CreateDirectory(submodule);
		File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/vendor-lib\n");
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
	public void ScanFindsTheRootNestedAndSubmoduleRepositories()
	{
		var repositories = GitRepositories.Scan(_root);

		Assert.Equal(
			[".", "apps/web", "packages/lib", "vendor-lib"],
			repositories.Select(static repository => repository.IsRoot ? "." : repository.Relative));
		Assert.True(repositories[0].IsRoot);
		Assert.Equal(_root, repositories[0].Path);
		Assert.Equal("packages/lib", repositories[2].Label);
	}

	[Fact]
	public void SubdirectoryCwdFindsItsContainingRepository()
	{
		// 会话落在仓库内的普通子目录（本身不是仓库）时，仓库根也要出现在列表里。
		var child = Path.Combine(_root, "docs", "notes");
		Directory.CreateDirectory(child);
		var repositories = GitRepositories.Scan(child);

		Assert.NotEmpty(repositories);
		Assert.Equal(_root, repositories[0].Path);
		Assert.Equal("../..", repositories[0].Relative.Replace('\\', '/'));
	}

	[Fact]
	public void MissingDirectoryScansToNothing()
	{
		Assert.Empty(GitRepositories.Scan(Path.Combine(_root, "nope")));
		Assert.Empty(GitRepositories.Scan(string.Empty));
	}

	private static void InitRepository(string path)
	{
		Directory.CreateDirectory(path);
		Run(path, "init", "-q", "-b", "main");
		Run(path, "config", "user.email", "test@example.com");
		Run(path, "config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(path, "readme.md"), "# repo\n");
		Run(path, "add", "--all");
		Run(path, "commit", "-qm", "base");
	}

	private static void Run(string cwd, params string[] arguments)
	{
		var startInfo = new System.Diagnostics.ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = cwd,
		};

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = System.Diagnostics.Process.Start(startInfo)!;
		process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit(30_000);
		Assert.Equal(0, process.ExitCode);
	}
}
