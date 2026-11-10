using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 文件差异浮层：头部给文件名、来源、完整路径与增删行数，主体是补丁文本或说明。
/// 轮末 chip 与右侧 Git 面板共用这一个浮层。
[Collection("mewui-controls")]
public sealed class FileDiffPreviewTests
{
	private const string Patch = "--- a/docs/a.md\n+++ b/docs/a.md\n@@ -1,1 +1,2 @@\n-old line\n+new line\n+another line\n";

	private static DiffPreviewContent Content(
		string patch = Patch, string context = "", string? message = null, bool isError = false, bool canOpen = true) =>
		new("a.md", context, "/repo/docs/a.md", 2, 1, patch, message, isError, canOpen);

	private const double WindowWidth = 1200;
	private const double WindowHeight = 900;

	private static string BigPatch(int lines = 600, int tail = 0)
	{
		var patch = new System.Text.StringBuilder("--- a/a.cs\n+++ b/a.cs\n@@ -1,600 +1,600 @@\n");
		for (var index = 0; index < lines; index++)
		{
			patch.Append('-').Append("old line ").Append(index);
			if (tail > 0) patch.Append(new string('x', tail));
			patch.Append('\n');
			patch.Append('+').Append("new line ").Append(index);
			if (tail > 0) patch.Append(new string('x', tail));
			patch.Append('\n');
		}

		return patch.ToString();
	}

	private static void ScrollTo(MultiLineTextBox text, double x, double y)
	{
		var viewport = text.TextViewportBounds;
		text.MakeVisible(new Rect(x, y, viewport.Width, viewport.Height));
	}

	/// 浮层只在 Show 里变可见；无窗口测试手动摆一次版。
	private static void Layout(FileDiffPreview preview)
	{
		var view = preview.View;
		view.IsVisible = true;
		for (var pass = 0; pass < 8; pass++)
		{
			foreach (var element in Find<UIElement>(view).ToArray()) element.InvalidateArrange();
			view.InvalidateMeasure();
			view.Measure(new Size(WindowWidth, WindowHeight));
			view.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
		}
	}

