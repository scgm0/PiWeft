using System.Reflection;
using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 轨迹页的检查器：分隔条能像 Git 面板那样拖动，入参/结果文本在自己的视口里滚动
/// （曾经套了一层 ScrollViewer，滚轮被外层吃掉，表现为「有滚动条但滚不动」）。
[Collection("mewui-controls")]
public sealed class TrajectoryViewTests
{
	private const long Base = 1_700_000_000_000;

	private static string Iso(long unixMs) =>
		DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
			.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

	private static TrajectoryView View()
	{
		var command = string.Join('\n', Enumerable.Range(0, 80).Select(static i => $"echo line-{i}"));
		var entries = new List<SessionEntry>
		{
			new SessionMessageEntry
			{
				Id = "user",
				Timestamp = Iso(Base),
				Message = new UserMessage { Content = MessageContent.FromText("run"), Timestamp = Base },
			},
			new SessionMessageEntry
			{
				Id = "assistant",
				ParentId = "user",
				Timestamp = Iso(Base + 100),
				Message = new AssistantMessage
				{
					Timestamp = Base + 100,
					StopReason = StopReason.ToolUse,
					Content =
					[
						new ToolCall
						{
							Id = "c1",
							Name = "bash",
							Arguments = new JsonObject { ["command"] = command },
						},
					],
				},
			},
			new SessionMessageEntry
			{
				Id = "result",
				ParentId = "assistant",
				Timestamp = Iso(Base + 400),
				Message = new ToolResultMessage
				{
					ToolCallId = "c1",
					ToolName = "bash",
					Timestamp = Base + 400,
					Content = [TextContent.Of("ok")],
				},
			},
		};

		return new TrajectoryView(() => new TrajectorySource(entries, [], false));
	}

	[Fact]
	public void InspectorSashResizesTheInspectorAndClampsTheHeight()
	{
		using var view = View();
		ConversationPerformanceTests.Layout(view.View, 420, 820);

		var sash = Assert.Single(Find<PaneSash>(view.View));
		var before = view.InspectorHostForTests.Bounds.Height;
		Assert.Equal(view.InspectorHeightForTests, before, 0.5);
		Assert.True(sash.Bounds.Y < view.InspectorHostForTests.Bounds.Y);

		// 向上拖 120 DIP：分隔条上面那一段（账本）让高，检查器长高。
		view.SetInspectorHeightForTests(before + 120);
		ConversationPerformanceTests.Layout(view.View, 420, 820);

		Assert.Equal(before + 120, view.InspectorHeightForTests, 0.5);
		Assert.Equal(before + 120, view.InspectorHostForTests.Bounds.Height, 0.5);
		Assert.True(sash.Bounds.Y < view.InspectorHostForTests.Bounds.Y);

		// 上限：面板高度里必须给表头/泳道/账本留出空间。
		view.SetInspectorHeightForTests(99_999);
		ConversationPerformanceTests.Layout(view.View, 420, 820);
		Assert.True(view.InspectorHostForTests.Bounds.Height <= TrajectoryView.MaxInspectorHeight + 0.5);
		Assert.True(view.InspectorHostForTests.Bounds.Height <= 820 - 260 + 0.5);

		// 下限：再矮也留得住几行参数。
		view.SetInspectorHeightForTests(1);
		ConversationPerformanceTests.Layout(view.View, 420, 820);
		Assert.Equal(TrajectoryView.MinInspectorHeight, view.InspectorHostForTests.Bounds.Height, 0.5);
	}

	[Fact]
	public void InspectorTextSectionScrollsWithItsOwnWheel()
	{
		using var view = View();
		ConversationPerformanceTests.Layout(view.View, 420, 820);
		view.SelectForSmoke();
		ConversationPerformanceTests.Layout(view.View, 420, 820);

		var input = Find<MultiLineTextBox>(view.View)
			.First(box => box.Text.Contains("line-0", StringComparison.Ordinal));
		Assert.True(input.Bounds.Height <= 200.5, $"the input section must stay bounded: {input.Bounds.Height:0}");
		Assert.Equal(0, input.ScrollOffset.Y);

		// 滚轮落在文本上时由文本控件自己消费（内层还有余量时不把事件漏给检查器）。
		var wheel = new MouseWheelEventArgs(new Point(10, 10), new Point(10, 10), new Vector(0, -1));
		RaiseWheel(input, wheel);

		Assert.True(wheel.Handled, "the text section must consume the wheel");
		Assert.True(input.ScrollOffset.Y > 0, "the text section did not scroll");
	}

	private static void RaiseWheel(Element target, MouseWheelEventArgs args) =>
		typeof(UIElement).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(target, [args]);
}
