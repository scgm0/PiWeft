using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>D-05：工作区检查点写入低层引用、可回滚文件、可按上限清理。</summary>
public class CheckpointTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-ckpt-" + Guid.NewGuid().ToString("N")[..8]);

	public CheckpointTests()
	{
		Directory.CreateDirectory(_root);
		RunGit("init", "-b", "main");
		RunGit("config", "user.email", "test@example.com");
		RunGit("config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one");
		RunGit("add", "a.txt");
		RunGit("commit", "-m", "initial");
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

	private string FileText(string name) => File.ReadAllText(Path.Combine(_root, name));

	[Fact]
	public void CreateWritesADedicatedRefWithoutTouchingBranches()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "two");
		File.WriteAllText(Path.Combine(_root, "new.txt"), "untracked");

		var checkpoint = Checkpoints.Create(_root, "before edit");

		Assert.NotNull(checkpoint);

		// 不产生新提交，分支仍停在初始提交。
		var head = GitOutput("rev-list", "--count", "HEAD").Trim();
		Assert.Equal("1", head);

		var listed = Checkpoints.List(_root);
		var single = Assert.Single(listed);
		Assert.Equal(checkpoint!.Id, single.Id);
		Assert.Equal("before edit", single.Label);

		// 快照包含未跟踪文件：恢复后新文件仍在。
		File.WriteAllText(Path.Combine(_root, "a.txt"), "three");
		File.Delete(Path.Combine(_root, "new.txt"));
		Assert.Null(Checkpoints.Restore(_root, checkpoint.Sha));
		Assert.Equal("two", FileText("a.txt"));
		Assert.True(File.Exists(Path.Combine(_root, "new.txt")), "快照里的未跟踪文件应被恢复");
	}

	[Fact]
	public void RestoreBringsBackTheSnapshotContent()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "before");
		var checkpoint = Checkpoints.Create(_root)!;

		File.WriteAllText(Path.Combine(_root, "a.txt"), "after");
		File.WriteAllText(Path.Combine(_root, "extra.txt"), "extra");

		Assert.Null(Checkpoints.Restore(_root, checkpoint.Sha));
		Assert.Equal("before", FileText("a.txt"));
		// 快照之后新增的未跟踪文件保留，避免误删用户工作。
		Assert.True(File.Exists(Path.Combine(_root, "extra.txt")));
	}

	[Fact]
	public void CleanTreeStillProducesACheckpoint()
	{
		var checkpoint = Checkpoints.Create(_root);

		Assert.NotNull(checkpoint);
		Assert.Single(Checkpoints.List(_root));
	}

	[Fact]
	public void AutoSnapshotSkipsCleanTreesAndRespectsTheInterval()
	{
		// 干净工作区：不打快照，也不消耗限频窗口。
		Assert.Null(Checkpoints.AutoSnapshot(_root, TimeSpan.Zero));
		Assert.Empty(Checkpoints.List(_root));

		File.WriteAllText(Path.Combine(_root, "a.txt"), "dirty");
		Assert.NotNull(Checkpoints.AutoSnapshot(_root, TimeSpan.FromMinutes(1)));
		Assert.Single(Checkpoints.List(_root));

		// 限频窗口内不再打。
		File.WriteAllText(Path.Combine(_root, "a.txt"), "dirtier");
		Assert.Null(Checkpoints.AutoSnapshot(_root, TimeSpan.FromMinutes(1)));
		Assert.Single(Checkpoints.List(_root));
	}

	[Fact]
	public void PruneKeepsTheNewestCheckpoints()
	{
		for (var index = 0; index < 4; index++)
		{
			File.WriteAllText(Path.Combine(_root, "a.txt"), $"v{index}");
			Assert.NotNull(Checkpoints.Create(_root, $"v{index}"));
		}

		Assert.Equal(4, Checkpoints.List(_root, int.MaxValue).Count);
		Assert.Equal(2, Checkpoints.Prune(_root, keep: 2));
		Assert.Equal(2, Checkpoints.List(_root, int.MaxValue).Count);
	}

	private string GitOutput(params string[] arguments)
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
		var output = process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit(15_000);
		return output;
	}
}
