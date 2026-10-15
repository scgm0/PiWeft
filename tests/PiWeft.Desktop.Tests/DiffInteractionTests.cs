using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;
using Aprillz.MewUI.Text;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class DiffInteractionTests
{
	[Fact]
	public void DiffIsOneSelectableReadOnlyDocumentWithoutLineNumbersInTheCopiedText()
	{
		const string patch = "--- a/file.cs\n+++ b/file.cs\n@@ -7,2 +7,2 @@\n-old value\n+new value\n context\n";
		var view = DiffView.Render(patch);
		var text = Find<MultiLineTextBox>(view).Single(control => control.IsHitTestVisible);
		var clipboard = new Clipboard();
		text.ClipboardService = clipboard;
		var start = patch.IndexOf("old value", StringComparison.Ordinal);
		var end = patch.IndexOf(" context", StringComparison.Ordinal);
		text.Select(start, end - start);
		text.Copy();
		Assert.Equal("old value\n+new value\n", clipboard.Text);
		text.ReplaceSelection("changed");
		text.Cut();
		Assert.Equal(patch, text.Text);
		text.SelectAll();
		Assert.Equal(patch, text.SelectedText);
		var gutter = Find<MultiLineTextBox>(view).Single(control => !control.IsHitTestVisible);
		Assert.Equal("\n\n\n7\n7\n8\n", gutter.Text);
		Assert.True(text.SizeToDocument);
		Assert.False(text.Wrap);
		UiLifetime.Release(view);
	}

	[Theory]
	[InlineData("+added", true, false)]
	[InlineData("-deleted", false, true)]
	[InlineData("+++ b/file", false, false)]
	public void PaintClassificationKeepsAddDeleteColors(string line, bool added, bool deleted)
	{
		var spans = new List<TextPaintSpan>();
		new DiffView.DiffClassifier().Classify(new TextClassificationContext(
			new LogicalTextLine(0, 0, line.Length, line.Length), line.AsMemory(), IdentityTextOffsetMap.Instance), spans);
		var span = Assert.Single(spans);
		Assert.Equal(new TextRange(0, line.Length), span.Range);
		Assert.Equal(added ? AppPalette.Dark.Success : deleted ? AppPalette.Dark.Error : AppPalette.Dark.TextFaint, span.Foreground);
	}

	[Fact]
	public void LongDiffLinesScrollHorizontallyWhileGutterRemainsFixedAndRowsStayAligned()
	{
		var view = DiffView.Render("@@ -10 +20 @@\n-" + new string('x', 1000) + "\n+short");
		ConversationPerformanceTests.Layout(view, 400, 200);
		var texts = Find<MultiLineTextBox>(view).ToArray();
		var gutter = texts.Single(text => !text.IsHitTestVisible);
		var body = texts.Single(text => text.IsHitTestVisible);
		Assert.InRange(Math.Abs(gutter.ActualHeight - body.ActualHeight), 0, 1);
		var horizontal = Find<ScrollViewer>(view).Single();
		Assert.Equal(ScrollMode.Disabled, horizontal.VerticalScroll);
		var left = gutter.Bounds.X;
		horizontal.SetScrollOffsets(100, 0);
		ConversationPerformanceTests.Layout(view, 400, 200);
		Assert.Equal(left, gutter.Bounds.X);
		Assert.True(horizontal.HorizontalOffset > 0);
		UiLifetime.Release(view);
	}

	private sealed class Clipboard : IClipboardService
	{
		public string Text = string.Empty;
		public bool TrySetText(string value) { Text = value; return true; }
		public bool TryGetText(out string value) { value = Text; return true; }
	}
}
