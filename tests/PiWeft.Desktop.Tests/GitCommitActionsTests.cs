using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 提交图上的提交操作：revert / cherry-pick / 软重置。语义全在 git 里，这里跑真实仓库。
public sealed class GitCommitActionsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitacts-" + Guid.NewGuid().ToString("N")[..8]);

	public GitCommitActionsTests()
	{
		Directory.CreateDirectory(_root);
		Run("init", "-q", "-b", "main");
		Run("config", "user.email", "test@example.com");
		Run("config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		Run("add", "--all");
		Run("commit", "-qm", "base");
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
	public void RevertCreatesAnInverseCommit()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ntwo\n");
		Run("commit", "-qam", "add two");
		var changed = Head();

		var result = GitWriter.RevertCommit(_root, changed);

		Assert.True(result.Ok, result.Message);
		Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		Assert.NotEqual(changed, Head());
		Assert.Contains("Revert", LastSubject(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void CherryPickBringsACommitToTheCurrentBranch()
	{
		Run("switch", "-q", "-c", "feature");
		File.WriteAllText(Path.Combine(_root, "feature.txt"), "feature\n");
		Run("add", "--all");
		Run("commit", "-qm", "feature work");
		var picked = Head();
		Run("switch", "-q", "main");

		var result = GitWriter.CherryPickCommit(_root, picked);

		Assert.True(result.Ok, result.Message);
		Assert.True(File.Exists(Path.Combine(_root, "feature.txt")));
		Assert.Equal("feature work", LastSubject());
		// 不比较哈希：cherry-pick 保留作者信息但 committer 时间是执行时刻，
		// 跨秒执行必然得到不同对象名，断言相等会偶发失败。
		Assert.NotEqual(picked, RunCapture("rev-parse", "HEAD~1").Trim());
	}

	[Fact]
	public void SoftResetKeepsTheChangesStaged()
	{
		var baseCommit = Head();
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ntwo\n");
		Run("commit", "-qam", "second");

		var result = GitWriter.ResetSoft(_root, baseCommit);

		Assert.True(result.Ok, result.Message);
		Assert.Equal(baseCommit, Head());
		Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		var change = Assert.Single(GitStatusReader.Read(_root).Changes);
		Assert.Equal("M", change.IndexStatus);
		Assert.Equal(" ", change.WorkTreeStatus);
	}

	[Fact]
	public void OptionLikeOrShortHashesAreRejected()
	{
		Assert.False(GitWriter.RevertCommit(_root, "--help").Ok);
		Assert.False(GitWriter.CherryPickCommit(_root, "abc1234").Ok);
		Assert.False(GitWriter.ResetSoft(_root, string.Empty).Ok);
	}

	private string Head() => RunCapture("rev-parse", "HEAD").Trim();

	private string LastSubject() => RunCapture("log", "-1", "--format=%s").Trim();

	private void Run(params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = _root,
		};

		startInfo.Environment["LC_ALL"] = "C";
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		process.WaitForExit(30_000);
		Assert.True(
			process.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {stdout.GetAwaiter().GetResult()}{stderr.GetAwaiter().GetResult()}");
	}

	private string RunCapture(params string[] arguments)
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
		var stdout = process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit(30_000);
		Assert.Equal(0, process.ExitCode);
		return stdout;
	}
}
