using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Git 状态解析。<c>git status --porcelain=v1 -b -z</c> 是稳定的 NUL 分隔格式，直接喂样本断言，
/// 不需要真的建仓库。真实仓库上的中文 / 空格 / emoji 路径见 <c>GitStatusPathTests</c>。
/// </summary>
public class GitStatusTests
{
	/// <summary>把可读的多行样本拼成 -z 形态（每行以 NUL 结尾，含尾随 NUL）。</summary>
	private static string Z(params string[] fields) => string.Concat(fields.Select(static field => field + "\0"));

	[Fact]
	public void ParsesBranchAheadBehindAndChanges()
	{
		var output = Z(
			"## main...origin/main [ahead 1, behind 2]",
			" M src/PiWeft.Desktop/MainWindow.cs",
			"?? src/PiWeft.Desktop/GitView.cs",
			"A  tests/PiWeft.Desktop.Tests/GitStatusTests.cs",
			"D  old/file.txt",
			// -z 形态：改名条目后面紧跟一个独立字段放原路径。
			"R  new/name.txt",
			"old/name.txt");

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
		var status = GitStatusReader.Parse(Z("## feature/desktop"));

		Assert.Equal("feature/desktop", status.Branch);
		Assert.Equal(0, status.Ahead);
		Assert.Equal(0, status.Behind);
		Assert.Empty(status.Changes);
	}

	[Fact]
	public void ParsesDetachedHeadAndConflictStatuses()
	{
		var status = GitStatusReader.Parse(Z("## HEAD (no branch)", "UU src/conflict.cs"));

		// 分离头指针不能表现成一个叫 "HEAD (no branch)" 的分支：它会被列进分支下拉、还能被「切换」。
		Assert.True(status.Detached);
		Assert.Equal(string.Empty, status.Branch);
		Assert.Equal("冲突", Assert.Single(status.Changes).Label);
	}

	[Fact]
	public void ParsesAFreshRepositoryWithoutCommits()
	{
		var status = GitStatusReader.Parse(Z("## No commits yet on main", "?? a.txt"));

		Assert.Equal("main", status.Branch);
		Assert.False(status.Detached);
		Assert.False(status.HasUpstream);
	}

	[Fact]
	public void RenameConsumesTheOriginalPathField()
	{
		// 原路径是独立字段，漏掉它会让紧随其后的真实条目被丢掉或错位。
		var status = GitStatusReader.Parse(Z("## main", "R  new.txt", "old.txt", " M after.txt"));

		Assert.Equal(2, status.Changes.Count);
		Assert.Equal("new.txt", status.Changes[0].Path);
		Assert.Equal("改名", status.Changes[0].Label);
		Assert.Equal("after.txt", status.Changes[1].Path);
	}

	[Fact]
	public void SummaryMentionsCleanAndAhead()
	{
		var clean = GitStatusReader.Parse(Z("## main...origin/main [ahead 3]"));
		Assert.Contains("main", clean.Summary());
		Assert.Contains("工作区干净", clean.Summary());
		Assert.Contains("↑3", clean.Summary());

		var dirty = GitStatusReader.Parse(Z("## main", " M a.txt"));
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
}
