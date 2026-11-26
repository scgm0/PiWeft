using System.Diagnostics;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 改动行的动作接线：放弃更改要先确认、先打检查点，冲突行的对勾就是 git add。
/// 直接点真实按钮，不绕开面板的拼装逻辑。
[Collection("mewui-controls")]
public sealed class GitPanelActionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitact-" + Guid.NewGuid().ToString("N")[..8]);

	public GitPanelActionTests()
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

	private static string NewWorktreePath() =>
		Path.Combine(Path.GetTempPath(), "piweft-gitact-copy-" + Guid.NewGuid().ToString("N")[..8]);

	[Fact]
	public void DiscardAsksFirstAndLeavesACheckpointBehind()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed\n");
		File.WriteAllText(Path.Combine(_root, "untracked.md"), "scratch\n");
		var asked = new List<string>();
		using var view = new GitView(() => _root, confirm: (message, _) =>
		{
			asked.Add(message);
			return Task.FromResult(true);
		});
		ConversationPerformanceTests.Layout(view.View, 360, 800);

		var tracked = Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitDiscard);
		ImagePreviewTests.Click(tracked);

		Assert.Single(asked);
		Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		// 放弃不可撤销：检查点必须已经落地，用户能去「检查点」分节找回。
		var checkpoint = Assert.Single(Checkpoints.List(_root));
		Assert.Equal("before-discard", checkpoint.Label);

		// 未跟踪文件的键是删除而不是回退：图标与文案都要分开。
		File.WriteAllText(Path.Combine(_root, "untracked.md"), "scratch\n");
		view.Refresh();
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		var untracked = Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitDiscardUntracked);
		ImagePreviewTests.Click(untracked);

		Assert.Equal(2, asked.Count);
		Assert.False(File.Exists(Path.Combine(_root, "untracked.md")));
	}

	[Fact]
	public void DecliningTheConfirmationKeepsTheFileUntouched()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed\n");
		using var view = new GitView(() => _root, confirm: static (_, _) => Task.FromResult(false));
		ConversationPerformanceTests.Layout(view.View, 360, 800);

		ImagePreviewTests.Click(Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitDiscard));

		Assert.Equal("changed\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		Assert.Empty(Checkpoints.List(_root));
	}

	[Fact]
	public void ConflictRowMarksTheFileResolved()
	{
		// 一条分支与 main 各改同一行：合不进去就停在冲突态，面板要给收尾的出口。
		File.WriteAllText(Path.Combine(_root, "conflict.md"), "main\n");
		Run("add", "--all");
		Run("commit", "-qm", "main side");
		Run("switch", "-q", "-c", "feature", "HEAD~1");
		File.WriteAllText(Path.Combine(_root, "conflict.md"), "feature\n");
		Run("add", "--all");
		Run("commit", "-qm", "feature side");
		Run("switch", "-q", "main");
		RunAllowingFailure("merge", "feature");

		using var view = new GitView(() => _root);
		ConversationPerformanceTests.Layout(view.View, 360, 800);

		Assert.Contains(Find<TextBlock>(view.View), static text => text.Text == Strings.GitMergeChanges);
		var resolve = Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitMarkResolved);
		ImagePreviewTests.Click(resolve);

		// git add 之后这个路径不再处于未合并状态：它变成一条已暂存改动。
		var change = Assert.Single(GitStatusReader.Read(_root).Changes);
		Assert.False(change.IsConflict);
		Assert.Equal("M", change.IndexStatus);
	}

	[Fact]
	public void MorePaneListsWorktreesAndOffersASession()
	{
		var worktree = NewWorktreePath();
		try
		{
			Assert.True(GitWriter.WorktreeAdd(_root, worktree, "piweft/act", "HEAD").Ok);
			string? openedPath = null;
			using var view = new GitView(() => _root, confirm: static (_, _) => Task.FromResult(true),
				openSessionIn: path => openedPath = path);
			view.OpenMorePaneForTests();
			ConversationPerformanceTests.Layout(view.View, 360, 900);

			var texts = Find<TextBlock>(view.View).Select(static text => text.Text).ToList();
			Assert.Contains("piweft/act", texts);
			Assert.Contains(worktree, texts);

			var session = Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitWorktreeSession);
			ImagePreviewTests.Click(session);
			Assert.Equal(worktree, openedPath);

			var remove = Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitWorktreeRemove);
			ImagePreviewTests.Click(remove);
			Assert.False(Directory.Exists(worktree));
			// 删副本不删分支。
			Assert.Contains("piweft/act", GitWriter.Branches(_root));
		}
		finally
		{
			if (Directory.Exists(worktree))
			{
				GitWriter.WorktreeRemoveOnly(_root, worktree);
			}
		}
	}

	[Fact]
	public void WorktreeEditorCreatesABranchAndCheckout()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-wtroot-" + Guid.NewGuid().ToString("N")[..8]);
		try
		{
			using var view = new GitView(() => _root, worktreeRoot: () => root);
			view.OpenMorePaneForTests();
			ConversationPerformanceTests.Layout(view.View, 360, 900);

			ImagePreviewTests.Click(Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitWorktreeNew));
			ConversationPerformanceTests.Layout(view.View, 360, 900);

			var inputs = Find<TextBox>(view.View)
				.Where(box => box.Placeholder is Strings.GitWorktreeNamePlaceholder or Strings.GitWorktreeBasePlaceholder)
				.ToArray();
			Assert.Equal(2, inputs.Length);
			inputs[0].Text = "piweft/ui-copy";

			ImagePreviewTests.Click(Find<Button>(view.View)
				.First(button => Find<TextBlock>(button).Any(text => text.Text == Strings.GitWorktreeCreate)));
			ConversationPerformanceTests.Layout(view.View, 360, 900);

			var created = Assert.Single(Directory.GetDirectories(root));
			Assert.True(Directory.Exists(created));
			Assert.Contains("piweft/ui-copy", GitWriter.Branches(_root));
			// 建好后列表里就有这一行，可以直接开新会话或删副本。
			Assert.Contains(Find<TextBlock>(view.View), text => text.Text == "piweft/ui-copy");

			GitWriter.WorktreeRemoveOnly(_root, created);
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	[Fact]
	public void SingleBranchDefaultsToTheWorkingTreeTarget()
	{
		// 只有一个分支时分支间比较没有意义：目标默认落到「工作区（含未提交）」。
		using var view = new GitView(() => _root);
		view.SetCompareHeightForTests(200);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);

		var combos = Find<ComboBox>(view.View).ToArray();
		Assert.Equal(2, combos.Length);
		Assert.Equal(Strings.GitCompareWorkingTree, combos[1].SelectedText);
	}

	[Fact]
	public void CompareWorktreeScopeListsDirtyAndUntrackedFiles()
	{
		Run("switch", "-q", "-c", "feature");
		File.WriteAllText(Path.Combine(_root, "feature.txt"), "feature\n");
		Run("add", "--all");
		Run("commit", "-qm", "feature work");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ndirty\n");
		File.WriteAllText(Path.Combine(_root, "untracked.txt"), "scratch\n");

		using var view = new GitView(() => _root);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);
		view.CompareWorkingTreeForTests("main");
		ConversationPerformanceTests.Layout(view.View, 360, 1000);

		var texts = Find<TextBlock>(view.View).Select(static text => text.Text).ToList();
		Assert.Contains(Strings.GitCompareWorkingTreeSummary(1), texts);
		Assert.Contains("feature.txt", texts);
		Assert.Contains("untracked.txt", texts);
		Assert.Contains("a.txt", texts);

		// 未跟踪文件没有 git 侧补丁：浮层应拿到合成出来的整份新增。
		var row = Find<Button>(view.View)
			.First(button => HoverHints.Text(button) == "untracked.txt");
		ImagePreviewTests.Click(row);

		Assert.NotNull(view.LastDiffContentForTests);
		Assert.Contains("scratch", view.LastDiffContentForTests!.Patch, StringComparison.Ordinal);
	}

	[Fact]
	public void CompareReviewLayersFilterAndViewedMarksTrackProgress()
	{
		Run("switch", "-q", "-c", "feature");
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		Directory.CreateDirectory(Path.Combine(_root, "tests"));
		Directory.CreateDirectory(Path.Combine(_root, "api"));
		File.WriteAllText(Path.Combine(_root, "src", "App.cs"), "class App {}\n");
		File.WriteAllText(Path.Combine(_root, "tests", "AppTests.cs"), "class AppTests {}\n");
		File.WriteAllText(Path.Combine(_root, "api", "thing.proto"), "message Thing {}\n");
		Run("add", "--all");
		Run("commit", "-qm", "feature files");
		Run("switch", "-q", "main");

		using var view = new GitView(() => _root);
		view.CompareBranchesForTests("main", "feature");
		ConversationPerformanceTests.Layout(view.View, 360, 1000);

		Assert.Equal(3, view.CompareVisibleCountForTests);
		Assert.Contains(Strings.GitCompareViewed(0, 3), Texts(view));

		// 只留测试层：实现与接口都从列表里消失。
		view.SetCompareLensForTests(GitReviewLayer.Tests);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);
		Assert.Equal(1, view.CompareVisibleCountForTests);
		Assert.Contains("AppTests.cs", Texts(view));
		Assert.DoesNotContain("App.cs", Texts(view));
		Assert.Contains(Strings.GitCompareViewed(0, 1), Texts(view));

		// 回到全部文件，点第一行的已看：计数前进，列表不重排。
		view.SetCompareLensForTests(GitReviewLayer.All);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);
		var mark = Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitCompareMarkViewed);
		ImagePreviewTests.Click(mark);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);
		Assert.Contains(Strings.GitCompareViewed(1, 3), Texts(view));
	}

	[Fact]
	public void CompareHonoursRepositoryReviewLayers()
	{
		// 仓库自己声明分层：内置规则会把 Impl.cs 当实现、把 *.g.cs 当实现；这里反过来。
		File.WriteAllText(
			Path.Combine(_root, ".gitattributes"),
			"src/Impl.cs piweft-review-layer=test\nsrc/Generated.g.cs piweft-review-layer=interface\n");
		Run("add", ".gitattributes");
		Run("commit", "-qm", "review layers");
		Run("switch", "-q", "-c", "feature");
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		File.WriteAllText(Path.Combine(_root, "src", "Impl.cs"), "class Impl {}\n");
		File.WriteAllText(Path.Combine(_root, "src", "Generated.g.cs"), "class Generated {}\n");
		Run("add", "--all");
		Run("commit", "-qm", "feature files");
		Run("switch", "-q", "main");

		using var view = new GitView(() => _root);
		view.CompareBranchesForTests("main", "feature");
		ConversationPerformanceTests.Layout(view.View, 360, 1000);
		Assert.Equal(2, view.CompareVisibleCountForTests);

		view.SetCompareLensForTests(GitReviewLayer.Tests);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);
		Assert.Equal(1, view.CompareVisibleCountForTests);
		Assert.Contains("Impl.cs", Texts(view));
		Assert.DoesNotContain("Generated.g.cs", Texts(view));

		view.SetCompareLensForTests(GitReviewLayer.Interface);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);
		Assert.Equal(1, view.CompareVisibleCountForTests);
		Assert.Contains("Generated.g.cs", Texts(view));
	}

	[Fact]
	public void ViewedMarksPersistForTheSameCommitPair()
	{
		Run("switch", "-q", "-c", "feature");
		File.WriteAllText(Path.Combine(_root, "one.cs"), "class One {}\n");
		File.WriteAllText(Path.Combine(_root, "two.cs"), "class Two {}\n");
		Run("add", "--all");
		Run("commit", "-qm", "feature files");
		Run("switch", "-q", "main");

		var saved = new List<DesktopPreference>();
		DesktopPreference Loader() => saved.Count > 0 ? saved[^1] : new DesktopPreference();
		using (var first = new GitView(() => _root)
		{
			PreferenceLoader = Loader,
			PreferenceSaver = preference => saved.Add(preference),
		})
		{
			first.CompareBranchesForTests("main", "feature");
			ConversationPerformanceTests.Layout(first.View, 360, 1000);
			ImagePreviewTests.Click(Find<Button>(first.View)
				.First(button => HoverHints.Text(button) == Strings.GitCompareMarkViewed));
			ConversationPerformanceTests.Layout(first.View, 360, 1000);
			Assert.Contains(Strings.GitCompareViewed(1, 2), Texts(first));
		}

		Assert.NotEmpty(saved);
		using var second = new GitView(() => _root)
		{
			PreferenceLoader = Loader,
			PreferenceSaver = preference => saved.Add(preference),
		};
		second.CompareBranchesForTests("main", "feature");
		ConversationPerformanceTests.Layout(second.View, 360, 1000);
		Assert.Contains(Strings.GitCompareViewed(1, 2), Texts(second));
	}

	[Fact]
	public void StageAllAndCommitCommitsEveryChangeAfterConfirmation()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed\n");
		var asked = 0;
		using var view = new GitView(() => _root, confirm: (_, _) =>
		{
			asked++;
			return Task.FromResult(true);
		});
		ConversationPerformanceTests.Layout(view.View, 360, 1000);

		var input = Find<MultiLineTextBox>(view.View).First();
		input.Text = "smart commit";

		var action = Find<Button>(view.View)
			.First(button => Find<TextBlock>(button).Any(text => text.Text == Strings.GitStageAllAndCommit));
		ImagePreviewTests.Click(action);

		Assert.Equal(1, asked);
		Assert.Empty(GitStatusReader.Read(_root).Changes);
		Assert.Equal("smart commit", Capture("log", "-1", "--format=%s").Trim());
	}

	private string Capture(params string[] arguments)
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
		return stdout;
	}

	[Fact]
	public void MissingWorktreeShowsPruneInsteadOfOpenActions()
	{
		var worktree = Path.Combine(Path.GetTempPath(), "piweft-gitact-missing-" + Guid.NewGuid().ToString("N")[..8]);
		Assert.True(GitWriter.WorktreeAdd(_root, worktree, "piweft/missing", "HEAD").Ok);
		Directory.Delete(worktree, recursive: true);

		using var view = new GitView(() => _root);
		view.OpenMorePaneForTests();
		ConversationPerformanceTests.Layout(view.View, 360, 1000);

		Assert.Contains(
			Find<TextBlock>(view.View),
			text => text.Text.Contains(Strings.GitWorktreeMissing, StringComparison.Ordinal));
		var prune = Find<Button>(view.View)
			.First(button => HoverHints.Text(button) == Strings.GitWorktreePrune);
		ImagePreviewTests.Click(prune);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);

		Assert.DoesNotContain(
			Find<TextBlock>(view.View),
			text => text.Text.Contains(Strings.GitWorktreeMissing, StringComparison.Ordinal));
		Assert.Contains("piweft/missing", GitWriter.Branches(_root));
	}

	[Fact]
	public void CommitDetailOffersTheCommitActionsMenu()
	{
		using var view = new GitView(() => _root, confirm: static (_, _) => Task.FromResult(true));
		view.ExpandFirstCommitForTests();
		ConversationPerformanceTests.Layout(view.View, 360, 1000);

		Assert.Contains(
			Find<Button>(view.View),
			button => HoverHints.Text(button) == Strings.GitCommitActions);
	}

	[Fact]
	public void SwitchingRepositoryDuringConfirmDiscardsTheOriginalTreeOnly()
	{
		// 确认框开着时切仓库：放弃必须落在点击那一刻的仓库上，而不是新选中的。
		var nested = Path.Combine(_root, "packages", "lib");
		Directory.CreateDirectory(nested);
		RunAt(nested, "init", "-q", "-b", "lib-main");
		RunAt(nested, "config", "user.email", "test@example.com");
		RunAt(nested, "config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(nested, "a.txt"), "nested-base\n");
		RunAt(nested, "add", "--all");
		RunAt(nested, "commit", "-qm", "lib base");
		File.WriteAllText(Path.Combine(nested, "a.txt"), "nested-dirty\n");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed\n");

		var gate = new TaskCompletionSource<bool>();
		using var view = new GitView(() => _root, confirm: (_, _) => gate.Task);
		ConversationPerformanceTests.Layout(view.View, 360, 1000);

		var discard = Find<Button>(view.View).First(button => HoverHints.Text(button) == Strings.GitDiscard);
		ImagePreviewTests.Click(discard);
		view.SelectRepositoryForTests(nested);
		gate.SetResult(true);

		Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		Assert.Equal("nested-dirty\n", File.ReadAllText(Path.Combine(nested, "a.txt")));
		Assert.Equal(nested, view.CurrentRepositoryForTests);
	}

	[Fact]
	public void RepositorySwitcherScansNestedReposAndSwitchesThePanel()
	{
		var nested = Path.Combine(_root, "packages", "lib");
		Directory.CreateDirectory(nested);
		RunAt(nested, "init", "-q", "-b", "lib-main");
		RunAt(nested, "config", "user.email", "test@example.com");
		RunAt(nested, "config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(nested, "lib.cs"), "class Lib {}\n");
		RunAt(nested, "add", "--all");
		RunAt(nested, "commit", "-qm", "lib base");

		using var view = new GitView(() => _root);
		Assert.Equal(2, view.RepositoriesForTests.Count);
		Assert.Equal(_root, view.CurrentRepositoryForTests);
		Assert.Equal("main", view.CurrentBranchForTests);

		view.SelectRepositoryForTests(nested);

		Assert.Equal(nested, view.CurrentRepositoryForTests);
		Assert.Equal("lib-main", view.CurrentBranchForTests);

		view.SelectRepositoryForTests(_root);
		Assert.Equal("main", view.CurrentBranchForTests);
	}

	private void RunAt(string cwd, params string[] arguments) =>
		RunIn(cwd, arguments, allowFailure: false);

	private static List<string> Texts(GitView view) =>
		[.. Find<TextBlock>(view.View).Select(static text => text.Text)];

	private void Run(params string[] arguments) => RunIn(_root, arguments, allowFailure: false);

	private void RunAllowingFailure(params string[] arguments) => RunIn(_root, arguments, allowFailure: true);

	private static void RunIn(string cwd, string[] arguments, bool allowFailure)
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
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		process.WaitForExit(30_000);
		var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
		Assert.True(
			allowFailure || process.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {output}");
	}
}
