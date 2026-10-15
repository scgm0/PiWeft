using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>在相同会话副本上逐字段对照真实 pi 的 RPC 响应，允许新增字段但不能遗漏上游字段。</summary>
public class PiBinaryRpcParityTests
{
	private static string? FindPi()
	{
		var path = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}

		var names = OperatingSystem.IsWindows() ? new[] { "pi.cmd", "pi.exe", "pi" } : ["pi"];
		foreach (var directory in path.Split(Path.PathSeparator))
		{
			if (directory.Length == 0)
			{
				continue;
			}

			foreach (var name in names)
			{
				var candidate = Path.Combine(directory, name);
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}
		}

		return null;
	}

	/// <summary>确定性的临时会话，不依赖用户的真实会话。</summary>
	private sealed class SessionFixture : IDisposable
	{
		public SessionFixture()
		{
			Root = Path.Combine(Path.GetTempPath(), "pi-parity-fixture-" + Guid.NewGuid().ToString("N"));
			var cwd = Path.Combine(Root, "project");
			Directory.CreateDirectory(cwd);

			var manager = SessionManager.Create(
				cwd,
				Path.Combine(Root, "sessions"),
				id: "parityfixture",
				now: DateTimeOffset.Parse("2025-01-02T03:04:05Z"));

			manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("把输出改成 UTF-8。") });
			manager.AppendMessage(new AssistantMessage
			{
				Content = [new TextContent { Text = "已经改成 UTF-8，读回来是「你好」。" }],
				Api = "openai-completions",
				Provider = "openai",
				Model = "gpt-test",
				StopReason = StopReason.Stop,
			});

			FilePath = manager.SessionFile!;
		}

		public string Root { get; }

		public string FilePath { get; }

		/// <summary>会话 id（写在头部里，文件名只是承载它）。</summary>
		public const string SessionId = "parityfixture";

		/// <summary>复制一份内容相同、文件名不同的会话，供两个实现各读一份。</summary>
		public string Copy(string label)
		{
			var target = System.IO.Path.Combine(
				System.IO.Path.GetDirectoryName(FilePath)!,
				$"2025-01-02T03-04-05-000Z_{label}.jsonl");

			File.Copy(FilePath, target, overwrite: true);
			return target;
		}

		public void Dispose()
		{
			Directory.Delete(Root, recursive: true);
		}
	}

	/// <summary>查询及重试开关命令；启动可能追加思考级别元数据，因此各自使用会话副本。</summary>
	private static readonly string[] Commands =
	[
		"""{"id":"1","type":"get_state"}""",
		"""{"id":"2","type":"get_session_stats"}""",
		"""{"id":"3","type":"get_fork_messages"}""",
		"""{"id":"4","type":"get_last_assistant_text"}""",
		"""{"id":"5","type":"get_entries"}""",
		"""{"id":"6","type":"get_tree"}""",
		"""{"id":"7","type":"get_available_thinking_levels"}""",
		"""{"id":"8","type":"set_auto_retry","enabled":true}""",
		"""{"id":"9","type":"exit"}""",
	];

	[Fact]
	public void FixtureIsExactlyAHeaderAndTwoMessages()
	{
		// 这个测试盯住 fixture 本身：文件里只能有头部 + 两条消息。
		// 一旦写入端偷偷追加别的条目（例如 thinking_level_change），两边实现的条目数就会不一致。
		using var fixture = new SessionFixture();
		var lines = File.ReadAllLines(fixture.FilePath);

		Assert.Equal(3, lines.Length);
		Assert.Contains("\"type\":\"session\"", lines[0]);
		Assert.Contains("\"role\":\"user\"", lines[1]);
		Assert.Contains("\"role\":\"assistant\"", lines[2]);
	}

	[Fact]
	public async Task ReadOnlyCommandsMatchTheReferenceImplementation()
	{
		var pi = FindPi();
		Assert.False(pi is null, "the real pi binary is not installed");

		using var fixture = new SessionFixture();

		// 两个实现各读一份**内容相同**的副本：跑在前面的那个会往会话里追加条目
		// （pi 会记录当前思考级别），共用一份文件会让后跑的一方凭空多出条目。
		var referenceCopy = fixture.Copy("reference");
		var oursCopy = fixture.Copy("ours");

		var reference = await RunPiAsync(pi!, referenceCopy);
		var ours = await RunPortAsync(oursCopy);

		// 写入行为也要一致：打开会话只应追加元数据条目，不该写进消息。
		Assert.Equal(
			File.ReadAllLines(referenceCopy).Length,
			File.ReadAllLines(oursCopy).Length);

		const string sessionId = SessionFixture.SessionId;

		foreach (var command in new[]
		{
			"get_state",
			"get_session_stats",
			"get_fork_messages",
			"get_last_assistant_text",
			"get_entries",
			"get_tree",
			"get_available_thinking_levels",
			"set_auto_retry",
		})
		{
			var expected = ResponseOf(reference, command);
			var actual = ResponseOf(ours, command);

			Assert.True(actual["success"]!.GetValue<bool>(), $"{command} failed in this port: {actual["error"]}");

			// Every field pi reports must be present here with the same shape.
			Compare(command, expected["data"], actual["data"]);
		}

		// The identity fields must match outright, not just in shape.
		Assert.Equal(sessionId, ResponseOf(ours, "get_state")["data"]!["sessionId"]!.GetValue<string>());
		Assert.Equal(
			ResponseOf(reference, "get_state")["data"]!["messageCount"]!.GetValue<int>(),
			ResponseOf(ours, "get_state")["data"]!["messageCount"]!.GetValue<int>());
		var referenceEntries = ResponseOf(reference, "get_entries")["data"]!["entries"]!.AsArray();
		var ourEntries = ResponseOf(ours, "get_entries")["data"]!["entries"]!.AsArray();
		Assert.True(
			referenceEntries.Count == ourEntries.Count,
			$"get_entries 数量不同：pi={referenceEntries.Count} " +
			$"[{string.Join(",", referenceEntries.Select(static e => e!["type"]!.GetValue<string>()))}] " +
			$"我们={ourEntries.Count} [{string.Join(",", ourEntries.Select(static e => e!["type"]!.GetValue<string>()))}] " +
			$"pi 文件={string.Join(",", File.ReadAllLines(referenceCopy).Select(static l => System.Text.Json.Nodes.JsonNode.Parse(l)!["type"]!.GetValue<string>()))} " +
			$"我们文件={string.Join(",", File.ReadAllLines(oursCopy).Select(static l => System.Text.Json.Nodes.JsonNode.Parse(l)!["type"]!.GetValue<string>()))}");
		Assert.Equal(
			ResponseOf(reference, "get_session_stats")["data"]!["userMessages"]!.GetValue<int>(),
			ResponseOf(ours, "get_session_stats")["data"]!["userMessages"]!.GetValue<int>());
		Assert.Equal(
			ResponseOf(reference, "get_last_assistant_text")["data"]!["text"]!.GetValue<string>(),
			ResponseOf(ours, "get_last_assistant_text")["data"]!["text"]!.GetValue<string>());
	}

	/// <summary>Asserts that the port's data has at least pi's fields, with matching shapes where both carry values.</summary>
	private static void Compare(string path, JsonNode? expected, JsonNode? actual)
	{
		if (expected is null)
		{
			return;
		}

		if (expected is JsonObject expectedObject)
		{
			var actualObject = Assert.IsType<JsonObject>(actual, exactMatch: false);
			foreach (var (key, value) in expectedObject)
			{
				Assert.True(
					actualObject.ContainsKey(key),
					$"{path}.{key} is missing from this port's response (pi sends {value?.ToJsonString() ?? "null"})");

				Compare($"{path}.{key}", value, actualObject[key]);
			}

			return;
		}

		if (expected is JsonArray expectedArray)
		{
			var actualArray = Assert.IsType<JsonArray>(actual, exactMatch: false);
			if (expectedArray.Count == 0 || actualArray.Count == 0)
			{
				return;
			}

			// Compare the first element's shape; entries can legitimately have different field sets per type.
			var expectedFirst = expectedArray[0];
			var matching = expectedFirst is JsonObject expectedFirstObject
				? actualArray.FirstOrDefault(node => node is JsonObject candidate &&
					expectedFirstObject.All(pair => candidate.ContainsKey(pair.Key)))
				: actualArray[0];

			Compare($"{path}[0]", expectedFirst, matching ?? actualArray[0]);
		}
	}

	private static JsonObject ResponseOf(List<JsonNode> records, string command) =>
		records.Single(node =>
			node["type"]!.GetValue<string>() == "response" && node["command"]!.GetValue<string>() == command)!
			.AsObject();

	private static async Task<List<JsonNode>> RunPiAsync(string pi, string session)
	{
		var info = new ProcessStartInfo(pi)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.ArgumentList.Add("--mode");
		info.ArgumentList.Add("rpc");
		info.ArgumentList.Add("--session");
		info.ArgumentList.Add(session);
		info.ArgumentList.Add("--no-tools");

		return await RunAsync(info);
	}

	private static async Task<List<JsonNode>> RunPortAsync(string session)
	{
		var info = new ProcessStartInfo("dotnet")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pi.dll"));
		info.ArgumentList.Add("--mode");
		info.ArgumentList.Add("rpc");
		info.ArgumentList.Add("--session");
		info.ArgumentList.Add(session);
		info.ArgumentList.Add("--no-tools");
		info.ArgumentList.Add("--no-mcp");

		// The port has to resolve a model; the session's own choice is used when the catalog knows it.
		var context = SessionManager.Open(session).BuildSessionContext();
		if (context.Model is { } model)
		{
			info.ArgumentList.Add("--model");
			info.ArgumentList.Add($"{model.Provider}/{model.ModelId}");
		}

		return await RunAsync(info);
	}

	private static async Task<List<JsonNode>> RunAsync(ProcessStartInfo info)
	{
		using var process = Process.Start(info)!;

		foreach (var command in Commands)
		{
			await process.StandardInput.WriteLineAsync(command);
		}

		await process.StandardInput.FlushAsync();
		process.StandardInput.Close();

		var stdout = await process.StandardOutput.ReadToEndAsync();
		var stderr = await process.StandardError.ReadToEndAsync();

		Assert.True(
			await WaitForExitAsync(process),
			$"process did not exit: {stderr}");

		return
		[
			.. stdout
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Where(static line => line.StartsWith('{'))
				.Select(static line => JsonNode.Parse(
					line,
					nodeOptions: null,
					documentOptions: new JsonDocumentOptions { MaxDepth = 4096 })!),
		];
	}

	private static async Task<bool> WaitForExitAsync(Process process)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
		try
		{
			await process.WaitForExitAsync(timeout.Token);
			return true;
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			return false;
		}
	}
}
