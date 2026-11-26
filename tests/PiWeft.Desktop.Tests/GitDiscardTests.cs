using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 放弃更改、stash 应用与隔离副本：面板里会丢数据的几条路，语义直接用真实 git 钉住。
/// 打桩只验证我们的假设，而「未跟踪文件删没删干净」「分支还在不在」全在 git 自己身上。
public sealed class GitDiscardTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitdiscard-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _worktree = Path.Combine(Path.GetTempPath(), "piweft-gitdiscard-copy-" + Guid.NewGuid().ToString("N")[..8]);

	public GitDiscardTests()
	{
		Directory.CreateDirectory(_root);
		Run("init", "-q", "-b", "main");
		Run("config", "user.email", "test@example.com");
		Run("config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		File.WriteAllText(Path.Combine(_root, "b.txt"), "two\n");
		Run("add", "--all");
		Run("commit", "-qm", "base");
	}

	public void Dispose()
	{
		foreach (var directory in new[] { _worktree, _root })
		{
			try
			{
				if (Directory.Exists(directory))
				{
					Directory.Delete(directory, recursive: true);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 临时目录删不掉不影响测试结论。
			}
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void DiscardRestoresTrackedFilesAndDeletesUntrackedOnes()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed\n");
		File.WriteAllText(Path.Combine(_root, "untracked.md"), "scratch\n");

		var result = GitWriter.Discard(_root, ["a.txt"], ["untracked.md"]);

		Assert.True(result.Ok, result.Message);
		Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		Assert.False(File.Exists(Path.Combine(_root, "untracked.md")));
		Assert.Empty(GitStatusReader.Read(_root).Changes);
	}

	[Fact]
	public void DiscardDirectoryRemovesItsWholeSubtree()
	{
		var directory = Path.Combine(_root, "src", "mod");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "one.cs"), "one\n");
		File.WriteAllText(Path.Combine(directory, "two.cs"), "two\n");
		File.WriteAllText(Path.Combine(_root, "b.txt"), "edited\n");

		var result = GitWriter.Discard(_root, ["b.txt"], ["src/"]);

		Assert.True(result.Ok, result.Message);
		Assert.False(Directory.Exists(Path.Combine(_root, "src")));
		Assert.Equal("two\n", File.ReadAllText(Path.Combine(_root, "b.txt")));
	}

	[Fact]
	public void DiscardKeepsTheStagedSideOfAPartiallyStagedFile()
	{
		// 先暂存一版，再改一版：放弃只能回退工作区那一侧，索引里的内容不能被一起吞掉。
		File.WriteAllText(Path.Combine(_root, "a.txt"), "staged\n");
		Run("add", "a.txt");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "worktree\n");

		var result = GitWriter.Discard(_root, ["a.txt"], []);

		Assert.True(result.Ok, result.Message);
		Assert.Equal("staged\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		var change = Assert.Single(GitStatusReader.Read(_root).Changes);
		Assert.Equal("M", change.IndexStatus);
		Assert.Equal(" ", change.WorkTreeStatus);
	}

	[Fact]
	public void ApplyingASpecificStashOnlyTouchesThatEntry()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "stash-a\n");
		Assert.True(GitWriter.StashPush(_root).Ok);
		File.WriteAllText(Path.Combine(_root, "b.txt"), "stash-b\n");
		Assert.True(GitWriter.StashPush(_root).Ok);
		Assert.Equal(2, GitWriter.Stashes(_root).Count);

		// stash@{1} 是较早那条：只把 a.txt 的改动带回来，b.txt 保持基线。
		var applied = GitWriter.StashApply(_root, "stash@{1}");

		Assert.True(applied.Ok, applied.Message);
		Assert.Equal("stash-a\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		Assert.Equal("two\n", File.ReadAllText(Path.Combine(_root, "b.txt")));
		Assert.Equal(2, GitWriter.Stashes(_root).Count);
	}

	[Fact]
	public void WorktreePruneRemovesMissingDirectoriesAndKeepsTheBranch()
	{
		Assert.True(GitWriter.WorktreeAdd(_root, _worktree, "piweft/missing", "HEAD").Ok);
		Directory.Delete(_worktree, recursive: true);
		Assert.Contains(GitWriter.Worktrees(_root), static worktree => !worktree.IsMain);

		Assert.True(GitWriter.WorktreePrune(_root).Ok);

		Assert.DoesNotContain(GitWriter.Worktrees(_root), static worktree => !worktree.IsMain);
		Assert.Contains("piweft/missing", GitWriter.Branches(_root));
	}

	[Fact]
	public void WorktreeRemoveRejectsOptionLikeOrRelativeInputs()
	{
		Assert.False(GitWriter.WorktreeRemove(_root, "-evil", "piweft/x").Ok);
		Assert.False(GitWriter.WorktreeRemove(_root, "relative/path", "piweft/x").Ok);
		Assert.False(GitWriter.WorktreeRemove(_root, _worktree, "has space").Ok);
		Assert.False(GitWriter.WorktreeRemove(_root, _worktree, string.Empty).Ok);
	}

	[Fact]
	public void PathsAreLiteralNotPathspecGlobs()
	{
		// 文件名里的 * 不是通配符：只暂存/删掉用户点中的那一个，不连带同名模式。
		File.WriteAllText(Path.Combine(_root, "a*.txt"), "literal\n");
		File.WriteAllText(Path.Combine(_root, "ab.txt"), "other\n");

		Assert.True(GitWriter.Stage(_root, ["a*.txt"]).Ok);
		var changes = GitStatusReader.Read(_root).Changes
			.ToDictionary(static change => change.Path, static change => change.IndexStatus);
		Assert.Equal("A", changes["a*.txt"]);
		Assert.Equal("?", changes["ab.txt"]);

		Assert.True(GitWriter.Unstage(_root, ["a*.txt"]).Ok);
		var discarded = GitWriter.Discard(_root, [], ["a*.txt"]);
		Assert.True(discarded.Ok, discarded.Message);
		Assert.False(File.Exists(Path.Combine(_root, "a*.txt")));
		Assert.True(File.Exists(Path.Combine(_root, "ab.txt")));
	}

	[Fact]
	public void DiscardRefusesToDeleteAnUntrackedNestedRepository()
	{
		var nested = Path.Combine(_root, "vendor-copy");
		Directory.CreateDirectory(nested);
		RunAt(nested, "init", "-q", "-b", "main");
		File.WriteAllText(Path.Combine(nested, "lib.txt"), "nested\n");

		var result = GitWriter.Discard(_root, [], ["vendor-copy/"]);

		Assert.False(result.Ok);
		Assert.Equal(Strings.GitDiscardNestedRepository("vendor-copy/"), result.Message);
		Assert.True(File.Exists(Path.Combine(nested, "lib.txt")));
	}

	[Fact]
	public void DiscardRestoresAnUnstagedRename()
	{
		// 未暂存改名在 porcelain 里是 “D old + ?? new”：两个路径都交给放弃，回到原文。
		File.Move(Path.Combine(_root, "b.txt"), Path.Combine(_root, "renamed.txt"));
		var changes = GitStatusReader.Read(_root).Changes;
		var tracked = changes.Where(static change => change.WorkTreeStatus is not " " && change.IndexStatus != "?")
			.Select(static change => change.Path).ToArray();
		var untracked = changes.Where(static change => change.IndexStatus == "?" && change.WorkTreeStatus == "?")
			.Select(static change => change.Path).ToArray();
		Assert.Equal(["b.txt"], tracked);
		Assert.Equal(["renamed.txt"], untracked);

		var result = GitWriter.Discard(_root, tracked, untracked);

		Assert.True(result.Ok, result.Message);
		Assert.True(File.Exists(Path.Combine(_root, "b.txt")));
		Assert.False(File.Exists(Path.Combine(_root, "renamed.txt")));
	}

	[Fact]
	public void DiscardRejectsAnEmptySelection()
	{
		var result = GitWriter.Discard(_root, [], []);

		Assert.False(result.Ok);
		Assert.Equal(Strings.GitNoSelection, result.Message);
	}

	[Fact]
	public void StashApplyKeepsTheEntryForAnotherTry()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed\n");
		Assert.True(GitWriter.StashPush(_root).Ok);
		Assert.Single(GitWriter.Stashes(_root));

		var applied = GitWriter.StashApply(_root);

		Assert.True(applied.Ok, applied.Message);
		Assert.Equal("changed\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		// pop 会把条目收走，apply 留着：误点一次还有退路。
		Assert.Single(GitWriter.Stashes(_root));
	}

	[Fact]
	public void WorktreeAddRejectsOptionLikeOrRelativeInputs()
	{
		// 三个参数都进 argv：分支名以 - 开头会被 git 当成选项，路径必须写绝对路径。
		Assert.False(GitWriter.WorktreeAdd(_root, Path.Combine(Path.GetTempPath(), NewSuffix()), "-evil", "HEAD").Ok);
		Assert.False(GitWriter.WorktreeAdd(_root, "relative/path", "piweft/x", "HEAD").Ok);
		Assert.False(GitWriter.WorktreeAdd(_root, Path.Combine(Path.GetTempPath(), NewSuffix()), "has space", "HEAD").Ok);
	}

	[Fact]
	public void WorktreeAddReportsAnExistingBranch()
	{
		Assert.True(GitWriter.WorktreeAdd(_root, _worktree, "piweft/dup", "HEAD").Ok);
		var second = Path.Combine(Path.GetTempPath(), NewSuffix());

		var result = GitWriter.WorktreeAdd(_root, second, "piweft/dup", "HEAD");

		Assert.False(result.Ok);
		Assert.Equal(Strings.GitWorktreeExists, result.Message);
		Assert.False(Directory.Exists(second));
	}

	[Fact]
	public void WorktreeListFlagsTheMainCheckoutAndRemoveKeepsItsBranch()
	{
		var added = GitWriter.WorktreeAdd(_root, _worktree, "piweft/copy", "HEAD");
		Assert.True(added.Ok, added.Message);

		var worktrees = GitWriter.Worktrees(_root);
		Assert.Equal(2, worktrees.Count);
		Assert.True(worktrees[0].IsMain);
		var linked = Assert.Single(worktrees, static worktree => !worktree.IsMain);
		Assert.Equal("piweft/copy", linked.BranchName);

		var removed = GitWriter.WorktreeRemoveOnly(_root, _worktree);

		Assert.True(removed.Ok, removed.Message);
		Assert.False(Directory.Exists(_worktree));
		// 面板里删副本不删分支：分支可能有别人还要用的提交。
		Assert.Contains("piweft/copy", GitWriter.Branches(_root));
	}

	private static void RunAt(string cwd, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = cwd,
		};

		startInfo.Environment["LC_ALL"] = "C";
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		var stderr = process.StandardError.ReadToEndAsync();
		process.StandardOutput.ReadToEnd();
		process.WaitForExit(30_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr.GetAwaiter().GetResult()}");
	}

	private static string NewSuffix() => "piweft-worktree-x-" + Guid.NewGuid().ToString("N")[..8];

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
		var stderr = process.StandardError.ReadToEndAsync();
		process.StandardOutput.ReadToEnd();
		process.WaitForExit(30_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr.GetAwaiter().GetResult()}");
	}
}
