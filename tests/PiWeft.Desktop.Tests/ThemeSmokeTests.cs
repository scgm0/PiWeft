using System.Diagnostics;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 运行时换肤（MewUI 的 <c>SetTheme</c>）应原位更新控件，并保留会话、草稿和选择范围。
/// 需要显示服务器，没有 DISPLAY 时跳过。
/// </summary>
[Collection("native-desktop")]
public class ThemeSmokeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-desktop-theme-" + Guid.NewGuid().ToString("N")[..8]);

	private readonly ITestOutputHelper _output;

	public ThemeSmokeTests(ITestOutputHelper output)
	{
		_output = output;
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task SwitchingThemeAtRuntimeRecolorsControlsAndKeepsDraftAndTabs()
	{
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
		{
			return; // 没有显示服务器：无法打开窗口
		}

		var shell = Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll");
		Assert.True(File.Exists(shell), $"the shell is not built next to the tests: {shell}");

		var agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(agentDir);
		File.WriteAllText(Path.Combine(agentDir, "desktop.json"), """{"theme":"dark"}""");
		var session = SessionManager.Create(_root, Path.Combine(agentDir, "sessions", "fixture"));
		session.AppendMessage(new UserMessage { Content = MessageContent.FromText("theme fixture question") });
		session.AppendMessage(new AssistantMessage
		{
			Content = [new ThinkingContent { Thinking = "fixture reasoning" }, new TextContent { Text = "**Fixture answer**\n\n```csharp\nvar value = 1;\n```" }],
		});
		session.AppendMessage(new ToolResultMessage
		{
			ToolName = "read", ToolCallId = "fixture", Content = [new TextContent { Text = "fixture tool output" }],
		});

		var info = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.ArgumentList.Add(shell);
		info.ArgumentList.Add("--theme-smoke");
		info.ArgumentList.Add("--cwd");
		info.ArgumentList.Add(_root);
		info.Environment["PI_CODING_AGENT_DIR"] = agentDir;

		using var process = Process.Start(info)!;
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		try { await process.WaitForExitAsync(timeout.Token); }
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
			throw;
		}
		var stdout = await stdoutTask;
		var stderr = await stderrTask;
		_output.WriteLine(stdout);

		Assert.True(process.ExitCode == 0, $"theme smoke 退出码 {process.ExitCode}\n{stdout}\n{stderr}");
		Assert.Contains("theme smoke ok", stdout);
		Assert.Contains("theme controls ok: selection, picker, border, draft, caret", stdout);
		Assert.DoesNotContain("换肤刷新失败", stderr);
		Assert.Contains("标签数=1", stdout);

		// 换肤确实改变了生效主题，而不是只写了偏好文件。
		Assert.Contains("切换前 主题=深色", stdout);
		Assert.Contains("切换后 主题=浅色", stdout);

		// 换肤后偏好被写回：切换按钮承担了「立即生效 + 记住选择」两件事。
		Assert.Contains("light", File.ReadAllText(Path.Combine(agentDir, "desktop.json")));
	}
}
