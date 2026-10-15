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
