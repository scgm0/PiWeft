using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class SessionCatalogTests
{
	[Fact]
	public void ScansSessionMessageAndInfoWithoutBuildingADocument()
	{
		var session = SessionCatalog.ScanLineForTest("""
			{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/pi"}
			""");
		Assert.Equal("/tmp/pi", session.Cwd);

		var info = SessionCatalog.ScanLineForTest("""
			{"type":"session_info","id":"i","parentId":"s","timestamp":"t","name":"Checkout fix"}
			""");
		Assert.Equal("Checkout fix", info.Name);

		var user = SessionCatalog.ScanLineForTest("""
			{"type":"message","id":"u","parentId":"s","timestamp":"t","message":{"role":"user","content":"first line\nsecond line","timestamp":1}}
			""");
		Assert.Equal(1, user.Messages);
		Assert.Equal("first line second line", user.Preview);

		var assistant = SessionCatalog.ScanLineForTest("""
			{"type":"message","id":"a","parentId":"u","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"answer"},{"type":"toolCall","id":"c"}],"timestamp":2}}
			""");
		Assert.Equal(1, assistant.Messages);
		Assert.Null(assistant.Preview);
	}

	[Fact]
	public void CountsToolResultsAndSkipsTheirLargeContent()
	{
		var line = """
			{"type":"message","id":"r","parentId":"a","timestamp":"t","message":{"role":"toolResult","toolCallId":"c","toolName":"bash","content":[{"type":"text","text":"a very long tool output"}],"timestamp":3}}
			""";
		var scan = SessionCatalog.ScanLineForTest(line);
		Assert.Equal(1, scan.Messages);
		Assert.Null(scan.Preview);
	}

	[Fact]
	public void HandlesContentBeforeRoleAndMalformedLines()
	{
		var line = """
			{"type":"message","id":"u","message":{"content":[{"type":"text","text":"preview"}],"role":"user"},"timestamp":"t"}
			""";
		var scan = SessionCatalog.ScanLineForTest(line);
		Assert.Equal(1, scan.Messages);
		Assert.Equal("preview", scan.Preview);

		var malformed = SessionCatalog.ScanLineForTest("""{"type":"message","message":{"role":"user","content":"unterminated"}""");
		Assert.Equal(0, malformed.Messages);
	}

	[Fact]
	public void EmptyFirstUserTextDoesNotHideTheNextUserPreview()
	{
		var scan = SessionCatalog.ScanLinesForTest([
			"""{"type":"message","message":{"role":"user","content":"   "}}""",
			"""{"type":"message","message":{"role":"user","content":"real preview"}}""",
		]);
		Assert.Equal(2, scan.Messages);
		Assert.Equal("real preview", scan.Preview);
	}

	[Fact]
	public void MalformedTrailingTokenDoesNotPartiallyApplyTheLine()
	{
		var scan = SessionCatalog.ScanLinesForTest([
			"""{"type":"message","message":{"role":"user","content":"kept"}}""",
			"""{"type":"message","message":{"role":"assistant","content":"dropped"}} trailing""",
		]);
		Assert.Equal(1, scan.Messages);
		Assert.Equal("kept", scan.Preview);
	}

	[Fact]
	public void LaterSessionInfoClearsThePreviousName()
	{
		var first = SessionCatalog.ScanLineForTest("""
			{"type":"session_info","id":"i1","parentId":"s","timestamp":"t","name":"First"}
			""");
		Assert.Equal("First", first.Name);

		var second = SessionCatalog.ScanLineForTest("""
			{"type":"session_info","id":"i2","parentId":"i1","timestamp":"t"}
			""");
		Assert.Equal(string.Empty, second.Name);
	}
}

