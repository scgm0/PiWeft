using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 「首次打开复杂会话，快速上滚时会话进度跳变/回退」的回归：打开时只有尾部行有实测高度，
/// 上方历史行用的是虚拟化器的运行平均估算；上滚后实测行高修正会改写偏移（锚点校正）。
/// 普通 <see cref="SmoothScroll"/> 要么把可见校正当读者接管而停止滑行，要么让目标留在旧
/// 坐标系里；结果是滚了很多却几乎没动、甚至反向滑回去。会话时间线必须把校当内容坐标系平移。
/// </summary>
/// <remarks>
/// 帧驱动分两条：<see cref="AnimationFrames.Frame(SmoothScroll)"/> 钉住每帧 dt（曲线确定，
/// 距离断言可以收到 ±2%），以及真实 dt 的 <see cref="RealFrames"/>（覆盖「不钳制 dt」那条性质，
/// 只断言不漂移/不拖长尾，默认跳过 —— 见其注释）。
/// </remarks>
[Collection("mewui-controls")]
public sealed class VirtualizedWheelCorrectionTests(ITestOutputHelper output)
{
	// 44 格 × 默认 50 DIP：40 格时最后几格仍在估算段里翻平均，44 格把手势推过整段估算区，
	// 校正集中在可数的几帧里完成，曲线在固定 dt 下完全确定。
	private const int Notches = 44;

