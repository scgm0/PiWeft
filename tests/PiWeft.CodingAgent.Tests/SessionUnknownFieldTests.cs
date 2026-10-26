using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 未知字段的无损往返。pi 与其外围工具会往会话行里塞模型层还不认识的字段
/// （codemode 的 <c>message.nestedCalls</c> 就是真实一例），本端口读进来再写回去时
/// 必须原样搬回 —— 否则 <c>fork</c>、导入后落盘就是一次静默的数据丢失。
/// </summary>
public class SessionUnknownFieldTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-unknown-fields-" + Guid.NewGuid().ToString("N"));

	public SessionUnknownFieldTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	private static string RoundTrip(string line)
	{
		var parsed = Assert.IsAssignableFrom<SessionEntry>(SessionJson.ParseLine(line));
		var buffer = new System.Buffers.ArrayBufferWriter<byte>();
		SessionJson.WriteEntryLine(buffer, parsed);
		return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan).TrimEnd('\n');
	}

	private static void AssertSameJson(string expected, string actual) =>
		Assert.True(
			JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
			$"预期:\n{expected}\n实际:\n{actual}");

	[Fact]
	public void ToolResultKeepsNestedCallsFromCodeMode()
	{
		// codemode 把嵌套工具调用台账写在 toolResult 消息的 nestedCalls 上。
		const string line = """
			{"type":"message","id":"6c5e73b6","parentId":"7bc692e4","timestamp":"2026-10-20T13:57:00.996Z","message":{"role":"toolResult","toolCallId":"call_a3|fc_0c","toolName":"codemode","content":[{"type":"text","text":"ok"}],"isError":false,"nestedCalls":{"calls":[{"id":"call_a3|fc_0c/1","name":"todo","status":"ok","arguments":{"action":"list"},"durationMs":13}]},"durationMs":42,"timestamp":2}}
			""";

		AssertSameJson(line, RoundTrip(line));
	}

	[Fact]
	public void EveryRoleKeepsUnknownFields()
	{
		string[] lines =
		[
			"""{"type":"message","id":"a1","parentId":null,"timestamp":"t","message":{"role":"system","content":"hi","timestamp":1,"someFutureFlag":true,"nested":{"k":[1,2]}}}""",
			"""{"type":"message","id":"a2","parentId":"a1","timestamp":"t","message":{"role":"user","content":"hi","timestamp":1,"userExtras":[{"x":1}]}}""",
			"""{"type":"message","id":"a3","parentId":"a2","timestamp":"t","message":{"role":"assistant","content":[],"api":"x","provider":"p","model":"m","usage":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"reasoning":0,"totalTokens":3,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"stop","timestamp":1,"futureMetricMs":12.5}}""",
		];

		foreach (var line in lines)
		{
			AssertSameJson(line, RoundTrip(line));
		}
	}

	[Fact]
	public void UnknownMessageRoleIsPreservedWhole()
	{
		// 未知角色整体落进 CustomMessage.Raw，已知 + 未知字段都不动。
		const string line = """{"type":"message","id":"b1","parentId":null,"timestamp":"t","message":{"role":"bashExecution","command":"ls","exitCode":0,"content":"out","timestamp":1}}""";
		AssertSameJson(line, RoundTrip(line));
	}

	[Fact]
	public void UnknownEntryTypeIsPreservedWhole()
	{
		// 以前未知类型会被当成 custom 收下，写回就变成 {"type":"custom","customType":"...","data":{整行}}。
		const string line = """{"type":"usage_v2","id":"c1","parentId":null,"timestamp":"t","kind":"cache_warm","tokens":{"in":3},"nested":{"a":1}}""";

		var parsed = Assert.IsType<UnknownSessionEntry>(SessionJson.ParseLine(line));
		Assert.Equal("usage_v2", parsed.Type);
		Assert.Equal("usage_v2", parsed.Raw["type"]!.GetValue<string>());
		AssertSameJson(line, RoundTrip(line));
	}

	[Fact]
	public void UnknownEntryFieldsArePreserved()
	{
		const string line = """{"type":"model_change","id":"d1","parentId":null,"timestamp":"t","provider":"p","modelId":"m","switchedBy":"user"}""";
		AssertSameJson(line, RoundTrip(line));

		var parsed = Assert.IsType<ModelChangeEntry>(SessionJson.ParseLine(line));
		Assert.Equal("user", parsed.Extra!["switchedBy"]!.GetValue<string>());
	}

	[Fact]
	public void HeaderKeepsUnknownFields()
	{
		const string line = """{"type":"session","version":3,"id":"s1","timestamp":"t","cwd":"/tmp","hermesExtras":{"v":1}}""";
		AssertSameJson(line, SessionJson.Serialize(SessionJson.ParseHeader(JsonNode.Parse(line)!.AsObject())));
	}

	[Fact]
	public void ForkKeepsUnknownFieldsOnDisk()
	{
		// 真实的丢失路径：fork 会逐条重序列化整棵历史再写新文件。
		var sourceDir = Path.Combine(_root, "source");
		Directory.CreateDirectory(sourceDir);
		var source = Path.Combine(sourceDir, "session.jsonl");
		File.WriteAllLines(source,
		[
			"""{"type":"session","version":3,"id":"s1","timestamp":"2026-10-20T13:56:27.107Z","cwd":"/tmp"}""",
			"""{"type":"message","id":"m1","parentId":null,"timestamp":"2026-10-20T13:56:28.000Z","message":{"role":"user","content":"跑一下","timestamp":1}}""",
			"""{"type":"message","id":"m2","parentId":"m1","timestamp":"2026-10-20T13:57:00.996Z","message":{"role":"toolResult","toolCallId":"c1","toolName":"codemode","content":[{"type":"text","text":"ok"}],"isError":false,"nestedCalls":{"calls":[{"id":"c1/1","name":"read","status":"ok"}]},"timestamp":2}}""",
		]);

		var forked = SessionManager.ForkFrom(source, _root, Path.Combine(_root, "sessions"));
		Assert.False(string.IsNullOrEmpty(forked.SessionFile));

		var rewritten = File.ReadAllLines(forked.SessionFile);
		var toolLine = Assert.Single(rewritten, line => line.Contains("\"toolResult\"", StringComparison.Ordinal));
		Assert.Contains("\"nestedCalls\"", toolLine, StringComparison.Ordinal);
		Assert.True(
			JsonNode.DeepEquals(
				JsonNode.Parse("""{"calls":[{"id":"c1/1","name":"read","status":"ok"}]}"""),
				JsonNode.Parse(toolLine)!["message"]!["nestedCalls"]),
			"nestedCalls 在 fork 后必须原样保留");
	}
}
