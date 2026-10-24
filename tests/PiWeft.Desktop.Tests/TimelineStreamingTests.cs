using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 时间线虚拟化在流式收尾时的行为：定稿只换行内容，不向虚拟化器发 Replace，
/// 已测行高（以及由它导出的滚动范围）不回退成估算值。
/// </summary>
[Collection("mewui-controls")]
public sealed class TimelineStreamingTests
{
	private static ChatEntry Live(string text) => new()
	{
		Role = ChatRole.Assistant,
		Text = text,
		EntryId = "live-assistant/42",
		Timestamp = 42,
		IsStreaming = true,
	};

	/// <summary>
	/// 代码块的横向视口在 Markdown 建块时才创建：必须与其它视口共用同一个平滑滚动控制器，
	/// 否则触控板横向滑动落进 ScrollViewer 的即时跳变，与 Shift+滚轮的平滑曲线不一致。
	/// </summary>
	[Fact]
	public void CodeBlockViewportsShareTheSmoothScrollController()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var code = "```csharp\n" + string.Join('\n', Enumerable.Range(0, 8).Select(i => $"var value{i} = {i}; // padding to exceed the column")) + "\n```";
		var entry = Live(code);
		using var message = new AssistantMessageView(entry, _ => { }, guard);
		message.View.Measure(new Size(500, 400));
		message.View.Arrange(new Rect(0, 0, 500, 400));

		var codeViewport = Find<ScrollViewer>(message.View)
			.SingleOrDefault(viewport => viewport.HorizontalScroll != ScrollMode.Disabled);
		Assert.NotNull(codeViewport);
		Assert.NotNull(SmoothScroll.Find(codeViewport));
		UiLifetime.Release(message.View);
	}

	/// <summary>
	/// 提示统一走宿主的延迟悬浮层：全应用 43 处 HoverTip 都是 350 ms 延迟，
	/// 代码块复制按钮不能是仅有的「悬停立即弹出」（内建 ToolTip 无延迟）。
	/// </summary>
	[Fact]
	public void CodeBlockToolTipsGoThroughTheDelayedHostLayer()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var entry = Live("```csharp\nvar x = 1;\n```");
		using var message = new AssistantMessageView(entry, _ => { }, guard);
		message.View.Measure(new Size(500, 400));
		message.View.Arrange(new Rect(0, 0, 500, 400));

		var copyButton = Find<Aprillz.MewUI.Controls.Button>(message.View)
			.SingleOrDefault(button => HoverHints.Text(button) == Strings.CopyCodeTooltip);
		Assert.NotNull(copyButton);
		// 内建 ToolTip 必须保持未设置：两套提示并存会同时弹两层。
		Assert.Null(copyButton.ToolTip);
		UiLifetime.Release(message.View);
	}

	/// <summary>
	/// 流式行落盘时 EntryId 从 live-assistant/… 变成会话条目 id（时间戳与图片不变）：
	/// 这仍是一次原位收尾。发 Replace 会把该行已测高度作废成估算值，长回复定稿的
	/// 一瞬间滚动范围先缩后涨，跟底读者看到一次跳动。
	/// </summary>
	[Fact]
	public void FinalizingAStreamingRowUpdatesInPlaceWithoutAReplaceNotification()
	{
		var rows = new ConversationTimeline.MessageRows();
		var changes = new List<ItemsChange>();
		rows.Changed += changes.Add;

		rows.Update([Live("流式正文")], 0, false, []);
		Assert.Equal(1, rows.Count);

		// 流式帧：同键同时间戳，原位更新。
		var inPlace = new List<int>();
		rows.Update([Live("流式正文，更多内容")], 0, false, inPlace);
		Assert.Contains(0, inPlace);
		Assert.DoesNotContain(changes, change => change.Kind == ItemsChangeKind.Replace);

		// 定稿：EntryId 变化、IsStreaming 归位 —— 仍应原位收尾。
		changes.Clear();
		inPlace.Clear();
		var final = Live("最终正文") with { EntryId = "message/committed", IsStreaming = false, DurationMs = 1200 };
		rows.Update([final], 0, false, inPlace);
		Assert.Contains(0, inPlace);
		Assert.DoesNotContain(changes, change => change.Kind == ItemsChangeKind.Replace);
		Assert.Same(final, rows.GetItem(0));
	}

	/// <summary>
	/// 定稿不发 Replace 之后，跟底读者不再被「高度回退成估算值 → 偏移被夹回 → 看起来像上滚」
	/// 误判成浏览意图：回复结束的瞬间仍然贴着末尾，跟随态不丢。
	/// </summary>
	[Fact]
	public void FinalizingTheStreamingReplyKeepsTheReaderFollowingAtTheEnd()
	{
		using var timeline = new ConversationTimeline(entry => new Border()
			.Height(entry.Role == ChatRole.Assistant ? 600 : 100)
			.Child(new TextBlock().Text(entry.Text)));
		using var conversation = new ConversationScroll(timeline.Scroll, _ => { });
		var smooth = SmoothScroll.For(timeline.Scroll);

		var history = Enumerable.Range(0, 20)
			.Select(index => new ChatEntry { Role = ChatRole.User, Text = $"消息 {index}", EntryId = $"user-{index}" })
			.ToList();
		history.Add(Live("一条很长的流式回复"));
		timeline.SetEntries(history);
		Pump(timeline.View);

		// 直接把偏移写到底：与拖动滚动条到底同一路径，几何上重新锁底。
		timeline.Scroll.SetScrollOffsets(0, smooth.MaxVerticalOffset);
		Pump(timeline.View);
		Assert.True(conversation.Following);
		Assert.Equal(smooth.MaxVerticalOffset, timeline.Scroll.VerticalOffset, 1);

		// 回复定稿：EntryId 换成会话条目 id，行高不变。
		history[^1] = Live("一条很长的流式回复") with { EntryId = "message/committed", IsStreaming = false };
		timeline.SetEntries(history);
		Pump(timeline.View);
		Pump(timeline.View);

		Assert.True(conversation.Following, "finalizing the streaming row must not look like an upward scroll");
		Assert.Equal(smooth.MaxVerticalOffset, timeline.Scroll.VerticalOffset, 1);
	}

	private static void Pump(UIElement view, double width = 500, double height = 400)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}
}
