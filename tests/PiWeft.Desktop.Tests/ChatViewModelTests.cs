using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>Scripted provider streams, so the view model is exercised without a network or a window.</summary>
internal static class FakeStreams
{
	public static Model Model(string id = "test-model") => new()
	{
		Id = id,
		Name = "Test Model",
		Provider = "test",
		Api = "anthropic-messages",
		BaseUrl = "https://example.invalid",
		MaxTokens = 4096,
		ContextWindow = 128_000,
		Reasoning = true,
		Cost = new ModelCost { Input = 1, Output = 2 },
	};

	public static AssistantMessageEventStream Text(Model model, string text, Task? gate = null)
	{
		var stream = new AssistantMessageEventStream();

		_ = Task.Run(async () =>
		{
			var blank = Blank(model);
			var message = blank with
			{
				Content = [new TextContent { Text = text }],
				StopReason = StopReason.Stop,
				Usage = new Usage { Input = 10, Output = 5, TotalTokens = 15, Cost = new UsageCost { Total = 0.5 } },
			};

			stream.Push(new StartEvent { Partial = blank });
			stream.Push(new TextStartEvent { ContentIndex = 0, Partial = blank });

			// Emit in two deltas so the view model's accumulation is observable.
			var half = text.Length / 2;
			if (half > 0)
			{
				stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text[..half], Partial = message });
			}

			if (gate is not null)
			{
				await gate.ConfigureAwait(false);
			}

			stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text[half..], Partial = message });
			stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });
			stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
		});

		return stream;
	}

	public static AssistantMessageEventStream ToolCall(Model model, string id, string name, JsonObject arguments)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var call = new ToolCall { Id = id, Name = name, Arguments = arguments };
		var message = blank with { Content = [call], StopReason = StopReason.ToolUse };

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new ToolCallStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = message });
		return stream;
	}

	private static AssistantMessage Blank(Model model) => new()
	{
		Api = model.Api,
		Provider = model.Provider,
		Model = model.Id,
		Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
	};
}

/// <summary>A provider that answers with whatever the test queued.</summary>
internal sealed class QueuedStreams
{
	private readonly Queue<Func<Model, AssistantMessageEventStream>> _responses = new();

	public void Enqueue(Func<Model, AssistantMessageEventStream> response) => _responses.Enqueue(response);

	public AssistantMessageEventStream Invoke(Model model, IReadOnlyList<Message> messages, StreamOptions options) =>
		_responses.Count > 0 ? _responses.Dequeue()(model) : FakeStreams.Text(model, "ok");
}

