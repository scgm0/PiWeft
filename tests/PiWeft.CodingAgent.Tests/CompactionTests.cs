using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public class CompactionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-compact-" + Guid.NewGuid().ToString("N")[..8]);

	public CompactionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionsDir => Path.Combine(_root, "sessions");

	private static SystemMessage SystemPromptMessage() => new()
	{
		Content = MessageContent.Empty,
		Sections = new Dictionary<string, string?> { ["preamble"] = "You are a coding assistant." },
		ToolsAdded = [new Tool { Name = "read", Description = "Read file contents", Parameters = new JsonObject { ["type"] = "object" } }],
		Timestamp = 0,
	};

	private static UserMessage User(string text, long timestamp = 1) =>
		new() { Content = MessageContent.FromText(text), Timestamp = timestamp };

	private static AssistantMessage Assistant(string text, long timestamp = 2, Usage? usage = null) => new()
	{
		Api = "test",
		Provider = "test",
		Model = "m",
		Content = [new TextContent { Text = text }],
		StopReason = StopReason.Stop,
		Usage = usage ?? new Usage(),
		Timestamp = timestamp,
	};

	// ------------------------------------------------------------------ tokens

	[Fact]
	public void ShouldCompact_RespectsReserveTokensAndEnabledFlag()
	{
		var settings = new CompactionSettings { Enabled = true, ReserveTokens = 1000 };

		Assert.False(ContextTokens.ShouldCompact(89_000, 100_000, settings));
		Assert.True(ContextTokens.ShouldCompact(99_001, 100_000, settings));
		Assert.False(ContextTokens.ShouldCompact(99_001, 100_000, settings with { Enabled = false }));
	}

	[Fact]
	public void EstimateContextTokens_UsesLastAssistantUsageAsBase()
	{
		var messages = new List<Message>
		{
			new UserMessage { Content = MessageContent.FromText(new string('x', 4000)), Timestamp = 1 },
			Assistant("hi", usage: new Usage { Input = 10_000, Output = 50, TotalTokens = 10_050 }),
			new UserMessage { Content = MessageContent.FromText(new string('y', 400)), Timestamp = 3 },
		};

		var (tokens, index) = ContextTokens.EstimateContextTokens(messages);

		Assert.Equal(1, index);
		// 10_050 usage + ~100 tokens for the trailing 400 characters.
		Assert.InRange(tokens, 10_140, 10_160);
	}

	[Fact]
	public void EstimateContextTokens_WithoutUsage_EstimatesEverything()
	{
		var messages = new List<Message> { User(new string('x', 4000)), Assistant("answer") };
		var (tokens, index) = ContextTokens.EstimateContextTokens(messages);

		Assert.Null(index);
		Assert.True(tokens >= 1000);
	}

	[Fact]
	public void EstimateTokens_CountsSystemSectionsAndTools()
	{
		var system = SystemPromptMessage();
		var tokens = ContextTokens.EstimateTokens(system);

		Assert.True(tokens > 10);
		Assert.True(tokens < 100);
	}

	[Fact]
	public void EstimateTokens_CountsBashExecutionOutput()
	{
		var message = CodingMessages.BashExecution(
			"ls",
			new string('o', 800),
			exitCode: 0,
			cancelled: false,
			truncated: false,
			fullOutputPath: null,
			excludeFromContext: false,
			timestamp: 1);

		Assert.True(ContextTokens.EstimateTokens(message) >= 200);
	}

	// ------------------------------------------------------------ serialization

	[Fact]
	public void Serialize_RendersRolesAndTruncatesToolResults()
	{
		var messages = new List<Message>
		{
			User("do the thing"),
			new AssistantMessage
			{
				Api = "test",
				Provider = "test",
				Model = "m",
				Content =
				[
					new TextContent { Text = "on it" },
					new ToolCall { Id = "c1", Name = "read", Arguments = new JsonObject { ["path"] = "a.txt" } },
				],
				Timestamp = 2,
			},
			new ToolResultMessage
			{
				ToolCallId = "c1",
				ToolName = "read",
				Content = [new TextContent { Text = new string('z', 3000) }],
				Timestamp = 3,
			},
		};

		var text = ConversationSerializer.Serialize(messages);

		Assert.Contains("[User]: do the thing", text);
		Assert.Contains("[Assistant]: on it", text);
		Assert.Contains("[Assistant tool calls]: read(path=\"a.txt\")", text);
		Assert.Contains("[Tool result]: zzz", text);
		Assert.Contains("more characters truncated", text);
	}

	[Fact]
	public void CollectFileOperations_TracksReadsAndWrites()
	{
		var messages = new List<Message>
		{
			new AssistantMessage
			{
				Api = "t",
				Provider = "t",
				Model = "m",
				Content =
				[
					new ToolCall { Id = "1", Name = "read", Arguments = new JsonObject { ["path"] = "read.txt" } },
					new ToolCall { Id = "2", Name = "write", Arguments = new JsonObject { ["path"] = "new.txt" } },
					new ToolCall { Id = "3", Name = "edit", Arguments = new JsonObject { ["path"] = "edit.txt" } },
				],
				Timestamp = 1,
			},
		};

		var operations = ConversationSerializer.CollectFileOperations(messages);

		Assert.Contains("read.txt", operations.ReadFiles);
		Assert.Contains("new.txt", operations.ModifiedFiles);
		Assert.Contains("edit.txt", operations.ReadFiles);
		Assert.Contains("edit.txt", operations.ModifiedFiles);

		var formatted = ConversationSerializer.FormatFileOperations(operations.ReadFiles, operations.ModifiedFiles);
		Assert.Contains("<read-files>", formatted);
		Assert.Contains("<modified-files>", formatted);
	}

	// -------------------------------------------------------------- cut points

	[Fact]
	public void FindCutPoint_PrefersTheTurnStartWhenKeepingRecentTokens()
	{
		var session = SessionManager.InMemory("/tmp");
		session.AppendMessage(SystemPromptMessage());
		session.AppendMessage(User("one", 1));
		session.AppendMessage(Assistant("a1", 2));
		session.AppendMessage(User("two", 3));
		session.AppendMessage(Assistant("a2", 4));

		var entries = session.BuildContextEntries();
		var plan = Compactor.FindCutPoint(entries, 1, entries.Count, keepRecentTokens: 1);

		Assert.NotNull(plan);
		// The budget is exhausted by the last assistant message, so the cut lands inside that turn.
		Assert.True(plan!.IsSplitTurn);
		Assert.True(plan.TurnStartIndex > 0);
		Assert.Equal("two", Assert.IsType<UserMessage>(
			SessionManager.MessagesForEntry(entries[plan.TurnStartIndex])[0]).Content.Text);
	}

	[Fact]
	public void FindCutPoint_WithoutCutPoints_KeepsFromTheStart()
	{
		var session = SessionManager.InMemory("/tmp");
		session.AppendMessage(SystemPromptMessage());
		session.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "c1",
			ToolName = "read",
			Content = [new TextContent { Text = "x" }],
			Timestamp = 1,
		});

		var entries = session.BuildContextEntries();
		var plan = Compactor.FindCutPoint(entries, 1, entries.Count, keepRecentTokens: 1);

		Assert.NotNull(plan);
		Assert.Equal(1, plan!.FirstKeptEntryIndex);
		Assert.False(plan.IsSplitTurn);
	}

	[Fact]
	public void SummarizationFailure_DetectsBrokenResponses()
	{
		var aborted = Assistant("", 1) with { StopReason = StopReason.Aborted };
		Assert.Contains("aborted", Compactor.SummarizationFailure(aborted)!);

		var error = Assistant("", 1) with { StopReason = StopReason.Error, ErrorMessage = "boom" };
		Assert.Contains("boom", Compactor.SummarizationFailure(error)!);

		var truncated = Assistant("partial", 1) with { StopReason = StopReason.Length };
		Assert.Contains("token cap", Compactor.SummarizationFailure(truncated)!);

		var withTool = Assistant("", 1) with
		{
			Content = [new ToolCall { Id = "c", Name = "read", Arguments = new JsonObject() }],
		};
		Assert.Contains("call a tool", Compactor.SummarizationFailure(withTool)!);

		Assert.Null(Compactor.SummarizationFailure(Assistant("fine")));
	}

	// ---------------------------------------------------------------- compacting

	[Fact]
	public async Task CompactAsync_AppendsACompactionEntryAndRewritesTheContext()
	{
		var session = SessionManager.Create(_root, SessionsDir);
		session.AppendMessage(SystemPromptMessage());
		session.AppendMessage(User("first question", 1));
		session.AppendMessage(Assistant("first answer", 2));
		session.AppendMessage(User("second question", 3));

		var script = new ScriptedStreamFn();
		script.EnqueueText("## Goal\nsummarized the first turn");

		var result = await Compactor.CompactAsync(
			session,
			ScriptedSessions.Model(),
			script.Invoke,
			new StreamOptions(),
			new CompactionSettings { ReserveTokens = 4096, KeepRecentTokens = 1 });

		Assert.NotNull(result);
		Assert.Equal("## Goal\nsummarized the first turn", result!.Summary);

		var compaction = Assert.IsType<CompactionEntry>(session.Entries[^1]);
		Assert.Equal(result.Summary, compaction.Summary);
		Assert.Contains(compaction.FirstKeptEntryId, session.Entries.Select(static e => e.Id));
		Assert.True(compaction.TokensBefore > 0);
		Assert.NotNull(compaction.SystemMessage);
		Assert.Equal("You are a coding assistant.", compaction.SystemMessage!.Sections!["preamble"]);

		// The summary enters the model context as a prefixed user message.
		var context = session.BuildSessionContext();
		Assert.Contains(
			context.Messages,
			static m => m is UserMessage user &&
				user.Content.Text is not null &&
				user.Content.Text.Contains("compacted into the following summary", StringComparison.Ordinal));

		// One summarization request was made, carrying the serialized conversation.
		var request = Assert.Single(script.Requests);
		Assert.Contains(
			request,
			static m => m is UserMessage user &&
				user.Content.Text is not null &&
				user.Content.Text.Contains("<conversation>", StringComparison.Ordinal));
	}

	[Fact]
	public async Task CompactAsync_SplitTurnSummarizesHistoryAndTurnPrefixSeparately()
	{
		var session = SessionManager.Create(_root, SessionsDir);
		session.AppendMessage(SystemPromptMessage());
		session.AppendMessage(User("first question", 1));
		session.AppendMessage(Assistant("first answer", 2));
		session.AppendMessage(User("second question", 3));
		session.AppendMessage(Assistant("second answer", 4));

		var script = new ScriptedStreamFn();
		script.EnqueueText("history summary");
		script.EnqueueText("turn prefix summary");

		var result = await Compactor.CompactAsync(
			session,
			ScriptedSessions.Model(),
			script.Invoke,
			new StreamOptions(),
			new CompactionSettings { ReserveTokens = 4096, KeepRecentTokens = 1 });

		Assert.NotNull(result);
		Assert.Contains("history summary", result!.Summary);
		Assert.Contains("**Turn Context (split turn):**", result.Summary);
		Assert.Contains("turn prefix summary", result.Summary);
		Assert.Equal(2, script.Requests.Count);
	}

	[Fact]
	public async Task CompactAsync_NothingToCompact_ReturnsNull()
	{
		var session = SessionManager.Create(_root, SessionsDir);
		session.AppendMessage(SystemPromptMessage());
		session.AppendMessage(User("only question", 1));

		var script = new ScriptedStreamFn();
		var result = await Compactor.CompactAsync(
			session,
			ScriptedSessions.Model(),
			script.Invoke,
			new StreamOptions(),
			new CompactionSettings { ReserveTokens = 4096, KeepRecentTokens = 100_000 });

		Assert.Null(result);
		Assert.Empty(script.Requests);
	}

	[Fact]
	public async Task CompactAsync_UsesTheUpdatePromptWhenAPreviousSummaryExists()
	{
		var session = SessionManager.Create(_root, SessionsDir);
		session.AppendMessage(SystemPromptMessage());
		session.AppendMessage(User("turn one", 1));
		session.AppendMessage(Assistant("answer one", 2));
		session.AppendCompaction("summary of turn one", session.Entries[1].Id, 500);
		session.AppendMessage(User("turn two", 3));

		var script = new ScriptedStreamFn();
		script.EnqueueText("updated summary");

		var result = await Compactor.CompactAsync(
			session,
			ScriptedSessions.Model(),
			script.Invoke,
			new StreamOptions(),
			new CompactionSettings { ReserveTokens = 4096, KeepRecentTokens = 1 });

		Assert.NotNull(result);
		var prompt = Assert.IsType<UserMessage>(script.Requests[0][1]).Content.Text!;
		Assert.Contains("<previous-summary>\nsummary of turn one\n</previous-summary>", prompt);
		Assert.Contains("Update the existing structured summary", prompt);
	}

	[Fact]
	public async Task SessionAutoCompactsBeforePrompting()
	{
		var session = SessionManager.Create(_root, SessionsDir);
		session.AppendMessage(SystemPromptMessage());
		session.AppendMessage(User("first", 1));
		session.AppendMessage(Assistant("answer", 2));
		session.AppendMessage(User("second", 3));

		var script = new ScriptedStreamFn();
		script.EnqueueText("summary text");
		script.EnqueueText("final answer");

		var settings = new CodingAgentSettings
		{
			// Tiny window so any context is over the threshold.
			Compaction = new CompactionSettings { Enabled = true, ReserveTokens = 1, KeepRecentTokens = 1 },
		};

		using var coding = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model() with { ContextWindow = 10 },
			StreamFn = script.Invoke,
			Session = session,
			Settings = settings,
		});

		Assert.True(coding.ShouldCompact());
		await coding.PromptAsync("next question");

		Assert.Contains(session.Entries, static entry => entry is CompactionEntry);
		Assert.Equal(2, script.Requests.Count);
	}

	[Fact]
	public async Task ManualCompactionIsExposedOnTheSession()
	{
		var session = SessionManager.Create(_root, SessionsDir);
		session.AppendMessage(SystemPromptMessage());
		session.AppendMessage(User("first", 1));
		session.AppendMessage(Assistant("answer", 2));
		session.AppendMessage(User("second", 3));

		var script = new ScriptedStreamFn();
		script.EnqueueText("manual summary");

		using var coding = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = session,
			Settings = new CodingAgentSettings
			{
				Compaction = new CompactionSettings { ReserveTokens = 4096, KeepRecentTokens = 1 },
			},
		});

		var result = await coding.CompactAsync("focus on the migration");

		Assert.NotNull(result);
		var prompt = Assert.IsType<UserMessage>(script.Requests[0][1]).Content.Text!;
		Assert.Contains("Additional focus: focus on the migration", prompt);
	}
}
