using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 后台解析另一个会话时，主区必须只剩加载提示：上一个会话的正文、输入区与右侧面板都要让位。
/// 历史缺陷是加载段落直接浮在旧会话正文之上（旧会话还没收起），这里用演练标记做回归。
/// </summary>
[Collection("native-desktop")]
public sealed class SessionLoadingSmokeTests(ITestOutputHelper output)
{
	[Fact]
	public async Task LoadingPromptReplacesThePreviousSessionWhileParsing()
	{
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
		{
			return; // 没有显示服务器：无法打开窗口
		}

		var root = Path.Combine(Path.GetTempPath(), "piweft-loading-" + Guid.NewGuid().ToString("N"));
		var agent = Path.Combine(root, "agent");
		Directory.CreateDirectory(agent);
		File.WriteAllText(Path.Combine(agent, "desktop.json"), """{"theme":"dark","sidebarProjects":true}""");
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

		// 会话必须落在该工作目录编码出来的会话目录里，侧栏才列得到（否则应用会自己新建一个空会话）。
		var sessions = Path.Combine(agent, "sessions", PiWeft.CodingAgent.Session.SessionPaths.EncodeCwd(root));
		// 启动会自动打开最新的一份；演练再打开另一份，于是在已有标签页之上进入加载态。
		var first = SessionManager.Create(root, sessions);
		first.AppendMessage(new UserMessage { Content = MessageContent.FromText("第一个会话") });
		first.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "第一个会话的回答" }] });
		var second = SessionManager.Create(root, sessions);
		second.AppendMessage(new UserMessage { Content = MessageContent.FromText("第二个会话") });
		second.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "第二个会话的回答" }] });
		File.SetLastWriteTimeUtc(first.SessionFile!, DateTime.UtcNow.AddMinutes(5));
		File.SetLastWriteTimeUtc(second.SessionFile!, DateTime.UtcNow);

		var shell = Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		Assert.True(File.Exists(shell), $"the shell is not built next to the tests: {shell}");
		var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		info.ArgumentList.Add(shell);
		info.ArgumentList.Add("--ui-smoke");
		info.ArgumentList.Add("loading");
		info.ArgumentList.Add("--cwd");
		info.ArgumentList.Add(root);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;

		using var process = Process.Start(info)!;
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
			throw;
		}

		var stdout = await stdoutTask;
		var stderr = await stderrTask;
		output.WriteLine(stdout);
		output.WriteLine(stderr);

		// 加载期间：标签页（旧会话）与右侧面板都不可见，准备阶段面板 + 加载提示在场。
		Assert.Contains("loading in-flight tabVisible=False welcomeVisible=True prompt=True panels=False", stdout);
		// 加载结束：两个标签页都在，准备阶段面板收起，加载提示消失。
		Assert.Contains("loading settled tabs=2 welcomeVisible=False prompt=False tabVisible=True", stdout);
		Assert.Contains("ui smoke ok", stdout);
		Assert.DoesNotContain("没有第二个会话", stdout + stderr);
	}
}
