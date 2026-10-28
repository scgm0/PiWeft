using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 折叠态思考预览的跑马灯：流式期每帧跟着最新一行并把行尾贴到右边缘，定稿后回到首行 + 省略号。
/// 与 PiDeck 的 SingleLinePreview（running = latestLine + 尾部跟随，结束 = firstLine）同语义。
/// </summary>
[Collection("mewui-controls")]
public sealed class ThinkingPreviewTests
{
	private const double ColumnWidth = 320;

	private static void Pump(UIElement view, double width = 560, double height = 240)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static ChatEntry Live(string thinking, bool streaming, string id = "live-assistant/1") => new()
	{
		Role = ChatRole.Assistant,
		Text = string.Empty,
		Thinking = thinking,
		EntryId = id,
		Timestamp = 1,
		IsStreaming = true,
		IsThinking = streaming,
		Compact = true,
	};

	private static string Rep(string value, int times) => string.Concat(Enumerable.Repeat(value, times));

	private static MarqueeLine PreviewLine(UIElement root) => Find<MarqueeLine>(root).Single();

	private static string PreviewText(MarqueeLine line) => Find<TextBlock>(line).Single().Text;

	[Fact]
	public void StreamingPreviewRunsTheLatestLineEveryFrame()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var view = new AssistantMessageView(Live("先看一下目录结构", streaming: true), _ => { }, guard);
		Pump(view.View);
		var line = PreviewLine(view.View);
		Assert.True(line.FollowTail, "流式思考的单行预览必须处于尾随（跑马灯）状态");
		Assert.Equal("先看一下目录结构", PreviewText(line));

		// 新一行写进来：预览立刻跟到最新一行，而不是停在首行不动。
		view.Update(Live("先看一下目录结构\n再看 entry 的投影方式", streaming: true));
		Pump(view.View);
		Assert.Equal("再看 entry 的投影方式", PreviewText(line));

		view.Update(Live("先看一下目录结构\n再看 entry 的投影方式\n最后改预览取行策略", streaming: true));
		Pump(view.View);
		Assert.Equal("最后改预览取行策略", PreviewText(line));

		// 同一行内继续追加也要实时推进：跑马灯的意义就是「新字一直露出来」。
		view.Update(Live("先看一下目录结构\n再看 entry 的投影方式\n最后改预览取行策略，加上尾部跟随", streaming: true));
		Pump(view.View);
		Assert.Equal("最后改预览取行策略，加上尾部跟随", PreviewText(line));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void SettledPreviewGoesBackToTheFirstLine()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var view = new AssistantMessageView(Live("第一行是摘要\n后面还有几行", streaming: true), _ => { }, guard);
		Pump(view.View);
		Assert.True(PreviewLine(view.View).FollowTail);

		// 思考结束：尾随关掉，摘要回到从头部读起。
		var settled = Live("第一行是摘要\n后面还有几行", streaming: false) with { IsStreaming = false };
		view.Update(settled);
		Pump(view.View);
		var line = PreviewLine(view.View);
		Assert.False(line.FollowTail, "思考定稿后不再跑马灯");
		Assert.Equal("第一行是摘要", PreviewText(line));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void RecycledRowRecomputesThePreviewForAnotherMessage()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var view = new AssistantMessageView(Live("上一条的首行\n上一条的次行", streaming: false), _ => { }, guard);
		Pump(view.View);
		Assert.Equal("上一条的首行", PreviewText(PreviewLine(view.View)));

		// 行视图被回收给另一条消息：首行缓存在身份变化时必须作废，否则新行穿着上一条的摘要。
		view.Update(Live("新一条的首行", streaming: false, id: "entry/2") with { Timestamp = 2 });
		Pump(view.View);
		Assert.Equal("新一条的首行", PreviewText(PreviewLine(view.View)));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void MarqueeKeepsTheLineTailAtTheRightEdgeAndTheHeadClipped()
	{
		var text = new TextBlock()
			.Text(string.Join("", Enumerable.Repeat("思考中的很长一行内容", 40)))
			.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro);
		var marquee = new MarqueeLine(text) { FollowTail = true };
		marquee.Measure(new Size(ColumnWidth, double.PositiveInfinity));
		marquee.Arrange(new Rect(0, 0, ColumnWidth, 16));

		// 容器只占可用宽度：长行不能把外层 Grid 的列撑开。
		Assert.InRange(marquee.Bounds.Width, ColumnWidth - 0.5, ColumnWidth + 0.5);
		Assert.True(text.Bounds.Width > ColumnWidth, "跟随态要按完整行宽排版才有行尾可贴");
		// 行尾贴右边缘，多出来的部分落在左边（由 ClipToBounds 切掉）。
		Assert.InRange(text.Bounds.Right, marquee.Bounds.Right - 0.5, marquee.Bounds.Right + 0.5);
		Assert.True(text.Bounds.X < marquee.Bounds.X, "左侧溢出必须是负偏移");

