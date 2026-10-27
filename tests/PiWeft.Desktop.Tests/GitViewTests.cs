using System.Diagnostics;
using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-04：Git 面板（PiDeck 式布局）的分支、提交、目录树、差异与 AI 摘要。</summary>
[Collection("mewui-controls")]
public sealed class GitViewTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitview-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentRoot = Path.Combine(Path.GetTempPath(), "piweft-gitview-agent-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousAgentDir;

	public GitViewTests()
	{
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(_agentRoot);
		_previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentRoot);
		RunGit("init", "-b", "main");
		RunGit("config", "user.email", "test@example.com");
		RunGit("config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _previousAgentDir);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		if (Directory.Exists(_agentRoot))
		{
			Directory.Delete(_agentRoot, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private void RunGit(params string[] arguments) => RunGitOutput(arguments);

	private void RunGitIn(string cwd, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
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

		using var process = Process.Start(startInfo)!;
		process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit(30_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} 失败：{error}");
	}

	private string RunGitOutput(params string[] arguments)
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
		return output.Trim();
	}

	private static Model TestModel() => new()
	{
		Id = "git-summary-test",
		Name = "Git Summary Test",
		Provider = "test",
		Api = "test",
	};

	private static GitView View(string root, GitCommitAssistant? assistant = null, Action? openSettings = null) =>
		new(() => root, assistant, openSettings);

	private static void ExpandMore(GitView view)
	{
		var header = Find<Button>(view.View).Single(button => ContentText(button) == Strings.GitMore);
		ImagePreviewTests.Click(header);
	}

	private static Button? FileAction(GitView view, string path, string tooltip) =>
		Find<Button>(view.View).FirstOrDefault(button =>
			button.Tag is GitChange change && change.Path == path && HoverHints.Text(button) == tooltip);

	private static Button? FileRow(GitView view, string path) =>
		Find<Button>(view.View).FirstOrDefault(button => button.Tag is GitChange change && change.Path == path);

	private static MultiLineTextBox CommitInput(GitView view) =>
		Find<MultiLineTextBox>(view.View).Single(input => input.Placeholder?.Contains("Ctrl+Enter", StringComparison.Ordinal) == true);

	private static void KeyDown(Element view, KeyEventArgs args) =>
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(view, [args]);

	[Fact]
	public void CheckpointsAreNoLongerInTheGitPanel()
	{
		var view = View(_root);

		Assert.DoesNotContain(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.CreateCheckpoint);
		Assert.DoesNotContain(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.RestoreCheckpoint);
	}

	[Fact]
	public void PanelShowsBranchCommitAndStageControls()
	{
		var view = View(_root);

		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitBranchMenu);
		Assert.Contains(Find<MultiLineTextBox>(view.View), box => box.Placeholder == Strings.GitCommitPlaceholderTo("main"));
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStage);
		Assert.Contains(Find<Button>(view.View), button => ContentText(button) == Strings.GitCommit);
	}

	[Fact]
	public void PanelShowsRemoteToolbarAndCollapsedMoreSection()
	{
		var view = View(_root);

		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitFetch);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitPull);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitPush);

		// 检查点/stash 收进折叠的更多区：默认不铺开。
		Assert.DoesNotContain(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStash);
		ExpandMore(view);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStash);
	}

	[Fact]
	public void ChangesAreGroupedIntoUnstagedAndStagedSections()
	{
		File.WriteAllText(Path.Combine(_root, "b.txt"), "second");
		RunGit("add", "--", "a.txt");

		var view = View(_root);

		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitUnstaged);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitStaged);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStageAll);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitUnstageAll);
	}

	[Fact]
	public void BulkStageMovesEverythingIntoTheStagedSection()
	{
		File.WriteAllText(Path.Combine(_root, "b.txt"), "second");

		var view = View(_root);
		ImagePreviewTests.Click(Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitStageAll));

		var status = GitStatusReader.Read(_root);
		Assert.All(status.Changes, static change => Assert.True(change.HasStaged));
		Assert.DoesNotContain(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStageAll);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitUnstageAll);
	}

	[Fact]
	public void FileChangedBothWaysShowsUpInBothSections()
	{
		// 「已暂存又改过」（MM）是日常最容易看漏的状态：只按索引列归类会让人以为已经提交干净了。
		File.WriteAllText(Path.Combine(_root, "c.txt"), "one");
		RunGit("add", "--", "c.txt");
		File.WriteAllText(Path.Combine(_root, "c.txt"), "two");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "staged only");
		RunGit("add", "--", "a.txt");

		var status = GitStatusReader.Read(_root);
		var both = status.Changes.Single(static change => change.Path == "c.txt");
		Assert.True(both.HasStaged);
		Assert.True(both.HasWorkTree);

		var view = View(_root);
		Assert.Equal(2, Find<Button>(view.View).Count(button =>
			button.Tag is GitChange change && change.Path == "c.txt" && button.Content is StackPanel));
	}

	[Fact]
	public void RemoteButtonsAreDisabledOnARepositoryWithoutRemotes()
	{
		var view = View(_root);

		Assert.False(Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitFetch).IsEnabled);
		Assert.False(Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitPull).IsEnabled);
		Assert.False(Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitPush).IsEnabled);

		ExpandMore(view);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitNoRemote);
	}

	[Fact]
	public void StashThroughThePanelCleansTheWorkTree()
	{
		// stash 需要至少一个提交作为基线，先把文件提交进去再改。
		RunGit("add", "--all");
		RunGit("commit", "-m", "baseline");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed");

		var view = View(_root);
		ExpandMore(view);
		var stash = Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitStash);
		ImagePreviewTests.Click(stash);

		Assert.Empty(GitStatusReader.Read(_root).Changes);
		Assert.Single(GitWriter.Stashes(_root));

		// 有了条目就出现「应用」与「丢弃」。
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStashPop);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStashResolved);
	}

	[Fact]
	public void StagingThroughThePanelUpdatesTheStatus()
	{
		var view = View(_root);

		var stage = Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitStage);
		ImagePreviewTests.Click(stage);

		// 暂存后按钮变成「取消暂存」，状态行里出现已暂存标记。
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitUnstage);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitStaged);

		var status = GitStatusReader.Read(_root);
		Assert.Equal("A", status.Changes.Single(static change => change.Path == "a.txt").IndexStatus);
	}

	[Fact]
	public void NestedChangesRenderAsDirectoryRowsAndFold()
	{
		Directory.CreateDirectory(Path.Combine(_root, "src", "Feature"));
		File.WriteAllText(Path.Combine(_root, "src", "Feature", "One.cs"), "one");
		File.WriteAllText(Path.Combine(_root, "src", "Feature", "Two.cs"), "two");

		var view = View(_root);
		Assert.Contains(Find<Button>(view.View), button => button.Content is StackPanel panel &&
			Find<TextBlock>(panel).Any(text => text.Text == "src.Feature"));
		Assert.True(FileRow(view, "src/Feature/One.cs") is not null);

		var directory = Find<Button>(view.View).Single(button => button.Content is StackPanel panel &&
			Find<TextBlock>(panel).Any(text => text.Text == "src.Feature"));
		ImagePreviewTests.Click(directory);
		Assert.Null(FileRow(view, "src/Feature/One.cs"));
	}

	[Fact]
	public void DirectoryStageStagesTheWholeSubtree()
	{
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		File.WriteAllText(Path.Combine(_root, "src", "One.cs"), "one");
		File.WriteAllText(Path.Combine(_root, "src", "Two.cs"), "two");

		var view = View(_root);
		var directory = Find<Button>(view.View).Single(button => button.Content is StackPanel panel &&
			Find<TextBlock>(panel).Any(text => text.Text == "src"));
		var stage = Find<Button>(view.View).Single(button =>
			HoverHints.Text(button) == Strings.GitStageDirectory);
		ImagePreviewTests.Click(stage);

		var status = GitStatusReader.Read(_root);
		Assert.All(status.Changes.Where(static change => change.Path.StartsWith("src/", StringComparison.Ordinal)),
			static change => Assert.True(change.HasStaged));
		Assert.NotNull(directory);
	}

	[Fact]
	public void CtrlEnterCommitsTheStagedDraft()
	{
		RunGit("add", "--all");
		RunGit("commit", "-m", "baseline");
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		File.WriteAllText(Path.Combine(_root, "src", "One.cs"), "one");
		RunGit("add", "--", "src/One.cs");

		var view = View(_root);
		var input = CommitInput(view);
		input.Text = "feat: add one";
		KeyDown(input, new KeyEventArgs(Key.Enter, 0, ModifierKeys.Control));

		Assert.Equal("feat: add one", RunGitOutput("log", "-1", "--format=%s"));
		Assert.DoesNotContain(GitStatusReader.Read(_root).Changes, static change => change.HasStaged);
	}

	[Fact]
	public void PrimaryButtonLabelsAreCentredInTheirButtons()
	{
		RunGit("add", "--all");
		RunGit("commit", "-m", "baseline");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed");
		RunGit("add", "--all");

		var view = View(_root);
		ConversationPerformanceTests.Layout(view.View, 420, 700);
		AssertCentered(view, Strings.GitCommit);

		// 非仓库那一屏只有一个主按钮：初始化。
		var bare = Path.Combine(Path.GetTempPath(), "piweft-git-notrepo-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(bare);
		try
		{
			var other = View(bare);
			ConversationPerformanceTests.Layout(other.View, 420, 700);
			AssertCentered(other, Strings.GitInit);
		}
		finally
		{
			Directory.Delete(bare, recursive: true);
		}
	}

	[Fact]
	public void BranchChipSizesToItsContentInsteadOfTheWholeRow()
	{
		var view = View(_root);
		ConversationPerformanceTests.Layout(view.View, 420, 700);

		var chip = Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitBranchMenu);
		Assert.True(chip.Bounds.Width < 200,
			$"分支键被填充位拉成 {chip.Bounds.Width:0} DIP：悬停底色与命中区会铺满整条空白");
	}

	/// <summary>主按钮的标签必须落在按钮水平中心：贴左缘就成了「文字挤在左边」的观感。</summary>
	private static void AssertCentered(GitView view, string label)
	{
		var button = Find<Button>(view.View).FirstOrDefault(candidate => ContentText(candidate) == label);
		Assert.NotNull(button);
		var text = Find<TextBlock>(button!).Single(block => block.Text == label);
		var drift = Math.Abs((text.Bounds.X + text.Bounds.Width / 2) - (button!.Bounds.X + button.Bounds.Width / 2));
		Assert.True(drift <= 1, $"「{label}」的文字偏离按钮中心 {drift:0.##} DIP");
	}

	[Fact]
	public void PushAndPullButtonsBadgeTheAheadBehindCounts()
	{
		// 远端用裸仓库：本地领先 1 → 推送按钮挂「1」；远端再前进 1 并 fetch → 拉取按钮挂「1」。
		var remote = Path.Combine(Path.GetTempPath(), "piweft-git-badge-remote-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(remote);
		try
		{
			RunGitIn(remote, "init", "-q", "--bare", "-b", "main");
			RunGit("remote", "add", "origin", remote);
			RunGit("add", "--all");
			RunGit("commit", "-m", "baseline");
			RunGit("push", "-q", "-u", "origin", "main");

			File.WriteAllText(Path.Combine(_root, "a.txt"), "local only");
			RunGit("add", "--all");
			RunGit("commit", "-m", "local work");

			var ahead = View(_root);
			var pushButton = Find<Button>(ahead.View).Single(button => HoverHints.Text(button) == Strings.GitPushAhead(1));
			Assert.Contains(Find<TextBlock>(pushButton).ToList(), static block => block.Text == "1");
			Assert.DoesNotContain(Find<Button>(ahead.View), button => HoverHints.Text(button)?.StartsWith(Strings.GitPullBehind(1), StringComparison.Ordinal) == true);

			// 远端侧多一个提交，fetch 之后拉取按钮才该有角标。
			var other = Path.Combine(remote, "..", Path.GetFileName(remote) + "-work");
			Directory.CreateDirectory(other);
			RunGitIn(other, "clone", "-q", remote, ".");
			RunGitIn(other, "config", "user.email", "other@example.com");
			RunGitIn(other, "config", "user.name", "Other");
			File.WriteAllText(Path.Combine(other, "remote-only.txt"), "remote");
			RunGitIn(other, "add", "--all");
			RunGitIn(other, "commit", "-q", "-m", "remote work");
			RunGitIn(other, "push", "-q");
			RunGit("fetch", "-q");

			var behind = View(_root);
			Assert.Contains(Find<Button>(behind.View), button => HoverHints.Text(button) == Strings.GitPullBehind(1));
			var pull = Find<Button>(behind.View).Single(button => HoverHints.Text(button) == Strings.GitPullBehind(1));
			Assert.Contains(Find<TextBlock>(pull).ToList(), static block => block.Text == "1");
			// 两个方向的差异同时存在：推送角标还在。
			Assert.Contains(Find<Button>(behind.View), button => HoverHints.Text(button) == Strings.GitPushAhead(1));
		}
		finally
		{
			if (Directory.Exists(remote))
			{
				Directory.Delete(remote, recursive: true);
			}
		}
	}

	[Fact]
	public void GraphExpandsACommitAndOpensItsFileDiff()
	{
		RunGit("add", "--all");
		RunGit("commit", "-m", "first");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed");
		RunGit("add", "--all");
		RunGit("commit", "-m", "second");

		var view = View(_root);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitGraph);
		var commit = ButtonWithText(view, "second");
		Assert.NotNull(commit);
		ImagePreviewTests.Click(commit!);

		var file = ButtonWithText(view, "a.txt");
		Assert.NotNull(file);
		ImagePreviewTests.Click(file!);

		Assert.Contains(Find<MultiLineTextBox>(view.View),
			text => text.Text.Contains("+changed", StringComparison.Ordinal));
	}

	[Fact]
	public void CompareChangesListsBranchFilesAndOpensTheirDiff()
	{
		RunGit("add", "--all");
		RunGit("commit", "-m", "base");
		RunGit("branch", "feature");
		RunGit("switch", "-q", "feature");
		File.WriteAllText(Path.Combine(_root, "feature.txt"), "feature");
		RunGit("add", "--all");
		RunGit("commit", "-m", "feature");
		RunGit("switch", "-q", "main");

		var view = View(_root);
		var compareHeader = ButtonWithText(view, Strings.GitCompare);
		Assert.NotNull(compareHeader);
		ImagePreviewTests.Click(compareHeader!);

		var pickers = Find<ComboBox>(view.View).ToArray();
		Assert.Equal(2, pickers.Length);
		pickers[0].SelectedIndex = 0; // main
		pickers[1].SelectedIndex = 1; // feature
		var compare = ButtonWithText(view, Strings.GitCompareAction);
		Assert.NotNull(compare);
		ImagePreviewTests.Click(compare!);

		var file = ButtonWithText(view, "feature.txt");
		Assert.NotNull(file);
		ImagePreviewTests.Click(file!);

		Assert.Contains(Find<MultiLineTextBox>(view.View),
			text => text.Text.Contains("+feature", StringComparison.Ordinal));
	}

	[Fact]
	public void StagedDiffAllReadsOnlyTheIndexAndReportsEmptyState()
	{
		RunGit("add", "--all");
		RunGit("commit", "-m", "baseline");
		var empty = GitStatusReader.StagedDiffAll(_root);
		Assert.True(empty.Ok);
		Assert.Equal(string.Empty, empty.Text);

		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed");
		RunGit("add", "--all");
		var staged = GitStatusReader.StagedDiffAll(_root);
		Assert.True(staged.Ok);
		Assert.Contains("+changed", staged.Text, StringComparison.Ordinal);
		Assert.Contains("-hello", staged.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void AiButtonFillsTheCommitDraftFromTheStagedDiff()
	{
		RunGit("add", "--all");
		RunGit("commit", "-m", "baseline");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed");
		RunGit("add", "--all");

		var model = TestModel();
		string? usedDiff = null;
		Model? usedModel = null;
		string? usedThinking = null;
		var assistant = new GitCommitAssistant(
			() => [model],
			() => model,
			() => ThinkingLevels.Off,
			(selected, thinking, diff, _) =>
			{
				usedModel = selected;
				usedThinking = thinking;
				usedDiff = diff;
				return Task.FromResult("test: generated summary");
			},
			() => false);

		var view = View(_root, assistant);
		var ai = Find<Button>(view.View).Single(button =>
			button.Tag is string tag && tag == Strings.GitGenerateCommitSummary);
		Assert.True(ai.IsEnabled);
		ImagePreviewTests.Click(ai);

		Assert.NotNull(usedDiff);
		Assert.Contains("changed", usedDiff!, StringComparison.Ordinal);
		Assert.Equal(model, usedModel);
		Assert.Equal(ThinkingLevels.Off, usedThinking);
		Assert.Equal("test: generated summary", CommitInput(view).Text);
	}

	[Fact]
	public void AiButtonUsesTheConfiguredModelAndThinkingLevel()
	{
		RunGit("add", "--all");
		RunGit("commit", "-m", "baseline");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed");
		RunGit("add", "--all");

		var sessionModel = TestModel();
		var fixedModel = new Model { Id = "fixed", Provider = "test", Api = "test", Name = "Fixed" };
		DesktopPreference.Load()
			.WithGitCommitModel("test/fixed")
			.WithGitCommitThinking("high")
			.Save();

		Model? usedModel = null;
		string? usedThinking = null;
		var assistant = new GitCommitAssistant(
			() => [sessionModel, fixedModel],
			() => sessionModel,
			() => ThinkingLevels.Low,
			(selected, thinking, _, _) =>
			{
				usedModel = selected;
				usedThinking = thinking;
				return Task.FromResult("test: fixed");
			},
			() => false);

		var view = View(_root, assistant);
		var ai = Find<Button>(view.View).Single(button =>
			button.Tag is string tag && tag == Strings.GitGenerateCommitSummary);
		ImagePreviewTests.Click(ai);

		Assert.Equal(fixedModel, usedModel);
		Assert.Equal("high", usedThinking);
	}

	[Fact]
	public void AiButtonIsDisabledWithoutStagedChanges()
	{
		var model = TestModel();
		var calls = 0;
		var assistant = new GitCommitAssistant(
			() => [model],
			() => model,
			() => ThinkingLevels.Off,
			(_, _, _, _) =>
			{
				calls++;
				return Task.FromResult("unused");
			},
			() => false);

		var view = View(_root, assistant);
		var ai = Find<Button>(view.View).Single(button =>
			button.Tag is string tag && tag == Strings.GitGenerateCommitSummary);
		Assert.False(ai.IsEnabled);
		ImagePreviewTests.Click(ai);
		Assert.Equal(0, calls);
	}

	[Fact]
	public void AiButtonIsDisabledWhileTheSessionIsBusy()
	{
		RunGit("add", "--all");
		RunGit("commit", "-m", "baseline");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed");
		RunGit("add", "--all");

		var model = TestModel();
		var assistant = new GitCommitAssistant(
			() => [model],
			() => model,
			() => ThinkingLevels.Off,
			(_, _, _, _) => Task.FromResult("unused"),
			() => true);

		var view = View(_root, assistant);
		var ai = Find<Button>(view.View).Single(button =>
			button.Tag is string tag && tag == Strings.GitGenerateCommitSummary);
		Assert.False(ai.IsEnabled);
	}

	private static Button? ButtonWithText(GitView view, string text) =>
		Find<Button>(view.View).FirstOrDefault(button =>
			Find<TextBlock>(button).Any(block => string.Equals(block.Text, text, StringComparison.Ordinal)));

	private static string? ContentText(Button button) =>
		button.Content switch
		{
			TextBlock text => text.Text,
			StackPanel panel => Find<TextBlock>(panel).Select(static block => block.Text)
				.FirstOrDefault(static text => !string.IsNullOrEmpty(text)),
			DockPanel panel => Find<TextBlock>(panel).Select(static block => block.Text)
				.FirstOrDefault(static text => !string.IsNullOrEmpty(text)),
			_ => null,
		};
}
