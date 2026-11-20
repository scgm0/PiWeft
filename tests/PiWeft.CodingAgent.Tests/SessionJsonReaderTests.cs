using System.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// `Utf8JsonReader` 解析路径与 `JsonNode` 路径必须逐字节等价：会话文件是互通契约，
/// 一条字段写错就是另一份历史。每条用例都把两边解析结果重新序列化再比较。
public sealed class SessionJsonReaderTests
{
	private static string Serialize(SessionLine line) =>
		line.Header is { } header ? SessionJson.Serialize(header) : SessionJson.Serialize(line.Entry!);

	public static TheoryData<string> Lines()
	{
		var big = new string('长', 20_000);
		var escaped = System.Text.Json.JsonEncodedText.Encode(
			string.Join('\n', Enumerable.Repeat("quote \" and \\ and tab \t", 600))).ToString();
		return new TheoryData<string>
		{
			"""{"type":"session","version":3,"id":"s1","timestamp":"2026-01-01T00:00:00.000Z","cwd":"/w","origin":"subagent"}""",
			"""{"type":"message","id":"e1","parentId":null,"timestamp":"2026-01-01T00:00:01.000Z","message":{"role":"user","content":"hello","timestamp":1}}""",
			"""{"type":"message","id":"e2","parentId":"e1","timestamp":"2026-01-01T00:00:02.000Z","message":{"role":"user","content":[{"type":"text","text":"block text"}],"customType":"note","display":false,"timestamp":2}}""",
			("""{"type":"message","id":"e3","parentId":"e2","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"__BIG__"},{"type":"thinking","thinking":"hmm","thinkingSignature":"sig","redacted":true},{"type":"toolCall","id":"c1","name":"bash","arguments":{"command":"ls"},"namespace":"mcp__x"}],"api":"openai-responses","provider":"openai","model":"gpt-6.2-sol","usage":{"input":10,"output":2,"cacheRead":3,"cacheWrite":4,"cacheWrite1h":5,"reasoning":6,"totalTokens":19,"cost":{"input":0.1,"output":0.2,"cacheRead":0.01,"cacheWrite":0.02,"total":0.33}},"stopReason":"stop","endTurn":true,"timestamp":3,"durationMs":12.5,"firstTokenMs":0.4,"streamingDurationMs":12.1}}""").Replace("__BIG__", big),
			("""{"type":"message","id":"e4","parentId":"e3","timestamp":"t","message":{"role":"toolResult","toolCallId":"c1","toolName":"bash","content":[{"type":"text","text":"__ESCAPED__"}],"details":{"exit":0},"isError":false,"timestamp":4,"durationMs":3.5,"nestedCalls":[{"name":"x"}]}}""").Replace("\"__ESCAPED__\"", "\"" + escaped + "\""),
			"""{"type":"message","id":"e5","parentId":"e4","timestamp":"t","message":{"role":"system","content":"sys","sections":{"preamble":"p","tools":null},"toolsAdded":[{"name":"read","description":"d","parameters":{"type":"object"}}],"toolsRemoved":[{"name":"write"}],"timestamp":5}}""",
			"""{"type":"custom_message","id":"e6","parentId":"e5","timestamp":"t","customType":"pi-deck-todo","content":[{"type":"text","text":"todo"}],"details":{"a":1},"display":true}""",
			"""{"type":"custom","id":"e7","parentId":"e6","timestamp":"t","customType":"subagent","data":{"tokens":42,"maxTokens":100,"version":3,"nested":{"ok":true}}}""",
			"""{"type":"context_edit","id":"e8","parentId":"e7","timestamp":"t","targetId":"e3","replacement":{"content":[{"type":"text","text":"replaced"}]}}""",
			"""{"type":"context_edit","id":"e9","parentId":"e8","timestamp":"t","targetId":"e3","replacement":null}""",
			"""{"type":"compaction","id":"e10","parentId":"e9","timestamp":"t","summary":"sum","firstKeptEntryId":"e3","tokensBefore":1234,"details":{"readFiles":["a"]},"usage":{"input":1,"output":2,"totalTokens":3},"fromHook":true,"systemMessage":{"role":"system","content":"checkpoint","sections":{"x":"y"},"timestamp":6}}""",
			"""{"type":"branch_summary","id":"e11","parentId":"e10","timestamp":"t","fromId":"e1","summary":"branch","details":{"k":1},"usage":{"input":1,"output":1,"totalTokens":2},"fromHook":false}""",
			"""{"type":"model_change","id":"e12","parentId":"e11","timestamp":"t","provider":"openai","modelId":"gpt-6.2-sol"}""",
			"""{"type":"thinking_level_change","id":"e13","parentId":"e12","timestamp":"t","thinkingLevel":"high"}""",
			"""{"type":"usage","id":"e14","parentId":"e13","timestamp":"t","kind":"cache_warm","provider":"p","model":"m","usage":{"input":1,"output":2,"totalTokens":3},"note":"n"}""",
			"""{"type":"label","id":"e15","parentId":"e14","timestamp":"t","targetId":"e3","label":"mine"}""",
			"""{"type":"deleted","id":"e16","parentId":"e15","timestamp":"t","originalEntryId":"e3","reason":"resend"}""",
			"""{"type":"session_info","id":"e17","parentId":"e16","timestamp":"t","name":"named"}""",
			"""{"type":"pi.future","id":"e18","parentId":"e17","timestamp":"t","payload":{"deep":[1,2,{"x":null}]},"flag":true}""",
			"""{"type":"message","id":"e19","parentId":"e18","timestamp":"t","message":{"role":"hookMessage","content":"legacy","timestamp":7,"extraField":{"k":"v"}}}""",
			"""{"type":"message","id":"e20","parentId":"e19","timestamp":"t","message":{"role":"user","content":[{"type":"image","data":"AAAA","mimeType":"image/png"}],"timestamp":8}}""",
			"""{"type":"message","id":"e21","parentId":"e20","timestamp":"t","message":{"role":"user","content":"","timestamp":9}}""",
			"""{"type":"message","id":"e22","parentId":"e21","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"x"}],"api":"openai-responses","provider":"openai","model":"gpt-6.2-sol","stopReason":"stop","timestamp":10,"diagnostics":[{"type":"openai_input_transformations","timestamp":11,"details":{"transformations":[{"type":"reasoning","id":"rs_1","action":"removed","reason":"prefix_mismatch"}]}}]}}""",
		};
	}

