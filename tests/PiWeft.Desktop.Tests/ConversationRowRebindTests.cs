using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// 自有虚拟化面板的行宿主/布局契约：
/// 1. 非原位替换（工具调用结束、行身份变化）必须重新绑定，否则视图停在旧内容上；
/// 2. 视口上方插入/删除行时，阅读位置按顶部行身份补偿，不能整体位移；
/// 3. 定位轴跳转把行顶留在视口上沿留白处（与 PiDeck 一致）。
[Collection("mewui-controls")]
public sealed class ConversationRowRebindTests(ITestOutputHelper output)
{
	private static void Pump(UIElement view, double width = 700, double height = 400) =>
		ConversationPerformanceTests.Layout(view, width, height);

	private static ChatEntry Row(string id, string text, double height = 100) => new()
	{
		Role = ChatRole.User,
		Text = text,
		EntryId = id,
	};

	private static ConversationTimeline BuildTimeline(double height = 100)
	{
		return new ConversationTimeline(entry => new Border().Height(height).Child(new TextBlock().Text(entry.Text)));
	}

	private static ContentControl HostOf(ConversationTimeline timeline, string entryId) =>
		VisualTree.Find<ContentControl>(timeline.View)
			.Single(host => host.Tag is ChatEntry entry && entry.EntryId == entryId);

	[Fact]
	public void ReplacedRowIsReboundEvenWhenItsHeightIsUnchanged()
	{
		using var timeline = BuildTimeline();
		var running = new ChatEntry
		{
			Role = ChatRole.Tool, ToolName = "read", ToolCallId = "call", EntryId = "tool",
			Text = "partial", IsStreaming = true, ToolArguments = "{\"path\":\"a\"}", Timestamp = 1,
		};
		var rows = new List<ChatEntry> { Row("user", "question"), running };
		timeline.SetEntries(rows);
		Pump(timeline.View);
		Assert.Same(running, HostOf(timeline, "tool").Tag);

		// 同一次工具调用结束：EntryId/ToolCallId 不变，但 IsStreaming 从 true 变 false，
		// 走不进「原位更新」快路径，必须重建行。
		var completed = running with { Text = "done", IsStreaming = false };
		rows[1] = completed;
		timeline.SetEntries(rows);
		Pump(timeline.View);
		Assert.Same(completed, HostOf(timeline, "tool").Tag);
		output.WriteLine("completed row rebound");
	}

	[Fact]
	public void InsertingARowAboveTheViewportKeepsTheReadingRowInPlace()
	{
		using var timeline = BuildTimeline();
		var rows = Enumerable.Range(0, 40).Select(index => Row($"r{index}", $"row {index}")).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);
		var scroll = timeline.Scroll;
		scroll.SetScrollOffsets(0, 800);
		Pump(timeline.View);

		var before = timeline.CurrentReadingAnchor();
		var hostBefore = HostOf(timeline, before.EntryId!);
		var screenBefore = hostBefore.Bounds.Y - scroll.Bounds.Y;

		rows.Insert(0, Row("inserted", "new row", height: 200));
		timeline.SetEntries(rows);
		Pump(timeline.View);

