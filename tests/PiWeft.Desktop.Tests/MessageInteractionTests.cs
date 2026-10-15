using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
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
		// Each expansion materializes the next level: run, process group, then tool/thinking detail.
		for (var depth = 0; depth < 3; depth++)
			foreach (var expander in Find<Expander>(tab.View).ToArray()) expander.IsExpanded = true;
		var messages = Find<MultiLineTextBox>(tab.View)
			.Concat(Find<Expander>(tab.View).SelectMany(expander => Find<MultiLineTextBox>(expander.Content!)))
			.Where(text => text.IsReadOnly).Distinct().ToArray();
		foreach (var expected in new[] { "user **literal**", "thinking text", "tool output" })
		{
			var text = Assert.Single(messages, text => text.Text == expected);
			text.SelectAll();
			Assert.Equal(expected, text.SelectedText);
		}
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
