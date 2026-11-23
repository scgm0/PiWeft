using System.Diagnostics;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 隔离副本的 git 生命周期：创建、取 patch、应用到主工作区、删除。
[Collection("worktree-registry")]
public sealed class GitWorktreeTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"piweft-worktree-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _worktree = Path.Combine(
		Path.GetTempPath(),
		"piweft-worktree-copy-" + Guid.NewGuid().ToString("N")[..8]);

	public GitWorktreeTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_worktree))
			{
				Directory.Delete(_worktree, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 临时目录删不掉不影响测试结论。
		}

		try
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 同上。
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void WorktreeCanBeCreatedPatchedAppliedAndRemoved()
	{
		Git("init", "--initial-branch=main");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		Git("add", "a.txt");
		Git("-c", "user.name=piweft", "-c", "user.email=piweft@example.com", "commit", "-m", "init");

		var baseline = GitWriter.WorktreeBase(_root, "HEAD");
		Assert.True(baseline.Ok, baseline.BaseCommit);
		Assert.False(string.IsNullOrEmpty(baseline.Repository));
		Assert.False(string.IsNullOrEmpty(baseline.BaseCommit));

		var added = GitWriter.WorktreeAdd(baseline.Repository, _worktree, "piweft/test", baseline.BaseCommit);
		Assert.True(added.Ok, added.Message);
		Assert.True(Directory.Exists(_worktree));

		File.WriteAllText(Path.Combine(_worktree, "a.txt"), "two\n");
		var patch = GitWriter.WorktreePatch(_worktree, baseline.BaseCommit);
		Assert.True(patch.Ok, patch.Text);
		Assert.Contains("a.txt", patch.Text, StringComparison.Ordinal);

		var stat = GitWriter.WorktreeStat(_worktree, baseline.BaseCommit);
		Assert.True(stat.Ok, stat.Text);
		Assert.Contains("a.txt", stat.Text, StringComparison.Ordinal);

		var applied = GitWriter.WorktreeApply(baseline.Repository, patch.Text);
		Assert.True(applied.Ok, applied.Message);
		Assert.Equal("two\n", File.ReadAllText(Path.Combine(_root, "a.txt")));

		var removed = GitWriter.WorktreeRemove(baseline.Repository, _worktree, "piweft/test");
		Assert.True(removed.Ok, removed.Message);
		Assert.False(Directory.Exists(_worktree));
	}

	[Fact]
	public void NestedCheckoutUsesItsOwnDiffBase()
	{
		Git("init", "--initial-branch=main");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		Git("add", "a.txt");
		Git("-c", "user.name=piweft", "-c", "user.email=piweft@example.com", "commit", "-m", "init");

		var baseline = GitWriter.WorktreeBase(_root, "HEAD");
		Assert.True(baseline.Ok);
		var added = GitWriter.WorktreeAdd(baseline.Repository, _worktree, "piweft/nested", baseline.BaseCommit);
		Assert.True(added.Ok, added.Message);

		// 主工作区在子代理运行期间继续改文件，隔离副本的 patch 不应该带上它。
		File.WriteAllText(Path.Combine(_root, "main-only.txt"), "main\n");
		var patch = GitWriter.WorktreePatch(_worktree, baseline.BaseCommit);
		Assert.True(patch.Ok);
		Assert.DoesNotContain("main-only.txt", patch.Text, StringComparison.Ordinal);

		Assert.True(GitWriter.WorktreeRemove(baseline.Repository, _worktree, "piweft/nested").Ok);
	}

	[Fact]
	public void CleanupRemovesTheWorktreeAndItsRegistryEntry()
	{
		Git("init", "--initial-branch=main");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		Git("add", "a.txt");
		Git("-c", "user.name=piweft", "-c", "user.email=piweft@example.com", "commit", "-m", "init");
		var baseline = GitWriter.WorktreeBase(_root, "HEAD");
		Assert.True(baseline.Ok);
		Assert.True(GitWriter.WorktreeAdd(baseline.Repository, _worktree, "piweft/cleanup", baseline.BaseCommit).Ok);

		var index = Path.Combine(_root, "registry.json");
		WorktreeRegistry.UseFileForTests(index);
		try
		{
			WorktreeRegistry.Record(new WorktreeEntry(
				baseline.Repository,
				_worktree,
				"piweft/cleanup",
				Path.Combine(_root, "parent-gone.json"),
				"worker-1",
				DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds()));
			Assert.Single(WorktreeRegistry.Orphans(TimeSpan.FromDays(1)));

			Assert.True(GitWriter.WorktreeRemove(baseline.Repository, _worktree, "piweft/cleanup").Ok);
			WorktreeRegistry.Forget(_worktree);
			WorktreeRegistry.UseFileForTests(index);
			Assert.Empty(WorktreeRegistry.Orphans(TimeSpan.FromDays(1)));
		}
		finally
		{
			WorktreeRegistry.UseFileForTests(null);
		}
	}

	[Fact]
	public void UnchangedWorktreeHasNoPatchOrStat()
	{
		Git("init", "--initial-branch=main");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		Git("add", "a.txt");
		Git("-c", "user.name=piweft", "-c", "user.email=piweft@example.com", "commit", "-m", "init");
		var baseline = GitWriter.WorktreeBase(_root, "HEAD");
		Assert.True(baseline.Ok);
		Assert.True(GitWriter.WorktreeAdd(baseline.Repository, _worktree, "piweft/clean", baseline.BaseCommit).Ok);

		var patch = GitWriter.WorktreePatch(_worktree, baseline.BaseCommit);
		Assert.True(patch.Ok, patch.Text);
		Assert.Equal(string.Empty, patch.Text);
		var stat = GitWriter.WorktreeStat(_worktree, baseline.BaseCommit);
		Assert.True(stat.Ok, stat.Text);
		Assert.Equal(string.Empty, stat.Text);

		Assert.True(GitWriter.WorktreeRemove(baseline.Repository, _worktree, "piweft/clean").Ok);
	}

	[Fact]
	public void ApplyRejectsAConflictingMainWorkspace()
	{
		Git("init", "--initial-branch=main");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		Git("add", "a.txt");
		Git("-c", "user.name=piweft", "-c", "user.email=piweft@example.com", "commit", "-m", "init");
		var baseline = GitWriter.WorktreeBase(_root, "HEAD");
		Assert.True(baseline.Ok);
		Assert.True(GitWriter.WorktreeAdd(baseline.Repository, _worktree, "piweft/conflict", baseline.BaseCommit).Ok);

		File.WriteAllText(Path.Combine(_worktree, "a.txt"), "two\n");
		var patch = GitWriter.WorktreePatch(_worktree, baseline.BaseCommit);
		Assert.True(patch.Ok);
		Assert.Contains("a.txt", patch.Text, StringComparison.Ordinal);

		// 主工作区也改了同一行：--check 失败，文件保持主工作区的内容。
		File.WriteAllText(Path.Combine(_root, "a.txt"), "three\n");
		var applied = GitWriter.WorktreeApply(baseline.Repository, patch.Text);
		Assert.False(applied.Ok);
		Assert.Contains(Strings.GitPatchConflict, applied.Message);
		Assert.Equal("three\n", File.ReadAllText(Path.Combine(_root, "a.txt")));

		Assert.True(GitWriter.WorktreeRemove(baseline.Repository, _worktree, "piweft/conflict").Ok);
	}

	private void Git(params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = _root,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo);
		if (process is null)
		{
			throw new InvalidOperationException("git is not available");
		}

		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"git {string.Join(' ', arguments)} failed: {stdout.GetAwaiter().GetResult()}{stderr.GetAwaiter().GetResult()}");
		}
	}
}
