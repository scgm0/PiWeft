using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>Opt-in X11 pointer automation, intended for an isolated Xvfb display.</summary>
[Collection("native-desktop")]
public sealed class NativeHoverTests(ITestOutputHelper output)
{
	[Fact]
	public async Task DelayedNativePopupsFollowTopAndBottomControlsAndDismissOnLeaveAndScroll()
	{
		if (Environment.GetEnvironmentVariable("PIWEFT_NATIVE_TESTS") != "1") return;
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		using var serverAbort = new CancellationTokenSource();
		var requests = 0;
		var server = Task.Run(async () =>
		{
			try
			{
				while (!serverAbort.IsCancellationRequested)
				{
					using var client = await listener.AcceptTcpClientAsync(serverAbort.Token);
					await using var stream = client.GetStream();
					using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
					while (await reader.ReadLineAsync(serverAbort.Token) is { Length: > 0 }) { }
					Interlocked.Increment(ref requests);
					const string json = "{\"balance\":12.99,\"unit\":\"USD\"}";
					var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}");
					await stream.WriteAsync(response, serverAbort.Token);
				}
			}
			catch (OperationCanceledException) { }
		});
		var root = Path.Combine(Path.GetTempPath(), "piweft-hover-" + Guid.NewGuid().ToString("N"));
		var agent = Path.Combine(root, "agent");
		Directory.CreateDirectory(agent);
		File.WriteAllText(Path.Combine(agent, "desktop.json"), "{\"theme\":\"dark\"}");
		File.WriteAllText(Path.Combine(agent, "settings.json"), "{\"defaultProvider\":\"fixture\",\"defaultModel\":\"fixture-model\"}");
		File.WriteAllText(Path.Combine(agent, "models.json"), new JsonObject
		{
			["providers"] = new JsonObject
			{
				["fixture"] = new JsonObject
				{
					["baseUrl"] = $"http://127.0.0.1:{port}", ["api"] = "openai-completions", ["apiKey"] = "fixture-token",
					["models"] = new JsonArray(new JsonObject { ["id"] = "fixture-model", ["name"] = "Fixture", ["contextWindow"] = 128000, ["maxTokens"] = 4096 }),
				},
			},
		}.ToJsonString());
		var session = SessionManager.Create(root, Path.Combine(agent, "sessions", "fixture"));
		for (var i = 0; i < 30; i++)
		{
			session.AppendMessage(new UserMessage { Content = MessageContent.FromText($"question {i}") });
			session.AppendMessage(new ToolResultMessage { ToolCallId = $"call-{i}", ToolName = "read", Content = [new TextContent { Text = $"output {i}" }] });
			session.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = $"answer {i}" }] });
		}
		var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll"));
		foreach (var argument in new[] { "--cwd", root, "--ui-smoke", "hints" }) info.ArgumentList.Add(argument);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;
		var points = new ConcurrentDictionary<string, string[]>();
		var lines = new ConcurrentQueue<string>();
		var open = false;
		using var process = new Process { StartInfo = info };
		process.OutputDataReceived += (_, args) =>
		{
			if (args.Data is not { } line) return;
			lines.Enqueue(line);
			if (line.StartsWith("hover-point:", StringComparison.Ordinal))
			{
				var parts = line.Split(':');
				points[parts[1]] = parts[2].Split(',');
			}
			if (line.StartsWith("hover-state:", StringComparison.Ordinal)) Volatile.Write(ref open, line.EndsWith("True", StringComparison.Ordinal));
		};
		try
		{
			process.Start();
			process.BeginOutputReadLine();
			var stderr = process.StandardError.ReadToEndAsync();
			await Wait(() => points.ContainsKey("statistics"));
			await Run("xdotool", "mousemove", "--sync", "1", "1");
			foreach (var name in new[] { "theme", "statistics" })
			{
				var point = points[name];
				await Run("xdotool", "mousemove", "--sync", point[0], point[1]);
				await Task.Delay(120);
				Assert.False(Volatile.Read(ref open), "Tooltip appeared before its delay.");
				await Wait(() => Volatile.Read(ref open));
				if (File.Exists("/usr/bin/import") && Environment.GetEnvironmentVariable("PIWEFT_SCREENSHOTS") is { } screenshots)
				{
					Directory.CreateDirectory(screenshots);
					await Run("import", "-window", "root", Path.Combine(screenshots, $"tooltip-{name}.png"));
				}
				await Run("xdotool", "mousemove", "--sync", "1", "1");
				await Wait(() => !Volatile.Read(ref open));
			}
			var statistics = points["statistics"];
			await Run("xdotool", "mousemove", "--sync", statistics[0], statistics[1]);
			await Wait(() => Volatile.Read(ref open));
			await Run("xdotool", "click", "5");
			await Wait(() => !Volatile.Read(ref open));
			await Run("xdotool", "click", "1");
			await Wait(() => Volatile.Read(ref requests) > 0);
			await Task.Delay(200);
			await Screenshot("statistics-balance");
			await Run("xdotool", "key", "Escape");
			if (points.TryGetValue("process", out var fold))
			{
				await Run("xdotool", "mousemove", "--sync", fold[0], fold[1]);
				await Run("xdotool", "click", "1");
				await Wait(() => lines.Any(line => line == "process-expanded:True"));
				await Task.Delay(100);
				await Screenshot("execution-process");
			}
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			await process.WaitForExitAsync(timeout.Token);
			Assert.Equal(0, process.ExitCode);
			Assert.DoesNotContain("UI 线程异常", await stderr);
		}
		finally
		{
			if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
			output.WriteLine(string.Join('\n', lines));
			serverAbort.Cancel();
			await server;
			Directory.Delete(root, true);
		}
		async Task Screenshot(string name)
		{
			if (File.Exists("/usr/bin/import") && Environment.GetEnvironmentVariable("PIWEFT_SCREENSHOTS") is { } directory)
			{
				Directory.CreateDirectory(directory);
				await Run("import", "-window", "root", Path.Combine(directory, $"{name}.png"));
			}
		}
	}

	private static async Task Wait(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
		while (!condition()) await Task.Delay(20, timeout.Token);
	}
	private static async Task Run(string executable, params string[] args)
	{
		var info = new ProcessStartInfo(executable) { UseShellExecute = false };
		foreach (var argument in args) info.ArgumentList.Add(argument);
		using var process = Process.Start(info)!;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await process.WaitForExitAsync(timeout.Token);
		Assert.Equal(0, process.ExitCode);
	}
}
