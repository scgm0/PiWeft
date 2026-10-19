using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public class SessionManagerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-sessions-" + Guid.NewGuid().ToString("N")[..8]);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionDir => Path.Combine(_root, "sessions");

	private static string UserText(Message message) =>
		Assert.IsType<UserMessage>(message).Content.Text ?? string.Empty;

	[Fact]
	public void Create_WritesPiCompatibleHeaderAndFileName()
	{
		var now = new DateTimeOffset(2026, 10, 12, 3, 53, 32, 360, TimeSpan.Zero);
		var manager = SessionManager.Create("/home/scgm/project", SessionDir, id: "01a12e3e-87c8-7569-a0de-3e85a3ed2265", now: now);

		var fileName = Path.GetFileName(manager.SessionFile!);
		Assert.Equal("2026-10-12T03-53-32-360Z_01a12e3e-87c8-7569-a0de-3e85a3ed2265.jsonl", fileName);

		var firstLine = File.ReadLines(manager.SessionFile!).First();
		var obj = JsonNode.Parse(firstLine)!.AsObject();
		Assert.Equal("session", obj["type"]!.GetValue<string>());
		Assert.Equal(3, obj["version"]!.GetValue<int>());
		Assert.Equal("01a12e3e-87c8-7569-a0de-3e85a3ed2265", obj["id"]!.GetValue<string>());
		Assert.Equal("/home/scgm/project", obj["cwd"]!.GetValue<string>());
		Assert.Equal("2026-10-12T03:53:32.360Z", obj["timestamp"]!.GetValue<string>());
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Open_FallsBackToProcessCwdWhenHeaderCwdIsBlank(string cwd)
	{
		Directory.CreateDirectory(_root);
		var path = Path.Combine(_root, "external.jsonl");
		var header = new SessionHeader
		{
			Id = "external-1",
			Timestamp = "2026-10-19T16:52:43.514Z",
			Cwd = cwd,
		};
		File.WriteAllText(path, SessionJson.Serialize(header) + "\n");

		var manager = SessionManager.Open(path);

		Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), manager.Cwd);
	}

	[Fact]
	public void Open_UsesExplicitFallbackWhenHeaderCwdIsBlank()
	{
		Directory.CreateDirectory(_root);
		var path = Path.Combine(_root, "external-fallback.jsonl");
		File.WriteAllText(path, SessionJson.Serialize(new SessionHeader
		{
			Id = "external-2",
			Timestamp = "2026-10-19T16:52:43.514Z",
			Cwd = string.Empty,
		}) + "\n");

		var manager = SessionManager.Open(path, cwdFallback: Path.Combine(_root, "project"));

		Assert.Equal(Path.GetFullPath(Path.Combine(_root, "project")), manager.Cwd);
	}

	[Fact]
	public void AppendMessage_WritesEntriesWithTreeLinks()
	{
		var manager = SessionManager.Create("/tmp/p", SessionDir);

		var user = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 });
		var assistant = manager.AppendMessage(new AssistantMessage
		{
			Api = "anthropic-messages",
			Provider = "anthropic",
			Model = "claude-sonnet-4-5",
			Content = [new TextContent { Text = "hello" }],
			StopReason = StopReason.Stop,
			Timestamp = 2,
		});

		var lines = File.ReadAllLines(manager.SessionFile!);
		Assert.Equal(3, lines.Length);

		var userEntry = JsonNode.Parse(lines[1])!.AsObject();
		Assert.Equal("message", userEntry["type"]!.GetValue<string>());
		Assert.Equal(user, userEntry["id"]!.GetValue<string>());
		Assert.True(userEntry.ContainsKey("parentId"));
		Assert.Null(userEntry["parentId"]);
		Assert.Equal("hi", userEntry["message"]!["content"]!.GetValue<string>());

		var assistantEntry = JsonNode.Parse(lines[2])!.AsObject();
		Assert.Equal(user, assistantEntry["parentId"]!.GetValue<string>());
		Assert.Equal(assistant, assistantEntry["id"]!.GetValue<string>());
		Assert.Equal("assistant", assistantEntry["message"]!["role"]!.GetValue<string>());
		Assert.Equal("stop", assistantEntry["message"]!["stopReason"]!.GetValue<string>());
	}

	[Fact]
	public void Open_RoundTripsEveryEntryType()
	{
		var manager = SessionManager.Create("/tmp/p", SessionDir);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello"), Timestamp = 1 });
		manager.AppendMessage(new AssistantMessage
		{
			Api = "anthropic-messages",
			Provider = "anthropic",
			Model = "m",
			Content = [new ToolCall { Id = "c1", Name = "read", Arguments = new JsonObject { ["path"] = "a" } }],
			StopReason = StopReason.ToolUse,
			Usage = new Usage { Input = 5, Output = 2, TotalTokens = 7 },
			Timestamp = 2,
		});
		manager.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "c1",
			ToolName = "read",
			Content = [new TextContent { Text = "contents" }],
			Timestamp = 3,
		});
		manager.AppendModelChange("openai", "gpt-5");
		manager.AppendThinkingLevelChange("high");
		manager.AppendUsage("cache_warm", "anthropic", "m", new Usage { Input = 1, TotalTokens = 1 }, "warm");
		manager.AppendCustom("my-ext", new JsonObject { ["count"] = 42 });
		manager.AppendSessionInfo("Refactor auth");
		manager.AppendLabelChange(manager.Entries[0].Id, "checkpoint-1");

		var reopened = SessionManager.Open(manager.SessionFile!);

		Assert.Equal(SessionFormat.CurrentVersion, reopened.Header!.Version);
		Assert.Equal("/tmp/p", reopened.Header.Cwd);
		Assert.Equal("Refactor auth", reopened.SessionName());
		Assert.Equal("checkpoint-1", reopened.Labels[reopened.Entries[0].Id]);

		var kinds = reopened.Entries.Select(entry => entry.Type).ToList();
		Assert.Equal(
			["message", "message", "message", "model_change", "thinking_level_change", "usage", "custom", "session_info", "label"],
			kinds);

		var toolResult = Assert.IsType<ToolResultMessage>(Assert.IsType<SessionMessageEntry>(reopened.Entries[2]).Message);
		Assert.Equal("c1", toolResult.ToolCallId);
		Assert.Equal("contents", Assert.IsType<TextContent>(toolResult.Content[0]).Text);

		var usage = Assert.IsType<UsageEntry>(reopened.Entries[5]);
		Assert.Equal("cache_warm", usage.Kind);
		Assert.Equal("warm", usage.Note);
		Assert.Equal(1, usage.Usage.TotalTokens);

		var model = reopened.BuildSessionContext().Model;
		Assert.Equal(("openai", "gpt-5"), model);
		Assert.Equal("high", reopened.BuildSessionContext().ThinkingLevel);
	}

	[Fact]
	public void BuildContextEntries_HonorsCompaction()
	{
		var manager = SessionManager.Create("/tmp/p", SessionDir);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("one"), Timestamp = 1 });
		manager.AppendMessage(new AssistantMessage { Api = "a", Provider = "p", Model = "m", Timestamp = 2 });
		var kept = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("two"), Timestamp = 3 });

		// Everything before "two" is summarized away; "two" is the first kept entry.
		manager.AppendCompaction("earlier conversation", kept, 1000);

		var entries = manager.BuildContextEntries();

		Assert.Equal(2, entries.Count);
		Assert.IsType<CompactionEntry>(entries[0]);
		Assert.Equal("two", UserText(Assert.IsType<SessionMessageEntry>(entries[1]).Message));

		var context = manager.BuildSessionContext();
		Assert.Equal(2, context.Messages.Count);
		var summary = Assert.IsType<UserMessage>(context.Messages[0]);
		Assert.Contains("earlier conversation", summary.Content.Text);
		Assert.Contains("<summary>", summary.Content.Text);
	}

	[Fact]
	public void BuildProjectedMessages_AppliesContextEdits()
	{
		var manager = SessionManager.Create("/tmp/p", SessionDir);
		var first = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("one"), Timestamp = 1 });

		// Append the edit through the file so the entry type is exercised end to end.
		var editLine = SessionJson.Serialize(new ContextEditEntry
		{
			Id = "eeeeeeee",
			ParentId = manager.LeafId,
			Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			TargetId = first,
			Replacement = MessageContent.FromText("one (edited)"),
		});
		File.AppendAllText(manager.SessionFile!, editLine + "\n");

		var reopened = SessionManager.Open(manager.SessionFile!);
		var messages = reopened.BuildProjectedMessages();

		var user = Assert.IsType<UserMessage>(Assert.Single(messages));
		Assert.Equal("one (edited)", user.Content.Text);
	}

	[Fact]
	public void BuildProjectedMessages_OmitsTargetsWithNullReplacement()
	{
		var manager = SessionManager.Create("/tmp/p", SessionDir);
		var first = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("drop me"), Timestamp = 1 });
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("keep me"), Timestamp = 2 });

		File.AppendAllText(
			manager.SessionFile!,
			SessionJson.Serialize(new ContextEditEntry
			{
				Id = "ffffffff",
				ParentId = manager.LeafId,
				Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
				TargetId = first,
				Replacement = null,
			}) + "\n");

		var messages = SessionManager.Open(manager.SessionFile!).BuildProjectedMessages();

		var user = Assert.IsType<UserMessage>(Assert.Single(messages));
		Assert.Equal("keep me", user.Content.Text);
	}

	[Fact]
	public void BuildProjectedMessages_ExtendsAnAppendedBranchAndInvalidatesForEdits()
	{
		var manager = SessionManager.InMemory("/tmp/p");
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("one"), Timestamp = 1 });
		var firstAssistant = manager.AppendMessage(new AssistantMessage
		{
			Api = "a", Provider = "p", Model = "m",
			Content = [new TextContent { Text = "first" }], StopReason = StopReason.Stop, Timestamp = 2,
		});

		var first = manager.BuildProjectedMessages();
		Assert.Equal(2, first.Count);
		var firstMessage = first[0];

		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("two"), Timestamp = 3 });
		var extended = manager.BuildProjectedMessages();
		Assert.Equal(3, extended.Count);
		Assert.Same(firstMessage, extended[0]);
		Assert.Equal("two", UserText(extended[2]));

		manager.AppendContextEdit(firstAssistant, MessageContent.FromText("first (edited)"));
		var edited = manager.BuildProjectedMessages();
		Assert.Equal("first (edited)", Assert.IsType<AssistantMessage>(edited[1]).Content
			.OfType<TextContent>().Single().Text);
		Assert.Equal("two", UserText(edited[2]));

		// A branch switch cannot reuse the prefix from the old leaf.
		manager.Branch(firstAssistant);
		manager.AppendMessage(new AssistantMessage
		{
			Api = "a", Provider = "p", Model = "m",
			Content = [new TextContent { Text = "branched" }], StopReason = StopReason.Stop, Timestamp = 4,
		});
		var branched = manager.BuildProjectedMessages();
		Assert.Equal(3, branched.Count);
		// The edit was appended after this leaf, so the new branch must not include it.
		Assert.Equal("first",
			Assert.IsType<AssistantMessage>(branched[1]).Content.OfType<TextContent>().Single().Text);
		Assert.Equal("branched",
			Assert.IsType<AssistantMessage>(branched[2]).Content.OfType<TextContent>().Single().Text);
	}

	[Fact]
	public void Branch_MovesTheLeafSoLaterEntriesFormANewChild()
	{
		var manager = SessionManager.Create("/tmp/p", SessionDir);
		var root = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("root"), Timestamp = 1 });
		var leftLeaf = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("left"), Timestamp = 2 });

		manager.Branch(root);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("right"), Timestamp = 3 });

		Assert.Equal(2, manager.GetBranch().Count);
		Assert.Equal("right", UserText(Assert.IsType<SessionMessageEntry>(manager.GetBranch()[1]).Message));
		Assert.Equal("left", UserText(Assert.IsType<SessionMessageEntry>(manager.GetBranch(leftLeaf)[1]).Message));

		// Both children exist in the file, so the tree survives a reload.
		var reopened = SessionManager.Open(manager.SessionFile!);
		Assert.Equal(3, reopened.Entries.Count);
		Assert.Equal(leftLeaf, reopened.Entries[1].Id);
		Assert.Equal(reopened.Entries[0].Id, reopened.Entries[1].ParentId);
		Assert.Equal(reopened.Entries[0].Id, reopened.Entries[2].ParentId);
	}

	[Fact]
	public void SessionPaths_EncodeCwdLikePi()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		Assert.Equal("--home-scgm-RiderProjects-Vintaglow--", SessionPaths.EncodeCwd("/home/scgm/RiderProjects/Vintaglow"));
		Assert.Equal("--tmp--", SessionPaths.EncodeCwd("/tmp"));
	}

	[Fact]
	public void ListSessions_ReturnsNewestFirst()
	{
		var older = SessionManager.Create("/tmp/p", SessionDir, id: "older", now: DateTimeOffset.UtcNow.AddHours(-2));
		older.AppendMessage(new UserMessage { Content = MessageContent.FromText("old"), Timestamp = 1 });
		var newer = SessionManager.Create("/tmp/p", SessionDir, id: "newer", now: DateTimeOffset.UtcNow);
		newer.AppendMessage(new UserMessage { Content = MessageContent.FromText("new"), Timestamp = 2 });

		var sessions = SessionManager.ListSessions(SessionDir, "/tmp/p");

		Assert.Equal(2, sessions.Count);
		Assert.Equal("newer", sessions[0].Id);
		Assert.Equal("new", sessions[0].FirstMessage);
		Assert.Equal(1, sessions[0].MessageCount);
	}

	[Fact]
	public void UnknownMessageRole_RoundTripsLosslessly()
	{
		const string line =
			"""{"type":"message","id":"aabbccdd","parentId":null,"timestamp":"2026-01-01T00:00:00.000Z","message":{"role":"bashExecution","command":"ls","output":"a\nb","exitCode":0,"cancelled":false,"truncated":false,"timestamp":1767225600000}}""";

		var parsed = Assert.IsAssignableFrom<SessionEntry>(SessionJson.ParseLine(line));
		var message = Assert.IsType<CustomMessage>(Assert.IsType<SessionMessageEntry>(parsed).Message);
		Assert.Equal("bashExecution", message.CustomRole);

		var reserialized = SessionJson.Serialize(parsed);
		Assert.True(
			JsonNode.DeepEquals(JsonNode.Parse(line), JsonNode.Parse(reserialized)),
			$"round trip changed the payload:\n{reserialized}");
	}
}

