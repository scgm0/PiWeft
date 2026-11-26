using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 更改列表的扁平行表：三段折成一份行序，分节标题行自己带着整节的改动
/// （「全部暂存」与行里的计数都靠它，丢了就只剩一个空的 0）。
public class GitChangeListTests
{
	private static GitChange Change(string path, string index = " ", string worktree = " ") =>
		new(index, worktree, path);

	private static GitStatus Status(params GitChange[] changes) =>
		new() { IsRepository = true, Branch = "main", Changes = changes };

	[Fact]
	public void SectionsKeepTheirOwnChangesInDisplayOrder()
	{
		var status = Status(
			Change("conflict.cs", "U", "U"),
			Change("staged.cs", "A", " "),
			Change("modified.cs", " ", "M"),
			Change("untracked.cs", "?", "?"));

		var rows = GitChangeList.Build(status);

		Assert.Equal(
			[
				GitChangeRowKind.Section, GitChangeRowKind.File,
				GitChangeRowKind.Section, GitChangeRowKind.File,
				GitChangeRowKind.Section, GitChangeRowKind.File, GitChangeRowKind.File,
			],
			rows.Select(static row => row.Kind));

		Assert.Equal(Strings.GitMergeChanges, rows[0].Title);
		Assert.True(rows[0].Conflict);
		// 冲突文件的「暂存」在 git 里就是标记已解决：面板要能收尾合并，不能只读。
		Assert.True(rows[0].AllowActions);
		Assert.Equal(["conflict.cs"], rows[0].Changes.Select(static change => change.Path));

		Assert.Equal(Strings.GitStaged, rows[2].Title);
		Assert.True(rows[2].Staged);
		Assert.Single(rows[2].Changes);
		Assert.True(rows[2].AllowActions);

		Assert.Equal(Strings.GitUnstaged, rows[4].Title);
		Assert.False(rows[4].Staged);
		Assert.Equal(["modified.cs", "untracked.cs"], rows[4].Changes.Select(static change => change.Path));
	}

	[Fact]
	public void EmptyStatusHasNoRows()
	{
		var rows = GitChangeList.Build(Status());

		Assert.Empty(rows);
	}

	[Fact]
	public void DirectoriesCarryTheirWholeSubtree()
	{
		var status = Status(
			Change("src/a/one.cs", " ", "M"),
			Change("src/a/two.cs", " ", "M"),
			Change("src/b/three.cs", " ", "M"));

		var rows = GitChangeList.Build(status);
		var src = Assert.Single(rows, static row => row.Tree?.Path == "src");
		var nested = Assert.Single(rows, static row => row.Tree?.Path == "src/a");

		// 目录行也能单独暂存：paths 来自这一行携带的整棵子树。
		Assert.Equal(["src/a/one.cs", "src/a/two.cs", "src/b/three.cs"], src.Changes.Select(static change => change.Path));
		Assert.Equal(["src/a/one.cs", "src/a/two.cs"], nested.Changes.Select(static change => change.Path));
	}

	[Fact]
	public void CollapsedDirectoryKeepsItsRowButDropsChildren()
	{
		var status = Status(
			Change("src/a/one.cs", " ", "M"),
			Change("src/b/two.cs", " ", "M"),
			Change("root.cs", " ", "M"));

		var expanded = GitChangeList.Build(status);
		var collapsed = GitChangeList.Build(status, new HashSet<string>(StringComparer.Ordinal) { "src" });

		Assert.Contains(expanded, static row => row.Tree?.Path == "src/a");
		Assert.DoesNotContain(collapsed, static row => row.Tree?.Path == "src/a");
		Assert.Single(collapsed, static row => row.Tree?.Path == "src");
		Assert.Single(collapsed, static row => row.Kind == GitChangeRowKind.File);
	}

	[Fact]
	public void RowKeysSeparateSectionsFromFilesAndDirectories()
	{
		var status = Status(
			Change("src/a.cs", "A", " "),
			Change("src/a.cs", " ", "M"));

		var keys = GitChangeList.Build(status).Select(static row => row.Key).ToList();

		Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
	}
}
