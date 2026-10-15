using System.Text.Json.Nodes;
using PiWeft.CodingAgent;

namespace PiWeft.Cli;

/// <summary>
/// 基于终端的 <see cref="IUiHost"/>：把 <c>ask_question</c> 的批量信封渲染成命令行问答，
/// 供 CLI 交互模式使用。print/JSON 模式不提供宿主，工具会明确报告不可用。
/// </summary>
internal sealed class ConsoleUiHost(TextReader input, TextWriter output) : IUiHost
{
	public bool HasUi => true;

	public async Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken)
	{
		var questions = AskQuestions.ParseEnvelope(request.Title);
		if (questions is null || questions.Count == 0)
		{
			return null;
		}

		var answers = new List<AskAnswer>(questions.Count);
		foreach (var question in questions)
		{
			output.WriteLine();
			output.WriteLine(question.Question);
			if (question.Options is { Count: > 0 } options)
			{
				for (var index = 0; index < options.Count; index++)
				{
					var option = options[index];
					output.WriteLine(option.Description is { Length: > 0 } description
						? $"  {index + 1}. {option.Label} — {description}"
						: $"  {index + 1}. {option.Label}");
				}

				if (question.Type == "select")
				{
					output.WriteLine("  (输入序号、选项文本，或直接输入自定义内容)");
				}
			}

			output.Write("> ");
			output.Flush();

			var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (line is null)
			{
				return AskQuestions.EncodeResponse(answers, cancelled: true);
			}

			answers.Add(AnswerFor(question, line.Trim()));
		}

		return AskQuestions.EncodeResponse(answers, cancelled: false);
	}

	private static AskAnswer AnswerFor(AskQuestion question, string value)
	{
		switch (question.Type)
		{
			case "confirm":
			{
				var yes = value.Equals("y", StringComparison.OrdinalIgnoreCase) ||
					value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
					value is "是" or "1" or "true";
				return new AskAnswer(question.Id, question.Type, JsonValue.Create(yes), null, false);
			}

			case "multi_select":
			{
				var selected = new List<JsonNode?>();
				foreach (var token in value.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					var option = MatchOption(question.Options, token);
					selected.Add(JsonValue.Create(option?.Value ?? token));
				}

				return new AskAnswer(question.Id, question.Type, new JsonArray([.. selected]), null, false);
			}

			case "select":
			{
				var option = MatchOption(question.Options, value);
				return option is not null
					? new AskAnswer(question.Id, question.Type, JsonValue.Create(option.Value), option.Label, false)
					: new AskAnswer(question.Id, question.Type, JsonValue.Create(value), null, true);
			}

			default:
				return new AskAnswer(
					question.Id,
					question.Type,
					JsonValue.Create(value.Length > 0 ? value : question.Prefill ?? string.Empty),
					null,
					false);
		}
	}

	private static AskOption? MatchOption(IReadOnlyList<AskOption>? options, string token)
	{
		if (options is null || token.Length == 0)
		{
			return null;
		}

		if (int.TryParse(token, out var index) && index >= 1 && index <= options.Count)
		{
			return options[index - 1];
		}

		foreach (var option in options)
		{
			if (string.Equals(option.Label, token, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(option.Value, token, StringComparison.OrdinalIgnoreCase))
			{
				return option;
			}
		}

		return null;
	}
}
