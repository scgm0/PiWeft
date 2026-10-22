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

/// <summary>
/// 侧栏只扫「会显示出来的那些」：文件数超过 limit 时按修改时间截断，
/// 但不能改变排序语义，也不能让截断之外的文件影响结果。
/// </summary>
// 改了进程级 PI_CODING_AGENT_DIR：必须与其它同类测试串行，否则会互相读到对方的会话目录。
[Collection("process-environment")]
public sealed class SessionCatalogTruncationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-catalog-limit-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly Dictionary<string, string> _previousAgentDir = new(StringComparer.Ordinal);

	public SessionCatalogTruncationTests()
	{
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		foreach (var (key, value) in _previousAgentDir)
		{
			Environment.SetEnvironmentVariable(key, value.Length == 0 ? null : value);
		}

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void ListReturnsTheNewestRowsInOrderEvenWhenOlderFilesAreSkipped()
	{
		var agent = Path.Combine(_root, "agent");
		var sessions = Path.Combine(agent, "sessions", PiWeft.CodingAgent.Session.SessionPaths.EncodeCwd(_root));
		Directory.CreateDirectory(sessions);
		_previousAgentDir["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", agent);

		// 8 份会话，修改时间从旧到新；limit=3 只应返回最新 3 份。
		var paths = new List<string>();
		for (var index = 0; index < 8; index++)
		{
			var path = Path.Combine(sessions, $"2026-01-0{index + 1}T00-00-00-000Z_session{index:00}.jsonl");
			File.WriteAllLines(path,
			[
				"""{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/x"}""",
				"{\"id\":\"u" + index + "\",\"parentId\":\"s\",\"type\":\"message\",\"timestamp\":\"t\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"问题 " + index + "\"}],\"timestamp\":1}}",
			]);
			File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index));
			paths.Add(path);
		}

		var catalog = new SessionCatalog();
		var limited = catalog.List(limit: 3);

		Assert.Equal(3, limited.Count);
		Assert.Equal([paths[7], paths[6], paths[5]], limited.Select(static item => item.Path));
		// 预览仍然正确（截断不影响扫描质量）。
		Assert.Equal("问题 7", limited[0].Preview);

		// limit 大于文件数时退回全量，顺序不变。
		var all = catalog.List(limit: 99);
		Assert.Equal(8, all.Count);
		Assert.Equal(paths[7], all[0].Path);
		Assert.Equal(paths[0], all[^1].Path);
	}

	[Fact]
	public void RepeatedListReusesSummariesInsteadOfRescanning()
	{
		var agent = Path.Combine(_root, "agent");
		var sessions = Path.Combine(agent, "sessions", PiWeft.CodingAgent.Session.SessionPaths.EncodeCwd(_root));
		Directory.CreateDirectory(sessions);
		_previousAgentDir["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", agent);

		for (var index = 0; index < 3; index++)
		{
			File.WriteAllLines(Path.Combine(sessions, $"2026-02-0{index + 1}T00-00-00-000Z_reuse{index}.jsonl"),
			[
				"""{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/x"}""",
				"""{"id":"u","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"hi"}],"timestamp":1}}""",
			]);
		}

		var catalog = new SessionCatalog();
		var first = catalog.List();
		var second = catalog.List();

		Assert.Equal(3, first.Count);
		// 第二次完全命中缓存：对象复用、内容一致（缓存键是长度 + 修改时间 + 首行指纹）。
		Assert.Equal(first.Select(static item => item.Path), second.Select(static item => item.Path));
		Assert.All(second, item => Assert.True(item.Modified <= DateTimeOffset.UtcNow));

		// 追加内容后同一份会话要重新扫出新的条数。
		var target = first[0].Path;
		File.AppendAllLines(target,
		[
			"""{"id":"u2","parentId":"u","type":"message","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"ok"}],"timestamp":2}}""",
		]);
		var third = catalog.List();
		var before = first.Single(item => item.Path == target).MessageCount;
		Assert.Equal(before + 1, third.Single(item => item.Path == target).MessageCount);
	}
}
