using System.Text.Json.Nodes;
using PiWeft.CodingAgent;

namespace PiWeft.Cli;

/// <summary>
/// 基于终端的 <see cref="IUiHost"/>：把 <c>ask_question</c> 的批量信封渲染成命令行问答，
/// 并提供 pi 扩展 UI 对话框方法（select/confirm/input/editor）的终端实现，
/// 供 CLI 交互模式使用。print/JSON 模式不提供宿主，工具会明确报告不可用。
/// </summary>
internal sealed class ConsoleUiHost(TextReader input, TextWriter output) : IUiHost
{
	public bool HasUi => true;

	/// <summary>终端没有真正的通知面：打到输出流（交互模式是 stderr 旁的提示流）。</summary>
	public void Notify(UiNotifyRequest request) =>
		output.WriteLine($"[notify:{request.NotifyType}] {request.Message}");

	public void SetStatus(UiStatusRequest request) =>
		output.WriteLine(request.Text is null ? $"[status:{request.Key}]" : $"[status:{request.Key}] {request.Text}");

	public void SetWidget(UiWidgetRequest request)
	{
		if (request.Lines is not { Count: > 0 } lines)
		{
			output.WriteLine($"[widget:{request.Key}]");
			return;
		}

		foreach (var line in lines)
		{
			output.WriteLine($"[widget:{request.Key}] {line}");
		}
	}

	public void SetTitle(UiTitleRequest request) => output.WriteLine($"[title] {request.Title}");

	/// <summary>终端输入循环不暴露可替换的编辑缓冲；set_editor_text 在这里保持 no-op。</summary>
	public void SetEditorText(string text)
	{
	}

	public async Task<string?> SelectAsync(UiSelectRequest request, CancellationToken cancellationToken)
	{
		output.WriteLine();
		output.WriteLine(request.Title);
		for (var index = 0; index < request.Options.Count; index++)
		{
			output.WriteLine($"  {index + 1}. {request.Options[index]}");
		}

		while (true)
		{
			output.Write("> ");
			output.Flush();
			var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (line is null)
			{
				return null;
			}

			var value = line.Trim();
			if (value.Length == 0)
			{
				return null; // 直接回车视为取消
			}

			if (int.TryParse(value, out var index) && index >= 1 && index <= request.Options.Count)
			{
				return request.Options[index - 1];
			}

			var match = request.Options.FirstOrDefault(option =>
				string.Equals(option, value, StringComparison.OrdinalIgnoreCase));
			if (match is not null)
			{
				return match;
			}

			output.WriteLine("  无效选择：请输入序号或选项文本。");
		}
	}

	public async Task<bool?> ConfirmAsync(UiConfirmRequest request, CancellationToken cancellationToken)
	{
		output.WriteLine();
		output.WriteLine(request.Title);
		if (request.Message is { Length: > 0 } message)
		{
			output.WriteLine(message);
		}

		while (true)
		{
			output.Write("y/n > ");
			output.Flush();
			var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (line is null)
			{
				return false;
			}

			var value = line.Trim();
			if (value is "y" or "Y" or "yes" or "是")
			{
				return true;
			}

			if (value is "n" or "N" or "no" or "否" or "")
			{
				return false;
			}
		}
	}

	public async Task<string?> EditorAsync(UiEditorRequest request, CancellationToken cancellationToken)
	{
		// 终端模式没有多行编辑器：退化为单行输入，空输入返回预填内容。
		output.WriteLine();
		output.WriteLine(request.Title);
		output.WriteLine("（终端模式为单行输入；直接回车保留原有内容）");
		if (request.Prefill is { Length: > 0 } prefill)
		{
			output.WriteLine($"当前内容：{prefill}");
		}

		output.Write("> ");
		output.Flush();
		var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
		if (line is null)
		{
			return null;
		}

		return line.Trim().Length == 0 ? request.Prefill ?? string.Empty : line.Trim();
	}

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