/// <summary>
/// 目录快速字节判定必须与完整 JSON 解析逐字段一致：条数、预览、名字、cwd 都不能因为
/// 「跳过解析」而变化。这里刻意覆盖边界形状：正文里出现同名字段、超长前缀、断行、纯图片消息。
/// </summary>
public sealed class SessionCatalogFastScanTests
{
	private static void AssertSameAgreement(params string[] lines)
	{
		var full = SessionCatalog.ScanLinesForTest(lines);
		var fast = SessionCatalog.ScanLinesFastForTest(lines);
		Assert.Equal(full, fast);
	}

	[Fact]
	public void CountsOrdinaryMessageLines()
	{
		AssertSameAgreement(
			"""{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/pi"}""",
			"""{"id":"a1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"第一个问题"}],"timestamp":1}}""",
			"""{"id":"a2","parentId":"a1","type":"message","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"答"}, {"type":"toolCall","id":"c","name":"bash","arguments":{"command":"ls"}}],"timestamp":2}}""",
			"""{"id":"a3","parentId":"a2","type":"message","timestamp":"t","message":{"role":"toolResult","toolCallId":"c","toolName":"bash","content":[{"type":"text","text":"输出"}],"timestamp":3}}""");

		var scan = SessionCatalog.ScanLinesFastForTest(new[]
		{
			"""{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/pi"}""",
			"""{"id":"a1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"第一个问题"}],"timestamp":1}}""",
			"""{"id":"a2","parentId":"a1","type":"message","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"答"}],"timestamp":2}}""",
		});
		Assert.Equal("/tmp/pi", scan.Cwd);
		Assert.Equal("第一个问题", scan.Preview);
		Assert.Equal(2, scan.Messages);
	}

	[Fact]
	public void MessageBodyContainingTypeMessageIsNotDoubleCounted()
	{
		// 正文里出现 {"type":"message"} 字样（在讨论会话格式时很常见）。
		AssertSameAgreement(
			"""{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/pi"}""",
			"""{"id":"a1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"请看 {\"type\":\"message\",\"role\":\"user\"} 这行 JSON"}],"timestamp":1}}""",
			"""{"id":"a2","parentId":"a1","type":"message","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"里面提到 {\"type\":\"message\"} 不是新条目"}],"timestamp":2}}""");
		var scan = SessionCatalog.ScanLinesFastForTest(new[]
		{
			"""{"id":"a1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"{\"type\":\"message\"}"}],"timestamp":1}}""",
			"""{"id":"a2","parentId":"a1","type":"message","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"{\"type\":\"message\",\"role\":\"user\"}"}],"timestamp":2}}""",
		});
		Assert.Equal(2, scan.Messages);
	}

	[Fact]
	public void TornAndOversizedPrefixLinesStillMatchTheFullScan()
	{
		AssertSameAgreement(
			// 写到一半被截断的尾行
			"""{"id":"a1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"断在这里""",
			// 尾部有多余 token
			"""{"id":"a2","parentId":"s","type":"message","timestamp":"t","message":{"role":"assistant","content":"ok"}} trailing""",
			// 极长 id 把 type 推出前缀窗口
			"{\"id\":\"" + new string('k', 400) + "\",\"parentId\":\"s\",\"type\":\"message\",\"timestamp\":\"t\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}",
			// role 在 content 之后（外部写者）
			"""{"type":"message","id":"u","message":{"content":[{"type":"text","text":"preview"}],"role":"user"},"timestamp":"t"}""");
	}

	[Fact]
	public void ImageOnlyAndBlankFirstUserLinesStillFindThePreview()
	{
		AssertSameAgreement(
			"""{"id":"u1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"image","data":"AAA","mimeType":"image/png"}],"timestamp":1}}""",
			"""{"id":"u2","parentId":"u1","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"   "}],"timestamp":2}}""",
			"""{"id":"u3","parentId":"u2","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"真正的预览"}],"timestamp":3}}""");
		var scan = SessionCatalog.ScanLinesFastForTest(new[]
		{
			"""{"id":"u1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"image","data":"AAA","mimeType":"image/png"}],"timestamp":1}}""",
			"""{"id":"u3","parentId":"u1","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"真正的预览"}],"timestamp":3}}""",
		});
		Assert.Equal("真正的预览", scan.Preview);
		Assert.Equal(2, scan.Messages);
	}

