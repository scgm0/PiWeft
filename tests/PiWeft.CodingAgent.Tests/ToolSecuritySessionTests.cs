using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Security;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 安全门的接线验证：纯策略单测在 <see cref="ToolSecurityTests"/>，这里证明它真的挂在
/// 工具调用链路上 —— 直接工具调用与 codemode 嵌套调用都要被拦住，<c>off</c> 等级下必须完全不干预。
/// </summary>
public class ToolSecuritySessionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-security-session-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _outside = Path.Combine(Path.GetTempPath(), "piweft-security-outside-" + Guid.NewGuid().ToString("N")[..8]);

	public ToolSecuritySessionTests()
	{
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(_outside);
	}

	public void Dispose()
	{
		foreach (var directory in new[] { _root, _outside })
		{
			try
			{
				Directory.Delete(directory, recursive: true);
			}
			catch (IOException)
			{
				// 临时目录清理失败不影响断言结论。
			}
		}
	}

	private CodingAgentSession CreateSession(ScriptedStreamFn script, ToolSecurity security, bool includeCodemode = false) =>
		new(new CodingAgentSessionOptions
		{
			// codemode 不在默认工具集里（与 pi 一致，要显式选中），需要它的用例自己带上完整目录。
			Tools = includeCodemode ? PiWeft.CodingAgent.Tools.BuiltinTools.CatalogTools(_root) : null,
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings { Security = security },
		});

	private static ToolResultMessage LastToolResult(CodingAgentSession session) =>
		Assert.IsType<ToolResultMessage>(
			session.Session.Entries.OfType<SessionMessageEntry>().Last(static entry => entry.Message is ToolResultMessage).Message);

	[Fact]
	public async Task OffLevelWritesOutsideTheWorkspace()
	{
		var target = Path.Combine(_outside, "written.txt");
		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "write", new JsonObject { ["path"] = target, ["content"] = "hello" });
		script.EnqueueText("done");

		using var session = CreateSession(script, ToolSecurity.Off);
		await session.PromptAsync("write it");

		var result = LastToolResult(session);
		Assert.False(result.IsError);
		Assert.Equal("hello", File.ReadAllText(target));
	}

	[Fact]
	public async Task StrictLevelBlocksTheSameWriteAndTellsTheModelWhy()
	{
		var target = Path.Combine(_outside, "blocked.txt");
		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "write", new JsonObject { ["path"] = target, ["content"] = "hello" });
		script.EnqueueText("ok");

		using var session = CreateSession(script, ToolSecurity.Strict);
		await session.PromptAsync("write it");

		var result = LastToolResult(session);
		Assert.True(result.IsError);
		Assert.Contains("安全策略", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.Contains("工作目录之外", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.False(File.Exists(target));
	}

	[Fact]
	public async Task StandardLevelStopsSensitiveReadsAndDangerousShellCommands()
	{
		File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=1");

		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "read", new JsonObject { ["path"] = ".env" });
		script.EnqueueToolCall("c2", "bash", new JsonObject { ["command"] = "rm -rf /" });
		script.EnqueueToolCall("c3", "read", new JsonObject { ["path"] = "README.md" });
		script.EnqueueText("done");
		File.WriteAllText(Path.Combine(_root, "README.md"), "hi");

		using var session = CreateSession(script, ToolSecurity.Standard);
		await session.PromptAsync("go");

		var results = session.Session.Entries.OfType<SessionMessageEntry>()
			.Select(static entry => entry.Message)
			.OfType<ToolResultMessage>()
			.ToList();

		Assert.Equal(3, results.Count);
		Assert.True(results[0].IsError);
		Assert.Contains("敏感文件", Assert.IsType<TextContent>(results[0].Content[0]).Text);

		Assert.True(results[1].IsError);
		Assert.Contains("危险模式", Assert.IsType<TextContent>(results[1].Content[0]).Text);

		// 普通文件照常可读：安全门不是「一刀切拒绝」。
		Assert.False(results[2].IsError);
		Assert.Contains("hi", Assert.IsType<TextContent>(results[2].Content[0]).Text);
	}

	[Fact]
	public async Task NestedCodemodeCallsGoThroughTheSameGate()
	{
		// codemode 的嵌套调用走 ExecuteNestedToolAsync，它复用同一个 beforeToolCall 钩子；
		// 这条钉住「嵌套调用不能成为绕过口」。
		var target = Path.Combine(_outside, "nested.txt");
		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "codemode", new JsonObject
		{
			["code"] = $"const r = await tools.write({{ path: {JsonValue.Create(target)!.ToJsonString()}, content: 'x' }}); return r;",
		});
		script.EnqueueText("done");

		using var session = CreateSession(script, ToolSecurity.Strict, includeCodemode: true);
		Assert.Contains(session.Tools, static tool => tool.Name == "codemode");

		await session.PromptAsync("go");

		// codemode 自己成功返回（脚本跑完了），但嵌套的写必须被拒绝。
		Assert.False(File.Exists(target));
		var texts = string.Join(
			"\n",
			session.Session.Entries.OfType<SessionMessageEntry>()
				.Select(static entry => entry.Message)
				.OfType<ToolResultMessage>()
				.SelectMany(static result => result.Content.OfType<TextContent>())
				.Select(static text => text.Text));
		Assert.Contains("工作目录之外", texts);
	}

	[Fact]
	public void SettingsDriveTheGate()
	{
		// settings.security 只从全局设置读取：写错的等级名按 off 处理，不猜更严格的等级。
		var off = CodingAgentSettings.Merge(null, null);
		Assert.False(off.Security.Enabled);

		var strict = CodingAgentSettings.Merge(new SettingsFile { Security = new SecurityFile { Level = "strict" } }, null);
		Assert.True(strict.Security.Enabled);
		Assert.Equal(ToolPathPolicy.Workspace, strict.Security.PathPolicy);
		Assert.True(strict.Security.ProtectSensitivePaths);

		var custom = CodingAgentSettings.Merge(
			new SettingsFile { Security = new SecurityFile { Level = "strict", PathPolicy = "custom", CustomAllowDirs = [_outside] } },
			null);
		Assert.Equal(ToolPathPolicy.Custom, custom.Security.PathPolicy);
		Assert.Null(custom.Security.BlockingReason("write", new JsonObject { ["path"] = Path.Combine(_outside, "a.txt") }, _root));

		var typo = CodingAgentSettings.Merge(new SettingsFile { Security = new SecurityFile { Level = "strcit" } }, null);
		Assert.False(typo.Security.Enabled);
	}
}
