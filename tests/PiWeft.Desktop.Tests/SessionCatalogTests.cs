using System.Text;
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

	[Fact]
	public void InvalidatingARewrittenSessionRecomputesPreviewAndCount()
	{
		// 编辑重发的墓碑截断会原地重写会话文件：文件长度可能反而变长，
		// 进程内摘要缓存里的续扫偏移就失效了，必须先丢弃再重扫。
		var agent = Path.Combine(_root, "agent");
		var sessions = Path.Combine(agent, "sessions", PiWeft.CodingAgent.Session.SessionPaths.EncodeCwd(_root));
		Directory.CreateDirectory(sessions);
		_previousAgentDir["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", agent);

		var path = Path.Combine(sessions, "2026-03-01T00-00-00-000Z_rewrite.jsonl");
		var header = """{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/x"}""";
		File.WriteAllLines(path,
		[
			header,
			"""{"id":"u1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"旧问题"}],"timestamp":1}}""",
			"""{"id":"a1","parentId":"u1","type":"message","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"旧回答"}],"timestamp":2}}""",
		]);

		var catalog = new SessionCatalog();
		var before = Assert.Single(catalog.List());
		Assert.Equal("旧问题", before.Preview);
		Assert.Equal(2, before.MessageCount);

		// 截断后的文件：原条目全部成为墓碑，新提问接在父条目后面。
		File.WriteAllLines(path,
		[
			header,
			"""{"type":"deleted","id":"u1","parentId":"s","timestamp":"t","originalEntryId":"u1","reason":"resend-truncate"}""",
			"""{"type":"deleted","id":"a1","parentId":"u1","timestamp":"t","originalEntryId":"a1","reason":"resend-truncate"}""",
			"""{"id":"u2","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"新问题"}],"timestamp":3}}""",
		]);

		catalog.Invalidate(path);
		var after = Assert.Single(catalog.List());
		Assert.Equal("新问题", after.Preview);
		Assert.Equal(1, after.MessageCount);
	}

	[Fact]
	public void InvalidatingBeforeTheSidebarLoadsIgnoresThePersistedSummary()
	{
		// 侧栏还没加载（磁盘摘要尚未装入）时发生编辑重发：Invalidate 必须能拦住磁盘上的旧摘要，
		// 否则会拿旧偏移续扫重写后的文件。
		var agent = Path.Combine(_root, "agent");
		var sessions = Path.Combine(agent, "sessions", PiWeft.CodingAgent.Session.SessionPaths.EncodeCwd(_root));
		Directory.CreateDirectory(sessions);
		_previousAgentDir["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", agent);

		var path = Path.Combine(sessions, "2026-03-02T00-00-00-000Z_stale.jsonl");
		File.WriteAllLines(path,
		[
			"""{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/x"}""",
			"""{"id":"u1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"真正的问题"}],"timestamp":1}}""",
		]);

		var headHash = Fnv1a(Encoding.UTF8.GetBytes(File.ReadLines(path).First()));
		SessionSummaryStore.Save(new Dictionary<string, SessionSummaryStamp>(StringComparer.Ordinal)
		{
			[path] = new SessionSummaryStamp
			{
				// 旧长度更短、旧偏移早已越界：这正是续扫会拿到错内容的条件。
				Length = 10,
				LastWriteTicks = new FileInfo(path).LastWriteTimeUtc.AddHours(-1).Ticks,
				HeadHash = headHash,
				Offset = 1_000_000,
				Id = "stale",
				Name = string.Empty,
				Cwd = "/tmp/x",
				MessageCount = 9,
				Preview = "过期预览",
			},
		}, agent);

		// Invalidate 只改内存（UI 线程上不读写磁盘）：这一次扫描必须全量重读，
		// 而不是拿那条越界偏移续扫，否则预览会是重写前的旧文本。
		var catalog = new SessionCatalog();
		catalog.Invalidate(path);
		var item = Assert.Single(catalog.List());
		Assert.Equal("真正的问题", item.Preview);
		Assert.Equal(1, item.MessageCount);

		// 写回磁盘的是重扫后的新摘要（旧偏移已被丢弃），下一次启动不会拿它续扫。
		Assert.True(SessionSummaryStore.Load(agent).TryGetValue(path, out var stamp));
		Assert.Equal(1, stamp.MessageCount);
		Assert.Equal("真正的问题", stamp.Preview);

		// 换一个实例模拟下一次启动。
		var reopened = Assert.Single(new SessionCatalog().List());
		Assert.Equal("真正的问题", reopened.Preview);
		Assert.Equal(1, reopened.MessageCount);
	}

	[Fact]
	public void AppendingResumesFromTheScannedOffsetInsteadOfRereadingTheFile()
	{
		// 侧栏每一轮结束都会重扫变长的会话文件。续扫依赖两件事：磁盘/内存摘要里存的是
		// 「首行」指纹（不是最后一行），以及续扫点之前的字节没有被改过。
		var agent = Path.Combine(_root, "agent");
		var sessions = Path.Combine(agent, "sessions", PiWeft.CodingAgent.Session.SessionPaths.EncodeCwd(_root));
		Directory.CreateDirectory(sessions);
		_previousAgentDir["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", agent);

		var path = Path.Combine(sessions, "2026-03-04T00-00-00-000Z_resume.jsonl");
		var lines = new List<string>
		{
			"""{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/x"}""",
		};
		var payload = new string('y', 400);
		for (var index = 0; index < 60; index++)
		{
			lines.Add("{\"id\":\"u" + index + "\",\"parentId\":\"s\",\"type\":\"message\",\"timestamp\":\"t\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + payload + "\"}],\"timestamp\":1}}");
			lines.Add("{\"id\":\"a" + index + "\",\"parentId\":\"u" + index + "\",\"type\":\"message\",\"timestamp\":\"t\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ok " + index + "\"}],\"timestamp\":2}}");
		}

		File.WriteAllLines(path, lines);
		var fullLength = new FileInfo(path).Length;
		var catalog = new SessionCatalog();
		var cold = Assert.Single(catalog.List());
		Assert.Equal(120, cold.MessageCount);
		Assert.Equal(fullLength, catalog.ScannedBytesForTests);

		File.AppendAllLines(path,
		[
			"""{"id":"u99","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"新的一轮"}],"timestamp":3}}""",
			"""{"id":"a99","parentId":"u99","type":"message","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"新的回答"}],"timestamp":4}}""",
		]);

		var warm = Assert.Single(catalog.List());
		Assert.Equal(122, warm.MessageCount);
		// 只读新增的那几十字节，而不是把整份文件再来一遍。
		Assert.True(
			catalog.ScannedBytesForTests < fullLength / 4,
			$"续扫没有生效：重读了 {catalog.ScannedBytesForTests} 字节（整份 {fullLength}）");
	}

	[Fact]
	public void InPlaceRewriteKeepsTheHeaderButDoesNotReuseTheStaleOffset()
	{
		// 典型场景：pi CLI（或本端口的墓碑截断）在同一条路径上重写了会话，首行头部原样保留。
		// 只比对首行指纹时缓存会认为「前缀没变」并按旧偏移续读，侧栏从此一直显示重写前的预览。
		var agent = Path.Combine(_root, "agent");
		var sessions = Path.Combine(agent, "sessions", PiWeft.CodingAgent.Session.SessionPaths.EncodeCwd(_root));
		Directory.CreateDirectory(sessions);
		_previousAgentDir["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", agent);

		var path = Path.Combine(sessions, "2026-03-03T00-00-00-000Z_rewrite.jsonl");
		var header = """{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/x"}""";
		var first = """{"id":"u1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"旧问题"}],"timestamp":1}}""";
		File.WriteAllLines(path, [header, first]);

		var catalog = new SessionCatalog();
		var before = Assert.Single(catalog.List());
		Assert.Equal("旧问题", before.Preview);
		Assert.Equal(1, before.MessageCount);

		// 原地重写：头部保持不变，正文换成另一段更长的内容（长度变大才会走「续扫」分支）。
		var second = """{"id":"u2","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"新问题"}],"timestamp":2}}""";
		var answer = """{"id":"a2","parentId":"u2","type":"message","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"一段足够长的回答，用来把文件写到比重写前更长。"}],"timestamp":3}}""";
		File.WriteAllLines(path, [header, second, answer]);
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

		// 新实例模拟下一次启动：只有磁盘摘要可用，没有任何「这个文件被重写过」的进程内记忆。
		var after = Assert.Single(new SessionCatalog().List());
		Assert.Equal("新问题", after.Preview);
		Assert.Equal(2, after.MessageCount);
	}

	/// <summary>与 <see cref="SessionCatalog"/> 的首行指纹同算法（缓存格式的一部分）。</summary>
	private static long Fnv1a(ReadOnlySpan<byte> value)
	{
		var hash = 1469598103934665603L;
		foreach (var b in value)
		{
			hash = (hash ^ b) * 1099511628211L;
		}

		return hash;
	}
}
