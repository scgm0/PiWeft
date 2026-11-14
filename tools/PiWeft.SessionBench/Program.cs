using System.Diagnostics;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;

// 第一个参数选模式：缺省跑原来的用量统计刷新，`memory` 跑大会话打开的稳态堆。
if (args.Length > 0 && args[0] is "memory" or "large")
{
	return LargeSessionBenchmarks.Run(args.Length > 1 ? args[1] : null);
}

var manager = SessionManager.InMemory(Environment.CurrentDirectory);
for (var i = 0; i < 5_000; i++)
{
	manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("benchmark prompt") });
	manager.AppendMessage(new AssistantMessage
	{
		Content = [new TextContent { Text = "benchmark response" }],
		StopReason = StopReason.Stop,
		Usage = new Usage { Input = 100, Output = 20, CacheRead = 800, CacheWrite = 100, TotalTokens = 1020 },
	});
}
using var session = new CodingAgentSession(new CodingAgentSessionOptions
{
	Cwd = manager.Cwd,
	Session = manager,
	Model = new Model { Id = "bench", Provider = "test", Api = "test", ContextWindow = 128_000 },
	StreamFn = static (_, _, _) => throw new InvalidOperationException("No network in benchmark"),
	Tools = [],
	Skills = [],
	PromptTemplates = [],
});
long checksum = 0;
for (var i = 0; i < 10; i++) Refresh();
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
var allocated = GC.GetAllocatedBytesForCurrentThread();
var started = Stopwatch.GetTimestamp();
const int iterations = 1000;
for (var i = 0; i < iterations; i++) Refresh();
var elapsed = Stopwatch.GetElapsedTime(started);
allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
Console.WriteLine($"Entries={manager.EntryCount}; refreshes={iterations}; elapsed={elapsed.TotalMilliseconds:F2} ms; allocated={allocated:N0} bytes; checksum={checksum}");

void Refresh()
{
	checksum += session.UsageTotals().Tokens;
	checksum += session.UsageBreakdown().Input;
	checksum += session.ContextTokens().Tokens;
}

return 0;

/// 大会话打开的内存基准：600 条 50 KB 的工具结果，量打开耗时与打开后的稳态托管堆。
internal static class LargeSessionBenchmarks
{
	private const int ToolResults = 600;
	private const int ResultBytes = 50 * 1024;

	public static int Run(string? existing)
	{
		var directory = Path.Combine(Path.GetTempPath(), "piweft-session-bench");
		Directory.CreateDirectory(directory);
		// 传入路径就只打开它：A/B 对比必须是同一份文件。
		var path = existing is { Length: > 0 } && File.Exists(existing) ? existing : Generate(directory);
		var size = new FileInfo(path).Length;

		// 预热一次，把 JIT、类型加载与首次读取的页缓存从计时里拿掉。
		Warmup(path);

		var samples = new List<(double Milliseconds, long Bytes, long Allocated, int Gen2)>();
		SessionManager? opened = null;
		for (var round = 0; round < 5; round++)
		{
			var baseline = Settled();
			var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var gen2Before = GC.CollectionCount(2);
			var started = Stopwatch.GetTimestamp();
			opened = SessionManager.Open(path);
			var elapsed = Stopwatch.GetElapsedTime(started);
			var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
			var gen2 = GC.CollectionCount(2) - gen2Before;
			// 打开后的稳态：Retained 会让所有能回收的都回收，剩下的才是常驻。
			var retained = Settled() - baseline;
			samples.Add((elapsed.TotalMilliseconds, retained, allocated, gen2));
			if (round == 0)
			{
				Console.WriteLine($"entries={opened.EntryCount}; 条目数={opened.Entries.Count}");
			}

			// 下一轮前拆掉引用，否则上一份会话会被当成常驻算进基线。
			GC.KeepAlive(opened);
			opened = null;
		}

		samples.Sort(static (left, right) => left.Milliseconds.CompareTo(right.Milliseconds));
		var median = samples[samples.Count / 2];
		Console.WriteLine($"file={path}");
		Console.WriteLine($"file-size={size:N0} bytes ({size / 1024.0 / 1024.0:F2} MiB)");
		Console.WriteLine($"open-ms-median={median.Milliseconds:F1}; heap={median.Bytes:N0} bytes ({median.Bytes / 1024.0 / 1024.0:F2} MiB)");
		Console.WriteLine($"open-ms=[{string.Join(", ", samples.Select(static sample => sample.Milliseconds.ToString("F1")))}]");
		Console.WriteLine($"heap=[{string.Join(", ", samples.Select(static sample => (sample.Bytes / 1024.0 / 1024.0).ToString("F2")))}] MiB");
		Console.WriteLine($"alloc-per-open=[{string.Join(", ", samples.Select(static sample => (sample.Allocated / 1024.0 / 1024.0).ToString("F2")))}] MiB; gen2=[{string.Join(", ", samples.Select(static sample => sample.Gen2))}]");
		Console.WriteLine($"heap-ratio={median.Bytes / (double)size:F2}x");
		Console.WriteLine($"per-result={median.Bytes / (double)ToolResults:N0} bytes");
		return 0;
	}

	private static long Settled()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		return GC.GetTotalMemory(forceFullCollection: true);
	}

	private static void Warmup(string path)
	{
		var manager = SessionManager.Open(path);
		GC.KeepAlive(manager.EntryCount);
		GC.KeepAlive(manager);
	}

	/// 生成一份普通对话形状的 v3 会话：用户提问、带工具调用的助手回复、50 KB 的工具结果。
	private static string Generate(string directory)
	{
		var manager = SessionManager.Create(directory, sessionDir: directory);
		var text = BuildResultText();
		for (var i = 0; i < ToolResults; i++)
		{
			if (i % 10 == 0)
			{
				manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"benchmark turn {i}") });
			}

			var callId = $"call-{i}";
			manager.AppendMessage(new AssistantMessage
			{
				Content = [new ToolCall { Id = callId, Name = "bash", Arguments = new System.Text.Json.Nodes.JsonObject { ["command"] = "ls" } }],
				StopReason = StopReason.ToolUse,
				Usage = new Usage { Input = 100, Output = 20, TotalTokens = 120 },
			});
			manager.AppendMessage(new ToolResultMessage
			{
				ToolCallId = callId,
				ToolName = "bash",
				Content = [new TextContent { Text = text }],
			});
		}

		return manager.SessionFile!;
	}

	private static string BuildResultText()
	{
		var line = "benchmark tool output line with some ASCII text 0123456789\n";
		var builder = new System.Text.StringBuilder(ResultBytes + line.Length);
		while (builder.Length < ResultBytes)
		{
			builder.Append(line);
		}

		return builder.ToString();
	}
}