/// <summary>
/// 外部工具写出的会话文件可能没有 session 头。追加时应当把头部补回第一行，
/// 否则 pi 与 PiDeck 都解析不出会话身份（它们都按「首行是 session 头」读）。
/// </summary>
public class HeaderRepairTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-header-" + Guid.NewGuid().ToString("N")[..8]);

	public HeaderRepairTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void AppendingToAHeaderlessFileWritesTheHeaderFirst()
	{
		var path = Path.Combine(_root, "headerless.jsonl");
		var message = new SessionMessageEntry
		{
			Id = "aaaa1111",
			Timestamp = "2026-10-13T06:56:46.268Z",
			Message = new PiWeft.Ai.UserMessage { Content = PiWeft.Ai.MessageContent.FromText("外部工具写的") },
		};

		File.WriteAllText(path, PiWeft.CodingAgent.Session.SessionJson.Serialize(message) + "\n");

		var manager = SessionManager.Open(path, _root);
		Assert.Null(manager.Header);

		manager.AppendMessage(new PiWeft.Ai.UserMessage { Content = PiWeft.Ai.MessageContent.FromText("本端口追加的") });

		var lines = File.ReadAllLines(path);
		Assert.Equal(3, lines.Length);

		var header = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!;
		Assert.Equal("session", header["type"]!.GetValue<string>());
		Assert.Equal(manager.SessionId, header["id"]!.GetValue<string>());
		// 无头文件没有工作目录可依据，按打开时的 cwd 记录（与 pi 相同）。
		Assert.Equal(manager.Cwd, header["cwd"]!.GetValue<string>());

		// 重新解析时头部已经在第一行，原有内容没有丢。
		var reopened = SessionManager.Open(path, _root);
		Assert.NotNull(reopened.Header);
		Assert.Equal(2, reopened.Entries.Count(static entry => entry.Type == "message"));
	}
}

