using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Tools;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>Opt-in visual check for the composer widget cards, under an isolated X server.</summary>
[Collection("native-desktop")]
public sealed class NativeWidgetCardTests(ITestOutputHelper output)
{
	[Fact]
	public async Task TodoAndExtensionCardsRenderAboveTheComposer()
	{
		if (Environment.GetEnvironmentVariable("PIWEFT_NATIVE_TESTS") != "1") return;
		var root = Path.Combine(Path.GetTempPath(), "piweft-widgets-" + Guid.NewGuid().ToString("N"));
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
		for (var i = 0; i < 8; i++)
		{
			session.AppendMessage(new UserMessage { Content = MessageContent.FromText($"question {i}") });
			session.AppendMessage(new ToolResultMessage { ToolCallId = $"call-{i}", ToolName = "bash", Content = [new TextContent { Text = $"npm test -- --runInBand {i}" }] });
			session.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = $"answer {i}\n\n```csharp\nvar value = {i};\n```" }] });
		}
		session.AppendMessage(new UserMessage
		{
			Content = MessageContent.FromText(
				"<skill name=\"mewui\" location=\"/home/scgm/.pi/agent/skills/mewui/SKILL.md\">\nReferences are relative to /home/scgm/.pi/agent/skills/mewui.\n\n# MewUI application development\n\n## Start here\n\nLong skill body.\n</skill>\n\n1. 请修复接收信息时，可能无法滑动到信息底部与自动显示最新信息失效的问题\n2. 参考 pideck，优化用户信息与待办等扩展 UI 的样式"),
			Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
		});
		session.AppendCustom(TodoTool.EntryType, TodoTool.Encode(new TodoState(
			new TodoPlan(1,
			[
				new TodoItem(1, "扫描仓库中的凭据、令牌、cookie、私钥与个人信息", "completed"),
				new TodoItem(2, "确认哪些是真实敏感信息、哪些是测试占位", "in_progress"),
				new TodoItem(3, "移除或脱敏敏感内容并检查引用", "pending"),
				new TodoItem(4, "运行构建/相关测试并复核 git diff", "pending"),
				new TodoItem(5, "补充回归测试覆盖新增的滚动跟随场景", "pending"),
				new TodoItem(6, "整理待办卡与扩展卡的深浅主题截图", "pending"),
				new TodoItem(7, "检查窄窗口下进度文案的省略行为", "pending"),
				new TodoItem(8, "更新设计契约与性能说明", "pending"),
			]),
			null, 2, 5)));

		var app = Environment.GetEnvironmentVariable("PIWEFT_INTERACTION_APP") ?? Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		var managed = app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
		var info = new ProcessStartInfo(managed ? "dotnet" : app) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		if (managed) info.ArgumentList.Add(app);
		foreach (var argument in new[] { "--cwd", root, "--ui-smoke", "hints" }) info.ArgumentList.Add(argument);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;
		using var process = new Process { StartInfo = info };
		var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
		try
		{
			process.Start();
			process.BeginOutputReadLine();
			var ready = false;
			process.OutputDataReceived += (_, args) =>
			{
				if (args.Data is { } line) lines.Enqueue(line);
				if (args.Data?.StartsWith("hover-point:", StringComparison.Ordinal) == true) Volatile.Write(ref ready, true);
			};
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			while (!Volatile.Read(ref ready)) await Task.Delay(50, timeout.Token);
			await Task.Delay(400);
			Assert.False(process.HasExited, "The desktop exited before the widget cards were rendered.");
			if (Environment.GetEnvironmentVariable("PIWEFT_SCREENSHOTS") is { } directory && File.Exists("/usr/bin/import"))
			{
				Directory.CreateDirectory(directory);
				var screenshot = Path.Combine(directory, "widget-cards.png");
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
