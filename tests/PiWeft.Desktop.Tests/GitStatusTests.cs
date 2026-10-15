using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Git 状态解析。<c>git status --porcelain=v1 -b</c> 的输出是稳定的行格式，直接喂样本断言，
/// 不需要真的建仓库。
/// </summary>
public class GitStatusTests
{
	[Fact]
	public void ParsesBranchAheadBehindAndChanges()
	{
		const string output = """
			## main...origin/main [ahead 1, behind 2]
			 M src/PiWeft.Desktop/MainWindow.cs
			?? src/PiWeft.Desktop/GitView.cs
			A  tests/PiWeft.Desktop.Tests/GitStatusTests.cs
			D  old/file.txt
			R  old/name.txt -> new/name.txt
			""";

		var status = GitStatusReader.Parse(output);

		Assert.True(status.IsRepository);
		Assert.Equal("main", status.Branch);
		Assert.Equal(1, status.Ahead);
		Assert.Equal(2, status.Behind);
		Assert.Equal(5, status.Changes.Count);

		Assert.Equal("修改", status.Changes[0].Label);
		Assert.Equal("src/PiWeft.Desktop/MainWindow.cs", status.Changes[0].Path);
		Assert.Equal("未跟踪", status.Changes[1].Label);
		Assert.Equal("新增", status.Changes[2].Label);
		Assert.Equal("删除", status.Changes[3].Label);

		// 改名显示目标路径，而不是 "old -> new"。
		Assert.Equal("改名", status.Changes[4].Label);
		Assert.Equal("new/name.txt", status.Changes[4].Path);
	}

	[Fact]
	public void ParsesABranchWithoutUpstream()
	{
		var status = GitStatusReader.Parse("## feature/desktop\n");

		Assert.Equal("feature/desktop", status.Branch);
		Assert.Equal(0, status.Ahead);
		Assert.Equal(0, status.Behind);
		Assert.Empty(status.Changes);
	}

	[Fact]
	public void ParsesDetachedHeadAndConflictStatuses()
	{
		var status = GitStatusReader.Parse("## HEAD (no branch)\nUU src/conflict.cs\n");

		Assert.Equal("HEAD (no branch)", status.Branch);
		Assert.Equal("冲突", Assert.Single(status.Changes).Label);
	}

	[Fact]
	public void SummaryMentionsCleanAndAhead()
	{
		var clean = GitStatusReader.Parse("## main...origin/main [ahead 3]\n");
		Assert.Contains("main", clean.Summary());
		Assert.Contains("工作区干净", clean.Summary());
		Assert.Contains("↑3", clean.Summary());

		var dirty = GitStatusReader.Parse("## main\n M a.txt\n");
		Assert.Contains("1 个改动", dirty.Summary());
	}

	[Fact]
	public void NonRepositoryIsSummarizedBrieflyAndKeepsTheDetail()
	{
		// git 的输出很长（带文件系统边界的解释），摘要只给一句结论，细节留在 Error 里。
		var status = new GitStatus { Error = "fatal: not a git repository (or any parent up to mount point /)" };

		Assert.False(status.IsRepository);
		Assert.Equal(Strings.GitNotRepository, status.Summary());
		Assert.Contains("not a git repository", status.Error);
	}

	[Fact]
	public void ReadingARealDirectoryDoesNotThrow()
	{
		// 本仓库是 git 工作区，读取应当成功；即便不是，也只返回错误而不抛异常。
		var status = GitStatusReader.Read(Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(15));
		Assert.True(status.IsRepository || status.Error is { Length: > 0 });
	}
}

/// <summary>Git diff 读取的截断逻辑：界面只展示前若干行，超出部分给出提示。</summary>
public class GitDiffTests
{
	[Fact]
	public void ShortOutputIsReturnedUnchanged()
	{
		var text = "diff --git a/x b/x\n+one\n";
		Assert.Equal(text, GitStatusReader.Truncate(text, 10));
	}

	[Fact]
	public void LongOutputIsCutWithAnExplanation()
	{
		var text = string.Join('\n', Enumerable.Range(1, 30).Select(index => $"line {index}"));
		var truncated = GitStatusReader.Truncate(text, 5);

		Assert.StartsWith("line 1", truncated);
		Assert.Contains("line 5", truncated);
		Assert.DoesNotContain("line 6", truncated);
		Assert.Contains("还有 25 行未显示", truncated);
	}

	[Fact]
	public void ReadingADiffOutsideARepositoryDoesNotThrow()
	{
		// 非仓库目录：git 会失败，这里只要求返回空串而不是抛异常。
		var diff = GitStatusReader.Diff(Path.GetTempPath(), "nothing.txt", untracked: true);
		Assert.NotNull(diff);
	}
}
