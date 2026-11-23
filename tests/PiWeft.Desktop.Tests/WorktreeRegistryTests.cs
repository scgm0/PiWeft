using Xunit;

namespace PiWeft.Desktop.Tests;

/// 隔离副本登记表：孤儿判定与落盘恢复。
[Collection("worktree-registry")]
public sealed class WorktreeRegistryTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"piweft-worktree-registry-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _index;

	public WorktreeRegistryTests()
	{
		Directory.CreateDirectory(_root);
		_index = Path.Combine(_root, "index.json");
		WorktreeRegistry.UseFileForTests(_index);
	}

	public void Dispose()
	{
		WorktreeRegistry.UseFileForTests(null);
		try
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 临时目录删不掉不影响测试结论。
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void OrphansNeedAnOldEntryAMissingParentAndAnExistingDirectory()
	{
		var repo = Path.Combine(_root, "repo");
		Directory.CreateDirectory(repo);
		var orphan = Path.Combine(_root, "orphan");
		Directory.CreateDirectory(orphan);
		var alive = Path.Combine(_root, "alive");
		Directory.CreateDirectory(alive);
		var recent = Path.Combine(_root, "recent");
		Directory.CreateDirectory(recent);
		var missing = Path.Combine(_root, "missing");
		var liveParent = Path.Combine(_root, "parent.json");
		File.WriteAllText(liveParent, "{}");
		var old = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		WorktreeRegistry.Record(new WorktreeEntry(repo, orphan, "b1", Path.Combine(_root, "gone.json"), "worker-1", old));
		WorktreeRegistry.Record(new WorktreeEntry(repo, alive, "b2", liveParent, "worker-2", old));
		WorktreeRegistry.Record(new WorktreeEntry(repo, recent, "b3", Path.Combine(_root, "gone.json"), "worker-3", now));
		WorktreeRegistry.Record(new WorktreeEntry(repo, missing, "b4", Path.Combine(_root, "gone.json"), "worker-4", old));

		var orphans = WorktreeRegistry.Orphans(TimeSpan.FromDays(1));
		var found = Assert.Single(orphans);
		Assert.Equal(orphan, found.Path);
	}

	[Fact]
	public void ForgetRemovesTheEntryAndSurvivesAReload()
	{
		var repo = Path.Combine(_root, "repo");
		var path = Path.Combine(_root, "copy");
		Directory.CreateDirectory(path);
		WorktreeRegistry.Record(new WorktreeEntry(
			repo,
			path,
			"piweft/test",
			Path.Combine(_root, "gone.json"),
			"worker-1",
			DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds()));
		Assert.Single(WorktreeRegistry.Orphans(TimeSpan.FromDays(1)));

		// 重读一次文件，确认记录真的落盘。
		WorktreeRegistry.UseFileForTests(_index);
		Assert.Single(WorktreeRegistry.Orphans(TimeSpan.FromDays(1)));

		WorktreeRegistry.Forget(path);
		WorktreeRegistry.UseFileForTests(_index);
		Assert.Empty(WorktreeRegistry.Orphans(TimeSpan.FromDays(1)));
	}

	[Fact]
	public void MissingWorktreeDirectoryIsNotAnOrphan()
	{
		var repo = Path.Combine(_root, "repo");
		var path = Path.Combine(_root, "already-gone");
		WorktreeRegistry.Record(new WorktreeEntry(
			repo,
			path,
			"piweft/test",
			Path.Combine(_root, "gone.json"),
			"worker-1",
			DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds()));

		Assert.Empty(WorktreeRegistry.Orphans(TimeSpan.FromDays(1)));
	}
}
