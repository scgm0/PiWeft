using System.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent.Import;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 外部会话导入：发现 → 转换 → 落盘。
/// <para>
/// 夹具按 Claude Code 与 Codex CLI 的**磁盘格式**手写（字段名取自这两个工具的公开实现，
/// 也与 PiDeck 的对应 importer 一致），因为这些工具的会话库不一定存在于开发机上。
/// 夹具之外还有一条「产物能被 <see cref="SessionManager"/> 正常打开」的断言 ——
/// 格式对不对，最终由 pi 自己的读取端判定。
/// </para>
/// </summary>
public class SessionImportTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-import-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _home;
	private readonly string _project;

	public SessionImportTests()
	{
		_home = Path.Combine(_root, "home");
		_project = Path.Combine(_root, "project");
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

	private string WriteFile(string relative, params string[] lines)
	{
		var path = Path.Combine(_home, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllLines(path, lines, new UTF8Encoding(false));
		return path;
	}

	/// <summary>Claude Code 的一段完整对话：用户 → 助手思考+工具调用 → 工具结果 → 助手结论。</summary>
	private string WriteClaudeSession(string sessionId = "sess-claude-1")
	{
		var lines = new[]
		{
			$$$"""{"type":"user","sessionId":"{{{sessionId}}}","cwd":{{{Json(_project)}}},"timestamp":"2026-10-20T10:00:00.000Z","message":{"role":"user","content":[{"type":"text","text":"帮我看看构建为什么失败"}]}}""",
			$$$"""{"type":"assistant","sessionId":"{{{sessionId}}}","cwd":{{{Json(_project)}}},"timestamp":"2026-10-20T10:00:05.000Z","message":{"role":"assistant","model":"claude-sonnet-4-20250514","usage":{"input_tokens":120,"output_tokens":30,"cache_read_input_tokens":1000,"cache_creation_input_tokens":50},"content":[{"type":"thinking","thinking":"先跑一次构建"},{"type":"text","text":"我先跑构建看看。"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"dotnet build"}}]}}""",
			$$$"""{"type":"user","sessionId":"{{{sessionId}}}","cwd":{{{Json(_project)}}},"timestamp":"2026-10-20T10:00:20.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","is_error":true,"content":[{"type":"text","text":"error CS1002: 应输入 ;"}]}]}}""",
			$$$"""{"type":"assistant","sessionId":"{{{sessionId}}}","cwd":{{{Json(_project)}}},"timestamp":"2026-10-20T10:00:30.000Z","message":{"role":"assistant","model":"claude-sonnet-4-20250514","content":[{"type":"text","text":"原因是第 42 行缺分号。"}]}}""",
			// 子代理旁支与宿主注入的元信息都不该进产物。
			$$$"""{"type":"user","sessionId":"{{{sessionId}}}","cwd":{{{Json(_project)}}},"isSidechain":true,"timestamp":"2026-10-20T10:00:35.000Z","message":{"role":"user","content":[{"type":"text","text":"子代理的旁支"}]}}""",
			$$$"""{"type":"user","sessionId":"{{{sessionId}}}","cwd":{{{Json(_project)}}},"isMeta":true,"timestamp":"2026-10-20T10:00:36.000Z","message":{"role":"user","content":[{"type":"text","text":"<command-name>meta</command-name>"}]}}""",
		};

		return WriteFile(Path.Combine(".claude", "projects", "-tmp-project", sessionId + ".jsonl"), lines);
	}

	/// <summary>Codex 的一段完整对话：session_meta → 用户 → 思考 → 调用 → 输出 → 结论。</summary>
	private string WriteCodexSession(string sessionId = "sess-codex-1")
	{
		var lines = new[]
		{
			$$$"""{"timestamp":"2026-10-21T09:00:00.000Z","type":"session_meta","payload":{"id":"{{{sessionId}}}","cwd":{{{Json(_project)}}},"model":"gpt-5-codex","model_provider":"openai"}}""",
			"""{"timestamp":"2026-10-21T09:00:01.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"把 README 的标题改掉"}]}}""",
			"""{"timestamp":"2026-10-21T09:00:02.000Z","type":"response_item","payload":{"type":"reasoning","summary":[{"type":"summary_text","text":"先读文件"}]}}""",
			"""{"timestamp":"2026-10-21T09:00:03.000Z","type":"response_item","payload":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{\"command\":[\"cat\",\"README.md\"]}"}}""",
			"""{"timestamp":"2026-10-21T09:00:10.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_1","output":"# 旧标题\n正文"}}""",
			"""{"timestamp":"2026-10-21T09:00:12.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"标题已改为「新标题」。"}]}}""",
			// 非消息行（turn_context / token_count 等）应被自然跳过。
			"""{"timestamp":"2026-10-21T09:00:13.000Z","type":"turn_context","payload":{"cwd":"/x"}}""",
		};

		return WriteFile(Path.Combine(".codex", "sessions", "2026", "10", "21", "rollout-1.jsonl"), lines);
	}

	/// <summary>只做 JSON 字符串转义。不用 <c>JsonSerializer.Serialize</c>：
	/// 反射式序列化在裁剪/AOT 下会告警，而这里只需要字符串的一种转义。</summary>
	private static string Json(string value)
	{
		var builder = new System.Text.StringBuilder(value.Length + 8).Append('"');
		foreach (var c in value)
		{
			switch (c)
			{
				case '\\': builder.Append("\\\\"); break;
				case '"': builder.Append("\\\""); break;
				case '\n': builder.Append("\\n"); break;
				default: builder.Append(c); break;
			}
		}

		return builder.Append('"').ToString();
	}

	// ---------------------------------------------------------------- 发现

	[Fact]
	public void DiscoveryListsBothSourcesAndFiltersByProject()
	{
		WriteClaudeSession();
		WriteCodexSession();
		// 另一个项目下的 Claude 会话：按项目过滤时不该出现。
		WriteFile(
			Path.Combine(".claude", "projects", "-tmp-other", "other.jsonl"),
			$$$"""{"type":"user","sessionId":"sess-other","cwd":{{{Json(Path.Combine(_root, "other"))}}},"timestamp":"2026-10-19T10:00:00.000Z","message":{"role":"user","content":[{"type":"text","text":"别的项目"}]}}""");

		var sources = SessionImportService.DiscoverSources(_home);
		Assert.Equal(2, sources.Count);
		Assert.All(sources, static source => Assert.True(source.Exists));
		Assert.Equal(2, sources.Single(static source => source.Key == "claude").Count);
		Assert.Equal(1, sources.Single(static source => source.Key == "codex").Count);

		var all = SessionImportService.DiscoverAll(home: _home);
		Assert.Equal(3, all.Count);
		// 最近的在前。
		Assert.Equal("sess-codex-1", all[0].SessionId);

		var scoped = SessionImportService.DiscoverAll(projectPath: _project, home: _home);
		Assert.Equal(2, scoped.Count);
		Assert.DoesNotContain(scoped, static session => session.SessionId == "sess-other");

		Assert.Equal("帮我看看构建为什么失败", scoped.Single(static session => session.Source == "claude").Title);
	}

	[Fact]
	public void MissingSourceRootYieldsNothingInsteadOfThrowing()
	{
		Assert.Empty(SessionImportService.Discover("claude", home: _home));
		Assert.Empty(SessionImportService.Discover("codex", home: _home));
		Assert.All(SessionImportService.DiscoverSources(_home), static source => Assert.False(source.Exists));
	}

	[Fact]
	public void MetadataLessFilesAreNotListed()
	{
		// 头部拿不到 sessionId/cwd 的文件放进列表也没法导入，直接不进清单。
		WriteFile(Path.Combine(".claude", "projects", "-tmp-project", "broken.jsonl"), """{"type":"summary","summary":"x"}""");

		Assert.Empty(SessionImportService.Discover("claude", home: _home));
	}

	// ---------------------------------------------------------------- Claude 转换

	[Fact]
	public void ClaudeSessionConvertsToPiEntries()
	{
		var source = WriteClaudeSession();
		var session = Assert.Single(SessionImportService.Discover("claude", home: _home));

		var importer = Assert.IsAssignableFrom<IExternalSessionImporter>(SessionImportService.Find("claude"));
		var entries = importer.Convert(session);
		var messages = entries.OfType<SessionMessageEntry>().Select(static entry => entry.Message).ToList();

		// 用户 → 助手(思考+文本+工具调用) → 工具结果 → 助手结论；旁支与 meta 被丢掉。
		Assert.Equal(4, messages.Count);
		Assert.IsType<UserMessage>(messages[0]);

		var assistant = Assert.IsType<AssistantMessage>(messages[1]);
		Assert.Contains(assistant.Content, static block => block is ThinkingContent { Thinking: "先跑一次构建" });
		Assert.Contains(assistant.Content, static block => block is TextContent { Text: "我先跑构建看看。" });
		var call = Assert.IsType<ToolCall>(assistant.Content.Single(static block => block is ToolCall));
		Assert.Equal("toolu_1", call.Id);
		Assert.Equal("Bash", call.Name);
		Assert.Equal("dotnet build", call.Arguments["command"]!.GetValue<string>());
		Assert.Equal(StopReason.ToolUse, assistant.StopReason);

		// 用量与模型来自源 transcript，不能是零。
		Assert.Equal(120, assistant.Usage.Input);
		Assert.Equal(1000, assistant.Usage.CacheRead);
		Assert.Equal("claude-sonnet-4-20250514", assistant.Model);

		// tool_result 必须还原成 pi 的 toolResult，而不是用户文本。
		var toolResult = Assert.IsType<ToolResultMessage>(messages[2]);
		Assert.Equal("toolu_1", toolResult.ToolCallId);
		Assert.True(toolResult.IsError);
		Assert.Contains("CS1002", Assert.IsType<TextContent>(toolResult.Content[0]).Text);

		// toolCall ↔ toolResult 的父子链不能断：pi 靠它做分支与投影。
		var entriesById = entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
		Assert.Equal(entries[1].Id, entriesById[entries[2].Id].ParentId ?? string.Empty );

		var last = Assert.IsType<AssistantMessage>(messages[3]);
		Assert.Equal("原因是第 42 行缺分号。", Assert.IsType<TextContent>(last.Content[0]).Text);
		Assert.Equal(StopReason.Stop, last.StopReason);

		Assert.Equal(source, session.SourcePath);
	}

	// ---------------------------------------------------------------- Codex 转换

	[Fact]
	public void CodexSessionConvertsToPiEntries()
	{
		WriteCodexSession();
		var session = Assert.Single(SessionImportService.Discover("codex", home: _home));

		var importer = SessionImportService.Find("codex")!;
		var messages = importer.Convert(session).OfType<SessionMessageEntry>().Select(static entry => entry.Message).ToList();

		Assert.Equal(4, messages.Count);
		Assert.Equal("把 README 的标题改掉", Assert.IsType<UserMessage>(messages[0]).Content.Text);

		// reasoning 与 toolCall 合成同一条助手消息：思考在前、调用在后。
		var callMessage = Assert.IsType<AssistantMessage>(messages[1]);
		Assert.Contains(callMessage.Content, static block => block is ThinkingContent { Thinking: "先读文件" });
		var call = Assert.IsType<ToolCall>(callMessage.Content.Single(static block => block is ToolCall));
		Assert.Equal("call_1", call.Id);
		Assert.Equal("shell", call.Name);
		Assert.Equal("cat", call.Arguments["command"]![0]!.GetValue<string>());
		Assert.Equal("openai", callMessage.Provider);
		Assert.Equal("gpt-5-codex", callMessage.Model);

		var toolResult = Assert.IsType<ToolResultMessage>(messages[2]);
		Assert.Equal("call_1", toolResult.ToolCallId);
		Assert.Equal("shell", toolResult.ToolName);
		Assert.Contains("旧标题", Assert.IsType<TextContent>(toolResult.Content[0]).Text);
		// Codex 只有 call/output 两个时间戳：派生耗时要写出来（9s）。
		Assert.Equal(7000, toolResult.DurationMs);

		Assert.Equal("标题已改为「新标题」。", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(messages[3]).Content[0]).Text);
	}

	[Fact]
	public void CodexLegacyEventMsgUserInputIsImported()
	{
		WriteFile(
			Path.Combine(".codex", "sessions", "2025", "12", "01", "rollout-old.jsonl"),
			$$$"""{"timestamp":"2025-12-01T08:00:00.000Z","type":"session_meta","payload":{"id":"old-1","cwd":{{{Json(_project)}}},"model":"gpt-5"}}""",
			"""{"timestamp":"2025-12-01T08:00:01.000Z","type":"event_msg","payload":{"type":"user_message","message":"旧版 CLI 的用户输入"}}""",
			"""{"timestamp":"2025-12-01T08:00:02.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"收到。"}]}}""");

		var session = Assert.Single(SessionImportService.Discover("codex", home: _home));
		Assert.Equal("旧版 CLI 的用户输入", session.Title);

		var messages = SessionImportService.Find("codex")!.Convert(session)
			.OfType<SessionMessageEntry>().Select(static entry => entry.Message).ToList();

		Assert.Equal(2, messages.Count);
		Assert.Equal("旧版 CLI 的用户输入", Assert.IsType<UserMessage>(messages[0]).Content.Text);
	}

	// ---------------------------------------------------------------- 落盘

	[Fact]
	public void ImportWritesAReadablePiSession()
	{
		WriteClaudeSession();
		var session = Assert.Single(SessionImportService.Discover("claude", home: _home));
		var sessionDir = Path.Combine(_root, "sessions");

		var result = SessionImportService.Import(session, sessionDir);

		Assert.True(result.Ok, result.Error);
		Assert.True(File.Exists(result.SessionFile));
		Assert.Equal(4, result.MessageCount);

		// 产物必须能被 pi 自己的读取端打开：格式对不对最终由这里判定。
		var manager = SessionManager.Open(result.SessionFile!);
		Assert.NotNull(manager.Header);
		Assert.Equal(3, manager.Header!.Version);
		Assert.Equal(_project, manager.Cwd);

		var context = manager.BuildSessionContext();
		Assert.Equal(4, context.Messages.Count);

		// 导入标记：可追溯来源，但不进模型上下文。
		var marker = manager.Entries.OfType<CustomEntry>().Single(static entry => entry.CustomType == SessionImportService.MarkerType);
		Assert.Equal("claude", marker.Data!["source"]!.GetValue<string>());
		Assert.Equal("sess-claude-1", marker.Data["sourceSessionId"]!.GetValue<string>());
		Assert.DoesNotContain(context.Messages, static message => message.Role == SessionImportService.MarkerType);
	}

	[Fact]
	public void ImportedSessionKeepsOriginalTimestamps()
	{
		WriteClaudeSession();
		var session = Assert.Single(SessionImportService.Discover("claude", home: _home));
		var result = SessionImportService.Import(session, Path.Combine(_root, "sessions"));

		var manager = SessionManager.Open(result.SessionFile!);
		var first = manager.Entries.OfType<SessionMessageEntry>().First();
		Assert.Equal("2026-10-20T10:00:00", first.Timestamp[..19]);
		Assert.Equal(DateTimeOffset.Parse("2026-10-20T10:00:00.000Z").ToUnixTimeMilliseconds(), first.Message.Timestamp);
	}

	[Fact]
	public void ReimportIsAnAdditionNotACollision()
	{
		// 每条产物用新的会话 id 与文件名：重复导入不该覆盖上一次的产物（用户可能想对比两次转换）。
		WriteClaudeSession();
		var session = Assert.Single(SessionImportService.Discover("claude", home: _home));
		var sessionDir = Path.Combine(_root, "sessions");

		var first = SessionImportService.Import(session, sessionDir);
		var second = SessionImportService.Import(session, sessionDir);

		Assert.True(first.Ok && second.Ok);
		Assert.NotEqual(first.SessionFile, second.SessionFile);
		Assert.Equal(2, Directory.GetFiles(sessionDir, "*.jsonl").Length);
	}

	[Fact]
	public void ImportFailsCleanlyOnAnEmptySource()
	{
		var path = WriteFile(Path.Combine(".claude", "projects", "-tmp-project", "empty.jsonl"),
			$$$"""{"type":"user","sessionId":"empty","cwd":{{{Json(_project)}}},"timestamp":"2026-10-20T10:00:00.000Z"}""");

		var session = new ImportedSession
		{
			Source = "claude",
			SourceLabel = "Claude Code",
			SessionId = "empty",
			SourcePath = path,
			Cwd = _project,
			Created = DateTimeOffset.UtcNow,
		};

		var result = SessionImportService.Import(session, Path.Combine(_root, "sessions"));

		Assert.False(result.Ok);
		Assert.Contains("没有可导入的消息", result.Error);
		// 失败不能留下半截产物。
		Assert.False(Directory.Exists(Path.Combine(_root, "sessions")) &&
			Directory.GetFiles(Path.Combine(_root, "sessions")).Length > 0);
	}

	[Fact]
	public void CwdOverrideIsHonoured()
	{
		WriteClaudeSession();
		var session = Assert.Single(SessionImportService.Discover("claude", home: _home));
		var elsewhere = Path.Combine(_root, "moved");

		var result = SessionImportService.Import(session, Path.Combine(_root, "sessions"), cwdOverride: elsewhere);

		Assert.True(result.Ok, result.Error);
		var manager = SessionManager.Open(result.SessionFile!);
		Assert.Equal(elsewhere, manager.Cwd);
	}

	[Fact]
	public void GarbageLinesAreSkippedInsteadOfFailingTheImport()
	{
		// 外部工具写到一半崩掉会留下半行 JSON；整条会话不能因此报废。
		WriteFile(
			Path.Combine(".claude", "projects", "-tmp-project", "partial.jsonl"),
			$$$"""{"type":"user","sessionId":"partial","cwd":{{{Json(_project)}}},"timestamp":"2026-10-20T10:00:00.000Z","message":{"role":"user","content":[{"type":"text","text":"第一句"}]}}""",
			"""{"type":"assistant","message":{"role":"assistant","conte""",
			$$$"""{"type":"assistant","sessionId":"partial","cwd":{{{Json(_project)}}},"timestamp":"2026-10-20T10:00:10.000Z","message":{"role":"assistant","content":[{"type":"text","text":"第二句"}]}}""");

		var session = Assert.Single(SessionImportService.Discover("claude", home: _home));
		var result = SessionImportService.Import(session, Path.Combine(_root, "sessions"));

		Assert.True(result.Ok, result.Error);
		Assert.Equal(2, result.MessageCount);
	}
}