	[Fact]
	public void LargePatchFillsTheWindowAndScrollsInTheCodeBlockOnly()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content(patch: BigPatch()));
		Layout(preview);

		var body = Find<MultiLineTextBox>(preview.View).Single(text => text.IsHitTestVisible);
		var gutter = Find<MultiLineTextBox>(preview.View).Single(text => !text.IsHitTestVisible);
		// 正文按窗口高度收口，不再停在固定 240 的小窗上。
		Assert.True(body.ActualHeight > MessageText.LargeTextMaxHeight * 2,
			$"正文高度应跟窗口走，实际 {body.ActualHeight}");
		// 纵向只有正文自己那条：行号列那条画成透明，外层宿主不滚动。
		var bodyBar = Find<ScrollBar>(body).Single(bar => bar.Orientation == Orientation.Vertical);
		Assert.True(bodyBar.IsVisible);
		Assert.Equal(1, bodyBar.RenderOpacity);
		Assert.Equal(0, Find<ScrollBar>(gutter).Single(bar => bar.Orientation == Orientation.Vertical).RenderOpacity);
		var host = Find<ScrollViewer>(preview.View).Single(scroller => scroller.Content is Border);
		Assert.Equal(ScrollMode.Disabled, host.VerticalScroll);
		// 滚动条在代码块边框内。
		var code = Find<Border>(preview.View).Single(border => border.StyleName == AppStyles.Code);
		Assert.InRange(bodyBar.Bounds.Right, code.Bounds.X, code.Bounds.Right);
		UiLifetime.Release(preview.View);
	}

	[Fact]
	public void SmallPatchHugsItsContent()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content());
		Layout(preview);

		var card = Find<Border>(preview.View).First(border => border.StyleName == AppStyles.Dialog);
		Assert.True(card.ActualHeight < WindowHeight / 2, $"小补丁的卡片应贴内容，实际 {card.ActualHeight}");
		UiLifetime.Release(preview.View);
	}

	[Fact]
	public void HeaderKeepsTheModeButtonsClearOfALongPath()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(new DiffPreviewContent(
			new string('t', 80), new string('c', 40), "/repo/" + new string('p', 300), 108, 27, Patch));
		Layout(preview);

		// 卡片封顶 1080 后，头部各段的 MaxWidth 之和放不下；标题/路径必须按星号宽度收缩。
		var path = Find<TextBlock>(preview.View).Single(text => text.Text.StartsWith("/repo/", StringComparison.Ordinal));
		var track = Find<Border>(preview.View).Single(border => border.StyleName == AppStyles.SegmentTrack);
		Assert.True(path.Bounds.Right <= track.Bounds.X + 1,
			$"路径右缘 {path.Bounds.Right} 压到了模式按钮 {track.Bounds.X}");
		UiLifetime.Release(preview.View);
	}

	[Fact]
	public void CardWidthIsCappedAndCentered()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content(patch: BigPatch()));
		Layout(preview);

		var card = Find<Border>(preview.View).First(border => border.StyleName == AppStyles.Dialog);
		// PreviewSurface 的 1080 上限（嵌套类里叫 MaxWidth 会解析到继承属性，等于没限）。
		Assert.InRange(card.ActualWidth, 1070, 1082);
		Assert.InRange(card.Bounds.X + card.ActualWidth / 2, WindowWidth / 2 - 2, WindowWidth / 2 + 2);
		UiLifetime.Release(preview.View);
	}

	[Fact]
	public void SideBySideColumnsEachKeepTheirScrollBarsAndStayInSync()
	{
		var preview = new FileDiffPreview();
		// 行足够长：两列都要出横向滚动条；行数足够多：两列都要出纵向滚动条。
		preview.LoadForTests(Content(patch: BigPatch(tail: 120)));
		ImagePreviewTests.Click(Find<Button>(preview.View).Single(button =>
			Find<TextBlock>(button).Any(text => text.Text == Strings.DiffSideBySide)));
		Layout(preview);

		var bodies = Find<MultiLineTextBox>(preview.View).Where(text => text.IsHitTestVisible).ToArray();
		Assert.Equal(2, bodies.Length);
		foreach (var body in bodies)
		{
			Assert.True(Find<ScrollBar>(body).Single(bar => bar.Orientation == Orientation.Vertical).IsVisible);
			Assert.True(Find<ScrollBar>(body).Single(bar => bar.Orientation == Orientation.Horizontal).IsVisible);
		}

		// 拖/滚一列，另一列跟到同一偏移。
		ScrollTo(bodies[1], 160, 0);
		Layout(preview);
		Assert.True(bodies[1].HorizontalOffset > 0);
		Assert.Equal(bodies[1].HorizontalOffset, bodies[0].HorizontalOffset, 1);
		UiLifetime.Release(preview.View);
	}

	[Fact]
	public void LoadsPatchIntoTheDiffBody()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content());

		Assert.False(preview.IsOpen);
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "a.md");
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "/repo/docs/a.md");
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "+2 −1");
		Assert.Contains(Find<MultiLineTextBox>(preview.View),
			view => view.Text.Contains("+another line", StringComparison.Ordinal));
	}

	[Fact]
	public void MissingPatchShowsPlaceholder()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content(patch: string.Empty));

		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.GitNoDiff);
	}

	[Fact]
	public void ContextAndReadFailureComeFromTheContent()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content(patch: string.Empty, context: "提交 abc1234",
			message: Strings.GitDiffFailedReason("boom"), isError: true, canOpen: false));

		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "提交 abc1234");
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.GitDiffFailedReason("boom"));
		// 历史来源（提交 / 比较）不给「在编辑器中打开」。
		Assert.False(preview.CanOpenFile);
	}

	[Fact]
	public void GitDiffUsesTheSameOverlay()
	{
		var preview = new FileDiffPreview();
		preview.LoadForTests(Content(context: "提交 abc1234"));

		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == "提交 abc1234");
		Assert.Contains(Find<MultiLineTextBox>(preview.View),
			view => view.Text.Contains("+new line", StringComparison.Ordinal));
		// 统一 / 并排两种模式都在。
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.DiffUnified);
		Assert.Contains(Find<TextBlock>(preview.View), block => block.Text == Strings.DiffSideBySide);
	}
}
