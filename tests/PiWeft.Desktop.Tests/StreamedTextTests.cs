using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 流式文本快照：整篇物化不再发生在每帧投影里（诊断面板「每帧分配」的主项），
/// 但正文、思考、预览与增量 Markdown 的结果必须与普通字符串完全一致。
/// </summary>
[Collection("mewui-controls")]
public sealed class StreamedTextTests
{
	private static StreamingBuffer Buffer(string text)
	{
		var buffer = new StreamingBuffer();
		buffer.Append(text);
		return buffer;
	}

	private static StreamedText Snapshot(StreamingBuffer buffer, int generation = 1) => new(buffer, generation);

	private static ChatEntry Live(StreamingBuffer body, StreamingBuffer thinking, int generation = 1) => new ChatEntry
	{
		Role = ChatRole.Assistant, Text = string.Empty, Thinking = string.Empty, EntryId = "live", Timestamp = 1,
		IsThinking = true, IsStreaming = true,
		StreamedText = null, StreamedThinking = null,
	} with
	{
		StreamedText = Snapshot(body, generation),
		StreamedThinking = Snapshot(thinking, generation),
	};

	[Fact]
	public void TextMaterializesLazilyAndRemainsACompatibleString()
	{
		var body = new StreamingBuffer();
		body.Append("第一段");
		var thinking = new StreamingBuffer();
		thinking.Append("思考一行");
		var entry = Live(body, thinking);
		// 快照长度是 O(1)，取正文才物化；重复取返回同一份缓存。
		Assert.Equal(3, entry.TextLength);
		Assert.Equal(4, entry.ThinkingLength);
		Assert.Equal("第一段", entry.Text);
		Assert.Same(entry.Text, entry.Text);
		Assert.Equal("思考一行", entry.Thinking);

		// 快照之后来源继续增长：旧快照仍表示它自己那一刻的内容（投影不会看到未来的字）。
		body.Append("，第二段");
		Assert.Equal("第一段", entry.Text);
		Assert.Equal(7, Snapshot(body).Length);
		Assert.Equal("第一段，第二段", Snapshot(body).Materialize());
	}

	[Fact]
	public void ExplicitTextWinsOverTheStreamAndEmptyTextClearsIt()
	{
		var entry = Live(Buffer("正文"), Buffer("思考"));
		Assert.Equal("替换", (entry with { Text = "替换" }).Text);
		// 投影把正文抹掉（空字符串）时不能回落到流式来源。
		var stripped = entry with { Text = string.Empty };
		Assert.Equal(string.Empty, stripped.Text);
		Assert.Equal(0, stripped.TextLength);
		// 思考同理：把思考抹掉后行里不再有思考文本。
		Assert.Equal(string.Empty, (entry with { Thinking = string.Empty }).Thinking);
		Assert.Equal(0, (entry with { Thinking = string.Empty }).ThinkingLength);
	}

	[Fact]
	public void SnapshotsSurviveAResetOfTheLiveBuffer()
	{
		// 会话线程在新回合清空缓冲，UI 线程可能还握着上一回合的快照（打字机/帧泵晚到一帧）。
		// 片段表只追加、Reset 换新表，所以旧快照读到的仍是它自己的内容，不会越界也不会串到新回合。
		var body = Buffer("上一回合的正文");
		var snapshot = Snapshot(body);
		body.Reset();
		body.Append("新回合");
		Assert.Equal(7, snapshot.Length);
		Assert.Equal("上一回合的正文", snapshot.Materialize());
		Assert.Equal("合的", snapshot.Slice(3, 2));
		Assert.Equal('上', snapshot.At(0));
		// 新回合不等于旧快照的继续：增量 Markdown 只能整篇重喂，不能把新文本接到旧前缀后面。
		var next = new StreamText(Snapshot(body, generation: 2));
		Assert.False(next.Continues(new StreamText(snapshot)));
	}

	[Fact]
	public void RowEqualityComparesStreamSnapshotsWithoutMaterializing()
	{
		var body = Buffer("abc");
		var thinking = Buffer(string.Empty);
		var first = Live(body, thinking);
		// 同一来源、同一回合、同一长度 = 内容相同（不必物化比较）。
		var same = Live(body, thinking);
		Assert.True(ConversationTimeline.SameEntry(first, same));
		// 长度增长就是新内容：流式行必须被视为变化并重绑。
		body.Append("def");
		var grown = Live(body, thinking);
		Assert.False(ConversationTimeline.SameEntry(first, grown));
		// 新一轮（代号不同）即使长度相同也不能当成同一行。
		var nextTurn = Live(body, thinking, generation: 2);
		Assert.False(ConversationTimeline.SameEntry(grown, nextTurn));
	}

	[Fact]
	public void ThinkingPreviewReadsOnlyTheTailWindowOfAStream()
	{
		// 很长的流式思考：预览只取行尾窗口，且与普通字符串版本逐字一致。
		var text = Buffer(string.Join("", Enumerable.Repeat("推理", 4000)));
		text.Append("\n最新一行结论");
		var snapshot = Snapshot(text);
		var fromStream = ThinkingPreview.LatestLine(new StreamText(snapshot), snapshot.Length);
		var fromString = ThinkingPreview.LatestLine(snapshot.Materialize());
		Assert.Equal(fromString, fromStream);
		Assert.Equal("最新一行结论", fromStream);

		// 打字机只推到半行时同理：看的是「已经露出的那几个字」，不是整篇。
		var partialBuffer = Buffer(string.Join("", Enumerable.Repeat("推理", 4000)));
		partialBuffer.Append("\n最新一行");
		var partial = Snapshot(partialBuffer);
		Assert.Equal("最新一行", ThinkingPreview.LatestLine(new StreamText(partial), partial.Length));
	}

	[Fact]
	public void StreamingMarkdownIsFedIncrementallyThroughTheSnapshot()
	{
		// 与真实流式一致：每帧只增长一段，Markdown 走增量 Append，最终与整篇赋值一致。
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var body = new StreamingBuffer();
		var entry = Live(body, new StreamingBuffer());
		var view = new AssistantMessageView(entry, _ => { }, focus) { PaceStreamForTest = true };
		var paragraphs = Enumerable.Range(0, 40).Select(i => $"## 段落 {i}\n\n这是第 {i} 段正文，带 `inline` 与 **粗体**。").ToArray();
		foreach (var paragraph in paragraphs)
		{
			body.Append(paragraph);
			body.Append("\n\n");
			entry = Live(body, new StreamingBuffer());
			view.Update(entry);
			for (var frame = 0; frame < 200; frame++) view.PumpStreamFrameForTest();
		}
		var markdown = Find<MarkdownView>(view.View).Single();
		Assert.Equal(Snapshot(body).Materialize().TrimEnd(), markdown.Source!.TrimEnd());
		view.Dispose();
	}
}
