using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>D-01：ask_question 的 PiDeck 兼容信封、回答解析与宿主交互。</summary>
public class AskQuestionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-ask-" + Guid.NewGuid().ToString("N")[..8]);

	public AskQuestionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private sealed class FakeUiHost : IUiHost
	{
		public bool HasUi { get; set; } = true;

		public UiInputRequest? LastRequest { get; private set; }

		public string? Response { get; set; }

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(Response);
		}
	}

	private CodingAgentSession Session(IUiHost? host) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			UiHost = host,
		});

	private static Task<AgentToolResult> AskAsync(CodingAgentSession session, JsonObject arguments)
	{
		var tool = session.Tools.Single(static item => item.Name == AskQuestionTool.Name);
		return tool.Execute(new AgentToolInvocation
		{
			ToolCallId = "1",
			ToolName = tool.Name,
			Arguments = arguments,
		});
	}

	[Fact]
	public async Task Tool_CarriesThePiDeckBatchEnvelope()
	{
		var host = new FakeUiHost
		{
			Response = AskQuestions.EncodeResponse(
				[new AskAnswer("q1", "select", JsonValue.Create("B"), "B", false)],
				cancelled: false),
		};
		using var session = Session(host);

		var result = await AskAsync(session, new JsonObject
		{
			["questions"] = new JsonArray(new JsonObject
			{
				["question"] = "选哪个？",
				["options"] = new JsonArray("A", "B"),
			}),
		});

		Assert.False(result.IsError);
		Assert.Equal("q1: B", Assert.IsType<TextContent>(result.Content[0]).Text);

		var request = Assert.IsType<UiInputRequest>(host.LastRequest);
		Assert.Equal(AskQuestions.EnvelopePlaceholder, request.Placeholder);

		var questions = AskQuestions.ParseEnvelope(request.Title);
		Assert.NotNull(questions);
		var question = Assert.Single(questions!);
		Assert.Equal("select", question.Type);
		Assert.Equal(2, question.Options!.Count);
		Assert.Equal("A", question.Options[0].Value);
	}

	[Fact]
	public async Task Tool_ToleratesANonBooleanReviewFlag()
	{
		// 模型把 review 写成 "true"/1 不能把整个工具调用弄挂：开关只认真正的布尔。
		var host = new FakeUiHost
		{
			Response = AskQuestions.EncodeResponse(
				[new AskAnswer("q1", "select", JsonValue.Create("B"), "B", false)],
				cancelled: false),
		};
		using var session = Session(host);

		var result = await AskAsync(session, new JsonObject
		{
			["questions"] = new JsonArray(new JsonObject
			{
				["question"] = "选哪个？",
				["options"] = new JsonArray("A", "B"),
			}),
			["review"] = "true",
		});

		Assert.False(result.IsError);
		Assert.False(AskQuestions.ReviewRequested(host.LastRequest!.Title));

		// 真的传 true 时才开审阅页。
		await AskAsync(session, new JsonObject
		{
			["questions"] = new JsonArray(new JsonObject
			{
				["question"] = "选哪个？",
				["options"] = new JsonArray("A", "B"),
			}),
			["review"] = true,
		});
		Assert.True(AskQuestions.ReviewRequested(host.LastRequest!.Title));
	}

	[Fact]
	public async Task Tool_WithoutUiReportsUnsupported()
	{
		using var session = Session(host: null);

		var result = await AskAsync(session, new JsonObject
		{
			["questions"] = new JsonArray(new JsonObject { ["question"] = "继续吗？", ["type"] = "confirm" }),
		});

		Assert.True(result.IsError);
		Assert.Contains("不支持交互式 UI", Assert.IsType<TextContent>(result.Content[0]).Text);
	}

	[Fact]
	public async Task SelectWithoutOptionsIsRejectedBeforeTheUi()
	{
		var host = new FakeUiHost();
		using var session = Session(host);

		var result = await AskAsync(session, new JsonObject
		{
			["questions"] = new JsonArray(new JsonObject { ["question"] = "选哪个？", ["type"] = "select" }),
		});

		Assert.True(result.IsError);
		Assert.Contains("必须提供 options", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.Null(host.LastRequest);
	}

	[Fact]
	public void ParseResponse_TreatsIncompleteAnswersAsCancelled()
	{
		var questions = new List<AskQuestion> { new("q1", "input", "名字？", null, null, null) };

		// 空响应 → 取消。
		Assert.True(AskQuestions.ParseResponse(null, questions).Cancelled);
		// 明确取消。
		Assert.True(AskQuestions.ParseResponse("""{"cancelled":true,"answers":[]}""", questions).Cancelled);
		// 回答了 → 完成。
		var (answers, cancelled) = AskQuestions.ParseResponse("""{"cancelled":false,"answers":[{"id":"q1","type":"input","value":"张三"}]}""", questions);
		Assert.False(cancelled);
		Assert.Equal("张三", Assert.Single(answers).Value!.GetValue<string>());
		// 数组必须非空才算作答。
		var multi = new List<AskQuestion> { new("q1", "multi_select", "选？", null, null, null) };
		Assert.True(AskQuestions.ParseResponse("""{"cancelled":false,"answers":[{"id":"q1","type":"multi_select","value":[]}]}""", multi).Cancelled);
	}

	[Fact]
	public void Envelope_RoundTripsOptionsAndTypes()
	{
		var questions = new List<AskQuestion>
		{
			new("a", "select", "pick", [new AskOption("标签", "值", "说明")], "ph", "pf"),
			new("b", "editor", "code", null, null, null),
		};

		var envelope = AskQuestions.BuildEnvelope(questions, review: true);
		var parsed = AskQuestions.ParseEnvelope(envelope)!;

		Assert.Equal(2, parsed.Count);
		Assert.Equal("select", parsed[0].Type);
		var options = parsed[0].Options!;
		Assert.Equal("值", options[0].Value);
		Assert.Equal("说明", options[0].Description);
		Assert.Equal("ph", parsed[0].Placeholder);
		Assert.Equal("editor", parsed[1].Type);
		Assert.Null(AskQuestions.ParseEnvelope("not json"));
	}
}
