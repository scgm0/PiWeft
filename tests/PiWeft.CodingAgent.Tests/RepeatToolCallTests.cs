using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 同一工具用相同参数反复调用时，第 3/5/8 次在结果末尾补一句提醒（DSH 的 repeat-tool-reminder 同义）。
/// 提醒不改变工具输出本身，用户发新消息后计数清零。
public sealed class RepeatToolCallTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-repeat-" + Guid.NewGuid().ToString("N")[..8]);

	public RepeatToolCallTests()
	{
		Directory.CreateDirectory(_root);
		File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private CodingAgentSession CreateSession(ScriptedStreamFn script) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings(),
		});

	private static List<string> ToolResultTexts(CodingAgentSession session) =>
		[.. session.Session.Entries.OfType<SessionMessageEntry>()
			.Select(static entry => entry.Message)
			.OfType<ToolResultMessage>()
			.Select(static result => string.Join("\n", result.Content.OfType<TextContent>().Select(static text => text.Text)))];

	[Fact]
	public async Task ThirdAndFifthIdenticalCallsCarryTheReminder()
	{
		var script = new ScriptedStreamFn();
		for (var index = 0; index < 5; index++)
		{
			script.EnqueueToolCall($"c{index}", "read", new JsonObject { ["path"] = "a.txt" });
		}

		script.EnqueueText("done");
		using var session = CreateSession(script);
		await session.PromptAsync("go");

		var texts = ToolResultTexts(session);
		Assert.Equal(5, texts.Count);
		Assert.DoesNotContain("重复调用提醒", texts[0], StringComparison.Ordinal);
		Assert.DoesNotContain("重复调用提醒", texts[1], StringComparison.Ordinal);
		Assert.Contains("已调用 3 次", texts[2], StringComparison.Ordinal);
		Assert.DoesNotContain("重复调用提醒", texts[3], StringComparison.Ordinal);
		Assert.Contains("已调用 5 次", texts[4], StringComparison.Ordinal);
		// 工具自己的输出还在，提醒只是追加。
		Assert.Contains("hello", texts[2], StringComparison.Ordinal);
	}

	[Fact]
	public async Task NewUserPromptResetsTheCounter()
	{
		var script = new ScriptedStreamFn();
		for (var index = 0; index < 3; index++)
		{
			script.EnqueueToolCall($"c{index}", "read", new JsonObject { ["path"] = "a.txt" });
		}

		script.EnqueueText("first");
		script.EnqueueToolCall("c9", "read", new JsonObject { ["path"] = "a.txt" });
		script.EnqueueText("second");

		using var session = CreateSession(script);
		await session.PromptAsync("go");
		await session.PromptAsync("again");

		var texts = ToolResultTexts(session);
		Assert.Contains("已调用 3 次", texts[2], StringComparison.Ordinal);
		// 第二轮重新从 1 计：新用户消息之后不再出现提醒。
		Assert.DoesNotContain("重复调用提醒", texts[3], StringComparison.Ordinal);
	}
}
