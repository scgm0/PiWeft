using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>Opt-in X11 automation. Run on an isolated Xvfb display; no real configuration or session is used.</summary>
[Collection("native-desktop")]
public sealed class NativeOutlineTests(ITestOutputHelper output)
{
	[Theory]
	[InlineData("dark")]
	[InlineData("light")]
	public async Task RailPreviewsJumpsAndPassesTheWheelToTheConversation(string theme)
	{
		if (Environment.GetEnvironmentVariable("PIWEFT_NATIVE_TESTS") != "1") return;
		var root = Path.Combine(Path.GetTempPath(), "piweft-outline-" + Guid.NewGuid().ToString("N"));
		var agent = Path.Combine(root, "agent");
		Directory.CreateDirectory(agent);
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
		for (var turn = 0; turn < 24; turn++)
		{
			session.AppendMessage(new UserMessage { Content = $"问题 {turn + 1}：检查布局与执行结果", Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
			session.AppendCustomMessage("pi-deck-goal-context", "[GOAL MODE ACTIVE]", display: false);
			if (turn == 23)
			{
				for (var step = 0; step < 12; step++)
				{
					var name = (step % 3) switch { 0 => "bash", 1 => "read", _ => "edit" };
					session.AppendMessage(new AssistantMessage
					{
						Content = [new ThinkingContent { Thinking = $"检查第 {step + 1} 步的结果，保留阅读位置，避免重复构造历史消息。" },
							new ToolCall { Id = $"call-{step}", Name = name, Arguments = new JsonObject { [name == "bash" ? "command" : "path"] = name == "bash" ? "dotnet test --filter LayoutTests" : "src/App.cs" } }],
					});
					session.AppendMessage(new ToolResultMessage { ToolCallId = $"call-{step}", ToolName = name, DurationMs = 24 + step, Content = [new TextContent { Text = "ok" }] });
				}
			}
			session.AppendMessage(new AssistantMessage
			{
				Content = [new TextContent { Text = turn == 23 ? "布局与交互检查完成。" : string.Join('\n', Enumerable.Range(1, 36).Select(line => $"回答 {turn + 1} — 第 {line} 行，用来验证可变高度虚拟化的跳转。")) }],
				DurationMs = 2400, StopReason = StopReason.Stop,
			});
		}
		var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll"));
		foreach (var arg in new[] { "--cwd", root, "--ui-smoke", "outline" }) info.ArgumentList.Add(arg);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;
		var points = new ConcurrentDictionary<int, int[]>();
		var lines = new ConcurrentQueue<string>();
		var state = "";
		var preview = "";
		using var process = new Process { StartInfo = info };
		process.OutputDataReceived += (_, args) =>
		{
			if (args.Data is not { } line) return;
			lines.Enqueue(line);
			if (line.StartsWith("outline-tick:", StringComparison.Ordinal))
			{
				var parts = line.Split(':');
				points[int.Parse(parts[1])] = parts[2].Split(',').Select(int.Parse).ToArray();
			}
			if (line.StartsWith("outline-state:", StringComparison.Ordinal)) Volatile.Write(ref state, line[14..]);
			if (line.StartsWith("outline-preview:", StringComparison.Ordinal)) Volatile.Write(ref preview, line[16..]);
		};
		try
		{
			process.Start();
			process.BeginOutputReadLine();
			var stderr = process.StandardError.ReadToEndAsync();
			await Wait(() => points.Count == 24, "24 real user ticks, excluding hidden context");
			await Hover(23);
			await Wait(() => State()[2] == "True" && Volatile.Read(ref preview).StartsWith("问题 24", StringComparison.Ordinal), "preview opens");
			Assert.StartsWith("问题 24", Volatile.Read(ref preview));
			await Screenshot("outline-preview");
			await Run("xdotool", "mousemove", "--sync", "1", "1");
			await Wait(() => State()[2] == "False", "preview closes on leave");

			await Hover(0);
			await Run("xdotool", "click", "1");
			await Wait(() => State()[0] == "0" && State()[1] == "0" && State()[3] == "False", "jump to first question and detach follow");
			await Run("xdotool", "key", "Down");
			await Wait(() => Volatile.Read(ref preview).StartsWith("问题 2：", StringComparison.Ordinal), "arrow-key navigation previews the next tick");
			await Run("xdotool", "key", "Return");
			await Wait(() => int.Parse(State()[0]) == points[1][0], "keyboard activation jumps to the question");
			await Hover(12);
			await Run("xdotool", "click", "1");
			await Wait(() => int.Parse(State()[0]) == points[12][0], "jump to a virtualized middle question");
			var before = int.Parse(State()[4]);
			await Run("xdotool", "click", "5");
			await Wait(() => int.Parse(State()[4]) > before + 10, "wheel on the rail scrolls the conversation");

			await Hover(23);
			await Run("xdotool", "click", "1");
			await Wait(() => int.Parse(State()[0]) == points[23][0], "jump to last question");
			await Run("xdotool", "key", "F8");
			await Task.Delay(600);
			await Screenshot("execution-compact");
			Assert.DoesNotContain(lines, line => line.Contains("UI 线程异常", StringComparison.Ordinal));
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
			Assert.DoesNotContain("UI 线程异常", await stderr);
		}
		finally
		{
			if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
			output.WriteLine(string.Join('\n', lines));
			Directory.Delete(root, recursive: true);
		}
		string[] State() => Volatile.Read(ref state).Length > 0 ? Volatile.Read(ref state).Split(',') : ["-1", "-1", "False", "True", "0"];
		async Task Hover(int index)
		{
			var point = points[index];
			await Run("xdotool", "mousemove", "--sync", point[1].ToString(), point[2].ToString());
			await Task.Delay(200);
		}
		async Task Wait(Func<bool> predicate, string purpose)
		{
			var timeout = Stopwatch.StartNew();
			while (!predicate() && !process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(30);
			Assert.True(predicate(), $"Timed out: {purpose}; state={Volatile.Read(ref state)}");
		}
		async Task Screenshot(string name)
		{
			if (Environment.GetEnvironmentVariable("PIWEFT_SCREENSHOTS") is not { } path || !File.Exists("/usr/bin/scrot")) return;
			Directory.CreateDirectory(path);
			await Run("scrot", "--overwrite", Path.Combine(path, $"{name}-{theme}.png"));
		}
	}

	private static async Task Run(string executable, params string[] args)
	{
		var info = new ProcessStartInfo(executable) { UseShellExecute = false };
		foreach (var arg in args) info.ArgumentList.Add(arg);
		using var process = Process.Start(info)!;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await process.WaitForExitAsync(timeout.Token);
		Assert.Equal(0, process.ExitCode);
	}
}