/// <summary>
/// 会话文件保留可读 UTF-8 内容，并能无损读回工具参数与回复耗时。
/// </summary>
public class SessionFileEncodingTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-enc-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionFileEncodingTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void ChineseAndQuotesAreWrittenReadable()
	{
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));

		manager.AppendMessage(new UserMessage
		{
			Content = MessageContent.FromText("echo \"=== 我这条会话的启动时间 ===\""),
		});
		manager.AppendMessage(new AssistantMessage
		{
			Timestamp = 1,
			Api = "openai-completions",
			Provider = "openai",
			Model = "gpt-test",
			StopReason = StopReason.ToolUse,
			DurationMs = 1234.5,
			Content =
			[
				new ThinkingContent { Thinking = "检查引号与中文" },
				new ToolCall
				{
					Id = "call_1",
					Name = "bash",
					Arguments = new JsonObject { ["command"] = "echo \"=== 你好 ===\"" },
				},
			],
		});

		var raw = File.ReadAllText(manager.SessionFile!);

		Assert.Contains("我这条会话的启动时间", raw);
		Assert.Contains("你好", raw);
		Assert.DoesNotContain("\\u0022", raw);
		Assert.DoesNotContain("\\u6211", raw);
		Assert.DoesNotContain("\\u4F60", raw);

		// 读回来仍然是同样的内容（转义不出现在值里）。
		var reopened = SessionManager.Open(manager.SessionFile!, _root);
		var user = (UserMessage)reopened.Entries.OfType<SessionMessageEntry>().First().Message;
		var text = user.Content.Text;
		Assert.Equal("echo \"=== 我这条会话的启动时间 ===\"", text);
		var assistant = Assert.IsType<AssistantMessage>(reopened.Entries.OfType<SessionMessageEntry>().Last().Message);
		Assert.Equal(1234.5, assistant.DurationMs);
		Assert.Equal("检查引号与中文", Assert.IsType<ThinkingContent>(assistant.Content[0]).Thinking);
		Assert.Equal("echo \"=== 你好 ===\"", Assert.IsType<ToolCall>(assistant.Content[1]).Arguments["command"]!.GetValue<string>());
	}
}
