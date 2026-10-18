using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 会话层事件（pi 的 <c>queue_update</c> / <c>thinking_level_changed</c> /
/// <c>session_info_changed</c> / <c>entry_appended</c> / <c>compaction_*</c>）的回归。
/// </summary>
public class SessionEventsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-events-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionEventsTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession CreateSession(ScriptedStreamFn script, CodingAgentSettings? settings = null) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
			Settings = settings ?? new CodingAgentSettings(),
		});

	[Fact]
	public void SteerAndFollowUpEmitQueueUpdatesWithTheFullQueues()
	{
		var script = new ScriptedStreamFn();
		using var session = CreateSession(script);

		var events = new List<AgentEvent>();
		using var subscription = session.Subscribe(@event =>
		{
			events.Add(@event);
			return ValueTask.CompletedTask;
		});

		session.Steer(new UserMessage { Content = MessageContent.FromText("s1"), Timestamp = 1 });
		session.FollowUp(new UserMessage { Content = MessageContent.FromText("f1"), Timestamp = 2 });

		// 每次队列变化都发一条全量快照：最后一次包含 steering 与 follow-up 各一条。
		var updates = events.OfType<QueueUpdateEvent>().ToList();
		Assert.Equal(2, updates.Count);
		Assert.Single(updates[0].Steering);
		Assert.Empty(updates[0].FollowUp);
		Assert.Single(updates[1].Steering);
		Assert.Single(updates[1].FollowUp);

		session.ClearQueue();

		var afterClear = events.OfType<QueueUpdateEvent>().Last();
		Assert.Empty(afterClear.Steering);
		Assert.Empty(afterClear.FollowUp);
	}

	[Fact]
	public void SettingTheThinkingLevelEmitsThinkingLevelChangedAndPersistsIt()
	{
		var script = new ScriptedStreamFn();
		using var session = CreateSession(script);

		var events = new List<AgentEvent>();
		using var subscription = session.Subscribe(@event =>
		{
			events.Add(@event);
			return ValueTask.CompletedTask;
		});

		session.SetThinkingLevel(ThinkingLevels.High);

		var changed = Assert.Single(events.OfType<ThinkingLevelChangedEvent>());
		Assert.Equal(ThinkingLevels.High, changed.Level);
		Assert.Equal(ThinkingLevels.High, session.ThinkingLevel);
		Assert.Contains(
			session.Session.Entries,
			static entry => entry is ThinkingLevelChangeEntry { ThinkingLevel: ThinkingLevels.High });
	}

	[Fact]
	public void SetSessionNameEmitsSessionInfoChanged()
	{
		var script = new ScriptedStreamFn();
		using var session = CreateSession(script);

		var events = new List<AgentEvent>();
		using var subscription = session.Subscribe(@event =>
		{
			events.Add(@event);
			return ValueTask.CompletedTask;
		});

		session.SetSessionName("refactor");

		var changed = Assert.Single(events.OfType<SessionInfoChangedEvent>());
		Assert.Equal("refactor", changed.Name);
		Assert.Equal("refactor", session.SessionName);
	}

	[Fact]
	public void AppendCustomMessageEmitsEntryAppendedWithTheWireEntry()
	{
		var script = new ScriptedStreamFn();
		using var session = CreateSession(script);

		var events = new List<AgentEvent>();
		using var subscription = session.Subscribe(@event =>
		{
			events.Add(@event);
			return ValueTask.CompletedTask;
		});

		session.AppendCustomMessage("notes", "hello");

		var appended = Assert.Single(events.OfType<EntryAppendedEvent>());
		Assert.Equal("custom_message", appended.Entry["type"]!.GetValue<string>());
		Assert.Equal("notes", appended.Entry["customType"]!.GetValue<string>());
	}

	[Fact]
	public async Task CompactAsyncEmitsStartAndEndEventsAroundTheRun()
	{
		var script = new ScriptedStreamFn();
		// keepRecentTokens 压到 1，让单轮对话也满足切分条件。
		var settings = new CodingAgentSettings
		{
			Compaction = new CompactionSettings { KeepRecentTokens = 1 },
		};
		using var session = CreateSession(script, settings);
		script.EnqueueText("the summary");
		// 切分轮次（split turn）时轮次前缀也要单独摘要一次。
		script.EnqueueText("turn summary");

		var events = new List<AgentEvent>();
		using var subscription = session.Subscribe(@event =>
		{
			events.Add(@event);
			return ValueTask.CompletedTask;
		});

		await session.PromptAsync("hello world", CancellationToken.None);
		var result = await session.CompactAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);

		Assert.NotNull(result);
		var start = Assert.Single(events.OfType<CompactionStartEvent>());
		Assert.Equal(CompactionReasons.Manual, start.Reason);

		var end = Assert.Single(events.OfType<CompactionEndEvent>());
		Assert.Equal(CompactionReasons.Manual, end.Reason);
		Assert.False(end.Aborted);
		Assert.Null(end.ErrorMessage);
		// 切分轮次时主摘要与轮次前缀摘要会拼接；这里只锁定事件契约与摘要产生。
		Assert.Contains("summary", end.Result!.Summary);
		Assert.NotNull(end.EstimatedTokensAfter);
	}

	[Fact]
	public async Task TheBashToolExposesTheSessionThroughPiEnvironmentVariables()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
		{
			return;
		}

		var script = new ScriptedStreamFn();
		using var session = CreateSession(script);

		var bash = session.Tools.Single(static tool => tool.Name == "bash");
		var result = await bash
			.Execute(new AgentToolInvocation
			{
				ToolCallId = "call-1",
				ToolName = "bash",
				Arguments = new JsonObject
				{
					["command"] = "printf '%s|%s' \"$PI_MODEL\" \"$PI_PROVIDER\"",
				},
			})
			.ConfigureAwait(false);

		Assert.Equal("test-model|test", Assert.IsType<TextContent>(result.Content[0]).Text.TrimEnd('\n'));
	}
}
