using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// Plays the desktop client's handshake against this port: the exact argument list PiDeck spawns pi with, the
/// <c>--version</c> probe it parses before starting, and the command sequence it sends once the process is up.
/// A failure here means PiDeck would show a startup error, so the shapes asserted are the ones it reads.
/// </summary>
public class PiDeckHandshakeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-pideck-" + Guid.NewGuid().ToString("N")[..8]);

	public PiDeckHandshakeTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static string? FixtureAssembly()
	{
		var directory = AppContext.BaseDirectory;
		var candidates = new[]
		{
			Path.Combine(directory, "PiWeft.Mcp.FixtureServer.dll"),
			Path.Combine(directory, "..", "..", "..", "..", "..", "tools", "PiWeft.Mcp.FixtureServer", "bin", "Debug", "net10.0", "PiWeft.Mcp.FixtureServer.dll"),
			Path.Combine(directory, "..", "..", "..", "..", "..", "tools", "PiWeft.Mcp.FixtureServer", "bin", "Release", "net10.0", "PiWeft.Mcp.FixtureServer.dll"),
		};

		return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
	}

	private static string? DotnetHost()
	{
		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		foreach (var directory in path.Split(Path.PathSeparator))
		{
			if (directory.Length == 0)
			{
				continue;
			}

			var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	private static int FreePort()
	{
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	/// <summary>The flags PiDeck passes, mirroring PiProcess.ts: <c>--mode rpc --no-themes --offline</c>.</summary>
	private static readonly string[] PiDeckFlags = ["--mode", "rpc", "--no-themes", "--offline"];

	private ProcessStartInfo StartInfo(params string[] extra)
	{
		var info = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = _root,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pi.dll"));

		foreach (var argument in PiDeckFlags.Concat(extra))
		{
			info.ArgumentList.Add(argument);
		}

		info.Environment["PI_CODING_AGENT_DIR"] = Path.Combine(_root, "agent");
		return info;
	}

	[Fact]
	public async Task VersionProbeParsesLikePiDeckExpects()
	{
		var info = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pi.dll"));
		info.ArgumentList.Add("--version");

		using var process = Process.Start(info)!;
		var stderrTask = process.StandardError.ReadToEndAsync();
		var stdout = await process.StandardOutput.ReadToEndAsync();
		await process.WaitForExitAsync();

		Assert.Equal(0, process.ExitCode);
		Assert.Empty(await stderrTask);
		Assert.Equal("pi", typeof(CliRunner).Assembly.GetName().Name);
		Assert.Equal(CliRunner.RpcContractVersion, stdout.Trim());

		// PiDeck: trim, drop a leading "v", then require a numeric prefix (extensionVersionGate.parsePiVersion).
		var normalized = stdout.Trim().TrimStart('v', 'V');
		Assert.Matches(@"^\d+(\.\d+)*", normalized);
		Assert.True(Version.TryParse(normalized, out _), $"not a parseable version: '{stdout.Trim()}'");
	}

	[Fact]
	public async Task HelpKeepsPiCommandsAndDoesNotInitializeLogging()
	{
		var info = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = _root,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pi.dll"));
		info.ArgumentList.Add("--help");
		var agentDir = Path.Combine(_root, "help-agent");
		info.Environment["PI_CODING_AGENT_DIR"] = agentDir;

		using var process = Process.Start(info)!;
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		var stdout = await stdoutTask;
		Assert.Equal(0, process.ExitCode);
		Assert.Empty(await stderrTask);
		Assert.StartsWith("pi -", stdout);
		Assert.Contains("pi [options]", stdout);
		Assert.Contains("json, or rpc", stdout);
		Assert.DoesNotContain("piweft [options]", stdout);
		Assert.False(Directory.Exists(agentDir));
	}

	[Fact]
	public async Task StartupHandshakeAnswersStateCommandsAndModels()
	{
		var agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(agentDir);
		File.WriteAllText(Path.Combine(agentDir, "models.json"), """
			{"providers":{"fixture-handshake":{"api":"openai-completions","apiKey":"test-key","baseUrl":"https://example.test",
			"models":[{"id":"handshake-model","contextWindow":128000,"maxTokens":8192}]}}}
			""");
		var info = StartInfo("--no-session", "--no-mcp", "--model", "fixture-handshake/handshake-model");
		using var process = Process.Start(info)!;

		var stdout = new List<string>();
		var reader = Task.Run(async () =>
		{
			while (await process.StandardOutput.ReadLineAsync() is { } line)
			{
				if (line.StartsWith('{'))
				{
					lock (stdout)
					{
						stdout.Add(line);
					}
				}
			}
		});

		// The startup sequence PiDeck sends: get_state first, then commands and models for its pickers.
		await SendAsync(process, """{"id":"1","type":"get_state"}""");
		var state = await WaitForResponseAsync(stdout, "get_state");

		Assert.True(state["success"]!.GetValue<bool>(), state["error"]?.GetValue<string>());
		var data = state["data"]!;
		Assert.NotNull(data["sessionId"]!.GetValue<string>());
		Assert.NotNull(data["model"]!["id"]!.GetValue<string>());
		Assert.Contains("text", data["model"]!["input"]!.AsArray().Select(static node => node!.GetValue<string>()));
		Assert.Contains(data["thinkingLevel"]!.GetValue<string>(), new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" });

		await SendAsync(process, """{"id":"2","type":"get_commands"}""");
		var commands = (await WaitForResponseAsync(stdout, "get_commands"))["data"]!["commands"]!.AsArray();

		// Every command carries the fields the slash-command menu renders.
		foreach (var command in commands)
		{
			Assert.NotNull(command!["name"]!.GetValue<string>());
			Assert.Contains(command["source"]!.GetValue<string>(), new[] { "extension", "prompt", "skill" });
			Assert.NotNull(command["sourceInfo"]!["path"]!.GetValue<string>());
		}

		await SendAsync(process, """{"id":"3","type":"get_available_models"}""");
		var models = (await WaitForResponseAsync(stdout, "get_available_models"))["data"]!["models"]!.AsArray();

		Assert.NotEmpty(models);
		Assert.Contains(models, model => model!["provider"]!.GetValue<string>() == "fixture-handshake" &&
			model["id"]!.GetValue<string>() == "handshake-model");
		var first = models[0]!;
		Assert.NotNull(first["id"]!.GetValue<string>());
		Assert.NotNull(first["provider"]!.GetValue<string>());
		Assert.NotNull(first["api"]!.GetValue<string>());
		Assert.True(first["contextWindow"]!.GetValue<long>() > 0);
		Assert.True(first["maxTokens"]!.GetValue<long>() > 0);

		await SendAsync(process, """{"id":"4","type":"get_available_thinking_levels"}""");
		var levels = (await WaitForResponseAsync(stdout, "get_available_thinking_levels"))["data"]!["levels"]!.AsArray();
		Assert.NotEmpty(levels);

		await SendAsync(process, """{"id":"5","type":"exit"}""");
		await reader;
		process.Kill(entireProcessTree: true);
	}

	[Fact]
	public async Task APromptStreamsTheEventsTheRendererConsumes()
	{
		var fixture = FixtureAssembly();
		var dotnet = DotnetHost();
		Assert.False(fixture is null || dotnet is null, $"fixture={fixture ?? "<null>"} dotnet={dotnet ?? "<null>"}");
		Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "pi.dll")), "the CLI is not built");

		var port = FreePort();
		var logPath = Path.Combine(_root, "requests.jsonl");

		using var provider = Process.Start(new ProcessStartInfo(dotnet!)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			ArgumentList = { fixture!, "--llm", port.ToString(), logPath },
		})!;

		try
		{
			var ready = provider.StandardOutput.ReadLineAsync();
			Assert.True(
				await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(30))) == ready,
				"the mock provider did not start");
			Assert.Contains("listening", await ready);

			// A models.json custom endpoint, which is how a desktop client points pi at a local gateway.
			var agentDir = Path.Combine(_root, "agent");
			Directory.CreateDirectory(agentDir);
			await File.WriteAllTextAsync(
				Path.Combine(agentDir, "models.json"),
				$$"""
				{
				  "providers": {
				    "mockllm": {
				      "baseUrl": "http://127.0.0.1:{{port}}",
				      "api": "anthropic-messages",
				      "apiKey": "mock-key",
				      "models": [ { "id": "mock-model", "contextWindow": 32000, "maxTokens": 4096 } ]
				    }
				  }
				}
				""");

			var info = StartInfo(
				"--no-session",
				"--no-mcp",
				"--no-skills",
				"--no-prompt-templates",
				"--no-context-files",
				"--model",
				"mockllm/mock-model");

			info.Environment.Remove("ANTHROPIC_API_KEY");
			using var process = Process.Start(info)!;

			var records = new List<JsonNode>();
			var errors = new List<string>();
			var reader = Task.Run(async () =>
			{
				while (await process.StandardOutput.ReadLineAsync() is { } line)
				{
					if (!line.StartsWith('{'))
					{
						continue;
					}

					lock (records)
					{
						records.Add(JsonNode.Parse(line)!);
					}
				}
			});

			var stderr = process.StandardError.ReadToEndAsync();

			await SendAsync(process, """{"id":"1","type":"prompt","message":"say hi"}""");
			await WaitForRecordAsync(records, static node => node["type"]?.GetValue<string>() == "agent_end");

			await SendAsync(process, """{"id":"2","type":"exit"}""");
			await reader;
			process.Kill(entireProcessTree: true);

			List<JsonNode> snapshot;
			lock (records)
			{
				snapshot = [.. records];
			}

			var types = snapshot
				.Select(static node => node["type"]!.GetValue<string>())
				.ToList();

			// The response to the prompt arrives before the stream ends.
			var prompt = snapshot.Single(static node =>
				node["type"]!.GetValue<string>() == "response" &&
				node["command"]!.GetValue<string>() == "prompt");
			Assert.True(prompt["success"]!.GetValue<bool>(), prompt["error"]?.GetValue<string>());
			Assert.Equal("started", prompt["data"]!["disposition"]!.GetValue<string>());

			// The event shapes the renderer projects into chat bubbles.
			Assert.Contains("agent_start", types);
			Assert.Contains("message_start", types);
			Assert.Contains("message_update", types);
			Assert.Contains("message_end", types);
			Assert.Contains("agent_end", types);

			var update = snapshot.First(static node => node["type"]!.GetValue<string>() == "message_update");
			Assert.NotNull(update["assistantMessageEvent"]!["type"]!.GetValue<string>());

			// At least one assistant turn carried content, and the last one reports usage for the footer.
			var assistants = snapshot
				.Select(static node => node["message"])
				.Where(static message => message?["role"]?.GetValue<string>() == "assistant")
				.Select(static message => message!)
				.ToList();

			Assert.Contains(assistants, static message => message["content"]!.AsArray().Count > 0);

			var usage = assistants[^1]["usage"]!;
			Assert.True(usage["input"]!.GetValue<long>() >= 0);
			Assert.True(usage["cost"]!["total"]!.GetValue<double>() >= 0);

			Assert.True(process.HasExited, "the CLI did not exit after the exit command");
		}
		finally
		{
			if (!provider.HasExited)
			{
				provider.Kill(entireProcessTree: true);
			}
		}
	}

	private static async Task SendAsync(Process process, string command)
	{
		await process.StandardInput.WriteLineAsync(command);
		await process.StandardInput.FlushAsync();
	}

	private static async Task<JsonObject> WaitForResponseAsync(List<string> stdout, string command)
	{
		for (var attempt = 0; attempt < 600; attempt++)
		{
			lock (stdout)
			{
				foreach (var line in stdout)
				{
					var node = JsonNode.Parse(line);
					if (node?["type"]?.GetValue<string>() == "response" &&
						node["command"]?.GetValue<string>() == command)
					{
						return node.AsObject();
					}
				}
			}

			await Task.Delay(50);
		}

		throw new TimeoutException($"no response for {command}");
	}

	private static async Task WaitForRecordAsync(List<JsonNode> records, Func<JsonNode, bool> predicate)
	{
		for (var attempt = 0; attempt < 1200; attempt++)
		{
			lock (records)
			{
				if (records.Any(predicate))
				{
					return;
				}
			}

			await Task.Delay(50);
		}

		throw new TimeoutException("the expected event never arrived");
	}
}
