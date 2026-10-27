using System.Diagnostics;
using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 「首次打开复杂会话，快速上滚时会话进度跳变/回退」的回归：打开时只有尾部行有实测高度，
/// 上方历史行用的是虚拟化器的运行平均估算；上滚后实测行高修正会改写偏移（锚点校正）。
/// 普通 <see cref="SmoothScroll"/> 要么把可见校正当读者接管而停止滑行，要么让目标留在旧
/// 坐标系里；结果是滚了很多却几乎没动、甚至反向滑回去。会话时间线必须把校正当成内容坐标系平移。
/// </summary>
[Collection("mewui-controls")]
public sealed class VirtualizedWheelCorrectionTests(ITestOutputHelper output)
{
	// 与 AnimationFrames 相同的反射通道；这里需要真实帧时间，让 SmoothScroll 的 dt 不是 0。
	private static readonly object Manager = typeof(AnimationManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
	private static readonly MethodInfo UpdateAt = typeof(AnimationManager).GetMethod("UpdateAt", BindingFlags.Instance | BindingFlags.NonPublic)!;

	private static void Pump(UIElement view, double width = 700, double height = 600)
	{
		// 固定视口：每帧重排会把虚拟化行的实际高度测出来并触发锚点校正。
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static void Frame()
	{
		Thread.Sleep(16);
		UpdateAt.Invoke(Manager, [Stopwatch.GetTimestamp()]);
	}

	private sealed class Fixture : IDisposable
	{
		public required ConversationTimeline Timeline { get; init; }
		public required ConversationScroll Conversation { get; init; }
		public required TextFocusScrollGuard Guard { get; init; }
		public required SmoothScroll Smooth { get; init; }
		public required double[] Heights { get; init; }

		public void Dispose()
		{
			Conversation.Dispose();
			Guard.Dispose();
			Timeline.Dispose();
		}

		public ContentControl? HostOf(string entryId) =>
			VisualTree.Find<ContentControl>(Timeline.View)
				.FirstOrDefault(host => host.Tag is ChatEntry { EntryId: var id } && id == entryId);

		/// <summary>行槽高度 = 正文高度 + 行外框/间距；同一时间线里是常量。</summary>
		public double ChromeFor(string entryId, int index)
		{
			var host = HostOf(entryId);
			Assert.NotNull(host);
			return host!.Bounds.Height - Heights[index];
		}
	}

	private static Fixture Build(bool historyTall)
	{
		// 尾部 20 行在打开时被实测量出，上方 300 行未测：估算来自尾部平均。
		// historyTall 时上滚进入高行，平均估算变大（偏移向后校正）；
		// 否则进入矮行，平均估算变小（偏移向前校正）。两种方向都要保住滚轮输入。
		var historyHeight = historyTall ? 100.0 : 40.0;
		var tailHeight = historyTall ? 40.0 : 100.0;
		var heights = Enumerable.Range(0, 320)
			.Select(index => index < 300 ? historyHeight : tailHeight)
			.ToArray();
		var rows = Enumerable.Range(0, heights.Length).Select(index => new ChatEntry
		{
			Role = ChatRole.User,
			Text = $"row {index}",
			EntryId = $"r{index}",
			Timestamp = index,
		}).ToArray();
		var timeline = new ConversationTimeline(entry =>
			new Border().Height(heights[int.Parse(entry.EntryId![1..])]).Child(new TextBlock().Text(entry.Text)));
		var guard = new TextFocusScrollGuard(timeline.Scroll);
		var conversation = new ConversationScroll(timeline.Scroll, _ => { });
		var smooth = SmoothScroll.For(timeline.Scroll);
		var fixture = new Fixture
		{
			Timeline = timeline,
			Conversation = conversation,
			Guard = guard,
			Smooth = smooth,
			Heights = heights,
		};
		timeline.SetEntries(rows);
		Pump(timeline.View);
		// 打开会话：跳到最新并让尾部行测量落定；上方历史仍只有估算高度。
		conversation.ScrollToLatest();
		for (var pass = 0; pass < 8; pass++)
		{
			Frame();
			Pump(timeline.View);
		}
		smooth.Stop();
		Pump(timeline.View);
		return fixture;
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void FastUpwardWheelKeepsItsDistanceWhenUnmeasuredRowsAreMeasured(bool historyTall)
	{
		using var fixture = Build(historyTall);
		var timeline = fixture.Timeline;
		var smooth = fixture.Smooth;
		var startReading = timeline.CurrentReadingAnchor();
		Assert.NotNull(startReading.EntryId);
		var startIndex = timeline.IndexOfEntryId(startReading.EntryId);
		Assert.True(startIndex >= 0, "the opening position must be inside the measured tail");

		var chrome = fixture.ChromeFor(startReading.EntryId!, startIndex);
		double Slot(int index) => fixture.Heights[index] + chrome;

		double ContentY(int index, double within)
		{
			var y = within;
			for (var row = 0; row < index; row++) y += Slot(row);
			return y;
		}

		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;
		const int notches = 40;
		var expected = notches * step;
		var startOffset = timeline.Scroll.VerticalOffset;
		var previous = startOffset;
		var sawLargeJump = false;
		var correctionKilledGlide = false;

		for (var notch = 0; notch < notches; notch++)
		{
			smooth.ScrollBy(0, -1);
			smooth.FlushPendingWheel();
			Frame();
			Pump(timeline.View);

			var offset = timeline.Scroll.VerticalOffset;
			var delta = offset - previous;
			// 滚轮每格只让偏移往一个方向走 step 的一小段；比这更大的跳变只能是
			// 虚拟化器重排时的锚点校正（方向可正可负）。
			if (Math.Abs(delta) > step * 1.5)
			{
				sawLargeJump = true;
				if (!smooth.IsAnimating) correctionKilledGlide = true;
			}
			previous = offset;
		}

		for (var settle = 0; settle < 160 && smooth.IsAnimating; settle++)
		{
			Frame();
			Pump(timeline.View);
		}
		Assert.False(smooth.IsAnimating, "the wheel glide should settle");

		var endReading = timeline.CurrentReadingAnchor();
		Assert.NotNull(endReading.EntryId);
		var endIndex = timeline.IndexOfEntryId(endReading.EntryId);
		Assert.True(endIndex >= 0, "the settled position must still be realized");

		var actual = ContentY(startIndex, startReading.WithinRow) - ContentY(endIndex, endReading.WithinRow);
		// 校正把内容坐标系整体平移了；最终偏移与「起点 - 滚轮距离」的差就是校正量。
		var correction = Math.Abs(timeline.Scroll.VerticalOffset - (startOffset - expected));
		output.WriteLine(
			$"historyTall={historyTall}: start={startIndex}+{startReading.WithinRow:0} end={endIndex}+{endReading.WithinRow:0} " +
			$"moved={actual:0} expected={expected:0} correction={correction:0} bigJump={sawLargeJump} killedGlide={correctionKilledGlide}");

		Assert.True(correction > expected * 0.5,
			"the fixture must exercise a virtualized anchor correction (content coordinates shifted)");
		Assert.False(correctionKilledGlide,
			"a measured-height correction must not stop the reader's wheel glide");
		// 默认 50 DIP/格：没有坐标补偿时这段手势只走 1200–1400 DIP，补偿后接近完整 2000 DIP。
		Assert.InRange(actual, expected * 0.82, expected * 1.15);
	}
}
