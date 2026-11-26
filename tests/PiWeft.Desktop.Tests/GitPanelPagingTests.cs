using System.Diagnostics;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 提交详情与分支比较的文件列表按页折出：一个提交或一次比较带上千个文件时，
/// 一次全建行会让图/比较面板在展开或比较完成的瞬间卡住。
[Collection("mewui-controls")]
public sealed class GitPanelPagingTests : IDisposable
{
	private const int Files = 200;

	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-git-paging-" + Guid.NewGuid().ToString("N")[..8]);

	public GitPanelPagingTests()
	{
		Directory.CreateDirectory(_root);
		RunGit("init", "-q", "-b", "main");
		RunGit("config", "user.email", "test@example.com");
		RunGit("config", "user.name", "PiWeft Test");
		for (var index = 0; index < Files; index++)
		{
			Write(index, "base\n");
		}

		RunGit("add", "--all");
		RunGit("commit", "-qm", "base");
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
	public void CommitDetailListsOnlyTheFirstPage()
	{
		using var view = new GitView(() => _root);
		view.ExpandFirstCommitForTests();

		var texts = Find<TextBlock>(view.View).Select(static text => text.Text).ToList();
		Assert.Equal(50, texts.Count(static text => text.StartsWith("page", StringComparison.Ordinal) && text.EndsWith(".cs", StringComparison.Ordinal)));
		Assert.Contains(Strings.GitLoadMore, texts);
		// 提交的文件行也带行数：每个文件新增一行；合计直接从 numstat 加出来。
		Assert.Contains(Strings.GitAddedLines(1), texts);
		Assert.Contains(Strings.GitCommitFiles(Files, Files, 0), texts);
	}

	[Fact]
	public void CompareResultListsOnlyTheFirstPage()
	{
		RunGit("switch", "-q", "-c", "other");
		for (var index = 0; index < Files; index++)
		{
			Write(index, "base\nchanged\n");
		}

		RunGit("commit", "-qam", "other");
		RunGit("switch", "-q", "main");

		using var view = new GitView(() => _root);
		view.CompareBranchesForTests("main", "other");

		var texts = Find<TextBlock>(view.View).Select(static text => text.Text).ToList();
		Assert.Equal(50, texts.Count(static text => text.StartsWith("page", StringComparison.Ordinal) && text.EndsWith(".cs", StringComparison.Ordinal)));
		Assert.Contains(Strings.GitLoadMore, texts);
		// 比较的文件行也带行数。
		Assert.Contains(Strings.GitAddedLines(1), texts);
	}

	[Fact]
	public void ComparingTheSameBranchNormalizesThePair()
	{
		RunGit("switch", "-q", "-c", "other");
		RunGit("switch", "-q", "main");

		using var view = new GitView(() => _root);
		// 两个下拉可以选成同一个分支：比较前先归一，界面显示的基准/目标要和实际跑的一致。
		view.CompareBranchesForTests("main", "main");

		var combos = Find<ComboBox>(view.View).ToList();
		Assert.Equal(2, combos.Count);
		Assert.Equal("other", combos[0].SelectedText);
		Assert.Equal("main", combos[1].SelectedText);
	}

	private void Write(int index, string content) =>
		File.WriteAllText(Path.Combine(_root, $"page{index}.cs"), content);

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