		var after = timeline.CurrentReadingAnchor();
		var hostAfter = HostOf(timeline, before.EntryId!);
		var screenAfter = hostAfter.Bounds.Y - scroll.Bounds.Y;
		output.WriteLine($"anchor {before.EntryId}/{before.WithinRow:F0} -> {after.EntryId}/{after.WithinRow:F0}; " +
			$"screen {screenBefore:F0} -> {screenAfter:F0}");
		Assert.Equal(before.EntryId, after.EntryId);
		Assert.InRange(Math.Abs(screenAfter - screenBefore), 0, 2);
	}

	[Fact]
	public void RemovingARowAboveTheViewportKeepsTheReadingRowInPlace()
	{
		using var timeline = BuildTimeline();
		var rows = Enumerable.Range(0, 40).Select(index => Row($"r{index}", $"row {index}")).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);
		timeline.Scroll.SetScrollOffsets(0, 800);
		Pump(timeline.View);

		var before = timeline.CurrentReadingAnchor();
		var screenBefore = HostOf(timeline, before.EntryId!).Bounds.Y - timeline.Scroll.Bounds.Y;
		rows.RemoveAt(0);
		timeline.SetEntries(rows);
		Pump(timeline.View);

		var after = timeline.CurrentReadingAnchor();
		var screenAfter = HostOf(timeline, before.EntryId!).Bounds.Y - timeline.Scroll.Bounds.Y;
		output.WriteLine($"after removal: {before.EntryId} -> {after.EntryId}; screen {screenBefore:F0} -> {screenAfter:F0}");
		Assert.Equal(before.EntryId, after.EntryId);
		Assert.InRange(Math.Abs(screenAfter - screenBefore), 0, 2);
	}

	[Fact]
	public void AppendingAtTheTailDoesNotMoveABrowsingReader()
	{
		using var timeline = BuildTimeline();
		var rows = Enumerable.Range(0, 40).Select(index => Row($"r{index}", $"row {index}")).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);
		timeline.Scroll.SetScrollOffsets(0, 800);
		Pump(timeline.View);

		var before = timeline.CurrentReadingAnchor();
		var screenBefore = HostOf(timeline, before.EntryId!).Bounds.Y - timeline.Scroll.Bounds.Y;
		rows.Add(Row("tail", "tail row", height: 300));
		timeline.SetEntries(rows);
		Pump(timeline.View);

		var after = timeline.CurrentReadingAnchor();
		var screenAfter = HostOf(timeline, before.EntryId!).Bounds.Y - timeline.Scroll.Bounds.Y;
		output.WriteLine($"tail append: screen {screenBefore:F0} -> {screenAfter:F0}");
		Assert.Equal(before.EntryId, after.EntryId);
		Assert.InRange(Math.Abs(screenAfter - screenBefore), 0, 2);
	}

	[Fact]
	public void FullReprojectionWithIdenticalContentKeepsTheExtentAndReadingRow()
	{
		using var timeline = BuildTimeline();
		var rows = Enumerable.Range(0, 40).Select(index => Row($"r{index}", $"row {index}")).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);
		timeline.Scroll.SetScrollOffsets(0, 800);
		Pump(timeline.View);

		var maxBefore = timeline.Scroll.Content!.DesiredSize.Height;
		var before = timeline.CurrentReadingAnchor();
		var screenBefore = HostOf(timeline, before.EntryId!).Bounds.Y - timeline.Scroll.Bounds.Y;

		// 全量重投（prefixReused=false）：内容一样，但每条都是新实例。
		var copies = rows.Select(entry => entry with { }).ToList();
		timeline.SetEntries(copies, prefixReused: false);
		Pump(timeline.View);

		var maxAfter = timeline.Scroll.Content!.DesiredSize.Height;
		var after = timeline.CurrentReadingAnchor();
		var screenAfter = HostOf(timeline, after.EntryId!).Bounds.Y - timeline.Scroll.Bounds.Y;
		output.WriteLine($"reproject: extent {maxBefore:F0} -> {maxAfter:F0}, anchor {before.EntryId} -> {after.EntryId}, " +
			$"screen {screenBefore:F0} -> {screenAfter:F0}");
		Assert.Equal(maxBefore, maxAfter, 0.5);
		Assert.Equal(before.EntryId, after.EntryId);
		Assert.InRange(Math.Abs(screenAfter - screenBefore), 0, 2);
	}

	[Fact]
	public void ClampedStructuralShiftIsNotReplayedAfterContentGrowsAgain()
	{
		using var timeline = BuildTimeline();
		var rows = Enumerable.Range(0, 40).Select(index => Row($"r{index}", $"row {index}")).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);
		// 停在最末尾附近，然后删掉末尾几行：extent 收缩，偏移被夹回新末尾。
		timeline.Scroll.SetScrollOffsets(0, 5200);
		Pump(timeline.View);
		rows.RemoveRange(24, 16);
		timeline.SetEntries(rows);
		Pump(timeline.View);
		var clamped = timeline.Scroll.VerticalOffset;

		// 之后内容又变长（流式追加）：阅读位置不该被上一次没补完的坐标系平移拽走。
		rows.Add(Row("tail", "tail row", height: 400));
		timeline.SetEntries(rows);
		Pump(timeline.View);
		var after = timeline.Scroll.VerticalOffset;
		output.WriteLine($"clamped {clamped:F0} -> {after:F0}");
		Assert.InRange(Math.Abs(after - clamped), 0, 2);
	}

	[Fact]
	public void JumpToRowLeavesTheReadingColumnTopInset()
	{
		using var timeline = BuildTimeline();
		var rows = Enumerable.Range(0, 40).Select(index => Row($"r{index}", $"row {index}")).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);

		timeline.JumpToRow(10);
		Pump(timeline.View);

		var host = HostOf(timeline, "r10");
		var top = host.Bounds.Y - timeline.Scroll.Bounds.Y;
		output.WriteLine($"row top offset {top:F1}");
		Assert.InRange(top, AppTheme.Space3 - 1, AppTheme.Space3 + 3);
	}
}
