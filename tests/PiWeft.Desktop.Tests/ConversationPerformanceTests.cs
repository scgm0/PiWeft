using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ConversationPerformanceTests(ITestOutputHelper output)
{
	[Fact]
	public void LongHistoryRealizesOnlyTheViewportAndCanBrowseBothEnds()
	{
		using var session = CreateSession(1000);
		GC.Collect();
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var started = Stopwatch.StartNew();
		using var tab = new SessionTab(session, [session.Model], "history");
		Layout(tab);
		output.WriteLine($"Open 2000 rows: {started.Elapsed.TotalMilliseconds:F2} ms; allocated {GC.GetAllocatedBytesForCurrentThread() - allocated:N0} bytes; Markdown views {Find<MarkdownPresenter>(tab.View).Count()}");
		Assert.InRange(Find<MarkdownPresenter>(tab.View).Count(), 1, 20);
		Assert.Contains(Find<MarkdownPresenter>(tab.View), view => view.Markdown.Contains("answer 999", StringComparison.Ordinal));
		var scroll = Find<ScrollViewer>(tab.View).First();
		scroll.SetScrollOffsets(0, 0);
		Layout(tab);
		Assert.Contains(Find<MultiLineTextBox>(tab.View), view => view.Text == "question 0");
		Assert.InRange(Find<MarkdownPresenter>(tab.View).Count(), 1, 20);
	}

	[Fact]
	public void ThemeRefreshKeepsMarkdownSelectionAndExpandedToolContent()
	{
		using var session = CreateSession(0);
		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("question") });
		session.Session.AppendMessage(new ToolResultMessage { ToolName = "read", ToolCallId = "call", Content = [new TextContent { Text = "tool output" }] });
		session.Session.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "final answer" }] });
		using var tab = new SessionTab(session, [session.Model], "history");
		Layout(tab);
		var markdown = Find<MarkdownPresenter>(tab.View).Single();
		markdown.SelectAll();
		var selected = markdown.SelectedText;
		for (var depth = 0; depth < 2; depth++)
			foreach (var expander in Find<Expander>(tab.View).ToArray()) expander.IsExpanded = true;
		var tool = Find<Expander>(tab.View).Last();
		tool.IsExpanded = true;
		var content = tool.Content;
		tab.ApplyTheme();
		Layout(tab);
		Assert.Same(markdown, Find<MarkdownPresenter>(tab.View).Single());
		Assert.Equal(selected, markdown.SelectedText);
		Assert.True(tool.IsExpanded);
		Assert.Same(content, tool.Content);
	}

	[Fact]
	public void RecycledToolsRestoreExpansionAndReleaseTheirPreviousContent()
	{
		var rows = Enumerable.Range(0, 200).Select(i => new ChatEntry
		{
			Role = ChatRole.Tool, ToolName = "read", Text = $"tool {i}", EntryId = $"entry-{i}",
		}).ToArray();
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		using var timeline = new ConversationTimeline(entry => new ToolMessageView(entry, focus).View);
		timeline.SetEntries(rows);
		timeline.View.ScrollIntoView(0);
		Layout(timeline.View);
		// 折叠标题是「状态点 + 动词短语」的组合，按标题子树里的摘要文本定位这一行。
		var first = Find<Expander>(timeline.View).Single(expander => expander.Header is { } header &&
			Find<TextBlock>(header).Any(text => text.Text == "read") && Find<TextBlock>(header).Any(text => text.Text!.EndsWith("tool 0", StringComparison.Ordinal)));
		first.IsExpanded = true;
		var original = first.Content;
		Assert.Contains(Find<MultiLineTextBox>(original!), text => text.Text == "tool 0");
		timeline.View.ScrollIntoView(rows.Length - 1);
		Layout(timeline.View);
		timeline.PruneRecycled(); // Drain the release normally queued on the native dispatcher.
		Assert.DoesNotContain(Find<MultiLineTextBox>(timeline.View), text => text.Text == "tool 0");
		timeline.View.ScrollIntoView(0);
		Layout(timeline.View);
		var restored = Find<Expander>(timeline.View).Single(expander => expander.Content is not null);
		Assert.True(restored.IsExpanded);
		Assert.NotSame(original, restored.Content);
		Assert.Contains(Find<MultiLineTextBox>(restored.Content!), text => text.Text == "tool 0");
	}

	[Fact]
	public void StreamingTailKeepsTheSelectedHistoryAndTheThinkingExpander()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		using var timeline = new ConversationTimeline(entry => new AssistantMessageView(entry, _ => { }, focus).View);
		var history = new ChatEntry { Role = ChatRole.Assistant, Text = "previous answer", EntryId = "previous" };
		var streaming = new ChatEntry { Role = ChatRole.Assistant, Text = "part", Thinking = "reasoning", IsStreaming = true, Timestamp = 1 };
		timeline.SetEntries([history, streaming]);
		Layout(timeline.View);
		var markdown = Find<MarkdownPresenter>(timeline.View).Single();
		markdown.SelectAll();
		var thinking = Find<Expander>(timeline.View).Last();
		thinking.IsExpanded = true;
		timeline.SetEntries([history, streaming with { Text = "partial answer" }]);
		Layout(timeline.View);
		Assert.Same(markdown, Find<MarkdownPresenter>(timeline.View).Single());
		Assert.Equal("previous answer", markdown.SelectedText);
		Assert.Contains(Find<MultiLineTextBox>(timeline.View), text => text.Text == "partial answer");
		Assert.True(thinking.IsExpanded);
		timeline.SetEntries([history, streaming with { Text = "completed answer", IsStreaming = false }]);
		Layout(timeline.View);
		Assert.Equal(2, Find<MarkdownPresenter>(timeline.View).Count());
		Assert.Same(thinking, Find<Expander>(timeline.View).Last());
		Assert.True(thinking.IsExpanded);
	}

	[Fact]
	public void StreamingTailUpdatesItsRowWithoutResettingTheMeasuredExtent()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		using var timeline = new ConversationTimeline(entry => entry.Role == ChatRole.Assistant
			? new AssistantMessageView(entry, _ => { }, focus).View
			: new Border().Height(100).Child(new TextBlock().Text(entry.Text)));
		using var conversation = new ConversationScroll(timeline.Scroll, _ => { });
		var history = Enumerable.Range(0, 20)
			.Select(i => new ChatEntry { Role = ChatRole.User, Text = $"history {i}", EntryId = $"h{i}" })
			.ToList();
		var streaming = new ChatEntry { Role = ChatRole.Assistant, Text = "part", IsStreaming = true, Timestamp = 1, EntryId = "live" };
		history.Add(streaming);
		timeline.SetEntries(history);
		Layout(timeline.View, 600, 500);
		var changes = new List<ItemsChange>();
		timeline.View.ItemsSource.Changed += changes.Add;
		var changed = streaming with { Text = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"paragraph {i}")) };
		history[^1] = changed;
		timeline.SetEntries(history);
		// Streaming rows update in place: no Replace, so the virtualized presenter keeps its
		// measured height instead of collapsing the extent to the generic 120 estimate.
		Assert.DoesNotContain(changes, change => change.Kind == ItemsChangeKind.Replace);
		Layout(timeline.View, 600, 500);
		Assert.Contains(Find<MultiLineTextBox>(timeline.View), text => text.Text.Contains("paragraph 39", StringComparison.Ordinal));
		Assert.True(conversation.Following);
		var smooth = SmoothScroll.For(timeline.Scroll);
		Assert.Equal(smooth.MaxVerticalOffset, timeline.Scroll.VerticalOffset, 1);
	}

	[Fact]
	public void NarrowLayoutLeavesRoomForLatestReplyWithoutChangingTheDraft()
	{
		using var session = CreateSession(60);
		using var tab = new SessionTab(session, [session.Model], "narrow");
		var composer = Find<MultiLineTextBox>(tab.View).Single(text => !text.IsReadOnly);
		composer.Text = "draft";
		composer.Select(1, 3);
		Layout(tab.View, 500, 420);
		Assert.Equal(56, composer.Height);
		Assert.Equal("draft", composer.Text);
		Assert.Equal(3, composer.SelectionLength);
		var scroll = Find<ScrollViewer>(tab.View).First();
		Assert.True(scroll.ViewportHeight >= 100);
		Assert.Contains(Find<MarkdownPresenter>(tab.View), view => view.Markdown.Contains("answer 59", StringComparison.Ordinal));
	}

	[Fact]
	public void ReplyTallerThanTheViewportStillShowsItsTail()
	{
		using var session = CreateSession(40);
		var text = string.Join("\n\n", Enumerable.Range(0, 80).Select(i => $"Long reply paragraph {i}"));
		session.Session.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = text }] });
		using var tab = new SessionTab(session, [session.Model], "long reply");
		Layout(tab.View, 500, 420);
		var scroll = Find<ScrollViewer>(tab.View).First();
		var reply = Find<MarkdownPresenter>(tab.View).Single(view => view.Markdown == text);
		Assert.True(reply.ActualHeight > scroll.ViewportHeight);
		Assert.True(reply.Bounds.Top < scroll.Bounds.Bottom && reply.Bounds.Bottom > scroll.Bounds.Top);
		Assert.InRange(Find<MarkdownPresenter>(tab.View).Count(), 1, 10);
	}

	internal static void Layout(SessionTab tab) => Layout(tab.View);

	internal static void Layout(UIElement view, double width = 960, double height = 720)
	{
		for (var i = 0; i < 8; i++)
		{
			// A window normally drains descendant layout requests. Drive them explicitly in this windowless fixture.
			foreach (var element in Find<UIElement>(view).ToArray()) element.InvalidateArrange();
			view.InvalidateMeasure();
			view.Measure(new Size(width, height));
			view.Arrange(new Rect(0, 0, width, height));
		}
	}

	internal static CodingAgentSession CreateSession(int turns)
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		for (var i = 0; i < turns; i++)
		{
			manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"question {i}") });
			manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = $"answer {i}\n\n```csharp\nvar value = {i};\n```" }] });
		}
		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = FakeStreams.Model(), Tools = [], Skills = [], PromptTemplates = [],
			StreamFn = static (_, _, _) => throw new InvalidOperationException(), Persist = false,
		});
	}
}
