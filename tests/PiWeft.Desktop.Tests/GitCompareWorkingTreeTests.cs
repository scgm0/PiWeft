using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 比较节的「工作区」范围：分叉点之后的分支提交，加上未提交与未跟踪文件。
/// dsh 的本地改动视图是同一件事；这里的断言全在真实 git 行为上。
public sealed class GitCompareWorkingTreeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitwtcmp-" + Guid.NewGuid().ToString("N")[..8]);

	public GitCompareWorkingTreeTests()
	{
		Directory.CreateDirectory(_root);
		Run("init", "-q", "-b", "main");
		Run("config", "user.email", "test@example.com");
		Run("config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		File.WriteAllText(Path.Combine(_root, "b.txt"), "base\n");
		File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n");
		Run("add", "--all");
		Run("commit", "-qm", "base");

		Run("switch", "-q", "-c", "feature");
		File.WriteAllText(Path.Combine(_root, "feature.txt"), "feature\n");
		File.WriteAllText(Path.Combine(_root, "b.txt"), "base\nfeature\n");
		Run("add", "--all");
		Run("commit", "-qm", "feature work");
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
	public void WorkingTreeScopeIncludesBranchCommitsUnstagedAndUntracked()
	{
		// 分支提交、未提交修改与未跟踪文件都要在这一份列表里；被忽略的产物不进来。
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ndirty\n");
		File.WriteAllText(Path.Combine(_root, "untracked.txt"), "scratch\n");
		File.WriteAllText(Path.Combine(_root, "build.log"), "ignored\n");

		var result = GitHistoryReader.CompareWorkingTree(_root, "main");

		Assert.Null(result.Error);
		Assert.Equal(1, result.Ahead);
		Assert.Equal(0, result.Behind);
		Assert.Equal(GitHistoryReader.WorktreeTarget, result.TargetHash);
		var paths = result.Files.Select(static file => file.Path).ToArray();
		Assert.Contains("a.txt", paths);
		Assert.Contains("b.txt", paths);
		Assert.Contains("feature.txt", paths);
		Assert.Contains("untracked.txt", paths);
		Assert.DoesNotContain("build.log", paths);
		Assert.Equal("?", result.Files.Single(static file => file.Path == "untracked.txt").Letter);
		Assert.NotNull(result.Files.Single(static file => file.Path == "a.txt").DiffStat);
	}

	[Fact]
	public void WorkingTreeFileDiffComparesAgainstTheForkPoint()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ndirty\n");
		var result = GitHistoryReader.CompareWorkingTree(_root, "main");

		var diff = GitHistoryReader.WorkingTreeFileDiff(_root, result.BaseHash, "a.txt");

		Assert.True(diff.Ok, diff.Error);
		Assert.Contains("+dirty", diff.Text, StringComparison.Ordinal);
		// 分叉点之前的内容不该被算成改动。
		Assert.DoesNotContain("-one", diff.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void UnknownBaseFailsInsteadOfDiffingSomethingElse()
	{
		var result = GitHistoryReader.CompareWorkingTree(_root, "no-such-branch");

		Assert.Equal(Strings.GitCompareFailed, result.Error);
		Assert.Empty(result.Files);
	}

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
}
