using System.Diagnostics;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-04：Git 面板暴露分支、提交与暂存控件，并能真正暂存文件。</summary>
[Collection("mewui-controls")]
public sealed class GitViewTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitview-" + Guid.NewGuid().ToString("N")[..8]);

	public GitViewTests()
	{
		Directory.CreateDirectory(_root);
		RunGit("init", "-b", "main");
		RunGit("config", "user.email", "test@example.com");
		RunGit("config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
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
		process.StandardError.ReadToEnd();
		process.WaitForExit(15_000);
	}

	[Fact]
	public void CreatingACheckpointAddsARestoreRow()
	{
		var view = new GitView(() => _root);

		var create = Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.CreateCheckpoint);
		ImagePreviewTests.Click(create);

		Assert.Single(Checkpoints.List(_root));
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.RestoreCheckpoint);
	}

	[Fact]
	public void PanelShowsBranchCommitAndStageControls()
	{
		var view = new GitView(() => _root);

		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitBranch);
		Assert.Contains(Find<TextBox>(view.View), box => box.Placeholder == Strings.GitCommitPlaceholder);
		Assert.Contains(Find<TextBox>(view.View), box => box.Placeholder == Strings.GitBranchPlaceholder);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStage);
	}

	[Fact]
	public void PanelShowsRemoteAndStashControls()
	{
		var view = new GitView(() => _root);

		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitRemotes);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitStashes);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitFetch);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStash);

		// 拉取/推送用的是文字按钮，按内容找。
		Assert.Contains(Find<Button>(view.View), button => ContentText(button) == Strings.GitPull);
		Assert.Contains(Find<Button>(view.View), button => ContentText(button) == Strings.GitPush);
	}

	[Fact]
	public void ChangesAreGroupedIntoUnstagedAndStagedSections()
	{
		File.WriteAllText(Path.Combine(_root, "b.txt"), "second");
		RunGit("add", "--", "a.txt");

		var view = new GitView(() => _root);

		// 两节标题都在（a.txt 已暂存、b.txt 未跟踪），不再靠行内标签区分。
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitUnstaged);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitStaged);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStageAll);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitUnstageAll);
	}

	[Fact]
	public void BulkStageMovesEverythingIntoTheStagedSection()
	{
		File.WriteAllText(Path.Combine(_root, "b.txt"), "second");

		var view = new GitView(() => _root);
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

		// 两节里各出现一次：一节给「再次暂存」，另一节给「取消暂存」。
		var view = new GitView(() => _root);
		Assert.Equal(2, Find<Button>(view.View).Count(button => ContentPath(button) == "c.txt"));
	}

	[Fact]
	public void RemoteButtonsAreDisabledOnARepositoryWithoutRemotes()
	{
		// 没有远端时把按钮变灰，而不是让用户点下去看报错。
		var view = new GitView(() => _root);

		Assert.False(Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitFetch).IsEnabled);
		Assert.False(Find<Button>(view.View).Single(button => ContentText(button) == Strings.GitPull).IsEnabled);
		Assert.False(Find<Button>(view.View).Single(button => ContentText(button) == Strings.GitPush).IsEnabled);

		// 面板还要说清楚为什么不能推。
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitNoRemote);
	}

	[Fact]
	public void StashThroughThePanelCleansTheWorkTree()
	{
		// stash 需要至少一个提交作为基线，先把文件提交进去再改。
		RunGit("add", "--all");
		RunGit("commit", "-m", "baseline");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed");

		var view = new GitView(() => _root);

		var stash = Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitStash);
		ImagePreviewTests.Click(stash);

		Assert.Empty(GitStatusReader.Read(_root).Changes);
		Assert.Single(GitWriter.Stashes(_root));

		// 有了条目就出现「应用」与「丢弃」。
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStashPop);
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitStashResolved);
	}

	/// <summary>改动行按钮的内容是 DockPanel（状态字 + 路径），取等宽字体的那一段文本。</summary>
	private static string? ContentPath(Button button) =>
		button.Content is DockPanel panel
			? Find<TextBlock>(panel)
				.Where(static block => block.FontFamily == AppTheme.MonoFont)
				.Select(static block => block.Text)
				.FirstOrDefault()
			: null;

	/// <summary>文字按钮的内容是 TextBlock 或在反色底上的 TextBlock，取它们的文本。</summary>
	private static string? ContentText(Button button) =>
		button.Content switch
		{
			TextBlock text => text.Text,
			DockPanel panel => Find<TextBlock>(panel).Select(static block => block.Text).FirstOrDefault(static text => text is { Length: > 0 }),
			_ => null,
		};

	[Fact]
	public void StagingThroughThePanelUpdatesTheStatus()
	{
		var view = new GitView(() => _root);

		var stage = Find<Button>(view.View).Single(button => HoverHints.Text(button) == Strings.GitStage);
		ImagePreviewTests.Click(stage);

		// 暂存后按钮变成「取消暂存」，状态行里出现已暂存标记。
		Assert.Contains(Find<Button>(view.View), button => HoverHints.Text(button) == Strings.GitUnstage);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitStaged);

		var status = GitStatusReader.Read(_root);
		Assert.Equal("A", status.Changes.Single(static change => change.Path == "a.txt").IndexStatus);
	}
}
