using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 全栈复现「组体展开置顶、滚轮向下（内容向上移动）缓慢；向上滚一下后恢复」：
/// 会话层跟底在流式增长时每帧跳回末尾，把正在读的组体整体往上顶——
/// 组体内的滚轮追不上这个漂移（或被轮空）。度量：每帧增长后组体在屏幕上的位置漂移。
/// </summary>
[Collection("mewui-controls")]
public sealed class NestedReadingProbe(ITestOutputHelper output)
{
	private static ChatEntry Row(string id, double height) => new()
	{
		Role = ChatRole.User, Text = id, EntryId = id, Timestamp = 0,
		// Row factory 会用固定高度的 Border。
	};

	private sealed class Fixture
	{
		public required ConversationTimeline Timeline { get; init; }
		public required ConversationScroll Conversation { get; init; }
		public required ScrollViewer ConversationViewport { get; init; }
		public required ScrollViewer Body { get; init; }
		public required SmoothScroll BodySmooth { get; init; }
		public required ExecutionProcessView ProcessView { get; init; }
		public required List<ChatEntry> Rows { get; init; }
		public required ChatEntry Process { get; init; }
		public required List<ChatEntry> Members { get; init; }
		public required ChatEntry Answer { get; init; }

		public void Pump() => ConversationPerformanceTests.Layout(Timeline.View);

		/// <summary>
		/// 一次流式增长：组体末尾的思考在内部滚动（组体已到 300 上限），组下方的回答行持续变高，
		/// 会话跟底跳回末尾——这正是「读者在组体里读、外层把组往上顶」的对抗来源。
		/// </summary>
		public void StreamTick(ref int counter)
		{
			var thinking = Members[^1];
			Members[^1] = thinking with { Thinking = thinking.Thinking + "\n\n追加的思考段落 " + counter + "，组体内部滚动。" };
			counter++;
			ProcessView.Update(Process with { ProcessEntries = [.. Members], IsStreaming = true });
			Rows[^1] = Answer with { Text = Answer.Text + "\n\n回答段落 " + counter + "，持续变高把组体往上顶。" };
			Timeline.SetEntries([.. Rows]);
			Pump();
			// SessionTab.Render 每帧都会做这件事。
			Conversation.FollowContent();
			Pump();
		}

		// MewUI 的 Bounds 是窗口绝对坐标（含滚动）：组体顶部的绝对位移就是屏幕上的漂移。
		public double BodyTopOnScreen => Body.Bounds.Y;
	}

	private static ScrollViewer FindBody(ConversationTimeline timeline) =>
		Find<ScrollViewer>(timeline.View).Single(v => v.VerticalScroll != ScrollMode.Disabled && v.Bounds.Height > 0 &&
			!ReferenceEquals(v, timeline.Scroll));

