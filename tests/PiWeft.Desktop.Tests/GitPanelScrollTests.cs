using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 图/比较两节的重建路径：展开提交不能把图的滚动位置丢回顶部，比较节不能缩到把按钮裁掉。
[Collection("mewui-controls")]
public sealed class GitPanelScrollTests : IDisposable
{
	private const int Commits = 24;

	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-git-scroll-" + Guid.NewGuid().ToString("N")[..8]);

	public GitPanelScrollTests()
	{
		Directory.CreateDirectory(_root);
		RunGit("init", "-q", "-b", "main");
		RunGit("config", "user.email", "test@example.com");
		RunGit("config", "user.name", "PiWeft Test");
		for (var index = 0; index < Commits; index++)
		{
			File.WriteAllText(Path.Combine(_root, $"file{index}.cs"), $"commit {index}\n");
			RunGit("add", "--all");
			RunGit("commit", "-qm", $"commit {index}");
		}
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
	public void ExpandingACommitKeepsTheGraphScrollPosition()
	{
		using var view = new GitView(() => _root);
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		view.ScrollGraphForTests(240);
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		var before = view.GraphScrollOffsetForTests;
		Assert.True(before > 100, $"图没有可滚动的内容，偏移 {before}");

		view.ToggleCommitForTests(3);
		ConversationPerformanceTests.Layout(view.View, 360, 800);

		// 旧实现每次 RebuildGraph 换掉整个 ScrollViewer，这里会变成 0。
		Assert.Equal(before, view.GraphScrollOffsetForTests, precision: 1);
	}

	[Fact]
	public void ComparePaneKeepsEnoughHeightForItsControls()
	{
		using var view = new GitView(() => _root);
		// 拖到最小：比较节的基准/目标与比较按钮仍在节高以内，不会被裁剪。
		view.SetCompareHeightForTests(60);
		ConversationPerformanceTests.Layout(view.View, 360, 800);

		Assert.True(
			view.ComparePaneHeightForTests >= GitPaneLayout.ExpandedHeight(120) - 0.5,
			$"比较节被压到 {view.ComparePaneHeightForTests}");
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
}
