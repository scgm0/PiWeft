using PiWeft.CodingAgent.Session;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 侧栏摘要的磁盘缓存。它换掉的是「每次启动把 300+ MiB 会话全扫一遍」——
/// 失效判据必须与内存缓存完全一致（长度 / mtime / 首行指纹），否则会显示过期的消息数与预览。
/// </summary>
public class SessionSummaryStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-summary-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionSummaryStoreTests() => Directory.CreateDirectory(_root);

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

	private static SessionSummaryStamp Stamp(string id, string preview, long length = 100, long mtime = 638000000000000000) => new()
	{
		Length = length,
		LastWriteTicks = mtime,
		HeadHash = 12345,
		Offset = 42,
		Id = id,
		Name = "名字",
		Cwd = "/tmp/x",
		MessageCount = 7,
		Preview = preview,
	};

	[Fact]
	public void RoundTripsThroughDisk()
	{
		var entries = new Dictionary<string, SessionSummaryStamp>(StringComparer.Ordinal)
		{
			[Path.Combine(_root, "a.jsonl")] = Stamp("a", "预览 A"),
			[Path.Combine(_root, "b.jsonl")] = Stamp("b", "预览 B"),
		};

		SessionSummaryStore.Save(entries, _root);
		var loaded = SessionSummaryStore.Load(_root);

		Assert.Equal(2, loaded.Count);
		var a = loaded[Path.Combine(_root, "a.jsonl")];
		Assert.Equal("a", a.Id);
		Assert.Equal("预览 A", a.Preview);
		Assert.Equal(7, a.MessageCount);
		Assert.Equal("/tmp/x", a.Cwd);
		Assert.Equal(100, a.Length);

		// 64 位字段不能丢精度：mtime ticks 与首行指纹都在 2^53 以上。
		Assert.Equal(638000000000000000, a.LastWriteTicks);
		Assert.Equal(12345, a.HeadHash);
		Assert.Equal(42, a.Offset);
	}

	[Fact]
	public void MissingFileLoadsAsEmpty()
	{
		Assert.Empty(SessionSummaryStore.Load(Path.Combine(_root, "nope")));
	}

	[Fact]
	public void CorruptFileIsIgnoredInsteadOfThrowing()
	{
		var path = SessionSummaryStore.Path(_root);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "{ 这不是缓存 ");

		Assert.Empty(SessionSummaryStore.Load(_root));
	}

	[Fact]
	public void VersionMismatchIsIgnored()
	{
		var path = SessionSummaryStore.Path(_root);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, """{"version":999,"entries":{"/x.jsonl":{"length":1,"mtime":2}}}""");

		// 旧/新版本的结构可能不同，宁可全量重扫也不能猜。
		Assert.Empty(SessionSummaryStore.Load(_root));
	}

	[Fact]
	public void LongPreviewIsTruncatedOnSave()
	{
		var entries = new Dictionary<string, SessionSummaryStamp>(StringComparer.Ordinal)
		{
			[Path.Combine(_root, "a.jsonl")] = Stamp("a", new string('长', 500)),
		};

		SessionSummaryStore.Save(entries, _root);

		Assert.Equal(200, SessionSummaryStore.Load(_root)[Path.Combine(_root, "a.jsonl")].Preview.Length);
	}

	[Fact]
	public void EntryCountIsCapped()
	{
		var entries = new Dictionary<string, SessionSummaryStamp>(StringComparer.Ordinal);
		for (var index = 0; index < SessionSummaryStore.MaxEntries + 20; index++)
		{
			// 时间戳递增：截断保留最近的那些。
			entries[Path.Combine(_root, $"s{index}.jsonl")] = Stamp("s", "p", mtime: 638000000000000000 + index);
		}

		SessionSummaryStore.Save(entries, _root);

		var loaded = SessionSummaryStore.Load(_root);
		Assert.Equal(SessionSummaryStore.MaxEntries, loaded.Count);
		Assert.True(loaded.ContainsKey(Path.Combine(_root, $"s{SessionSummaryStore.MaxEntries + 19}.jsonl")));
		Assert.False(loaded.ContainsKey(Path.Combine(_root, "s0.jsonl")));
	}
}

