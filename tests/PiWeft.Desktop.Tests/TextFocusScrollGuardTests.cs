using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Focusing a reading control must not drive any enclosing viewport. A MarkdownView is often
/// taller than the execution group body, so MewUI's focus-into-view would otherwise scroll the
/// group (and the conversation) to the focused view's top or bottom.
/// </summary>
[Collection("mewui-controls")]
public sealed class TextFocusScrollGuardTests(ITestOutputHelper output)
{
	private static void Pump(UIElement view, double width = 500, double height = 400)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	[Fact]
	public void FocusingATallMarkdownViewPreservesEveryAncestorScrollViewport()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = string.Join("\n\n", Enumerable.Range(0, 80).Select(index =>
			$"paragraph {index} " + new string('x', 120)));
		var inner = new ScrollViewer
		{
			Content = new StackPanel().Children(view),
			VerticalScroll = ScrollMode.Auto,
			MaxHeight = 300,
		};
		var outer = new ScrollViewer
		{
			Content = new StackPanel().Children(
				new Border().Height(120),
				inner,
				new Border().Height(800)),
			VerticalScroll = ScrollMode.Auto,
		};
		using var window = new Window().Content(outer);
		Pump(outer);
		inner.SetScrollOffsets(0, 700);
		outer.SetScrollOffsets(0, 180);
		Pump(outer);
		var expectedInner = inner.VerticalOffset;
		var expectedOuter = outer.VerticalOffset;
		Assert.True(expectedInner > 0, "the nested body must be scrollable");
		Assert.True(expectedOuter > 0, "the outer conversation must be scrollable");

		using var guard = new TextFocusScrollGuard(outer);
		guard.Track(view);
		Assert.True(view.Focus());
		Pump(outer);

		output.WriteLine($"inner {expectedInner:0.#}->{inner.VerticalOffset:0.#}, " +
			$"outer {expectedOuter:0.#}->{outer.VerticalOffset:0.#}, realized={view.RealizedBlockCount}");
		Assert.Equal(expectedInner, inner.VerticalOffset, 1);
		Assert.Equal(expectedOuter, outer.VerticalOffset, 1);
		Assert.True(view.RealizedBlockCount > 0, "focusing the view must not blank its Markdown");
	}
}
