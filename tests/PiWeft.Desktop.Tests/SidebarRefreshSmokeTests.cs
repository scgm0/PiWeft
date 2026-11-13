using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// 侧栏刷新现在是「后台扫描 + 回 UI 线程落地」。解析出来的列表没落地时，侧栏就一直停在旧快照，
/// 这种故障在别的测试里看不到（它们不刷新）。这条演练在真窗口里新建一份会话再刷新，
/// 要求刷新返回 applied=True 且行数长出 1。
[Collection("native-desktop")]
public sealed class SidebarRefreshSmokeTests(ITestOutputHelper output)
{
	[Fact]
	public async Task RefreshAppliesTheNewSessionToTheSidebar()
	{
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
		{
			return; // 没有显示服务器：无法打开窗口
		}

		var root = Path.Combine(Path.GetTempPath(), "piweft-sidebar-" + Guid.NewGuid().ToString("N"));
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
					["models"] = new JsonArray(new JsonObject
					{
						["id"] = "fixture-model", ["name"] = "Fixture",
						["contextWindow"] = 128000, ["maxTokens"] = 4096,
					}),
				},
			},
		}.ToJsonString());

		// 侧栏只列当前 cwd 编码出来的会话目录里的会话。
		var sessions = Path.Combine(agent, "sessions", SessionPaths.EncodeCwd(root));
		var existing = SessionManager.Create(root, sessions);
		existing.AppendMessage(new UserMessage { Content = MessageContent.FromText("第一个会话") });
		existing.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "回答" }] });
		var sessionFile = existing.SessionFile!;
		Assert.True(File.Exists(sessionFile), $"会话文件没有落盘：{sessionFile}");

		var shell = Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		Assert.True(File.Exists(shell), $"the shell is not built next to the tests: {shell}");
		var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		info.ArgumentList.Add(shell);
		info.ArgumentList.Add("--ui-smoke");
		info.ArgumentList.Add("sidebar-refresh");
		info.ArgumentList.Add("--cwd");
		info.ArgumentList.Add(root);
		info.Environment["PI_CODING_AGENT_DIR"] = agent;

		try
		{
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

			Assert.Contains("piweft-desktop: sidebar-refresh applied=True before=1 after=2", stdout);
			Assert.Contains("piweft-desktop: ui smoke ok", stdout);
			Assert.Equal(0, process.ExitCode);
		}
		finally
		{
			TempDirectory.Delete(root);
		}
	}
}