public class ChatViewModelTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-desktop-vm-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly QueuedStreams _streams = new();

	public ChatViewModelTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession NewSession(SessionManager? manager = null) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = FakeStreams.Model(),
			StreamFn = _streams.Invoke,
			ThinkingLevel = "off",
			Session = manager ?? SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			Settings = new CodingAgentSettings(),
		});

	[Fact]
	public void AssistantRowSummarizesFilesChangedThisTurn()
	{
		var manager = SessionManager.InMemory(_root);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("edit"), Timestamp = 1 });
		manager.AppendMessage(new AssistantMessage
		{
			Api = "anthropic-messages",
			Provider = "test",
			Model = "test-model",
			Content =
			[
				new ToolCall { Id = "c1", Name = "edit", Arguments = new JsonObject { ["path"] = "src/app.cs" } },
				new ToolCall { Id = "c2", Name = "write", Arguments = new JsonObject { ["path"] = "src/new.cs" } },
			],
			StopReason = StopReason.ToolUse,
			Timestamp = 2,
		});
		manager.AppendMessage(new ToolResultMessage { ToolCallId = "c1", ToolName = "edit", Content = [new TextContent { Text = "ok" }], Timestamp = 3 });
		// 失败的写入不计入汇总。
		manager.AppendMessage(new ToolResultMessage { ToolCallId = "c2", ToolName = "write", Content = [new TextContent { Text = "fail" }], IsError = true, Timestamp = 4 });
		manager.AppendMessage(new AssistantMessage
		{
			Api = "anthropic-messages",
			Provider = "test",
			Model = "test-model",
			Content = [new TextContent { Text = "done" }],
			StopReason = StopReason.Stop,
			Timestamp = 5,
		});

		using var session = NewSession(manager);
		using var viewModel = new ChatViewModel();
		viewModel.Attach(session);

		var assistant = viewModel.Entries().Last(static entry => entry.Role == ChatRole.Assistant);
		Assert.Equal(["src/app.cs"], assistant.ChangedFiles);
	}

	[Fact]
	public async Task UserAndAssistantTurnsBecomeRows()
	{
		_streams.Enqueue(static model => FakeStreams.Text(model, "hello back"));

		using var session = NewSession();
		using var viewModel = new ChatViewModel();
		viewModel.Attach(session);

		Assert.Empty(viewModel.Entries());

		await viewModel.SendAsync("hello");

		var entries = viewModel.Entries();
		Assert.Equal(2, entries.Count);
		Assert.Equal(ChatRole.User, entries[0].Role);
		Assert.Equal("hello", entries[0].Text);
		Assert.Equal(ChatRole.Assistant, entries[1].Role);
		Assert.Equal("hello back", entries[1].Text);
		Assert.False(entries[1].IsStreaming);
		Assert.False(viewModel.IsStreaming);
	}

	[Fact]
	public async Task StreamingTextAppearsBeforeTheMessageIsCommitted()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_streams.Enqueue(model => FakeStreams.Text(model, "streamed answer", gate.Task));

		using var session = NewSession();
		using var viewModel = new ChatViewModel();
		viewModel.Attach(session);

		var run = viewModel.SendAsync("go");

		// The first delta arrives while the provider is still open.
		var streaming = await WaitForAsync(() => viewModel.Entries().LastOrDefault(static entry => entry.IsStreaming));
		Assert.NotNull(streaming);
		// Half of "streamed answer" arrived; the rest is still buffered in the provider.
		Assert.StartsWith(streaming!.Text, "streamed answer");
		Assert.NotEqual("streamed answer", streaming.Text);

		gate.SetResult();
		await run;

		var final = viewModel.Entries();
		Assert.DoesNotContain(final, static entry => entry.IsStreaming);
		Assert.Equal("streamed answer", final[^1].Text);
	}

	[Fact]
	public async Task ThinkingAndMultipleTextBlocksRemainSeparateWhileStreamingAndAfterCompletion()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_streams.Enqueue(model =>
		{
			var stream = new AssistantMessageEventStream();
			var message = new AssistantMessage
			{
				Api = model.Api, Provider = model.Provider, Model = model.Id, Timestamp = 123,
				Content = [new ThinkingContent { Thinking = "plan" }, new TextContent { Text = "first" }, new TextContent { Text = "second" }],
				StopReason = StopReason.Stop,
			};
			_ = Task.Run(async () =>
			{
				stream.Push(new StartEvent { Partial = message with { Content = [] } });
				stream.Push(new ThinkingStartEvent { ContentIndex = 0, Partial = message });
				stream.Push(new ThinkingDeltaEvent { ContentIndex = 0, Delta = "plan", Partial = message });
				stream.Push(new ThinkingEndEvent { ContentIndex = 0, Content = "plan", Partial = message });
				stream.Push(new TextStartEvent { ContentIndex = 1, Partial = message });
				stream.Push(new TextDeltaEvent { ContentIndex = 1, Delta = "first", Partial = message });
				stream.Push(new TextStartEvent { ContentIndex = 2, Partial = message });
				stream.Push(new TextDeltaEvent { ContentIndex = 2, Delta = "second", Partial = message });
				await gate.Task;
				stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
			});
			return stream;
		});

		using var session = NewSession();
		using var viewModel = new ChatViewModel();
		viewModel.Attach(session);
		var run = viewModel.SendAsync("go");
		try
		{
			var live = await WaitForAsync(() => viewModel.Entries().LastOrDefault(entry => entry.IsStreaming && entry.Text == "firstsecond"));
			Assert.NotNull(live);
			Assert.Equal("plan", live.Thinking);
			Assert.False(live.IsThinking);
			Assert.Equal(123, live.Timestamp);
		}
		finally
		{
			gate.TrySetResult();
			await run;
		}

		var final = viewModel.Entries()[^1];
		Assert.Equal("firstsecond", final.Text);
		Assert.Equal("plan", final.Thinking);
		Assert.False(final.IsStreaming);
	}

	[Fact]
	public void HistoricalThinkingOnlyMessagesRemainVisibleButRedactedContentDoesNot()
	{
		var manager = SessionManager.InMemory(_root);
		manager.AppendMessage(new AssistantMessage
		{
			Content =
			[
				new ThinkingContent { Thinking = "visible", ThinkingSignature = "opaque" },
				new ThinkingContent { Thinking = "secret", Redacted = true, ThinkingSignature = "encrypted" },
			],
			DurationMs = 2500,
		});
		using var session = NewSession(manager);
		using var viewModel = new ChatViewModel();
		viewModel.Attach(session);
		var row = Assert.Single(viewModel.Entries());
		Assert.Equal("visible", row.Thinking);
		Assert.Empty(row.Text);
		Assert.Equal(2500, row.DurationMs);
		viewModel.Detach();
		Assert.Empty(viewModel.Entries());
	}

	[Fact]
	public void CommandSearchSupportsMultipleWordsAndKeepsThePrefixFirst()
	{
		PaletteCommand[] commands =
		[
			new("Open Git", "Current project", static () => { }),
			new("Git", "Project changes", static () => { }),
			new("Settings", "Environment", static () => { }),
		];
		Assert.Equal("Git", CommandPalette.Search(commands, "git")[0].Title);
		Assert.Equal(2, CommandPalette.Search(commands, "GIT project").Count);
		Assert.Empty(CommandPalette.Search(commands, "git missing"));
		Assert.Equal(3, CommandPalette.Search(commands, " ").Count);
		PaletteCommand[] models =
		[
			new("same-model", "provider-a · 图片", static () => { }),
			new("same-model", "provider-b · 推理", static () => { }),
		];
		Assert.Equal("provider-b · 推理", Assert.Single(CommandPalette.Search(models, "same-model provider-b")).Detail);
		Assert.Single(CommandPalette.Search(models, "图片"));
	}

	[Fact]
	public async Task ToolCallsAndResultsAreShownAsToolRows()
	{
		_streams.Enqueue(static model => FakeStreams.ToolCall(model, "call-1", "echo", new JsonObject { ["message"] = "hi" }));
		_streams.Enqueue(static model => FakeStreams.Text(model, "done"));

		using var session = NewSession();
		using var viewModel = new ChatViewModel();
		viewModel.Attach(session);

		var tool = new AgentTool
		{
			Name = "echo",
			Label = "echo",
			Description = "Echo a message",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject { ["message"] = new JsonObject { ["type"] = "string" } },
			},
			Execute = static invocation => Task.FromResult(new AgentToolResult
			{
				Content = [new TextContent { Text = "echoed:hi" }],
			}),
		};

		session.SetTools([.. session.Tools, tool]);

		await viewModel.SendAsync("use the tool");

		var entries = viewModel.Entries();
		Assert.Contains(entries, static entry => entry.Role == ChatRole.Tool && entry.ToolName == "echo");
		Assert.Contains(entries, static entry => entry.Role == ChatRole.Tool && entry.Text.Contains("echoed:hi", StringComparison.Ordinal));
		Assert.Equal("done", entries[^1].Text);
	}

	[Fact]
	public async Task UserRowsCarryTheirEntryIdSoTheUiCanBranch()
	{
		_streams.Enqueue(static model => FakeStreams.Text(model, "answer"));

		var sessionDir = Path.Combine(_root, "sessions");
		using var session = NewSession(SessionManager.Create(_root, sessionDir));
		using var viewModel = new ChatViewModel();
		viewModel.Attach(session);

		await viewModel.SendAsync("branch from me");

		var user = viewModel.Entries().First(static entry => entry.Role == ChatRole.User);
		Assert.False(string.IsNullOrEmpty(user.EntryId), "用户行必须带上会话条目 id，界面才能据此分支");

		// 界面点的「在此分支」就是这一步：会话换成新文件，旧文件留在历史里。
		var original = session.Session.SessionFile;
		var text = session.ForkAt(user.EntryId!);

		Assert.Equal("branch from me", text);
		Assert.NotEqual(original, session.Session.SessionFile);
		Assert.True(File.Exists(session.Session.SessionFile));
		Assert.Empty(session.Messages.OfType<UserMessage>());
	}

	[Fact]
	public async Task ToolPatchDetailBecomesADiffOnTheToolRow()
	{
		const string patch = """
			--- a/note.txt
			+++ b/note.txt
			@@ -1 +1 @@
			-old
			+new
			""";

		_streams.Enqueue(static model => FakeStreams.ToolCall(model, "call-1", "patch_tool", new JsonObject { ["path"] = "note.txt" }));
		_streams.Enqueue(static model => FakeStreams.Text(model, "done"));

		using var session = NewSession();
		using var viewModel = new ChatViewModel();
		viewModel.Attach(session);

		// 名字不能与内置工具重名，否则解析到的是内置那个。
		var tool = new AgentTool
		{
			Name = "patch_tool",
			Label = "patch_tool",
			Description = "返回带补丁细节的工具结果",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
			},
			Execute = _ => Task.FromResult(new AgentToolResult
			{
				Content = [new TextContent { Text = "已替换 1 处" }],
				Details = new JsonObject { ["patch"] = patch },
			}),
		};

		session.SetTools([.. session.Tools, tool]);
		await viewModel.SendAsync("改一下");

		var toolMessage = session.Messages.OfType<ToolResultMessage>().FirstOrDefault();
		var toolText = toolMessage is null
			? "<no toolResult>"
			: string.Concat(toolMessage.Content.OfType<TextContent>().Select(static block => block.Text));

		Assert.True(
			toolMessage?.Details is not null,
			$"details 丢失：isError={toolMessage?.IsError} text={toolText}");

		// 工具调用行在前、结果行在后；补丁挂在结果行上。
		var toolRow = viewModel.Entries().Last(static entry => entry.Role == ChatRole.Tool);
		Assert.True(toolRow.Patch is not null, $"patch 为空；行文本={toolRow.Text}");
		Assert.Contains("+new", toolRow.Patch!);
		Assert.Contains("@@ -1 +1 @@", toolRow.Patch!);
	}

	[Fact]
	public void StatusLineReportsModelUsageAndName()
	{
		using var session = NewSession();
		using var viewModel = new ChatViewModel();

		Assert.Equal(Strings.NoSession, viewModel.StatusLine());

		viewModel.Attach(session);
		session.Session.AppendSessionInfo("checkout fix");

		// 状态行只保留决策相关信息：会话名、模型、思考级别、上下文占用。
		var status = viewModel.StatusLine();
		Assert.StartsWith("checkout fix", status);
		Assert.Contains("test-model", status);
		Assert.Contains(Strings.ThinkingShort("off"), status);
		Assert.Contains(Strings.Context, status);
		Assert.DoesNotContain("cacheRead", status);
	}

	[Fact]
	public void AttachReplacesThePreviousSession()
	{
		_streams.Enqueue(static model => FakeStreams.Text(model, "one"));

		using var first = NewSession();
		using var second = NewSession();
		using var viewModel = new ChatViewModel();

		viewModel.Attach(first);
		Assert.Same(first, viewModel.Session);

		viewModel.Attach(second);
		Assert.Same(second, viewModel.Session);
		Assert.Empty(viewModel.Entries());

		viewModel.Detach();
		Assert.Null(viewModel.Session);
	}

	private static async Task<ChatEntry?> WaitForAsync(Func<ChatEntry?> probe)
	{
		for (var attempt = 0; attempt < 400; attempt++)
		{
			if (probe() is { } entry)
			{
				return entry;
			}

			await Task.Delay(10);
		}

		return null;
	}
}
