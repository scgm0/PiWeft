using System.Diagnostics;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;

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
