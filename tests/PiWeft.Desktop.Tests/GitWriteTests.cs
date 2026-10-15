using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>D-04：Git 写操作。用真实临时仓库验证暂存、提交与分支切换。</summary>
public class GitWriteTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-git-" + Guid.NewGuid().ToString("N")[..8]);

	public GitWriteTests()
	{
		Directory.CreateDirectory(_root);
		RunGit("init", "-b", "main");
		RunGit("config", "user.email", "test@example.com");
		RunGit("config", "user.name", "PiWeft Test");
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private void RunGit(params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = _root,
		};

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit(15_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
	}

	private string Write(string name, string content)
	{
		var path = Path.Combine(_root, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return name;
	}

	[Fact]
	public void StageCommitAndUnstage_RoundTrip()
	{
		var file = Write("a.txt", "hello");

		// 未暂存时 Stage 成功，status 里该文件进入索引（IndexStatus 非空格）。
		Assert.True(GitWriter.Stage(_root, [file]).Ok);
		var staged = GitStatusReader.Read(_root);
		Assert.Equal("A", staged.Changes.Single(static change => change.Path == "a.txt").IndexStatus);

		Assert.True(GitWriter.Unstage(_root, [file]).Ok);
		var unstaged = GitStatusReader.Read(_root);
		Assert.Equal("?", unstaged.Changes.Single(static change => change.Path == "a.txt").IndexStatus);

		// 提交后工作区干净，且存在一个提交。
		Assert.True(GitWriter.Stage(_root, [file]).Ok);
		var commit = GitWriter.Commit(_root, "add a.txt");
		Assert.True(commit.Ok, commit.Message);
		Assert.Empty(GitStatusReader.Read(_root).Changes);
	}

	[Fact]
	public void CommitWithEmptyMessageIsRejected()
	{
		var result = GitWriter.Commit(_root, "   ");

		Assert.False(result.Ok);
		Assert.Equal(Strings.GitCommitMessageRequired, result.Message);
	}

	[Fact]
	public void CreateAndSwitchBranches_ListsCurrentFirst()
	{
		Write("a.txt", "hello");
		RunGit("add", "a.txt");
		RunGit("commit", "-m", "initial");

		Assert.True(GitWriter.CreateBranch(_root, "feature/one").Ok);
		Assert.Equal("feature/one", GitStatusReader.Read(_root).Branch);

		Assert.True(GitWriter.SwitchBranch(_root, "main").Ok);
		Assert.Equal("main", GitStatusReader.Read(_root).Branch);

		var branches = GitWriter.Branches(_root);
		Assert.Equal("main", branches[0]);
		Assert.Contains("feature/one", branches);
	}

	[Fact]
	public void InvalidBranchNamesAreRejectedWithoutTouchingGit()
	{
		Assert.False(GitWriter.CreateBranch(_root, "").Ok);
		Assert.False(GitWriter.CreateBranch(_root, "-danger").Ok);
		Assert.False(GitWriter.SwitchBranch(_root, "has space").Ok);
		Assert.False(GitWriter.Stage(_root, []).Ok);
	}
}
