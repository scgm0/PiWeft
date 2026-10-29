using System.Diagnostics;
using System.Reflection;
using Aprillz.MewUI;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class StreamingRenderBudgetTests(ITestOutputHelper output)
{
	private static readonly MethodInfo RenderMethod =
		typeof(SessionTab).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!;

	/// <summary>
	/// 端到端渲染预算：真实 SessionTab + 150K 字流式回复下，每帧 Render 必须是常数级开销。
	/// 回归的是「每帧把累积回复整篇 ToString()」：它让 20 万字的回复每帧分配 ~400 KB，
	/// 进而把 gen0/gen2 GC 拖成 20–400 ms 的长帧（诊断文件里 frame.long draws=0 的那批）。
	/// </summary>
	[Fact]
	public async Task StreamingRenderStaysConstantCostPerFrame()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-tab-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var manager = SessionManager.InMemory(root);
			for (var i = 0; i < 10; i++)
			{
				manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"question {i}") });
				manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = $"answer {i}" }] });
			}
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var streams = new Queue<Func<Model, AssistantMessageEventStream>>();
			streams.Enqueue(model => FakeStreams.Text(model, LongReply(3000), gate.Task));
			var session = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root, Model = FakeStreams.Model(), StreamFn = (m, _, _) => streams.Dequeue()(m),
				Session = manager, Persist = false, Skills = [], Tools = [],
			});
			using var tab = new SessionTab(session, [session.Model], "probe");
			ConversationPerformanceTests.Layout(tab);
			var send = Task.Run(() => tab.Chat.SendAsync("长回复"));
			var timeout = Stopwatch.StartNew();
			while (!(tab.Chat.IsStreaming && tab.Chat.Entries().Any(static row => row.IsStreaming)) && timeout.Elapsed < TimeSpan.FromSeconds(5))
				await Task.Delay(20);
			var frameMs = new List<double>();
			var allocBytes = new List<long>();
			var rows = 0;
			for (var frame = 0; frame < 120; frame++)
			{
				var before = GC.GetAllocatedBytesForCurrentThread();
				var watch = Stopwatch.StartNew();
				RenderMethod.Invoke(tab, [false]);
				frameMs.Add(watch.Elapsed.TotalMilliseconds);
				allocBytes.Add(GC.GetAllocatedBytesForCurrentThread() - before);
				ConversationPerformanceTests.Layout(tab);
				rows = tab.Chat.Entries().Count;
				await Task.Delay(5);
			}
			static (double p50, double p95, double max) Stats(List<double> values)
			{
				var sorted = values.OrderBy(static v => v).ToArray();
				return (sorted[sorted.Length / 2], sorted[(int)(sorted.Length * 0.95)], sorted[^1]);
			}
			var s = Stats(frameMs);
			var medianAlloc = allocBytes.OrderBy(static b => b).ElementAt(allocBytes.Count / 2);
			var textLength = tab.Chat.Entries().LastOrDefault(static r => r.IsStreaming)?.TextLength ?? 0;
			output.WriteLine($"rows={rows} Render(): p50={s.p50:0.00} p95={s.p95:0.00} max={s.max:0.00} ms; alloc/frame={medianAlloc:N0} B; text={textLength:N0} chars");
			Assert.True(textLength > 100_000, $"样本要够长：{textLength}");
			// 分配与回复长度无关：整篇物化只在复制/导出这类冷路径发生。
			Assert.InRange(medianAlloc, 0, 16 * 1024);
			// 时间只做粗略护栏（负载抖动大）：真正的回归守卫是上面的分配预算。
			Assert.True(s.p50 < 5, $"Render 中位耗时超预算：{s.p50:0.00} ms");
			gate.SetResult();
			await send.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally
		{
			Diagnostics.SetEnabled(false);
			Directory.Delete(root, recursive: true);
		}
	}

	private static string LongReply(int paragraphs) =>
		string.Join("\n\n", Enumerable.Range(0, paragraphs).Select(i =>
			$"## 段落 {i}\n\n这是第 {i} 段的正文，包含一句普通文本、`inline code` 与一个[链接](https://example.com/{i})，长度接近真实回复的平均段落。"));
}