	private static Fixture Build(int historyRows, int steps)
	{
		ConversationScroll? conversation = null;
		var rows = Enumerable.Range(0, historyRows)
			.Select(index => new ChatEntry { Role = ChatRole.User, Text = $"history {index}", EntryId = $"h{index}" })
			.ToList<ChatEntry>();
		var members = Enumerable.Range(0, steps).Select(index => new ChatEntry
		{
			Role = ChatRole.Tool, EntryId = $"tool/call-{index}", ToolName = "bash", ToolCallId = $"call-{index}",
			ToolArguments = """{"command":"dotnet test"}""",
			Text = $"output {index}", DurationMs = 12,
		}).ToList();
		members.Add(new ChatEntry
		{
			Role = ChatRole.Assistant, Text = string.Empty, EntryId = "thinking/live/0", Timestamp = 1,
			IsStreaming = true, IsThinking = true, Thinking = "初始思考",
		});
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process/nested", IsStreaming = true,
			ProcessEntries = [.. members],
		};
		rows.Add(process);
		var answer = new ChatEntry
		{
			Role = ChatRole.Assistant, Text = "回答开始", EntryId = "live-assistant/1", Timestamp = 1, IsStreaming = true,
		};
		rows.Add(answer);
		var height = 0;
		ExecutionProcessView? processView = null;
		var timeline = new ConversationTimeline(entry =>
		{
			if (entry.Role == ChatRole.Process)
			{
				var view = new ExecutionProcessView(entry, inner => new Border().Height(40).Child(new TextBlock().Text(inner.Text)), null);
				processView = view;
				return view.View;
			}
			height++;
			// 回答行用 AssistantMessageView 太重；流式行给一个高度随文本行数变化的 Border。
			var lines = Math.Max(1, entry.Text.Split('\n').Length);
			return new Border().Height(20 + lines * 18).Child(new TextBlock().Text(entry.Text[..Math.Min(40, entry.Text.Length)]));
		});
		var guard = new TextFocusScrollGuard(timeline.Scroll, anchor => conversation!.PreserveAnchor(anchor));
		conversation = new ConversationScroll(timeline.Scroll, _ => { });
		using (guard)
		{
			timeline.SetEntries(rows);
			ConversationPerformanceTests.Layout(timeline.View);
			// 展开过程组（运行中的最新组自动展开并贴底）；再手动置顶模拟「读者从顶部开始读」。
			foreach (var expander in Find<Expander>(timeline.View)
				.Where(expander => !expander.IsExpanded &&
					HoverHints.Text(expander) is Strings.ExecutionExpandHint or Strings.ExecutionGroupHint).ToArray())
			{
				expander.IsExpanded = true;
			}
			ConversationPerformanceTests.Layout(timeline.View);
			// 会话保持在末尾（跟底，流式中的常态）。
			timeline.Scroll.SetScrollOffsets(0, SmoothScroll.For(timeline.Scroll).MaxVerticalOffset);
			ConversationPerformanceTests.Layout(timeline.View);
			conversation.FollowContent();
			ConversationPerformanceTests.Layout(timeline.View);
		}
		var body = FindBody(timeline);
		var fixture = new Fixture
		{
			Timeline = timeline, Conversation = conversation, ConversationViewport = timeline.Scroll,
			Body = body, BodySmooth = SmoothScroll.For(body),
			ProcessView = processView!, Rows = rows, Process = process, Members = members, Answer = answer,
		};
		// 读者从组体顶部开始读（置顶），会话保持跟底。
		body.SetScrollOffsets(0, 0);
		ConversationPerformanceTests.Layout(timeline.View);
		Assert.True(conversation.Following, "the conversation should be following at the tail");
		return fixture;
	}

	[Fact]
	public void WheelingInsideTheBodyAwayFromItsTailStopsTheConversationDrift()
	{
		var fixture = Build(12, 20);
		try
		{
			var counter = 1;
			// 读者在置顶的组体里向下滚一格（离开组体自己的末尾）：外层跟底必须停追。
			fixture.BodySmooth.ScrollBy(0, 1);
			fixture.BodySmooth.FlushPendingWheel();
			Assert.False(fixture.Conversation.Following,
				"a wheel consumed by a nested viewport away from its tail is reading intent: the conversation must browse");

			// 之后流式继续增长：组体在屏幕上的位置不再被跟底顶走。
			var parked = fixture.BodyTopOnScreen;
			for (var tick = 0; tick < 6; tick++) fixture.StreamTick(ref counter);
			var drift = Math.Abs(fixture.BodyTopOnScreen - parked);
			output.WriteLine($"drift after 6 growth ticks: {drift:0.0} DIP (was pushed up by the follow jump before the fix)");
			Assert.True(drift <= 1, $"the conversation follow still drifts the group: {drift:0.0} DIP");

			// 对照：贴着组体自己的末尾滚（读最新步骤）不解除会话跟底——那是与跟底同向的阅读。
			fixture.Body.SetScrollOffsets(0, fixture.BodySmooth.MaxVerticalOffset);
			fixture.Pump();
			fixture.Conversation.ScrollToLatest();
			fixture.Pump();
			Assert.True(fixture.Conversation.Following);
			fixture.BodySmooth.ScrollBy(0, -1);
			fixture.BodySmooth.FlushPendingWheel();
			Assert.True(fixture.Conversation.Following,
				"a wheel while pinned to the nested tail keeps the conversation following");
		}
		finally
		{
			UiLifetime.Release(fixture.Timeline.View);
		}
	}

}

