using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 流式纯文本视图：纯追加走文档增量编辑，回退/改写才整篇替换。
/// 这里钉住文本正确性与既有阅读状态（选区）在增长时不被重置。
/// </summary>
[Collection("mewui-controls")]
public sealed class StreamingTextViewTests
{
	private static string Build(int length)
	{
		var builder = new System.Text.StringBuilder(length + 64);
		var index = 0;
		while (builder.Length < length)
		{
			builder.Append("第 ").Append(index).Append(" 段：一段用于模拟模型思考过程的文本。\n");
			index++;
		}
		return builder.ToString();
	}

	[Fact]
	public void AppendingKeepsTheWholeText()
	{
		var view = new StreamingTextView();
		view.Update("第一段");
		view.Update("第一段\n\n第二段");
		view.Update("第一段\n\n第二段\n\n第三段");

		Assert.Equal("第一段\n\n第二段\n\n第三段", view.Text);
		Assert.Equal(view.Text, view.View.Text);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ManyAppendsToALongTextStayConsistent()
	{
		var view = new StreamingTextView();
		var text = Build(32_000);
		view.Update(text);
		for (var index = 0; index < 200; index++)
		{
			text += "追加的增量 " + index + "\n";
			view.Update(text);
		}

		Assert.Equal(text, view.Text);
		Assert.Equal(text, view.View.Text);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ShrinkingAndRewritingThePrefixFallsBackToFullReplace()
	{
		var view = new StreamingTextView();
		view.Update("alpha beta gamma");
		view.Update("alpha beta");
		Assert.Equal("alpha beta", view.Text);
		Assert.Equal("alpha beta", view.View.Text);

		// 前缀被改写：不是追加，也不是缩短，必须整篇替换。
		view.Update("ALPHA beta delta");
		Assert.Equal("ALPHA beta delta", view.Text);
		Assert.Equal("ALPHA beta delta", view.View.Text);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void CarriageReturnsAreNormalizedBeforeComparingPrefixes()
	{
		var view = new StreamingTextView();
		view.Update("line one\r\nline two");
		view.Update("line one\r\nline two\r\nline three");

		Assert.Equal("line one\nline two\nline three", view.Text);
		Assert.Equal(view.Text, view.View.Text);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void AppendingDoesNotResetTheReadersSelection()
	{
		var view = new StreamingTextView();
		view.Update("第一段\n\n第二段\n\n第三段");
		view.View.Measure(new Size(420, double.PositiveInfinity));
		view.View.Arrange(new Rect(0, 0, 420, view.View.DesiredSize.Height));
		view.View.Select(2, 4);

		view.Update("第一段\n\n第二段\n\n第三段\n\n第四段");

		// 追加只在文档尾部插入：前面的选区保持原样，读者不会被跳回起点。
		Assert.Equal(2, view.View.SelectionStart);
		Assert.Equal(4, view.View.SelectionLength);
		Assert.Equal("段\n\n第", view.View.SelectedText);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandedThinkingUsesAnIncrementalTextDuringStreaming()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var entry = new ChatEntry
		{
			Role = ChatRole.Assistant,
			Text = string.Empty,
			Thinking = "step 0",
			EntryId = "live",
			Timestamp = 1,
			IsStreaming = true,
			IsThinking = true,
		};
		var message = new AssistantMessageView(entry, _ => { }, guard);
		message.View.Measure(new Size(420, double.PositiveInfinity));
		message.View.Arrange(new Rect(0, 0, 420, message.View.DesiredSize.Height));
		var thinking = VisualTree.Find<Expander>(message.View).Single();
		thinking.IsExpanded = true;

		var markdown = VisualTree.Find<MarkdownView>(message.View).Single();
		var longText = string.Join('\n', Enumerable.Range(0, 4_000).Select(index => $"思考内容 {index}"));
		message.Update(entry with { Thinking = longText });
		message.View.Measure(new Size(420, double.PositiveInfinity));
		message.View.Arrange(new Rect(0, 0, 420, message.View.DesiredSize.Height));

		// 32 K 级别的思考在流式阶段仍是同一个增量视图：只更新尾块，不重建整棵文档。
		Assert.Same(markdown, VisualTree.Find<MarkdownView>(message.View).Single());
		Assert.Equal(longText, markdown.Source);
		Assert.True(markdown.IsStreaming);
		message.Dispose();
	}
}
