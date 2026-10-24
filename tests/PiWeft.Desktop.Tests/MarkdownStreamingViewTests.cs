using System.Text;
using Aprillz.MewUI;
using Markdig;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Streaming behavior at the control level: append-only frames, tail repair while streaming,
/// finalize correction, and bounded per-frame cost.
/// </summary>
[Collection("mewui-controls")]
public sealed class MarkdownStreamingViewTests
{
	private readonly ITestOutputHelper _output;

	public MarkdownStreamingViewTests(ITestOutputHelper output) => _output = output;

	private static void Pump(Element view, double width = 520, double height = 400)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static string Sections(int count)
	{
		var builder = new StringBuilder();
		for (var index = 0; index < count; index++)
		{
			builder.Append("## Section ").Append(index).Append('\n').Append('\n');
			builder.Append("Paragraph ").Append(index).Append(" has **bold**, `code` and a [link](https://example.com/")
				.Append(index).Append(") with enough words to wrap in a reading column.\n\n");
		}
		return builder.ToString();
	}

	[Fact]
	public void AppendStreamingMatchesSourceAssignmentAndReusesElements()
	{
		string text = Sections(12);

		var reference = new MarkdownView { FontSize = 14 };
		reference.Source = text;
		Pump(reference, 520, 20_000);

		var view = new MarkdownView { FontSize = 14 };
		view.IsStreaming = true;
		Pump(view, 520, 20_000);
		FrameworkElement? first = null;
		FrameworkElement? tailBefore = null;
		for (int offset = 0; offset < text.Length; offset += 128)
		{
			view.Append(text.AsSpan(offset, Math.Min(128, text.Length - offset)).ToString());
			Pump(view, 520, 20_000);
			first ??= view.GetBlockElement(0);
			tailBefore = view.GetBlockElement(view.BlockCount - 1);
		}
		view.IsStreaming = false;
		Pump(view, 520, 20_000);

		Assert.Equal(reference.Text(), view.Text());
		Assert.NotNull(first);
		Assert.Same(first, view.GetBlockElement(0));
		// The final append only touches the tail: the element built one frame earlier survives.
		Assert.NotNull(tailBefore);
		Assert.Same(tailBefore, view.GetBlockElement(view.BlockCount - 1));
	}

