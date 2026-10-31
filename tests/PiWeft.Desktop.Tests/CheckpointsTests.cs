using System.Diagnostics;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// D-05：工作区检查点（<c>refs/pi-checkpoints</c>）的后端与右侧面板视图。
/// 后端用真实 git 仓库跑；视图只断言结构（新增/刷新/差异/恢复入口与空态），
/// 恢复确认框与异步创建单独由后端用例覆盖。
/// </summary>
[Collection("mewui-controls")]
public sealed class CheckpointsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-checkpoints-" + Guid.NewGuid().ToString("N")[..8]);

	public CheckpointsTests()
	{
		Directory.CreateDirectory(Repo);
		RunGit("init", "-q", "-b", "main");
		RunGit("config", "user.email", "checkpoint@example.com");
		RunGit("config", "user.name", "PiWeft Checkpoints");
		File.WriteAllText(Path.Combine(Repo, "a.txt"), "hello");
		RunGit("add", "--", "a.txt");
		RunGit("commit", "-q", "-m", "initial");
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string Repo => Path.Combine(_root, "repo");

	private void RunGit(params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = Repo,
		};
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit(30_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} 失败：{error}{output}");
	}

	private Checkpoint CreateCheckpoint(string label = "manual")
	{
		var checkpoint = Checkpoints.Create(Repo, label);
		Assert.NotNull(checkpoint);
		return checkpoint!;
	}

	[Fact]
	public void CreateListDiffAndRestoreRoundTrip()
	{
		var checkpoint = CreateCheckpoint();

		Assert.Contains(
			Checkpoints.List(Repo),
			candidate => candidate.Id == checkpoint.Id && candidate.Sha == checkpoint.Sha);

		File.WriteAllText(Path.Combine(Repo, "a.txt"), "hello world\nsecond line\n");
		var (patch, error) = Checkpoints.Patch(Repo, checkpoint.Sha);
		Assert.Null(error);
		Assert.Contains("-hello", patch, StringComparison.Ordinal);
		Assert.Contains("+hello world", patch, StringComparison.Ordinal);
		Assert.Contains("second line", patch, StringComparison.Ordinal);

		var (stat, statFailure) = Checkpoints.Stat(Repo, checkpoint.Sha);
		Assert.Null(statFailure);
		Assert.Contains("a.txt", stat, StringComparison.Ordinal);
		// 概览是逐文件的一行，不是 5 万行补丁：这正是面板默认展示它的原因。
		Assert.DoesNotContain("@@", stat, StringComparison.Ordinal);

		var (shortStat, statError) = Checkpoints.ShortStat(Repo, checkpoint.Sha);
		Assert.Null(statError);
		Assert.Contains("1 file changed", shortStat, StringComparison.Ordinal);

		Assert.Null(Checkpoints.Restore(Repo, checkpoint.Sha));
		Assert.Equal("hello", File.ReadAllText(Path.Combine(Repo, "a.txt")).Trim());
	}

	[Fact]
	public void NoChangesMeansEmptyDiffAndShortStat()
	{
		var checkpoint = CreateCheckpoint();

		Assert.Equal(string.Empty, Checkpoints.Patch(Repo, checkpoint.Sha).Patch);
		Assert.Equal(string.Empty, Checkpoints.ShortStat(Repo, checkpoint.Sha).Summary);
		Assert.Equal(string.Empty, Checkpoints.Stat(Repo, checkpoint.Sha).Stat);
	}

	[Fact]
	public void ListIsNewestFirstAndPruneKeepsTheRequestedCount()
	{
		var first = CreateCheckpoint("manual");
		File.WriteAllText(Path.Combine(Repo, "b.txt"), "second");
		RunGit("add", "--", "b.txt");
		var second = CreateCheckpoint("manual");

		var listed = Checkpoints.List(Repo);
		Assert.True(listed.Count >= 2);
		Assert.Contains(listed, candidate => candidate.Id == second.Id);

		var removed = Checkpoints.Prune(Repo, keep: 1);
		Assert.True(removed >= 1);
		var pruned = Checkpoints.List(Repo);
		Assert.Single(pruned);
		// 保留的是较新的那个：同一秒内创建时靠 id 里的毫秒兜底。
		Assert.Equal(second.Id, pruned[0].Id);
		Assert.NotEqual(first.Id, pruned[0].Id);
	}

	[Fact]
	public void CustomLabelsArePreserved()
	{
		var checkpoint = CreateCheckpoint("before-restore");

		var listed = Checkpoints.List(Repo);
		Assert.Equal("before-restore", listed.Single(candidate => candidate.Id == checkpoint.Id).Label);
	}

	[Fact]
	public void ViewShowsRowsAndActions()
	{
		CreateCheckpoint();
		var view = new CheckpointsView(() => Repo);

		var buttons = Find<Button>(view.View).ToList();
		Assert.Contains(buttons, button => HoverHints.Text(button) == Strings.CreateCheckpoint);
		Assert.Contains(buttons, button => HoverHints.Text(button) == Strings.CheckpointRefresh);
		Assert.Contains(buttons, button => HoverHints.Text(button) == Strings.CheckpointDiff);
		Assert.Contains(buttons, button => HoverHints.Text(button) == Strings.RestoreCheckpoint);
		Assert.Contains(Find<TextBlock>(view.View), text => text.Text.Contains("检查点", StringComparison.Ordinal));
	}

	[Fact]
	public void ViewExplainsNonGitDirectories()
	{
		// 必须在仓库之外：git 会向上找到 Repo，否则这里会被判成「在仓库里」。
		var plain = Path.Combine(Path.GetTempPath(), "piweft-plain-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(plain);

		var view = new CheckpointsView(() => plain);

		Assert.Contains(
			Find<TextBlock>(view.View),
			text => text.Text.Contains("Git 仓库", StringComparison.Ordinal));
		Assert.DoesNotContain(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.CheckpointDiff);
		Directory.Delete(plain, recursive: true);
	}
}