	private static void Pump(UIElement view, double width = 700, double height = 600)
	{
		// 固定视口：每帧重排会把虚拟化行的实际高度测出来并触发锚点校正。
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	/// <summary>
	/// 真实 dt 的一帧：睡满名义帧长再推时钟，SmoothScroll 读到的是墙钟时间。
	/// 只用于 <see cref="RealFrameClockKeepsTheGlideDriftFree"/>。
	/// </summary>
	private static void RealFrames() => Thread.Sleep(AnimationFrames.FrameMs);

	private sealed class Fixture : IDisposable
	{
		public required ConversationTimeline Timeline { get; init; }
		public required ConversationScroll Conversation { get; init; }
		public required TextFocusScrollGuard Guard { get; init; }
		public required SmoothScroll Smooth { get; init; }
		public required double[] Heights { get; init; }
		/// <summary>本用例把进程级滚轮步长临时钉成的值；Dispose 时还原。</summary>
		public required ThemeMetrics MetricsBefore { get; init; }

		public void Dispose()
		{
			Conversation.Dispose();
			Guard.Dispose();
			Timeline.Dispose();
			ThemeManager.DefaultMetrics = MetricsBefore;
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
		// 滚轮步长是**进程级**全局量：AppFonts.Register()（桌面启动与 AppFontTests 都会调）把它
		// 设成 100 DIP，而 MewUI 缺省是 50。于是同一段断言在「本用例先跑」和「字体测试先跑」
		// 两种进程里读到不同的步长，手势走过的内容量差一倍，比例断言自然一个过一个挂。
		// 用例自己钉住它并在 Dispose 里还原：与执行顺序无关，也不影响其它用例。
		var metricsBefore = ThemeManager.DefaultMetrics;
		ThemeManager.DefaultMetrics = metricsBefore with { ScrollWheelStep = AppFonts.ScrollWheelStepDip };
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
		// 整条用例都跑在钉住的 dt 上（开场跳底 + 手势 + 收尾），曲线因此逐帧确定；
		// 真实 dt 的性质由 RealFrameClockKeepsTheGlideDriftFree 单独覆盖。
		AnimationFrames.Pin(smooth);
		var fixture = new Fixture
		{
			Timeline = timeline,
			Conversation = conversation,
			Guard = guard,
			Smooth = smooth,
			Heights = heights,
			MetricsBefore = metricsBefore,
		};
		timeline.SetEntries(rows);
		Pump(timeline.View);
		// 打开会话：跳到最新并让尾部行测量落定；上方历史仍只有估算高度。
		conversation.ScrollToLatest();
		for (var pass = 0; pass < 8; pass++)
		{
			AnimationFrames.Advance();
			Pump(timeline.View);
		}
		smooth.Stop();
		Pump(timeline.View);
		return fixture;
	}

	/// <summary>一段手势的测量结果：全部换算到内容坐标，偏移侧的校正另算。</summary>
	private sealed record Glide(
		double Moved, double Expected, double Correction, bool BigJump, bool KilledGlide, bool Settled);

	/// <summary>
	/// 跑「40 格快速上滚 + 收尾」：每格推一帧（帧由 <paramref name="frame"/> 决定是钉住 dt
	/// 还是真实 dt），随后按同一节奏等动画收敛。
	/// </summary>
	private Glide Run(Fixture fixture, Action frame)
	{
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
		var expected = Notches * step;
		var startOffset = timeline.Scroll.VerticalOffset;
		var previous = startOffset;
		var sawLargeJump = false;
		var correctionKilledGlide = false;

		for (var notch = 0; notch < Notches; notch++)
		{
			smooth.ScrollBy(0, -1);
			smooth.FlushPendingWheel();
			frame();
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

		for (var settle = 0; settle < 400 && smooth.IsAnimating; settle++)
		{
			frame();
			Pump(timeline.View);
		}

		var endReading = timeline.CurrentReadingAnchor();
		Assert.NotNull(endReading.EntryId);
		var endIndex = timeline.IndexOfEntryId(endReading.EntryId);
		Assert.True(endIndex >= 0, "the settled position must still be realized");

		var moved = ContentY(startIndex, startReading.WithinRow) - ContentY(endIndex, endReading.WithinRow);
		// 校正把内容坐标系整体平移了；最终偏移与「起点 - 滚轮距离」的差就是校正量。
		var correction = Math.Abs(timeline.Scroll.VerticalOffset - (startOffset - expected));
		return new Glide(moved, expected, correction, sawLargeJump, correctionKilledGlide, !smooth.IsAnimating);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void FastUpwardWheelKeepsItsDistanceWhenUnmeasuredRowsAreMeasured(bool historyTall)
	{
		using var fixture = Build(historyTall);
		var run = Run(fixture, AnimationFrames.Advance);
		output.WriteLine(
			$"historyTall={historyTall}: moved={run.Moved:0} expected={run.Expected:0} " +
			$"correction={run.Correction:0} bigJump={run.BigJump} killedGlide={run.KilledGlide} settled={run.Settled}");

		Assert.True(run.Correction > run.Expected * 0.5,
			"the fixture must exercise a virtualized anchor correction (content coordinates shifted)");
		Assert.True(run.Settled, "the wheel glide should settle");
		Assert.False(run.KilledGlide,
			"a measured-height correction must not stop the reader's wheel glide");
		// 默认 50 DIP/格。没有坐标补偿时这段手势只走 ~0.3×；补偿后走满输入距离。
		// 剩下的 12–18% 是估算段的二次收缩：补偿只平移「写回视口的那次校正」，而估算段的行高是
		// 运行平均（40–100 混合），上滚途中平均被新测量行逐行重算，extent 跟着收了几次——
		// 内容坐标里再被扣掉一段。固定 dt + 钉住滚轮步长之后这是确定值：
		//   historyTall=False → 0.82×、historyTall=True → 0.88×。
		// 关掉坐标补偿时实测是 0.40× / 0.55×（且 killedGlide 变 true），所以这条带宽
		// 既能抓住「补偿丢了」，也不会因为两档几何差异而误报。
		Assert.InRange(run.Moved, run.Expected * 0.78, run.Expected * 0.95);
	}

	/// <summary>
	/// 真实 dt 下的同一段手势：只断言「不漂移、不拖长尾」这类性质，容差留足机器负载的余量。
	/// 固定 dt 的用例把距离钉死了，这条负责守住「Advance 不钳制 dt」时曲线仍然按真实时间收敛
	/// （见 SmoothScroll.Advance 的注释：钳制 dt 会把主线程停顿拉长成追赶、松手后拖尾巴）。
	/// 它按墙钟sleep，满载机器上本身会抖，所以默认跳过：设 PIWEFT_NATIVE_TESTS=1 才跑。
	/// </summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void RealFrameClockKeepsTheGlideDriftFree(bool historyTall)
	{
		if (Environment.GetEnvironmentVariable("PIWEFT_NATIVE_TESTS") != "1") return;
		using var fixture = Build(historyTall);
		// 开场也用真实 dt：Build 里那 8 帧钉过 dt 会把控制器的 dt 留着，这里显式清回墙钟。
		fixture.Smooth.FrameDtSeconds = null;
		var run = Run(fixture, () =>
		{
			RealFrames();
			AnimationFrames.Advance();
		});
		output.WriteLine(
			$"real-dt historyTall={historyTall}: moved={run.Moved:0} expected={run.Expected:0} settled={run.Settled}");

		Assert.True(run.Settled, "the glide must converge under real frame times too");
		Assert.False(run.KilledGlide,
			"a measured-height correction must not stop the reader's wheel glide");
		// 下界仍远高于「没有补偿」的 ~0.3×，上界容忍帧长抖动带来的过冲。
		Assert.InRange(run.Moved, run.Expected * 0.6, run.Expected * 1.2);
	}
}