	[Fact]
	public void StreamingTailRepairsThenFinalizes()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "hello ";
		Pump(view);
		var paragraph = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(0));

		view.IsStreaming = true;
		view.Append("hi **wor");
		Pump(view);
		Assert.Contains(paragraph.Runs, static run => (run.Style & MarkdownRunStyle.Bold) != 0);
		Assert.Equal("hello hi wor", paragraph.Text);

		view.IsStreaming = false;
		Pump(view);
		Assert.Equal("hello hi **wor", paragraph.Text);
		Assert.DoesNotContain(paragraph.Runs, static run => (run.Style & MarkdownRunStyle.Bold) != 0);
	}

	[Fact]
	public void AppendFramesStayBounded()
	{
		string text = Sections(180);
		var view = new MarkdownView { FontSize = 14 };
		view.Source = text;
		view.IsStreaming = true;
		Pump(view, 520, 20_000);

		var times = new List<double>();
		var allocations = new List<long>();
		for (var index = 0; index < 40; index++)
		{
			string delta = $"\n\nstreamed tail {index} with **bold** and `code`";
			var before = GC.GetAllocatedBytesForCurrentThread();
			var watch = System.Diagnostics.Stopwatch.StartNew();
			view.Append(delta);
			Pump(view, 520, 20_000);
			times.Add(watch.Elapsed.TotalMilliseconds);
			allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
		}
		times.Sort();
		allocations.Sort();
		_output.WriteLine(
			$"append median={times[times.Count / 2]:F2} ms p90={times[(int)(times.Count * 0.9)]:F2} ms " +
			$"alloc={allocations[allocations.Count / 2] / 1024:F0} KiB");
		Assert.True(times[times.Count / 2] < 40, $"append median {times[times.Count / 2]:F2} ms");
	}

	[Fact]
	public void GitHubAlertsRenderLabelAndColoredBorder()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "> [!NOTE]\n> useful info\n\n> plain quote";
		Pump(view, 520, 400);

		var alert = Assert.IsType<Border>(view.GetBlockElement(0));
		Assert.Contains(global::PiWeft.Desktop.Tests.VisualTree.Find<TextBlock>(alert).Select(static label => label.Text), static text => text == "Note");
		// The decorative label stays out of the text units; content copies as before.
		view.SelectAll();
		Assert.Contains("useful info", view.SelectedText, StringComparison.Ordinal);
		Assert.DoesNotContain("Note", view.SelectedText, StringComparison.Ordinal);
		var plain = Assert.IsType<Border>(view.GetBlockElement(1));
		Assert.DoesNotContain(global::PiWeft.Desktop.Tests.VisualTree.Find<TextBlock>(plain).Select(static label => label.Text), static text => text is "Note" or "说明");
	}

	[Fact]
	public void AlertQuoteSelectionMapsToTheRightParagraphs()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "> [!NOTE]\n> first content\n>\n> second content";
		Pump(view, 520, 400);

		// The label row sits at panel index 0; hit-testing the first content paragraph must
		// select the first paragraph, not shift one row down past the label.
		var border = Assert.IsType<Border>(view.GetBlockElement(0));
		var paragraphs = global::PiWeft.Desktop.Tests.VisualTree.Find<MarkdownParagraphView>(border).ToArray();
		Assert.Equal(2, paragraphs.Length);
		var first = paragraphs.Single(static paragraph => paragraph.Text == "first content");

		var from = new Point(first.Bounds.X + 2, first.Bounds.Y + first.Bounds.Height / 2);
		var to = new Point(first.Bounds.Right - 2, first.Bounds.Y + first.Bounds.Height / 2);
		Assert.True(view.BeginSelectionAtForTest(from));
		view.ExtendSelectionToForTest(to);
		Assert.Equal("first content", view.SelectedText);

		// Streaming an extra paragraph into the alert keeps the mapping stable.
		view.Source = "> [!NOTE]\n> first content\n>\n> second content\n>\n> third content";
		Pump(view, 520, 400);
		view.SelectAll();
		Assert.Equal("first content\nsecond content\nthird content", view.SelectedText);
	}

	[Fact]
	public void CachedBlocksStillMeasureWhenRealizedSoTheyRender()
	{
		MarkdownView.ClearHeightCacheForTest();
		string text = Sections(40);

		var first = new MarkdownView { FontSize = 14 };
		first.Source = text;
		Pump(first, 520, 400);

		// Second view gets every height from the cache: its realized elements are created during
		// RealizeRange and must still be measured there, otherwise their text layouts never build
		// and the paragraphs paint blank.
		var second = new MarkdownView { FontSize = 14 };
		second.Source = text;
		Pump(second, 520, 400);
		Assert.Equal(first.TotalHeight, second.TotalHeight);

		var paragraphs = global::PiWeft.Desktop.Tests.VisualTree.Find<MarkdownParagraphView>(second).ToArray();
		Assert.NotEmpty(paragraphs);
		Assert.All(paragraphs, static paragraph => Assert.NotEqual(double.NaN, paragraph.LayoutWidthForTest));
	}

	[Fact]
	public void HeightCacheMakesReentryExactWithoutElements()
	{
		MarkdownView.ClearHeightCacheForTest();
		string text = Sections(40);

		var first = new MarkdownView { FontSize = 14 };
		first.Source = text;
		Pump(first, 520, 20_000);
		double[] heights = Enumerable.Range(0, first.BlockCount).Select(first.GetBlockHeight).ToArray();
		Assert.True(MarkdownView.HeightCacheEntryCountForTest > 0, "measured blocks should populate the cache");

		var second = new MarkdownView { FontSize = 14 };
		second.Source = text;
		Pump(second, 520, 20_000);
		Assert.Equal(heights, Enumerable.Range(0, second.BlockCount).Select(second.GetBlockHeight).ToArray());
		Assert.Equal(first.TotalHeight, second.TotalHeight);
		// Heights exist for every block while only the viewport neighbourhood has elements: the
		// cache served the offscreen blocks without building anything.
		Assert.All(heights, static height => Assert.False(double.IsNaN(height)));
		Assert.True(second.RealizedBlockCount < second.BlockCount,
			$"realized={second.RealizedBlockCount} blocks={second.BlockCount}");
	}

	[Fact]
	public void TabMovesLinkFocusAcrossParagraphs()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "[first](https://a.com) text\n\nmiddle\n\n[second](https://b.com) more\n\n[third](https://c.com)";
		var window = new Aprillz.MewUI.Window().Content(view);
		Pump(view, 520, 400);

		var paragraphs = global::PiWeft.Desktop.Tests.VisualTree.Find<MarkdownParagraphView>(view).ToArray();
		Assert.True(paragraphs.Length >= 3, $"paragraphs={paragraphs.Length}");
		var first = paragraphs.Single(static paragraph => paragraph.Text.Contains("first", StringComparison.Ordinal));
		var second = paragraphs.Single(static paragraph => paragraph.Text.Contains("second", StringComparison.Ordinal));
		var third = paragraphs.Single(static paragraph => paragraph.Text.Contains("third", StringComparison.Ordinal));

		first.FocusLink(0);
		Assert.True(view.TryMoveLinkFocusForTest(forward: true), "Tab should move to the next paragraph with links");
		Assert.Same(second, window.FocusManager.FocusedElement);
		Assert.Equal(0, second.FocusedLink);

		Assert.True(view.TryMoveLinkFocusForTest(forward: true));
		Assert.Same(third, window.FocusManager.FocusedElement);

		Assert.True(view.TryMoveLinkFocusForTest(forward: false), "Shift+Tab should move back");
		Assert.Same(second, window.FocusManager.FocusedElement);
		Assert.Equal(0, second.FocusedLink);

		// Back to the end of the document: the last paragraph leaves Tab to the default focus
		// navigation.
		Assert.True(view.TryMoveLinkFocusForTest(forward: true));
		Assert.Same(third, window.FocusManager.FocusedElement);
		Assert.False(view.TryMoveLinkFocusForTest(forward: true));
	}

	private sealed class CountingMathView : ContentControl, IMarkdownCustomView
	{
		public int Updates { get; private set; }

		public bool TryUpdate(MarkdownBlock block)
		{
			Updates++;
			Content = new TextBlock { Text = block.Text };
			return true;
		}
	}

	private static readonly MarkdownOptions Mathematics = new()
	{
		UseHtmlFormatting = false,
		ConfigurePipeline = static builder => builder.UseMathematics(),
	};

	[Fact]
	public void CustomRendererBuildsAndUpdatesInPlace()
	{
		var view = new MarkdownView { FontSize = 14 };
		var renderers = new MarkdownRenderers()
			.RegisterBlock("MathBlock", (block, context) =>
			{
				Assert.Same(view, context.View);
				Assert.Equal(view.MarkdownTheme, context.Theme);
				if (context.Block.Text.Length == 0)
				{
					return null;
				}
				return new CountingMathView { Content = new TextBlock { Text = context.Block.Text } };
			});
		view.Options = Mathematics;
		view.Renderers = renderers;
		view.Source = string.Empty;
		view.IsStreaming = true;
		Pump(view, 520, 400);

		// The unclosed math fence is the streaming tail: every append reaches the custom view.
		view.Append("$$\nx^2");
		Pump(view, 520, 400);
		var math = Assert.IsType<CountingMathView>(view.GetBlockElement(0));
		Assert.Equal("x^2", ((TextBlock)math.Content).Text);

		view.Append(" + y^3");
		Pump(view, 520, 400);
		Assert.Same(math, view.GetBlockElement(0));
		Assert.Equal(1, math.Updates);
		Assert.Equal("x^2 + y^3", ((TextBlock)math.Content).Text);

		// Closing the fence appends a paragraph block after it.
		view.Append("\n$$\n\nafter");
		Pump(view, 520, 400);
		Assert.Same(math, view.GetBlockElement(0));
		Assert.True(view.GetBlockElement(1) is MarkdownParagraphView { Text: "after" });

		// Without a registration the same document renders literal text.
		var literal = new MarkdownView { FontSize = 14, Options = Mathematics };
		literal.Source = "$$\nx^2\n$$";
		Pump(literal, 520, 400);
		var paragraph = Assert.IsType<MarkdownParagraphView>(literal.GetBlockElement(0));
		Assert.Contains("x^2", paragraph.Text, StringComparison.Ordinal);
	}

	private sealed class FakeImageResolver(ImageSource? source) : IMarkdownImageResolver
	{
		public int Calls { get; private set; }

		public ValueTask<ImageSource?> ResolveAsync(MarkdownImageRequest request, CancellationToken cancellation)
		{
			Calls++;
			return ValueTask.FromResult(source);
		}
	}

	private static ImageSource TinyImage() => ImageSource.FromBgraPixels(2, 1, new byte[2 * 4]);

	[Fact]
	public void ImageRunsResolveAndReMeasureTheBlock()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.ImageResolver = new FakeImageResolver(TinyImage());
		view.Source = "before\n\n![alt](https://example.com/a.png)\n\nafter";
		Pump(view, 520, 400);

		// The synchronous-completing resolver applies inline: the image paragraph is taller than
		// its text-only equivalent, and a second layout pass does not oscillate.
		var reference = new MarkdownView { FontSize = 14 };
		reference.Source = "before\n\nalt\n\nafter";
		Pump(reference, 520, 400);
		Assert.True(view.TotalHeight > reference.TotalHeight,
			$"image height {view.TotalHeight:F1} must exceed text height {reference.TotalHeight:F1}");
		double first = view.TotalHeight;
		Pump(view, 520, 400);
		Assert.Equal(first, view.TotalHeight);

		// Selection still copies the alt text from the model.
		view.SelectAll();
		Assert.Contains("alt", view.SelectedText, StringComparison.Ordinal);
	}

	[Fact]
	public void WithoutAResolverImagesStayTextSized()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "before\n\n![alt](https://example.com/a.png)\n\nafter";
		Pump(view, 520, 400);
		var reference = new MarkdownView { FontSize = 14 };
		reference.Source = "before\n\nalt\n\nafter";
		Pump(reference, 520, 400);
		Assert.Equal(reference.TotalHeight, view.TotalHeight);
	}

	[Fact]
	public void FailedResolutionsAreNotRetriedOnEveryStreamingFrame()
	{
		var view = new MarkdownView { FontSize = 14 };
		var resolver = new FakeImageResolver(null);
		view.ImageResolver = resolver;
		view.Source = "![a](https://example.com/broken.png)";
		Pump(view, 520, 400);

		view.IsStreaming = true;
		for (int frame = 0; frame < 3; frame++)
		{
			view.Append(" tail");
			Pump(view, 520, 400);
		}
		Assert.Equal(1, resolver.Calls);
	}

	[Fact]
	public void CustomRendererReturningNullFallsBackToLiteralText()
	{
		var renderers = new MarkdownRenderers()
			.RegisterBlock("MathBlock", static (block, context) => null);
		var view = new MarkdownView { FontSize = 14, Options = Mathematics, Renderers = renderers };
		view.Source = "$$\nx^2\n$$";
		Pump(view, 520, 400);
		var paragraph = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(0));
		Assert.Contains("x^2", paragraph.Text, StringComparison.Ordinal);
	}
}

