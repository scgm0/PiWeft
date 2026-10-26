using PiWeft.Cli;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// <c>pi import</c> 的命令面。来源目录与会话目录都通过参数注入，
/// 因此测试不改进程环境变量（也就不会影响并行运行的其它测试）。
/// </summary>
public sealed class ImportCommandTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-import-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _home;
	private readonly string _project;
	private readonly string _sessions;

	public ImportCommandTests()
	{
		_home = Path.Combine(_root, "home");
		_project = Path.Combine(_root, "project");
		_sessions = Path.Combine(_root, "sessions");
		Directory.CreateDirectory(_home);
		Directory.CreateDirectory(_project);
	}

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

	private void WriteClaudeSession(string sessionId, string text, string timestamp = "2026-10-20T10:00:00.000Z")
	{
		var path = Path.Combine(_home, ".claude", "projects", "-tmp-project", sessionId + ".jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var cwd = JsonString(_project);
		File.WriteAllLines(path,
		[
			$$$"""{"type":"user","sessionId":"{{{sessionId}}}","cwd":{{{cwd}}},"timestamp":"{{{timestamp}}}","message":{"role":"user","content":[{"type":"text","text":"{{{text}}}"}]}}""",
			$$$"""{"type":"assistant","sessionId":"{{{sessionId}}}","cwd":{{{cwd}}},"timestamp":"{{{timestamp}}}","message":{"role":"assistant","content":[{"type":"text","text":"好的"}]}}""",
		]);
	}

	private int Run(string[] rest, StringWriter output, StringWriter error) =>
		ImportCommands.Run(rest, output, error, _project, _home, _sessions);

	/// <summary>只做 JSON 字符串转义（Windows 路径里的反斜杠必须转义），避免在测试里引入 JSON 序列化上下文。</summary>
	private static string JsonString(string value) =>
		"\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

	private string[] ImportedFiles() =>
		Directory.Exists(_sessions) ? Directory.GetFiles(_sessions, "*.jsonl") : [];

	[Fact]
	public void ListingDoesNotWriteAnything()
	{
		WriteClaudeSession("a", "第一条");
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(0, Run([], output, error));

		Assert.Contains("第一条", output.ToString(), StringComparison.Ordinal);
		Assert.Contains("pi import", output.ToString(), StringComparison.Ordinal);
		// 「列出来就写盘」在导入这种不可逆动作上是错的默认。
		Assert.Empty(ImportedFiles());
	}

	[Fact]
	public void ImportByIndexWritesOneSession()
	{
		WriteClaudeSession("a", "第一条", "2026-10-20T10:00:00.000Z");
		WriteClaudeSession("b", "第二条", "2026-10-21T10:00:00.000Z");
		var output = new StringWriter();
		var error = new StringWriter();

		// 清单按时间倒序：1 = 第二条。
		Assert.Equal(0, Run(["1"], output, error));

		var file = Assert.Single(ImportedFiles());
		var manager = SessionManager.Open(file);
		var first = manager.Entries.OfType<SessionMessageEntry>().First();
		Assert.Equal("第二条", Assert.IsType<PiWeft.Ai.UserMessage>(first.Message).Content.Text);
		Assert.Empty(error.ToString());
	}

	[Fact]
	public void SourceFilterAndAllImportsEverything()
	{
		WriteClaudeSession("a", "Claude 的");
		var codexDirectory = Path.Combine(_home, ".codex", "sessions", "2026", "10", "21");
		Directory.CreateDirectory(codexDirectory);
		File.WriteAllLines(Path.Combine(codexDirectory, "rollout-1.jsonl"),
		[
			$$$"""{"timestamp":"2026-10-21T09:00:00.000Z","type":"session_meta","payload":{"id":"codex-1","cwd":{{{JsonString(_project)}}},"model":"gpt-5"}}""",
			"""{"timestamp":"2026-10-21T09:00:01.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Codex 的"}]}}""",
		]);

		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(0, Run(["--sources"], output, error));
		Assert.Contains("claude", output.ToString(), StringComparison.Ordinal);
		Assert.Contains("codex", output.ToString(), StringComparison.Ordinal);

		Assert.Equal(0, Run(["--from", "codex", "--all"], output, error));
		Assert.Single(ImportedFiles());

		// 再来一次 --all：两条源会话各自产出新文件，已有的那条不会被覆盖
		// （重复导入是「再留一份产物」，不是「覆盖上一次的转换结果」）。
		Assert.Equal(0, Run(["--all"], output, error));
		Assert.Equal(3, ImportedFiles().Length);
	}

	[Fact]
	public void ProjectFilterExcludesOtherDirectories()
	{
		WriteClaudeSession("a", "本项目的");
		var other = Path.Combine(_root, "other");
		Directory.CreateDirectory(other);
		var path = Path.Combine(_home, ".claude", "projects", "-tmp-other", "b.jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllLines(path,
		[
			$$$"""{"type":"user","sessionId":"b","cwd":{{{JsonString(other)}}},"timestamp":"2026-10-20T10:00:00.000Z","message":{"role":"user","content":[{"type":"text","text":"别的项目的"}]}}""",
		]);

		var output = new StringWriter();
		var error = new StringWriter();
		Assert.Equal(0, Run(["--project", _project, "--all"], output, error));

		var file = Assert.Single(ImportedFiles());
		var manager = SessionManager.Open(file);
		Assert.Equal(_project, manager.Cwd);
	}

	[Fact]
	public void UnknownSourceIsRejectedWithTheAvailableKeys()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(2, Run(["--from", "cursor"], output, error));
		Assert.Contains("claude", error.ToString(), StringComparison.Ordinal);
		Assert.Empty(ImportedFiles());
	}

	[Fact]
	public void EmptyLibraryExitsNonZeroWithoutWriting()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(1, Run([], output, error));
		Assert.Contains("没有找到可导入的外部会话", error.ToString(), StringComparison.Ordinal);
		Assert.Empty(ImportedFiles());
	}

	[Fact]
	public void OutOfRangeIndexIsRejected()
	{
		WriteClaudeSession("a", "只有一条");
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(2, Run(["7"], output, error));
		Assert.Contains("超出范围", error.ToString(), StringComparison.Ordinal);
		Assert.Empty(ImportedFiles());
	}

	[Fact]
	public void CwdOverrideRetargetsTheImportedSession()
	{
		WriteClaudeSession("a", "要搬走的");
		var moved = Path.Combine(_root, "moved");
		Directory.CreateDirectory(moved);

		var output = new StringWriter();
		var error = new StringWriter();
		Assert.Equal(0, Run(["1", "--cwd", moved], output, error));

		var manager = SessionManager.Open(Assert.Single(ImportedFiles()));
		Assert.Equal(moved, manager.Cwd);
	}
}
