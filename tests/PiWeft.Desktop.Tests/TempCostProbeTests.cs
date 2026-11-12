using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class TempCostProbeTests(ITestOutputHelper output)
{
	private static void Pump(UIElement view, double width = 560, double height = 420)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static ChatEntry Thinking(string text) => new()
	{
		Role = ChatRole.Assistant, Text = string.Empty, Thinking = text,
		EntryId = "thinking/live/0", Timestamp = 1, IsStreaming = true, IsThinking = true,
	};

	private static ChatEntry Bash(int index) => new()
	{
		Role = ChatRole.Tool, EntryId = $"tool/call-{index}", ToolName = "bash", ToolCallId = $"call-{index}",
		ToolArguments = """{"command":"ls -la"}""", Text = "output", DurationMs = 12,
	};

	private static string ThinkingText(int lines) =>
		string.Join('\n', Enumerable.Range(0, lines).Select(line => $"第 {line} 行思考：这一段写长一点，让组体真的超出视口高度。"));

	[Fact]
	public void StreamingGroupBodyFrameCost()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var members = new List<ChatEntry> { Bash(0), Bash(1), Thinking(ThinkingText(30)) };
		ChatEntry Process() => new()
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process/turn", IsStreaming = true,
			ProcessEntries = [.. members],
		};
		var view = new ExecutionProcessView(Process(), entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, guard).View
			: new AssistantMessageView(entry, _ => { }, guard).View, guard);
		Pump(view.View);
		foreach (var expander in Find<Expander>(view.View).Where(e => !e.IsExpanded).ToArray()) expander.IsExpanded = true;
		SmoothScroll.For(Find<ScrollViewer>(view.View).First()).FollowGrowth();
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);

		for (var warm = 0; warm < 20; warm++)
		{
			members[^1] = Thinking(ThinkingText(30 + warm));
			view.Update(Process());
			Pump(view.View);
		}

		GC.Collect();
		var before = GC.GetAllocatedBytesForCurrentThread();
		var watch = Stopwatch.StartNew();
		const int frames = 120;
		for (var frame = 0; frame < frames; frame++)
		{
			members[^1] = Thinking(ThinkingText(30 + frame % 10) + $"\n追加第 {frame} 行");
			view.Update(Process());
			Pump(view.View);
		}
		watch.Stop();
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		output.WriteLine($"group-body frame: {watch.Elapsed.TotalMilliseconds / frames:F3} ms, {allocated / frames} B/frame, follows={view.ContentSizeFollows}");
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void StreamingTimelineRepaintCount()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		using var timeline = new ConversationTimeline(entry => new AssistantMessageView(entry, _ => { }, focus).View);
		var history = new ChatEntry { Role = ChatRole.Assistant, Text = "previous", EntryId = "previous" };
		var live = new ChatEntry { Role = ChatRole.Assistant, Text = "part", EntryId = "live-assistant/1", Timestamp = 1, IsStreaming = true };
		timeline.SetEntries([history, live]);
		ConversationPerformanceTests.Layout(timeline.View);
		var before = timeline.RepaintOnStructuralChangeCount;
		for (var frame = 0; frame < 120; frame++)
		{
			live = live with { Text = "part " + new string('x', frame) };
			timeline.SetEntries([history, live], prefixReused: true);
			ConversationPerformanceTests.Layout(timeline.View);
		}
		output.WriteLine($"streaming repaints: {timeline.RepaintOnStructuralChangeCount - before} / 120 frames");
	}
}
