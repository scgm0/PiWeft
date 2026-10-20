using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>Opt-in visual check for the PiDeck-style execution timeline and expanded tool card.</summary>
[Collection("native-desktop")]
public sealed class NativeExecutionTests(ITestOutputHelper output)
{
	[Fact]
	public async Task ExecutionTimelineRendersExpanded()
	{
		if (Environment.GetEnvironmentVariable("PIWEFT_NATIVE_TESTS") != "1") return;
		var root = Path.Combine(Path.GetTempPath(), "piweft-execution-" + Guid.NewGuid().ToString("N"));
		var agent = Path.Combine(root, "agent");
		Directory.CreateDirectory(agent);
		var theme = Environment.GetEnvironmentVariable("PIWEFT_WIDGET_THEME") ?? "dark";
		File.WriteAllText(Path.Combine(agent, "desktop.json"), $"{{\"theme\":\"{theme}\"}}");
		File.WriteAllText(Path.Combine(agent, "settings.json"), "{\"defaultProvider\":\"fixture\",\"defaultModel\":\"fixture-model\"}");
		File.WriteAllText(Path.Combine(agent, "models.json"), new JsonObject
		{
			["providers"] = new JsonObject
			{
				["fixture"] = new JsonObject
				{
					["baseUrl"] = "http://127.0.0.1:9", ["api"] = "openai-completions", ["apiKey"] = "fixture-token",
					["models"] = new JsonArray(new JsonObject { ["id"] = "fixture-model", ["name"] = "Fixture", ["contextWindow"] = 128000, ["maxTokens"] = 4096 }),
				},
			},
		}.ToJsonString());

		var session = SessionManager.Create(root, Path.Combine(agent, "sessions", "fixture"));
		session.AppendMessage(new UserMessage { Content = MessageContent.FromText("参考 PiDeck 优化执行过程界面"), Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
		session.AppendMessage(new AssistantMessage
		{
			Content = [new ThinkingContent { Thinking = "先看一下当前执行过程的层级，再决定哪些行需要合并。" }],
			DurationMs = 1800,
		});
		session.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "call-1", ToolName = "bash",
			Content = [new TextContent { Text = "20:14:31 查找到 3 个 ExecutionProcessView 引用\n20:14:32 检查折叠状态与滚动位置" }],
			DurationMs = 276,
		});
		session.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "已定位问题。" }], DurationMs = 500 });
		session.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "call-2", ToolName = "bash",
			Content = [new TextContent { Text = "dotnet test --filter FullyQualifiedName~ExecutionProcessTests\n已通过 7/7" }],
			DurationMs = 150,
		});
		for (var i = 0; i < 20; i++)
		{
			session.AppendMessage(new ToolResultMessage
			{
				ToolCallId = $"call-more-{i}",
				ToolName = (i % 3) switch { 0 => "bash", 1 => "read", _ => "grep" },
				Content = [new TextContent { Text = (i % 3) switch
				{
					0 => $"cd /home/scgm/RiderProjects/PiWeft && dotnet test --filter ExecutionProcessTests --no-restore #{i}",
					1 => $"src/PiWeft.Desktop/ExecutionProcessView.cs 第 {i + 1} 段",
					_ => $"grep -rn \"ExecutionProcess\" src/PiWeft.Desktop --include=*.cs #{i}",
				} }],
				DurationMs = 40 + i * 13,
			});
		}
		session.AppendMessage(new AssistantMessage
		{
			Content = [new TextContent { Text = "执行过程时间线已经整理完成。" }],
			DurationMs = 64000,
		});

		var app = Environment.GetEnvironmentVariable("PIWEFT_INTERACTION_APP") ?? Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		var managed = app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
		var info = new ProcessStartInfo(managed ? "dotnet" : app) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		if (managed) info.ArgumentList.Add(app);
		foreach (var argument in new[] { "--cwd", root, "--ui-smoke", "process" }) info.ArgumentList.Add(argument);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;
		using var process = new Process { StartInfo = info };
		var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
		var ready = false;
		process.OutputDataReceived += (_, args) =>
		{
			if (args.Data is { } line) lines.Enqueue(line);
			if (args.Data == "process-ready") Volatile.Write(ref ready, true);
		};
		try
		{
			process.Start();
			process.BeginOutputReadLine();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			while (!Volatile.Read(ref ready)) await Task.Delay(50, timeout.Token);
			Assert.False(process.HasExited, "The desktop exited before the execution timeline was rendered.");
			await Task.Delay(400);
			if (Environment.GetEnvironmentVariable("PIWEFT_SCREENSHOTS") is { } directory && File.Exists("/usr/bin/import"))
			{
				Directory.CreateDirectory(directory);
				var screenshot = Path.Combine(directory, "execution.png");
				await Run("import", "-window", "root", screenshot);
				Assert.True(new FileInfo(screenshot).Length > 5_000);
			}
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
		}
		finally
		{
			if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
			output.WriteLine(await process.StandardError.ReadToEndAsync());
			output.WriteLine(string.Join('\n', lines));
			Directory.Delete(root, true);
		}
	}

	private static async Task Run(string executable, params string[] arguments)
	{
		var info = new ProcessStartInfo(executable) { UseShellExecute = false };
		foreach (var argument in arguments) info.ArgumentList.Add(argument);
		using var process = Process.Start(info)!;
		await process.WaitForExitAsync();
		Assert.Equal(0, process.ExitCode);
	}
}
