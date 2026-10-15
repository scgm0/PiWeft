using System.Diagnostics;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.Desktop;

// Isolated catalog benchmark. The typed scan matches the old catalog's JSONL parsing workload.
var root = Path.Combine(Path.GetTempPath(), "piweft-catalog-bench-" + Guid.NewGuid().ToString("N"));
var previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
try
{
	Directory.CreateDirectory(root);
	Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", Path.Combine(root, "agent"));
	for (var i = 0; i < 40; i++)
	{
		var manager = SessionManager.Create(root);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"Question {i}") });
		for (var j = 0; j < 50; j++)
			manager.AppendMessage(new ToolResultMessage { ToolName = "read", ToolCallId = $"call{j}", Content = [new TextContent { Text = new string('x', 8192) }] });
		manager.AppendSessionInfo($"Session {i}");
	}
	var paths = Directory.GetFiles(Path.Combine(root, "agent", "sessions"), "*.jsonl", SearchOption.AllDirectories);
	var cached = new SessionCatalog();
	TypedScan();
	cached.List(root);
	Measure("legacyTypedScan", TypedScan);
	Measure("coldSummaryScan", () => new SessionCatalog().List(root).Count);
	Measure("cachedSummaryScan", () => cached.List(root).Count);

	int TypedScan()
	{
		foreach (var path in paths)
			foreach (var line in File.ReadLines(path))
				if (line.Length > 0 && line[0] == '{') _ = SessionJson.ParseLine(line);
		return paths.Length;
	}
	void Measure(string name, Func<int> action)
	{
		var timings = new List<double>();
		long allocations = 0;
		for (var i = 0; i < 7; i++)
		{
			GC.Collect();
			var start = GC.GetAllocatedBytesForCurrentThread();
			var watch = Stopwatch.StartNew();
			if (action() != 40) throw new InvalidOperationException("Catalog lost fixture sessions");
			watch.Stop();
			allocations += GC.GetAllocatedBytesForCurrentThread() - start;
			timings.Add(watch.Elapsed.TotalMilliseconds);
		}
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			writer.WriteString("name", name);
			writer.WriteNumber("sessions", 40);
			writer.WriteNumber("rows", 2000);
			writer.WriteNumber("medianMs", timings.Order().ElementAt(3));
			writer.WriteNumber("allocatedBytes", allocations / 7);
			writer.WriteEndObject();
		}
		Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
	}
}
finally
{
	Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", previousAgentDir);
	if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
