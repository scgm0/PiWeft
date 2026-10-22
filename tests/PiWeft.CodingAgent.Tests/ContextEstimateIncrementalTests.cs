using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 增量上下文估算必须和 <see cref="ContextTokens.EstimateContextTokens"/> 逐位一致：
/// 它按消息引用复用已估算的前缀，所以任何改写历史的路径（追加、分支、上下文编辑、压缩）
/// 都不能让读数偏小或偏大。
/// </summary>
public sealed class ContextEstimateIncrementalTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-ctx-estimate-" + Guid.NewGuid().ToString("N")[..8]);

	public ContextEstimateIncrementalTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static SystemMessage System() => new()
	{
		Content = MessageContent.Empty,
		Sections = new Dictionary<string, string?> { ["preamble"] = "You are a coding assistant." },
		ToolsAdded = [new Tool { Name = "read", Description = "Read file contents", Parameters = new JsonObject { ["type"] = "object" } }],
		Timestamp = 0,
	};

	private static UserMessage User(string text, long timestamp) =>
		new() { Content = MessageContent.FromText(text), Timestamp = timestamp };

	private static AssistantMessage Assistant(string text, long timestamp, Usage? usage = null) => new()
	{
		Content = [new TextContent { Text = text }],
		Timestamp = timestamp,
		Usage = usage ?? new Usage(),
	};

	private static ToolResultMessage Tool(string text, long timestamp) => new()
	{
		ToolName = "read",
		ToolCallId = "call",
		Content = [new TextContent { Text = text }],
		Timestamp = timestamp,
	};

	private CodingAgentSession Create(SessionManager manager) => new(new CodingAgentSessionOptions
	{
		Cwd = _root,
		Model = ScriptedSessions.Model(),
		StreamFn = static (_, _, _) => throw new InvalidOperationException(),
		Session = manager,
		Skills = [],
	});

	[Fact]
	public void MatchesReferenceEstimateWhileTheHistoryGrows()
	{
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var session = Create(manager);

		AssertSame(session, manager);
		manager.AppendMessage(System());
		AssertSame(session, manager);
		manager.AppendMessage(User("第一个问题，稍微长一点好让估算有意义", 1));
		AssertSame(session, manager);
		manager.AppendMessage(Assistant("第一个回答", 2));
		AssertSame(session, manager);
		manager.AppendMessage(Tool("工具输出：一大段文本用来撑估算", 3));
		AssertSame(session, manager);
		manager.AppendMessage(User("第二个问题", 4));
		AssertSame(session, manager);

		// 反复读取走缓存，读数不漂。
		var stable = session.ContextTokens();
		Assert.Equal(stable, session.ContextTokens());
	}

	[Fact]
	public void MatchesReferenceEstimateWithAUsableUsageAnchor()
	{
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var session = Create(manager);
		manager.AppendMessage(System());
		manager.AppendMessage(User("问题", 1));
		manager.AppendMessage(Assistant("回答", 2, new Usage { Input = 1000, Output = 250, TotalTokens = 1250 }));
		manager.AppendMessage(User("后续问题", 3));
		manager.AppendMessage(Assistant("后续回答", 4, new Usage { Input = 2000, Output = 80, TotalTokens = 2080 }));
		manager.AppendMessage(User("再后续", 5));

		AssertSame(session, manager);
		var (tokens, index) = session.ContextTokens();
		Assert.NotNull(index);
		Assert.True(tokens >= 2080, "锚点之后的估算必须叠在真实用量上");
	}

	[Fact]
	public void MatchesReferenceEstimateAfterContextEditAndBranchNavigation()
	{
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var session = Create(manager);
		manager.AppendMessage(System());
		var first = manager.AppendMessage(User("将被替换的问题", 1));
		manager.AppendMessage(Assistant("回答", 2));
		var second = manager.AppendMessage(User("第二个问题", 3));
		manager.AppendMessage(Assistant("第二个回答", 4));

		AssertSame(session, manager);

		// 上下文编辑改写中间一条：前缀复用必须在那里停下来重算。
		manager.AppendContextEdit(first, MessageContent.FromText("替换后的问题，长度完全不同了"));
		AssertSame(session, manager);

		// 从早期节点重新分叉：投影整段换了引用，同样不能读到旧缓存。
		manager.Branch(second);
		AssertSame(session, manager);
		manager.AppendMessage(Assistant("分叉后的回答", 9));
		AssertSame(session, manager);
	}

	private static void AssertSame(CodingAgentSession session, SessionManager manager)
	{
		var reference = ContextTokens.EstimateContextTokens(manager.BuildProjectedMessages());
		var incremental = session.ContextTokens();
		Assert.Equal(reference.Tokens, incremental.Tokens);
		Assert.Equal(reference.LastUsageIndex, incremental.LastUsageIndex);
	}
}
