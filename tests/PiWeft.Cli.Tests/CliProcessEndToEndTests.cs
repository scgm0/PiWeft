using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// Drives the real <c>pi</c> CLI assembly as a child process against a local mock Anthropic server. This exercises the
/// whole published stack: argument parsing, <c>models.json</c> custom endpoints, provider HTTP + SSE, the agent loop,
/// tool execution, session persistence and the JSON event stream. Skips when the binaries are not built.
/// </summary>
public class CliProcessEndToEndTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-proc-" + Guid.NewGuid().ToString("N")[..8]);

	public CliProcessEndToEndTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
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

	private static int FreePort()
	{
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	[Fact]
	public async Task RunsAFullAgentTurnAgainstALocalProvider()
	{
		var dotnet = DotnetHost();
		var fixture = FixtureAssembly();
		var cliDll = Path.Combine(AppContext.BaseDirectory, "pi.dll");

		Assert.False(dotnet is null, "dotnet host not found on PATH");
		Assert.True(File.Exists(fixture!), $"fixture server not built: {fixture}");
		Assert.True(File.Exists(cliDll), $"cli not built: {cliDll}");

		var port = FreePort();
		var logPath = Path.Combine(_root, "llm-requests.jsonl");

		// 1. Start the mock provider.
		var serverInfo = new ProcessStartInfo(dotnet!)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		serverInfo.ArgumentList.Add(fixture!);
		serverInfo.ArgumentList.Add("--llm");
		serverInfo.ArgumentList.Add(port.ToString());
		serverInfo.ArgumentList.Add(logPath);

		using var server = Process.Start(serverInfo)!;
		try
		{
			var ready = server.StandardOutput.ReadLineAsync();
			Assert.True(await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(30))) == ready, "mock provider did not start");
			Assert.Contains("listening", await ready);

			// 2. A workspace with a file, and a custom endpoint declared in models.json.
			var work = Path.Combine(_root, "work");
			var agentDir = Path.Combine(_root, "agent");
			var sessionDir = Path.Combine(_root, "sessions");
			Directory.CreateDirectory(work);
			Directory.CreateDirectory(agentDir);
			File.WriteAllText(Path.Combine(work, "note.txt"), "MOCK TOOL OUTPUT\n");

			File.WriteAllText(Path.Combine(agentDir, "models.json"), $$"""
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

			// 3. Run the real CLI as a child process.
			var cliInfo = new ProcessStartInfo(dotnet!)
			{
				WorkingDirectory = work,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};

			cliInfo.Environment["PI_CODING_AGENT_DIR"] = agentDir;
			cliInfo.Environment.Remove("ANTHROPIC_API_KEY");

			foreach (var argument in new[]
			{
				cliDll,
				"--mode", "json",
				"--model", "mockllm/mock-model",
				"--session-dir", sessionDir,
				"--no-skills", "--no-prompt-templates", "--no-context-files", "--no-mcp",
				"please read note.txt",
			})
			{
				cliInfo.ArgumentList.Add(argument);
			}

			using var cli = Process.Start(cliInfo)!;
			var stdout = await cli.StandardOutput.ReadToEndAsync();
			var stderr = await cli.StandardError.ReadToEndAsync();
			await cli.WaitForExitAsync();

			Assert.True(cli.ExitCode == 0, $"cli exited {cli.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");

			var records = stdout
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Select(static line => JsonNode.Parse(line)!)
				.ToList();

			// The JSON stream starts with the session header.
			Assert.Equal("session", records[0]!["type"]!.GetValue<string>());

			var types = records.Select(static record => record["type"]!.GetValue<string>()).ToList();
			Assert.Contains("agent_start", types);
			Assert.Contains("tool_execution_start", types);
			Assert.Contains("tool_execution_end", types);
			Assert.Equal("agent_settled", types[^1]);

			// The built-in read tool ran and produced the file contents.
			var toolResult = records
				.Where(static record => record["type"]!.GetValue<string>() == "message_end")
				.Select(static record => record["message"]!)
				.First(static message => message["role"]!.GetValue<string>() == "toolResult");

			Assert.Equal("read", toolResult["toolName"]!.GetValue<string>());
			Assert.Contains("MOCK TOOL OUTPUT", toolResult["content"]![0]!["text"]!.GetValue<string>());

			// The second model call returned the final answer.
			var assistantTexts = records
				.Where(static record => record["type"]!.GetValue<string>() == "message_end")
				.Select(static record => record["message"]!)
				.Where(static message => message["role"]!.GetValue<string>() == "assistant")
				.SelectMany(static message => message["content"]!.AsArray())
				.Where(static block => block!["type"]!.GetValue<string>() == "text")
				.Select(static block => block!["text"]!.GetValue<string>())
				.ToList();

			Assert.Contains("mock final answer", assistantTexts);

			// 4. Both model calls reached the mock provider: the second one carried the tool result.
			var requests = (await File.ReadAllLinesAsync(logPath))
				.Where(static line => line.Trim().Length > 0)
				.ToList();

			Assert.Equal(2, requests.Count);
			Assert.Contains("please read note.txt", requests[0]);
			Assert.Contains("MOCK TOOL OUTPUT", requests[1]);

			// 5. The session was persisted with the expected shape.
			var sessionFile = Assert.Single(Directory.GetFiles(sessionDir, "*.jsonl"));
			var lines = await File.ReadAllLinesAsync(sessionFile);
			var roles = lines
				.Select(static line => JsonNode.Parse(line)!)
				.Where(static entry => entry["type"]?.GetValue<string>() == "message")
				.Select(static entry => entry["message"]!["role"]!.GetValue<string>())
				.ToList();

			// 系统提示词不入库（与 pi 一致），文件里只有真实往来消息。
			Assert.Equal("user", roles[0]);
			Assert.Equal("assistant", roles[1]);
			Assert.Equal("toolResult", roles[2]);
			Assert.Equal("assistant", roles[3]);
		}
		finally
		{
			try
			{
				if (!server.HasExited)
				{
					server.Kill(entireProcessTree: true);
				}
			}
			catch (InvalidOperationException)
			{
				// Already gone.
			}
		}
	}

	[Fact]
	public async Task ReportsAMissingModelForAnUnknownCustomProvider()
	{
		var dotnet = DotnetHost();
		var cliDll = Path.Combine(AppContext.BaseDirectory, "pi.dll");
		Assert.False(dotnet is null);
		Assert.True(File.Exists(cliDll));

		var agentDir = Path.Combine(_root, "agent-empty");
		Directory.CreateDirectory(agentDir);

		var info = new ProcessStartInfo(dotnet!)
		{
			WorkingDirectory = _root,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.Environment["PI_CODING_AGENT_DIR"] = agentDir;
		info.ArgumentList.Add(cliDll);
		info.ArgumentList.Add("--model");
		info.ArgumentList.Add("nope/nope");

		using var process = Process.Start(info)!;
		var stderr = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();

		Assert.NotEqual(0, process.ExitCode);
		Assert.Contains("Model not found", stderr);
	}
}
