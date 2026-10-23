using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;
using PiWeft.Markdown;
using Aprillz.MewUI.Platform;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class MessageInteractionTests
{
	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void PlainMessagesAllowPartialCopyWithoutEditing(bool muted, bool mono)
	{
		const string original = "plain **text**\nsecond line";
		var text = MessageText.Create(original, muted, mono);
		var clipboard = new Clipboard();
		text.ClipboardService = clipboard;
		text.Select(6, 8);
		text.Copy();
		Assert.Equal("**text**", clipboard.Text);
		text.ReplaceSelection("changed");
		text.Cut();
		Assert.Equal(original, text.Text);
		Assert.True(text.SizeToDocument);
		Assert.Equal(0, text.BorderThickness);
		// 只读正文不参与输入法组合，避免组合范围在文本替换后过期。
		Assert.Equal(ImeMode.Disabled, text.ImeMode);
	}

	[Fact]
	public void StreamingAppendKeepsTheReadersSelectionButReplacementDoesNot()
	{
		var text = MessageText.Create("first line");
		text.Select(0, 5);
		MessageText.Update(text, "first line\nsecond line");
		Assert.Equal("first", text.SelectedText);
		MessageText.Update(text, "first line\nsecond line");
		Assert.Equal("first", text.SelectedText);
		MessageText.Update(text, "replacement");
		Assert.Equal(0, text.SelectionLength);
	}

	[Fact]
	public void CodeBlockCopyButtonUsesTheLucideIconSet()
	{
		// 图标必须来自 tools/fetch-lucide-icons.sh 下载并内嵌的 Lucide SVG，而不是控件自己
		// 用路径画的图形：笔画风格要与全应用一致，还要跟主题换色（AppIcons 的 role 跟踪）。
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var entry = new ChatEntry
		{
			Role = ChatRole.Assistant, EntryId = "code", Timestamp = 1,
			Text = "```csharp\nif (!args.IsRepeat) _activate?.Invoke(_links[_focusedLink].Run);\n```",
		};
		var view = new AssistantMessageView(entry, _ => { }, guard);
		ConversationPerformanceTests.Layout(view.View);

		var code = Find<PiWeft.Markdown.MarkdownCodeBlockView>(view.View).Single();
		var copy = Find<Button>(code).Single();
		Assert.Equal("复制代码", Assert.IsType<TextBlock>(copy.ToolTip).Text);
		var icon = Assert.IsType<Image>(copy.Content);
		Assert.NotNull(icon.Source);
		Assert.Equal(14, icon.Width);
		Assert.Equal(14, icon.Height);
		// 用户报的就是这枚图标贴在按钮左侧：动作内容必须在按钮里两向居中。
		Assert.Equal(Math.Round(copy.Bounds.X + copy.Bounds.Width / 2, 1),
			Math.Round(icon.Bounds.X + icon.Bounds.Width / 2, 1), 0.6);
		Assert.Equal(Math.Round(copy.Bounds.Y + copy.Bounds.Height / 2, 1),
			Math.Round(icon.Bounds.Y + icon.Bounds.Height / 2, 1), 0.6);
		// 顶栏里不再有文字按钮：只有宿主给的那一枚图标。
		Assert.DoesNotContain("复制", Find<TextBlock>(code).Select(block => block.Text));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void UserToolAndThinkingRowsUseSelectableOriginalText()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("user **literal**") });
		manager.AppendMessage(new AssistantMessage { Content = [new ThinkingContent { Thinking = "thinking text" }, new TextContent { Text = "answer" }] });
		manager.AppendMessage(new ToolResultMessage { ToolCallId = "call", ToolName = "read", Content = [new TextContent { Text = "tool output" }] });
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Model = FakeStreams.Model(), Session = manager, Persist = false,
			Tools = [], Skills = [], PromptTemplates = [],
			StreamFn = static (_, _, _) => throw new InvalidOperationException(),
		});
		using var tab = new SessionTab(session, [session.Model], "test");
		ConversationPerformanceTests.Layout(tab);
		// Each expansion materializes the next level: run, process group (virtualized rows), then tool detail.
		for (var depth = 0; depth < 3; depth++)
		{
			foreach (var expander in Find<Expander>(tab.View).ToArray()) expander.IsExpanded = true;
			ConversationPerformanceTests.Layout(tab);
		}
		var messages = Find<MultiLineTextBox>(tab.View)
			.Concat(Find<Expander>(tab.View).SelectMany(expander => Find<MultiLineTextBox>(expander.Content!)))
			.Where(text => text.IsReadOnly).Distinct().ToArray();
		foreach (var expected in new[] { "user **literal**", "tool output" })
		{
			var text = Assert.Single(messages, text => text.Text == expected);
			text.SelectAll();
			Assert.Equal(expected, text.SelectedText);
		}
		// 思考正文已改为 Markdown，仍可整体选择复制原文。
		var thinking = Find<MarkdownView>(tab.View).Single(view => view.Source == "thinking text");
		thinking.SelectAll();
		Assert.Equal("thinking text", thinking.SelectedText);
	}

	[Fact]
	public void LongToolOutputAndPatchAreCreatedOnlyOnFirstExpansion()
	{
		var entry = new ChatEntry
		{
			Role = ChatRole.Tool, ToolName = "edit", Text = new string('x', 100_000),
			Patch = "@@ -1 +1 @@\n-old\n+new",
		};
		var view = new ToolMessageView(entry, new TextFocusScrollGuard(new ScrollViewer()));
		var expander = Find<Expander>(view.View).Single();
		Assert.Null(expander.Content);
		expander.IsExpanded = true;
		var content = expander.Content;
		Assert.NotNull(content);
		Assert.Contains(Find<MultiLineTextBox>(content!), text => text.Text == entry.Text && text.IsReadOnly);
		expander.IsExpanded = false;
		expander.IsExpanded = true;
		Assert.Same(content, expander.Content);
	}

	[Fact]
	public void MarkdownCodeUsesAFullHeightSelectableSurfaceInsteadOfANestedViewport()
	{
		var code = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line {i}"));
		var text = AssistantMessageView.CreateCodeText(code, "text", new TextFocusScrollGuard(new ScrollViewer()));
		Assert.True(text.IsReadOnly);
		Assert.True(text.SizeToDocument);
		Assert.False(text.Wrap);
		Assert.Equal(AppTheme.TextPrimary, text.Foreground);
		Assert.Contains("line 99", text.Text);
		text.SelectAll();
		Assert.Equal(text.Text, text.SelectedText);
	}

	[Fact]
	public void ThinkingBodyRendersMarkdownWhileCollapsedPreviewStaysPlainText()
	{
		var entry = new ChatEntry
		{
			Role = ChatRole.Assistant,
			Text = string.Empty,
			Thinking = "**bold**\n\n- item",
			EntryId = "thinking",
		};
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var view = new AssistantMessageView(entry, _ => { }, focus);
		try
		{
			ConversationPerformanceTests.Layout(view.View);
			var thinking = Find<Expander>(view.View).Single(expander =>
				expander.Header is { } header && Find<TextBlock>(header).Any(text => text.Text == Strings.ThinkingComplete));
			var preview = Find<TextBlock>(thinking.Header!).Single(text => text.Text == "**bold**");
			Assert.True(preview.IsVisible);
			Assert.DoesNotContain(Find<MarkdownView>(view.View), markdown => markdown.Source.Contains("**bold**", StringComparison.Ordinal));

			thinking.IsExpanded = true;
			ConversationPerformanceTests.Layout(view.View);

			Assert.Contains(Find<MarkdownView>(view.View), markdown => markdown.Source.Contains("**bold**", StringComparison.Ordinal));
			Assert.False(preview.IsVisible);
		}
		finally
		{
			UiLifetime.Release(view.View);
		}
	}

	[Fact]
	public void CodeTextThemeRefreshRevalidatesArrangeAfterADirtyMeasure()
	{
		var text = AssistantMessageView.CreateCodeText("line 1\nline 2", "text", new TextFocusScrollGuard(new ScrollViewer()));
		var host = new ScrollViewer { HorizontalScroll = ScrollMode.Auto, VerticalScroll = ScrollMode.Disabled, Content = text };
		host.Measure(new Size(600, 400));
		host.Arrange(new Rect(0, 0, 600, 400));
		Assert.False(text.IsArrangeDirty);

		// 换肤时 MewUI 已把文本视图标成测量失效；此时再调用 InvalidateMeasure 不会重新置排布失效，
		// 新视图的视口宽度停在 0，代码块只会画背景。刷新必须显式失效排布。
		text.InvalidateMeasure();
		text.Arrange(text.Bounds);
		Assert.True(text.IsMeasureDirty);
		Assert.False(text.IsArrangeDirty);

		AppStyles.RefreshTextView(text);

		Assert.True(text.IsArrangeDirty);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CodeWheelSeparatesConversationAndHorizontalScrolling(bool shift)
	{
		var code = new Border().Width(1200).Height(1000);
		var horizontal = new ScrollViewer().Content(code).AutoHorizontalScroll().NoVerticalScroll();
		var conversation = new ScrollViewer().Content(horizontal).AutoVerticalScroll().NoHorizontalScroll();
		conversation.Measure(new Size(200, 100));
		conversation.Arrange(new Rect(0, 0, 200, 100));
		var wheel = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false,
			shift ? ModifierKeys.Shift : ModifierKeys.None);
		new TextFocusScrollGuard(conversation).RouteWheel(code, wheel);
		Assert.True(wheel.Handled);
		AnimationFrames.Advance(200);
		Assert.Equal(shift, horizontal.HorizontalOffset > 0);
		Assert.Equal(!shift, conversation.VerticalOffset > 0);
		SmoothScroll.For(horizontal).Dispose();
		SmoothScroll.For(conversation).Dispose();
	}

	[Fact]
	public void HorizontalTrackpadWheelIsLeftToTheCodeScrollContainer()
	{
		var wheel = new MouseWheelEventArgs(default, default, new Vector(-1, 0), false, false, false, ModifierKeys.None);
		new TextFocusScrollGuard(new ScrollViewer()).RouteWheel(new Border(), wheel);
		Assert.False(wheel.Handled);
	}

	private sealed class Clipboard : IClipboardService
	{
		public string Text { get; private set; } = string.Empty;
		public bool TrySetText(string text) { Text = text; return true; }
		public bool TryGetText(out string text) { text = Text; return true; }
	}
}