	[Fact]
	public void NamesSetAndClearedBySessionInfoMatchTheFullScan()
	{
		AssertSameAgreement(
			"""{"id":"i1","parentId":"s","type":"session_info","timestamp":"t","name":"改名"}""",
			"""{"id":"a1","parentId":"i1","type":"message","timestamp":"t","message":{"role":"assistant","content":"答"}}""",
			"""{"id":"i2","parentId":"a1","type":"session_info","timestamp":"t"}""",
			"""{"id":"a2","parentId":"i2","type":"message","timestamp":"t","message":{"role":"assistant","content":"再答"}}""");
		var scan = SessionCatalog.ScanLinesFastForTest(new[]
		{
			"""{"id":"i1","parentId":"s","type":"session_info","timestamp":"t","name":"改名"}""",
			"""{"id":"a1","parentId":"i1","type":"message","timestamp":"t","message":{"role":"assistant","content":"答"}}""",
		});
		Assert.Equal("改名", scan.Name);
		Assert.Equal(1, scan.Messages);
	}

	[Fact]
	public void ExternalExportSpacingAndKeyOrderStillAgree()
	{
		// 外部导出的写法：type 在前、冒号后带空格。快速路径必须也认这种形状。
		var lines = new[]
		{
			"""{"type": "session", "version": 3, "id": "s", "timestamp": "t", "cwd": "/tmp/pi"}""",
			"""{"type": "message", "id": "u1", "parentId": "s", "message": {"role": "user", "content": [{"type": "text", "text": "第一个问题"}]}}""",
			"""{"type": "message", "id": "a1", "parentId": "u1", "message": {"role": "assistant", "content": [{"type": "text", "text": "答"}]}}""",
			"""{"type": "message", "id": "r1", "parentId": "a1", "message": {"role": "toolResult", "toolName": "bash", "content": [{"type": "text", "text": "输出"}]}}""",
			"""{"type": "session_info", "id": "i1", "parentId": "r1", "name": "外部导出"}""",
		};

		AssertSameAgreement(lines);

		var scan = SessionCatalog.ScanLinesFastForTest(lines);
		Assert.Equal("/tmp/pi", scan.Cwd);
		Assert.Equal("外部导出", scan.Name);
		Assert.Equal("第一个问题", scan.Preview);
		Assert.Equal(3, scan.Messages);
	}

	[Fact]
	public void ContentCarryingTheHeaderKeysDoesNotConfuseTheFastPath()
	{
		// 正文里就写着 "type" / "role"，而且 content 出现在真键之前 —— 必须退回完整解析。
		AssertSameAgreement(
			"""{"content": "讨论 \"type\": \"message\" 与 \"role\": \"assistant\" 的写法", "type": "message", "id": "u", "message": {"role": "user", "content": "正文"}}""",
			"""{"id":"x","parentId":"u","type":"message","message":{"role":"assistant","content":"ok"}}""");
	}

	[Fact]
	public void NonMessageEntryTypesAreIgnoredTheSameWay()
	{
		AssertSameAgreement(
			"""{"id":"c1","parentId":"s","type":"compaction","timestamp":"t","tokensBefore":10}""",
			"""{"id":"m1","parentId":"c1","type":"model_change","timestamp":"t","provider":"p","modelId":"m"}""",
			"""{"id":"t1","parentId":"m1","type":"thinking_level_change","timestamp":"t","thinkingLevel":"high"}""",
			"""{"id":"x1","parentId":"t1","type":"custom","customType":"k","data":{"a":1}}""",
			"""{"id":"a1","parentId":"x1","type":"message","timestamp":"t","message":{"role":"assistant","content":"答"}}""");
	}
}
