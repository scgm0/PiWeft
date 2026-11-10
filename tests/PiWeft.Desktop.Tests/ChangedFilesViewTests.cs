using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 增删行数口径：只数内容行，文件头与 hunk 头都不算。
public sealed class DiffStatsTests
{
	[Fact]
	public void CountsOnlyContentLines()
	{
		const string patch =
			"--- a/src/app.cs\n" +
			"+++ b/src/app.cs\n" +
			"@@ -1,2 +1,3 @@\n" +
			" context\n" +
			"-removed\n" +
			"+added\n" +
			"+also added\n";
		Assert.Equal((2, 1), DiffStats.Count(patch));
	}

	[Fact]
	public void HandlesMultipleFileSections()
	{
		const string patch =
			"--- a/one.cs\n+++ b/one.cs\n@@ -1 +1 @@\n-a\n+b\n" +
			"--- a/two.cs\n+++ b/two.cs\n@@ -1,2 +1 @@\n-c\n-d\n+e\n";
		Assert.Equal((2, 3), DiffStats.Count(patch));
	}

	[Fact]
	public void CountsContentThatStartsWithDiffMarkers()
	{
		// 删除的内容是 "-- 注释"，补丁行变成 "--- 注释"；新增内容是 "++ 注释"，行变成 "+++ 注释"。
		// 按前缀判文件头会把这两行一起丢掉，hunk 头里的行数才是权威。
		const string patch =
			"--- a/x.cs\n+++ b/x.cs\n@@ -1,3 +1,3 @@\n" +
			" keep\n" +
			"--- 注释\n" +
			"+++ 注释\n" +
			" tail\n";
		Assert.Equal((1, 1), DiffStats.Count(patch));
	}

	[Fact]
	public void EmptyPatchHasNoLines()
	{
		Assert.Equal((0, 0), DiffStats.Count(string.Empty));
	}
}

/// 轮末文件 chip：说明文字、文件名、次数与增删行数；放不下时收进 +N，点开后展开。
[Collection("mewui-controls")]
public sealed class ChangedFilesViewTests
{
	private static ChatEntry Answer(params ChatFileChange[] files) => new()
	{
		Role = ChatRole.Assistant,
		Text = "done",
		Timestamp = 2,
		ChangedFiles = files,
	};

	private static ChatFileChange File(string path, int count = 1, int added = 0, int removed = 0) =>
		new(path, count, added, removed, "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new\n");

	[Fact]
	public void AssistantRowShowsCaptionAndFileChips()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		using var view = new AssistantMessageView(
			Answer(File("docs/a.md", added: 2, removed: 1), File("src/b.cs", count: 3, added: 5)),
			_ => { }, focus);

		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == "本轮修改 2 个文件");
		var chips = Chips(view.View);
		Assert.Equal(2, chips.Count);
		Assert.Contains(Find<TextBlock>(chips[0]), block => block.Text == "a.md");
		Assert.Contains(Find<TextBlock>(chips[0]), block => block.Text == "+2");
		Assert.Contains(Find<TextBlock>(chips[0]), block => block.Text == "−1");
		// 同一文件改了三次：次数跟在文件名后。
		Assert.Contains(Find<TextBlock>(chips[1]), block => block.Text == "×3");
	}

	[Fact]
	public void ChipHintsTheResolvedPath()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		using var view = new AssistantMessageView(
			Answer(File("docs/a.md")), _ => { }, focus, cwd: "/repo");

		var chip = Chips(view.View).Single();
		Assert.Equal("/repo/docs/a.md", HoverHints.Text(chip));
	}

	[Fact]
	public void OverflowCollapsesToOneRowAndExpandsOnClick()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		using var view = new AssistantMessageView(
			Answer(
				File("src/VeryLongFileNameOne.cs"),
				File("src/VeryLongFileNameTwo.cs"),
				File("src/VeryLongFileNameThree.cs"),
				File("src/VeryLongFileNameFour.cs")),
			_ => { }, focus);

		var row = Find<FileChipRow>(view.View).Single();
		row.Measure(new Size(240, 200));
		row.Arrange(new Rect(0, 0, 240, 200));

		Assert.True(row.MoreButton.IsVisible);
		Assert.Contains(Find<TextBlock>(row.MoreButton), block => block.Text.StartsWith('+'));
		Assert.True(Chips(row).Count(chip => chip.IsVisible) < 4);

		ImagePreviewTests.Click(row.MoreButton);
		row.Measure(new Size(240, 200));
		row.Arrange(new Rect(0, 0, 240, 200));

		Assert.True(row.IsExpanded);
		Assert.All(Chips(row), chip => Assert.True(chip.IsVisible));
		Assert.Contains(Find<TextBlock>(row.MoreButton), block => block.Text == "收起");
	}

	[Fact]
	public void NoFilesRendersNothing()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		using var view = new AssistantMessageView(Answer(), _ => { }, focus);
		Assert.Empty(Chips(view.View));
	}

	private static List<Button> Chips(Element root) =>
		[.. Find<Button>(root).Where(button => button.StyleName == AppStyles.ChangedFileChip)];
}
