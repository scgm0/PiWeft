using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

public class SideBySideDiffTests
{
	private const string PairedPatch = """
		--- a/a.txt
		+++ b/a.txt
		@@ -1,4 +1,4 @@
		 context
		-old one
		-old two
		+new one
		+new two
		 tail
		""";

	[Fact]
	public void PairsDeletionsWithAdditionsAndKeepsLineNumbers()
	{
		var rows = SideBySideDiff.Parse(PairedPatch);

		Assert.Equal(DiffRowKind.Header, rows[0].Kind);

		Assert.Equal(DiffRowKind.Context, rows[1].Kind);
		Assert.Equal(1, rows[1].OldNumber);
		Assert.Equal(1, rows[1].NewNumber);

		Assert.Equal(DiffRowKind.Change, rows[2].Kind);
		Assert.Equal(2, rows[2].OldNumber);
		Assert.Equal("old one", rows[2].OldText);
		Assert.Equal(2, rows[2].NewNumber);
		Assert.Equal("new one", rows[2].NewText);

		Assert.Equal(DiffRowKind.Change, rows[3].Kind);
		Assert.Equal("old two", rows[3].OldText);
		Assert.Equal("new two", rows[3].NewText);

		Assert.Equal(DiffRowKind.Context, rows[4].Kind);
		Assert.Equal(4, rows[4].OldNumber);
		Assert.Equal(4, rows[4].NewNumber);

		// 两列行数一致才可能逐行对齐。
		Assert.Equal(5, rows.Count);
	}

	[Fact]
	public void UnpairedAdditionOrDeletionLeavesTheOtherSideEmpty()
	{
		var rows = SideBySideDiff.Parse("""
			@@ -1,1 +1,2 @@
			 keep
			+added
			-deleted
			""");

		var addition = rows.Single(static row => row.Kind == DiffRowKind.Addition);
		Assert.Null(addition.OldNumber);
		Assert.Equal("added", addition.NewText);

		var deletion = rows.Single(static row => row.Kind == DiffRowKind.Deletion);
		Assert.Null(deletion.NewNumber);
		Assert.Equal("deleted", deletion.OldText);
	}

	[Fact]
	public void MultipleHunksRestartLineNumbers()
	{
		var rows = SideBySideDiff.Parse("""
			@@ -1,1 +1,1 @@
			-a
			+b
			@@ -10,1 +10,1 @@
			-c
			+d
			""");

		Assert.Equal(2, rows.Count(static row => row.Kind == DiffRowKind.Header));

		var changes = rows.Where(static row => row.Kind == DiffRowKind.Change).ToList();
		Assert.Equal(1, changes[0].OldNumber);
		Assert.Equal(1, changes[0].NewNumber);
		Assert.Equal(10, changes[1].OldNumber);
		Assert.Equal(10, changes[1].NewNumber);
	}

	[Fact]
	public void EmptyPatchProducesNoRows() => Assert.Empty(SideBySideDiff.Parse(string.Empty));
}

[Collection("mewui-controls")]
public sealed class DiffViewRenderTests
{
	private const string Patch = """
		@@ -1,2 +1,2 @@
		-old
		+new
		 same
		""";

	[Fact]
	public void SideBySideRendersTwoColumns()
	{
		var view = DiffView.RenderSideBySide(Patch, null);

		Assert.NotNull(view);
	}

	[Fact]
	public void SideBySideColumnsAreSelectableDocuments()
	{
		// 并排的两列都是可选中、可复制的整篇文档，和统一视图一样。
		var view = DiffView.RenderSideBySide(Patch, null);
		var bodies = Find<MultiLineTextBox>(view).Where(text => text.IsHitTestVisible).ToArray();
		Assert.Equal(2, bodies.Length);

		var clipboard = new Clipboard();
		bodies[0].ClipboardService = clipboard;
		var start = bodies[0].Text.IndexOf("old", StringComparison.Ordinal);
		bodies[0].Select(start, 3);
		bodies[0].Copy();
		Assert.Equal("old", clipboard.Text);
		UiLifetime.Release(view);
	}

	[Fact]
	public void SideBySideColumnsFillTheHostWidthEvenly()
	{
		var view = DiffView.RenderSideBySide(Patch, null);
		ConversationPerformanceTests.Layout(view, 800, 480);

		var columns = Find<Grid>(view).First(grid => grid.Children.Count == 2 && grid.ColumnDefinitions.Count == 2);
		Assert.Equal(2, columns.Children.Count);
		Assert.Equal(columns.Children[0].Bounds.Width, columns.Children[1].Bounds.Width, 1);
		// 列宽跟着宿主（800 - 边框/内边距/间距）走，不再固定为阅读宽度的一半。
		Assert.True(columns.Children[0].Bounds.Width > 340);
		Assert.True(columns.Bounds.Width > 700);
		UiLifetime.Release(view);
	}

	[Fact]
	public void ScrollingOneColumnHorizontallyMovesTheOtherToTheSameOffset()
	{
		var longLine = new string('甲', 400);
		var view = DiffView.RenderSideBySide($"@@ -1 +1 @@\n-{longLine}\n+{longLine}", null);
		ConversationPerformanceTests.Layout(view, 700, 480);

		var scrollers = Find<ScrollViewer>(view)
			.Where(scroller => scroller.HorizontalScroll == ScrollMode.Auto).ToArray();
		Assert.Equal(2, scrollers.Length);

		scrollers[0].SetScrollOffsets(180, 0);
		ConversationPerformanceTests.Layout(view, 700, 480);
		Assert.True(scrollers[0].HorizontalOffset > 0);
		Assert.Equal(scrollers[0].HorizontalOffset, scrollers[1].HorizontalOffset, 1);

		// 反方向同样同步：滚右列，左列跟到同一绝对值。
		scrollers[1].SetScrollOffsets(90, 0);
		ConversationPerformanceTests.Layout(view, 700, 480);
		Assert.Equal(90, scrollers[1].HorizontalOffset, 1);
		Assert.Equal(scrollers[1].HorizontalOffset, scrollers[0].HorizontalOffset, 1);
		UiLifetime.Release(view);
	}

