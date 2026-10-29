using System.Text.Json.Nodes;
using PiWeft.Agent;

namespace PiWeft.CodingAgent.Tools;

/// <summary>
/// 原生 <c>ask_question</c> 工具：通过宿主 UI（RPC、CLI 交互或桌面）向用户提问，
/// 并使用与 PiDeck 完全一致的批量信封，因此 PiDeck 的桌面端能直接渲染问询卡片。
/// </summary>
public static class AskQuestionTool
{
	/// <summary>工具名。</summary>
	public const string Name = "ask_question";

	private const string Description =
		"Ask the user for input, a selection, or a confirmation; blocks until the user responds through the desktop UI. " +
		"Pass questions:[{type,question,options,placeholder,prefill}] — one or more questions in a single card, even for a single one; id is auto-assigned when omitted. " +
		"type is optional (defaults to select with options, else input); set it explicitly for confirm/editor/multi_select.";

	/// <summary>构建工具。<paramref name="session"/> 返回当前会话（其 <see cref="CodingAgentSession.UiHost"/> 提供交互）。</summary>
	public static BuiltinTool Create(Func<CodingAgentSession?> session) => new(
		new AgentTool
		{
			Name = Name,
			Label = "Ask Question",
			Description = Description,
			Parameters = Parameters(),
			Execute = invocation => ExecuteAsync(session(), invocation),
		},
		"Ask the user a question (or several in one call) and wait for responses",
		[
			"IMPORTANT RULE: Whenever you need ANY input from the user (a choice, confirmation, text, or multi-line content), you MUST use the ask_question tool. Do NOT write questions in plain text — that forces the user to type free-form replies and breaks the desktop UI interaction flow.",
			"Pass all questions in ONE questions array per call, never split related questions into repeated calls.",
			"Types: select = pick from options (strings or {label, value?, description?}; a custom free-text input is always shown); multi_select = pick multiple, returns an array; confirm = yes/no (e.g. destructive or irreversible actions); input = short text; editor = multi-line content like code.",
		]);

	private static JsonObject Parameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["questions"] = new JsonObject
			{
				["type"] = "array",
				["description"] = "One or more questions to ask in a single card. ALWAYS use this array, even for a single question.",
				["items"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["id"] = new JsonObject { ["type"] = "string", ["description"] = "Short unique id for this question; auto-assigned (q1, q2, …) when omitted" },
						["type"] = new JsonObject
						{
							["type"] = "string",
							["enum"] = new JsonArray([.. AskQuestions.Types.Select(static type => (JsonNode?)JsonValue.Create(type))]),
							["description"] = "Defaults to select when options are provided, otherwise input; set it explicitly for confirm/editor/multi_select",
						},
						["question"] = new JsonObject { ["type"] = "string", ["description"] = "The question or prompt to display" },
						["options"] = new JsonObject
						{
							["type"] = "array",
							["description"] = "Options for select / multi_select type questions",
							["items"] = new JsonObject
							{
								["anyOf"] = new JsonArray(
									new JsonObject { ["type"] = "string", ["description"] = "Option label; value defaults to the label itself" },
									new JsonObject
									{
										["type"] = "object",
										["properties"] = new JsonObject
										{
											["label"] = new JsonObject { ["type"] = "string", ["description"] = "Display label for the option" },
											["value"] = new JsonObject { ["type"] = "string", ["description"] = "Value returned when selected (defaults to label)" },
											["description"] = new JsonObject { ["type"] = "string", ["description"] = "Optional description shown alongside the label" },
										},
										["required"] = new JsonArray("label"),
									}),
							},
						},
						["placeholder"] = new JsonObject { ["type"] = "string", ["description"] = "Placeholder for input/editor questions" },
						["prefill"] = new JsonObject { ["type"] = "string", ["description"] = "Prefill for input/editor questions" },
					},
					["required"] = new JsonArray("question"),
				},
			},
			["review"] = new JsonObject { ["type"] = "boolean", ["description"] = "When true, shows a Submit/review tab of all answers for confirmation. Default false." },
		},
		["additionalProperties"] = false,
	};

	private static async Task<AgentToolResult> ExecuteAsync(CodingAgentSession? session, AgentToolInvocation invocation)
	{
		var questions = AskQuestions.FromToolArguments(invocation.Arguments);

		// 非交互环境：不阻塞，直接告知模型改用回复文本提问（与 PiDeck headless 行为一致）。
		if (session?.UiHost is not { HasUi: true } host)
		{
			return AgentToolResult.Error("ask_question 无法执行：当前环境不支持交互式 UI。");
		}

		foreach (var question in questions)
		{
			if ((question.Type is "select" or "multi_select") && question.Options is not { Count: > 0 })
			{
				return AgentToolResult.Error($"ask_question 未执行：{question.Type} 类型必须提供 options（问题: {question.Question}）");
			}
		}

		// review 可以是模型写的 true / "true" / 1：只在真的是布尔时生效，
		// 直接 GetValue<bool>() 遇到字符串会抛，整个工具调用就被一个无关紧要的开关弄挂。
		var review = invocation.Arguments["review"] is JsonValue reviewValue
			&& reviewValue.TryGetValue<bool>(out var reviewFlag)
			&& reviewFlag;
		var envelope = AskQuestions.BuildEnvelope(questions, review);
		var raw = await host
			.InputAsync(new UiInputRequest(envelope, AskQuestions.EnvelopePlaceholder), invocation.CancellationToken)
			.ConfigureAwait(false);

		var (answers, cancelled) = AskQuestions.ParseResponse(raw, questions);
		return new AgentToolResult
		{
			Content = [new PiWeft.Ai.TextContent { Text = AskQuestions.ResultText(answers, cancelled) }],
			Details = AskQuestions.ResultDetails(questions, answers, cancelled),
		};
	}
}