/// <summary>
/// 目录级行为：上一轮运行留下的摘要必须真的被下一轮用上，且文件变了要能失效。
/// </summary>
// 改了进程级 PI_CODING_AGENT_DIR：必须与其它同类测试串行。
[Collection("process-environment")]
public sealed class SessionCatalogSummaryCacheTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-catalog-cache-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly Dictionary<string, string> _previous = new(StringComparer.Ordinal);
	private readonly string _agent;
	private readonly string _sessions;

	public SessionCatalogSummaryCacheTests()
	{
		_agent = Path.Combine(_root, "agent");
		_sessions = Path.Combine(_agent, "sessions", SessionPaths.EncodeCwd(_root));
		Directory.CreateDirectory(_sessions);
		_previous["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? string.Empty;
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agent);
	}

	public void Dispose()
	{
		foreach (var (key, value) in _previous)
		{
			Environment.SetEnvironmentVariable(key, value.Length == 0 ? null : value);
		}

		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	private string WriteSession(string id, string text)
	{
		var path = Path.Combine(_sessions, $"2026-01-01T00-00-00-000Z_{id}.jsonl");
		File.WriteAllLines(path,
		[
			"""{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/x"}""",
			$$$"""{"id":"u1","parentId":"s","type":"message","timestamp":"t","message":{"role":"user","content":[{"type":"text","text":"{{{text}}}"}],"timestamp":1}}""",
		]);
		return path;
	}

	[Fact]
	public void SummarySurvivesIntoTheNextRun()
	{
		var path = WriteSession("s1", "AAAA");
		var first = Assert.Single(new SessionCatalog().List());
		Assert.Equal("AAAA", first.Preview);
		Assert.Equal(1, first.MessageCount);
		Assert.True(File.Exists(SessionSummaryStore.Path(_agent)), "摘要应写回磁盘");

		// 同长度、同 mtime 地改内容：只有「真的没有重扫」才会仍然报旧预览。
		var stamp = File.GetLastWriteTimeUtc(path);
		var original = File.ReadAllText(path);
		File.WriteAllText(path, original.Replace("\"AAAA\"", "\"BBBB\"", StringComparison.Ordinal));
		File.SetLastWriteTimeUtc(path, stamp);

		// 新实例 = 下一次启动：内存缓存是空的，只能靠磁盘摘要。
		var cached = Assert.Single(new SessionCatalog().List());
		Assert.Equal("AAAA", cached.Preview);
		Assert.Equal(stamp, cached.Modified);

		// 一旦 mtime 变了就必须重扫，否则界面会一直显示过期预览。
		File.SetLastWriteTimeUtc(path, stamp.AddSeconds(5));
		var refreshed = Assert.Single(new SessionCatalog().List());
		Assert.Equal("BBBB", refreshed.Preview);
	}

	[Fact]
	public void DeletedSessionIsPrunedFromTheSavedSummary()
	{
		var path = WriteSession("s1", "甲");
		WriteSession("s2", "乙");
		Assert.Equal(2, new SessionCatalog().List().Count);
		Assert.Equal(2, SessionSummaryStore.Load(_agent).Count);

		File.Delete(path);
		Assert.Single(new SessionCatalog().List());

		// 已删除的路径不该留在缓存文件里（缓存不能变成会话内容的残留副本）。
		var saved = SessionSummaryStore.Load(_agent);
		Assert.Single(saved);
		Assert.DoesNotContain(path, saved.Keys);
	}

	[Fact]
	public void CorruptSummaryFallsBackToAFullScan()
	{
		WriteSession("s1", "丙");
		var path = SessionSummaryStore.Path(_agent);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "垃圾数据");

		var item = Assert.Single(new SessionCatalog().List());
		Assert.Equal("丙", item.Preview);
	}
}