	[Fact]
	public void ShiftWheelOnOneColumnScrollsBothHorizontally()
	{
		// 真实输入路径：Shift+滚轮落在正文上，由焦点守卫找到最近的横向视口并滚动，
		// 另一列跟着到同一绝对值。
		var longLine = new string('丙', 400);
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var view = DiffView.RenderSideBySide($"@@ -1 +1 @@\n-{longLine}\n+{longLine}", guard);
		ConversationPerformanceTests.Layout(view, 700, 480);

		var scrollers = Find<ScrollViewer>(view).Where(s => s.HorizontalScroll == ScrollMode.Auto).ToArray();
		Assert.Equal(2, scrollers.Length);
		var body = Find<MultiLineTextBox>(view).First(text => text.IsHitTestVisible);

		// 滚轮向下（Delta.Y = -1）在偏移 0 处才会真正向右滚；向上滚在左缘是空操作。
		var wheel = new MouseWheelEventArgs(new Point(0, 0), new Point(0, 0), new Vector(0, -1),
			modifiers: ModifierKeys.Shift);
		// 滚轮走平滑动画：钉住每帧 dt，再喂几帧让它收敛。
		AnimationFrames.Pin(SmoothScroll.For(scrollers[0]));
		guard.RouteWheel(body, wheel);
		for (var frame = 0; frame < 40; frame++)
		{
			AnimationFrames.Advance();
			ConversationPerformanceTests.Layout(view, 700, 480);
		}

		Assert.True(wheel.Handled);
		Assert.True(scrollers[0].HorizontalOffset > 0);
		Assert.Equal(scrollers[0].HorizontalOffset, scrollers[1].HorizontalOffset, 1);
		UiLifetime.Release(view);
	}

	[Fact]
	public void GutterStaysPutWhileColumnsScrollHorizontally()
	{
		var longLine = new string('乙', 400);
		var view = DiffView.RenderSideBySide($"@@ -1 +1 @@\n-{longLine}\n+{longLine}", null);
		ConversationPerformanceTests.Layout(view, 700, 480);

		var gutters = Find<MultiLineTextBox>(view).Where(text => !text.IsHitTestVisible).ToArray();
		Assert.Equal(2, gutters.Length);
		var before = gutters[0].Bounds.X;
		var scroller = Find<ScrollViewer>(view).First(s => s.HorizontalScroll == ScrollMode.Auto);
		scroller.SetScrollOffsets(160, 0);
		ConversationPerformanceTests.Layout(view, 700, 480);
		Assert.Equal(before, gutters[0].Bounds.X, 1);
		UiLifetime.Release(view);
	}

	[Fact]
	public void LargeSideBySideStillUsesSelectableTextColumns()
	{
		// 大补丁：正文限高并在内部纵向滚动，两侧与行号列共用同一纵向偏移。
		var lines = string.Join('\n', Enumerable.Range(0, MessageText.LargeTextLines + 1).Select(index => $" line {index}"));
		var view = DiffView.RenderSideBySide($"@@ -1,0 +1,0 @@\n{lines}", null);

		Assert.Equal(2, Find<MultiLineTextBox>(view).Count(text => text.IsHitTestVisible));
		Assert.NotEmpty(Find<ScrollViewer>(view));
	}

	[Fact]
	public void ToolViewOffersBothDiffModesWhenExpanded()
	{
		var entry = new ChatEntry
		{
			Role = ChatRole.Tool,
			Text = "ok",
			ToolName = "edit",
			ToolArguments = """{"path":"a.txt"}""",
			Patch = Patch,
		};

		var scroll = new ScrollViewer();
		using var focus = new TextFocusScrollGuard(scroll);
		var toolView = new ToolMessageView(entry, focus);

		var expander = Find<Expander>(toolView.View).Single();
		expander.IsExpanded = true;

		Assert.Contains(Find<Button>(toolView.View), button => Contains(button, Strings.DiffUnified));
		var sideBySide = Find<Button>(toolView.View).Single(button => Contains(button, Strings.DiffSideBySide));

		// 工具卡里的并排同样是两列可选中文本，各自横向滚动并同步偏移。
		ImagePreviewTests.Click(sideBySide);
		ConversationPerformanceTests.Layout(toolView.View, 880, 720);
		Assert.True(Find<MultiLineTextBox>(toolView.View).Count(text => text.IsHitTestVisible) >= 2);
		Assert.Contains(Find<MultiLineTextBox>(toolView.View), text => text.Text.Contains("old", StringComparison.Ordinal));
		UiLifetime.Release(toolView.View);
	}

	private static bool Contains(Button button, string text) => Find<TextBlock>(button).Any(block => block.Text == text);

	private sealed class Clipboard : IClipboardService
	{
		public string Text = string.Empty;

		public bool TrySetText(string value) { Text = value; return true; }

		public bool TryGetText(out string text) { text = Text; return Text.Length > 0; }

		public bool HasText() => Text.Length > 0;
	}
}
