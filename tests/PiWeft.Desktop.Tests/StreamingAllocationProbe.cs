using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 流式渲染管线的分配探针：视图模型投影、过程投影与时间线更新各自每帧分配多少。
/// 这些数字直接对应诊断面板的「每帧分配」与 LOH 压力，优化前后用它对照。
/// </summary>
[Collection("mewui-controls")]
public sealed class StreamingAllocationProbe(ITestOutputHelper output)
{
	[Fact]
	public async Task MeasureStreamingPipelineAllocations()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-alloc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var manager = SessionManager.InMemory(root);
			for (var i = 0; i < 20; i++)
			{
				manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"question {i}") });
				manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = $"answer {i}" }] });
			}
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var streams = new Queue<Func<Model, AssistantMessageEventStream>>();
			streams.Enqueue(model => FakeStreams.Text(model, LongReply(4000), gate.Task));
			var session = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root, Model = FakeStreams.Model(), StreamFn = (m, _, _) => streams.Dequeue()(m),
				Session = manager, Persist = false, Skills = [], Tools = [],
			});
			using var chat = new ChatViewModel();
			chat.Attach(session);
			var send = Task.Run(() => chat.SendAsync("长回复"));
			// 等第一半 delta 到达（gate 之前），此时处于流式中段。
			await WaitForAsync(() => chat.IsStreaming && chat.Entries().Any(static row => row.IsStreaming));
			var streamingRows = chat.Entries();
			Assert.Contains(streamingRows, row => row.IsStreaming);

			// ── 视图模型：一次「事件作废后的 Entries() 重建」（= 一帧的投影输入）。
			// 真实流式里每个 delta 事件都会作废快照，渲染帧必然重建一次。
			var snapshotField = typeof(ChatViewModel).GetField("_rowsSnapshot",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
			IReadOnlyList<ChatEntry> InvalidateAndEntries()
			{
				snapshotField.SetValue(chat, null);
				return chat.Entries();
			}
			GC.Collect();
			var before = GC.GetAllocatedBytesForCurrentThread();
			var rows1 = InvalidateAndEntries();
			var rows2 = InvalidateAndEntries();
			var entriesBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 2;
			output.WriteLine($"Entries() rebuild: {entriesBytes:N0} bytes/frame (rows={rows1.Count}, streaming text={rows1[^1].TextLength:N0} chars)");

			// ── 过程投影：尾部一遍 Project 的成本。
			var projector = new ExecutionProcessProjector();
			projector.Project(rows1, true);
			GC.Collect();
			before = GC.GetAllocatedBytesForCurrentThread();
			var projected = projector.Project(rows1, true);
			var projectBytes = GC.GetAllocatedBytesForCurrentThread() - before;
			output.WriteLine($"Project(tail): {projectBytes:N0} bytes/frame (rows={projected.Count})");

			// ── 时间线 + Markdown：连续 25 帧 SetEntries + 布局。
			// 每帧「增长后的文本」在测量区外预先构造（等价于 Entries() 的 ToString 产物），
			// 探针自身的字符串拼接不计入测量结果。
			using var focus = new TextFocusScrollGuard(new ScrollViewer());
			using var timeline = new ConversationTimeline(entry => entry.Role switch
			{
				ChatRole.Process => new ExecutionProcessView(entry, _ => new Border().Height(60), focus).View,
				ChatRole.Assistant => new AssistantMessageView(entry, _ => { }, focus).View,
				_ => new Border().Height(60).Child(new TextBlock().Text(entry.Text)),
			});
			var frames = new List<ChatEntry[]>(25);
			for (var frame = 0; frame < 25; frame++)
			{
				var next = chat.Entries().ToArray();
				var live = next[^1];
				next[^1] = live with { Text = live.Text + Paragraph(frame) };
				frames.Add([.. next.Select(Adapt)]);
			}
			timeline.SetEntries(frames[0]);
			Layout(timeline.View);
			GC.Collect();
			var before2 = GC.GetAllocatedBytesForCurrentThread();
			var watch = Stopwatch.StartNew();
			foreach (var frameRows in frames)
			{
				timeline.SetEntries(frameRows);
				Layout(timeline.View);
			}
			var timelineBytes = (GC.GetAllocatedBytesForCurrentThread() - before2) / 25;
			output.WriteLine($"Timeline+Markdown: {timelineBytes:N0} bytes/frame over 25 frames in {watch.Elapsed.TotalMilliseconds:0} ms");

			output.WriteLine($"TOTAL estimate: {entriesBytes + projectBytes + timelineBytes:N0} bytes/frame");
			// 回归护栏：整条流式管线（行投影 + 过程投影 + 时间线 + 增量 Markdown）每帧只允许
			// 常数级垃圾。流式正文走快照（来源 + 长度），不再按回复长度每帧物化整篇。
			Assert.InRange(timelineBytes, 0, 4096);
			Assert.InRange(projectBytes, 0, 4096);
			Assert.InRange(entriesBytes, 0, 32 * 1024);
			// 冷路径（复制/导出）仍能整篇物化，内容与快照长度一致。
			Assert.Equal(rows1[^1].TextLength, rows1[^1].Text.Length);
			gate.SetResult();
			await send.WaitAsync(TimeSpan.FromSeconds(10));
			await WaitForAsync(() => !chat.IsStreaming);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static string LongReply(int paragraphs) =>
		string.Join("\n\n", Enumerable.Range(0, paragraphs).Select(i =>
			$"## 段落 {i}\n\n这是第 {i} 段的正文，包含一句普通文本、`inline code` 与一个[链接](https://example.com/{i})，长度接近真实回复的平均段落。"));

	private static string Paragraph(int i) => $"\n\n追加段落 {i}，模拟每帧的流式增量。";

	/// <summary>会话里的行可以直接进时间线；测试会话的 EntryId 语义与桌面一致。</summary>
	private static ChatEntry Adapt(ChatEntry entry) => entry;

	private static void Layout(UIElement view, double width = 700, double height = 500)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		var timeout = Stopwatch.StartNew();
		while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
		Assert.True(condition());
	}
}