		// 定稿态回到行首，宽度交回可用空间，由 TextBlock 自己截省略号。
		marquee.FollowTail = false;
		marquee.Measure(new Size(ColumnWidth, double.PositiveInfinity));
		marquee.Arrange(new Rect(0, 0, ColumnWidth, 16));
		Assert.InRange(text.Bounds.X, marquee.Bounds.X - 0.5, marquee.Bounds.X + 0.5);
		Assert.InRange(text.Bounds.Width, ColumnWidth - 0.5, ColumnWidth + 0.5);
	}

	[Fact]
	public void PacedPreviewTypesCharactersInsteadOfJumpingWholeChunks()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		// 没窗口就没有帧时钟：单测里手动强制走「有节奏」那条通道。
		var view = new AssistantMessageView(Live("", streaming: true), _ => { }, guard) { PaceStreamForTest = true };
		Pump(view.View);
		var line = PreviewLine(view.View);

		// 一次到达一整块 delta：本帧只能露出开头几个字，剩下的排队逐帧打。
		view.Update(Live("第一段思考内容一二三四五六七八九十", streaming: true));
		Pump(view.View);
		var typed = PreviewText(line);
		Assert.True(typed.Length > 0, "首个 delta 就要开始出字");
		Assert.True(typed.Length < "第一段思考内容一二三四五六七八九十".Length, $"一帧不该把整块打完，实际「{typed}」");

		// 继续推帧：字数单调增长，最终追平到整行。
		const string whole = "第一段思考内容一二三四五六七八九十";
		for (var frame = 0; frame < 60 && typed.Length < whole.Length; frame++)
		{
			view.PumpStreamFrameForTest();
			Pump(view.View);
			Assert.True(PreviewText(line).Length >= typed.Length, "打字机不能后退");
			typed = PreviewText(line);
		}
		Assert.Equal(whole, typed);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void HeadlessPreviewRevealsTheWholeLineAtOnce()
	{
		// PaceStreamForTest 不开：没时钟可摊平，预览直接显示完整一行（历史行为，单测与自检依赖它）。
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var view = new AssistantMessageView(Live("一帧就到齐的整块内容", streaming: true), _ => { }, guard);
		Pump(view.View);
		Assert.Equal("一帧就到齐的整块内容", PreviewText(PreviewLine(view.View)));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandedThinkingBodyTypesTheRevealedPrefixPerFrame()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var view = new AssistantMessageView(Live("第一段思考", streaming: true), _ => { }, guard) { PaceStreamForTest = true };
		Pump(view.View);
		Find<Expander>(view.View).Single().IsExpanded = true;
		Pump(view.View);
		var markdown = Find<MarkdownView>(view.View).Single(candidate => candidate.IsStreaming);
		const string whole = "第一段思考\n第二段内容一二三四五六七八九十";

		view.Update(Live(whole, streaming: true));
		Pump(view.View);
		Assert.True(markdown.Source!.Length < whole.Length, $"展开的思考正文也要逐帧长，实际一帧就到齐：{markdown.Source.Length}");
		for (var frame = 0; frame < 60 && markdown.Source!.Length < whole.Length; frame++) view.PumpStreamFrameForTest();
		Assert.Equal(whole, markdown.Source);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void AnswerBodyTypesPerFrameAndSettledRowsShowEverything()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var streaming = new ChatEntry
		{
			Role = ChatRole.Assistant,
			Text = "回答内容一二三四五六七八九十",
			Thinking = string.Empty,
			EntryId = "live-assistant/2",
			Timestamp = 2,
			IsStreaming = true,
			Compact = true,
		};
		// 构造器里的首次 Update 跑在 PaceStreamForTest 之前（对象初始化器只能在构造后生效），
		// 所以先用空行建视图，再把流式文本喂进去：这才是真实流式的第一帧到后续帧的顺序。
		var view = new AssistantMessageView(streaming with { Text = string.Empty }, _ => { }, guard) { PaceStreamForTest = true };
		Pump(view.View);
		view.Update(streaming);
		Pump(view.View);
		var markdown = Find<MarkdownView>(view.View).Single();
		Assert.True(markdown.Source!.Length < streaming.Text.Length, $"流式回答也按帧逐字推进，实际 {markdown.Source.Length}");
		for (var frame = 0; frame < 60 && markdown.Source!.Length < streaming.Text.Length; frame++) view.PumpStreamFrameForTest();
		Assert.Equal(streaming.Text, markdown.Source);

		// 定稿帧直接全量：历史行同理，打开旧会话不能把整篇从头打一遍。
		view.Update(streaming with { IsStreaming = false });
		Pump(view.View);
		Assert.Equal(streaming.Text, markdown.Source);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void TypewriterThrottlesLongContentAndNeverRetypesExistingBulk()
	{
		// 短内容逐帧推进：一帧一个字也看得见，不该被间隔闸拦掉。
		var shortRun = new Typewriter();
		shortRun.Set(Rep("一", 60), animated: true);
		Assert.True(shortRun.Advance());
		Assert.True(shortRun.Advance(), "8K 字以内每帧都该推进");

		// 长内容降频：同一行从零长到 8K 档以上后，同一时刻的第二次推进要被拦下（少一次尾块重排）。
		var longRun = new Typewriter();
		longRun.Set(Rep("字", 100), animated: true);
		Assert.True(longRun.Advance(), "从零开始的行要逐字");
		longRun.Set(Rep("字", 9000), animated: true);
		Assert.True(longRun.Advance());
		Assert.False(longRun.Advance(), "长文本要按 16ms 降频，不能每帧都重排尾块");

		// 视图接手已有存量（回收后重建的行）：整段直接显示，不能把半篇旧文本重打一遍。
		var born = new Typewriter();
		born.Set(Rep("字", 4000), animated: true);
		Assert.Equal(4000, born.Shown);
		Assert.True(born.CaughtUp);

		// 从零开始的行才逐字。
		var live = new Typewriter();
		live.Set("先看一下目录结构", animated: true);
		Assert.True(live.Advance());
		Assert.True(live.Shown < "先看一下目录结构".Length, "小步推进才是打字机");
	}

	[Fact]
	public void LatestLineWindowsTheTailInsteadOfScanningTheWholeReveal()
	{
		// 不带换行的超长可见前缀：只看行尾那一窗，不回扫整篇（否则每帧一次全篇反向扫描）。
		var tail = ThinkingPreview.LatestLine(string.Concat(Enumerable.Repeat("推", 50_000)), 50_000);
		Assert.True(tail.Length <= 600, $"行尾必须封顶，实际 {tail.Length}");
		// 换行落在窗口里时仍然精确到行首。
		Assert.Equal("第二行", ThinkingPreview.LatestLine("第一行内容\n第二行", 10));
		// 打字机只走到半行时，预览就只露出那半个行尾。
		Assert.Equal("第二行刚写出", ThinkingPreview.LatestLine("第一行内容\n第二行刚写出来", "第一行内容\n第二行刚写出".Length));
	}

	[Fact]
	public void TypewriterCatchesUpAndNeverSplitsASurrogatePair()
	{
		var type = new Typewriter();
		type.Set("abcdefgh", animated: true);
		type.Advance();
		Assert.True(type.Shown is > 0 and < 8, "逐帧推进，一帧不该打完");
		while (type.Advance()) { }
		Assert.Equal(8, type.Shown);

		// 没动画通道（无窗口）：直接全露出。
		type.Set("立即显示", animated: false);
		Assert.Equal(4, type.Shown);

		// 积压封顶：落下太远会补跳到尾部附近，跑马灯才不会停在半句上重打。
		type.Set(new string('字', 4000), animated: true);
		for (var frame = 0; frame < 60; frame++) type.Advance();
		Assert.True(4000 - type.Shown <= 264, $"滞后必须封在尾部附近，实际落下 {4000 - type.Shown}");

		// 代理对：切点不能落在半个 emoji 上。
		type.Reset();
		type.Set(string.Concat(Enumerable.Repeat("🙂", 20)), animated: true);
		for (var frame = 0; frame < 40; frame++)
		{
			type.Advance();
			var shown = string.Concat(Enumerable.Repeat("🙂", 20))[..type.Shown];
			Assert.False(shown.Length % 2 != 0, "预览前缀不能把 emoji 拆成半截");
		}
	}

	[Fact]
	public void LatestLineStaysBoundedAndKeepsTheTail()
	{
		// 不带换行的长思考：跟尾只能露出行尾，取行也要封顶，否则每帧排版整行。
		var longLine = string.Join("", Enumerable.Repeat("推理", 4000)) + "结论";
		var line = ThinkingPreview.LatestLine(longLine);
		Assert.True(line.Length <= 600, $"跟尾预览必须封住行尾长度，实际 {line.Length}");
		Assert.EndsWith("结论", line, StringComparison.Ordinal);
		// 代理对不能被切坏：行首不能是半个 emoji（低代理），行尾不能留下孤立的高代理。
		Assert.False(char.IsLowSurrogate(line[0]), "行首不能落在代理对中间");
		Assert.False(char.IsHighSurrogate(line[^1]), "行尾不能留下半个代理对");
		var emoji = ThinkingPreview.LatestLine(string.Concat(Enumerable.Repeat("🙂", 3000)) + "尾");
		Assert.False(char.IsLowSurrogate(emoji[0]), "emoji 行被封顶时也不能拆字");

		Assert.Equal("b", ThinkingPreview.LatestLine("a\nb"));
		Assert.Equal("b", ThinkingPreview.LatestLine("a\r\nb\n \n"));
		Assert.Equal(string.Empty, ThinkingPreview.LatestLine("   \n\n"));
	}

	[Fact]
	public void ShortStreamingLineStaysAtTheLeftEdge()
	{
		// 行还没写满可用宽度时不该往右贴：PiDeck 的 scrollLeft = scrollWidth - clientWidth 在这种情况下是 0。
		var text = new TextBlock().Text("刚起头").FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro);
		var marquee = new MarqueeLine(text) { FollowTail = true };
		marquee.Measure(new Size(ColumnWidth, double.PositiveInfinity));
		marquee.Arrange(new Rect(0, 0, ColumnWidth, 16));
		Assert.InRange(text.Bounds.X, marquee.Bounds.X - 0.5, marquee.Bounds.X + 0.5);
	}
}