	[Theory]
	[MemberData(nameof(Lines))]
	public void ReaderMatchesJsonNodePathByteForByte(string line)
	{
		var expected = SessionJson.ParseLine(line);
		var actual = SessionJsonReader.Parse(Encoding.UTF8.GetBytes(line));

		Assert.NotNull(expected);
		Assert.NotNull(actual);
		Assert.Equal(Serialize(expected.Value), Serialize(actual.Value));
	}

	[Fact]
	public void LargeTextIsDeferredWithoutChangingTheBytes()
	{
		var body = new string('x', 20_000);
		var line = """{"type":"message","id":"e1","parentId":null,"timestamp":"t","message":{"role":"toolResult","toolCallId":"c","toolName":"bash","content":[{"type":"text","text":"__BODY__"}],"timestamp":1}}"""
			.Replace("__BODY__", body);
		var parsed = SessionJsonReader.Parse(Encoding.UTF8.GetBytes(line));
		var entry = Assert.IsType<SessionMessageEntry>(parsed!.Value.Entry);
		var result = Assert.IsType<ToolResultMessage>(entry.Message);
		var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
		Assert.True(text.IsTextDeferred);
		Assert.Equal(body, text.Text);
	}

	[Fact]
	public void EscapedLargeTextCountsUtf16Units()
	{
		// 含换行 → 走解转义路径；emoji 是代理对，TextLength 必须与其它路径一样按 UTF-16 计。
		var body = string.Join('\n', Enumerable.Repeat("行 with emoji 😀 and text", 600));
		var line = """{"type":"message","id":"e1","parentId":null,"timestamp":"t","message":{"role":"toolResult","toolCallId":"c","toolName":"bash","content":[{"type":"text","text":"__BODY__"}],"timestamp":1}}"""
			.Replace("\"__BODY__\"", "\"" + System.Text.Json.JsonEncodedText.Encode(body) + "\"");
		var parsed = SessionJsonReader.Parse(Encoding.UTF8.GetBytes(line));
		var entry = Assert.IsType<SessionMessageEntry>(parsed!.Value.Entry);
		var result = Assert.IsType<ToolResultMessage>(entry.Message);
		var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
		Assert.True(text.IsTextDeferred);
		Assert.Equal(body.Length, text.TextLength);
		Assert.Equal(body, text.Text);
	}

	[Fact]
	public void CrossTypeAndCrossRoleFieldsStayInExtra()
	{
		var modelChange = """{"type":"model_change","id":"e1","parentId":null,"timestamp":"t","provider":"p","modelId":"m","usage":{"input":1}}""";
		var entry = Assert.IsType<ModelChangeEntry>(SessionJsonReader.Parse(Encoding.UTF8.GetBytes(modelChange))!.Value.Entry);
		Assert.True(entry.Extra!.ContainsKey("usage"));

		var messageLine = """{"type":"message","id":"e2","parentId":null,"timestamp":"t","message":{"role":"assistant","content":[],"details":{"x":1},"usage":{"input":1},"timestamp":1}}""";
		var message = Assert.IsType<AssistantMessage>(
			Assert.IsType<SessionMessageEntry>(SessionJsonReader.Parse(Encoding.UTF8.GetBytes(messageLine))!.Value.Entry).Message);
		// details 是 toolResult 的字段；assistant 上的同名字段不能被子句 switch 吃掉。
		Assert.True(message.Extra!.ContainsKey("details"));
	}
}
