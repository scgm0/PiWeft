using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// 工具参数里的一行键值。
internal readonly record struct ToolCallField(string Label, string Value);

/// 待办工具的一项计划内容。
internal readonly record struct ToolCallTodo(string Status, string Text);

/// ask_question 的一问一答；答案为空表示这一题还没作答。
internal readonly record struct ToolCallExchange(string Question, string? Answer);

/// 展开工具行时的结构化参数：分栏字段、正文块、待办项、逐题问答与解析失败时的原文兜底。
/// 内置工具按自己的参数形状出字段，其它工具（MCP、扩展）只在参数全为标量时平铺，否则保留缩进 JSON。
internal sealed record ToolCallDetails(
	IReadOnlyList<ToolCallField> Fields,
	string? BlockLabel,
	string? BlockText,
	IReadOnlyList<ToolCallTodo> Todos,
	string? Fallback)
{
	/// ask_question 的题目与答案：结果正文只有 id，配回题干才能看出哪题答了什么。
	public IReadOnlyList<ToolCallExchange> Exchanges { get; init; } = [];

	private static readonly ToolCallDetails Empty = new([], null, null, [], null);

	private static readonly JsonWriterOptions Indented = new()
	{
		Indented = true,
		// 输入是已解析的 JsonElement，跳过二次校验；中文要保留原样，默认编码器会写成 \uXXXX。
		SkipValidation = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static ToolCallDetails For(ChatEntry entry)
	{
		if (entry.ToolArguments is not { Length: > 0 } arguments) return Empty;
		// 历史上缺少调用参数时，投影会拿结果正文顶替 ToolArguments；展开时别把它再当参数显示一遍。
		// 中断的工具相反：Text 还停在参数占位，参数要留在正文里，结果区不该再抄一遍。
		if (!entry.IsStreaming && !entry.IsStopped && string.Equals(arguments, entry.Text, StringComparison.Ordinal)) return Empty;
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(arguments);
		}
		catch (Exception error) when (error is JsonException or ArgumentException)
		{
			return Empty with { Fallback = arguments };
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return Empty with { Fallback = Pretty(root) };
			return entry.ToolName switch
			{
				"bash" or "terminal" or "powershell" => Shell(root) ?? Unknown(root),
				"read" => Read(root) ?? Unknown(root),
				"write" => Write(root) ?? Unknown(root),
				"edit" => Edit(root, entry.Patch) ?? Unknown(root),
				"grep" => Grep(root) ?? Unknown(root),
				"find" => Find(root) ?? Unknown(root),
				"ls" => Ls(root) ?? Unknown(root),
				"todo" => Todo(root) ?? Unknown(root),
				"ask_question" => Ask(root, entry) ?? Unknown(root),
				"codemode" => Code(root) ?? Unknown(root),
				"delegate" => Delegate(root) ?? Unknown(root),
				"wait_subagents" => SubagentWait(root) ?? Unknown(root),
				"send_to_subagent" => SubagentSend(root) ?? Unknown(root),
				"stop_subagent" => IdField(root) ?? Unknown(root),
				_ => Unknown(root),
			};
		}
	}

	/// 组头与工具行摘要用的一行 todo 文案；读不到参数时返回 null，让调用方退回原文。
	internal static string? TodoPreview(string? arguments)
	{
		if (arguments is not { Length: > 0 }) return null;
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(arguments);
		}
		catch (Exception error) when (error is JsonException or ArgumentException)
		{
			return null;
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || Text(root, "action") is not { } action) return null;
			var label = Strings.ToolTodoAction(action);
			if (Text(root, "text") is { } text) return $"{label} {Strings.ToolDetail(text)}";
			if (Integer(root, "id") is { } id) return $"{label} #{id}";
			if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
				return $"{label} {Strings.ToolTodoCount(items.GetArrayLength())}";
			return label;
		}
	}

	/// 一次委派：角色、名字、上下文模式是字段，任务全文放进正文块。
	private static ToolCallDetails? Delegate(JsonElement root)
	{
		if (Text(root, "agent") is not { } agent) return null;
		var fields = new List<ToolCallField> { new(Strings.ToolFieldAgent, agent) };
		if (Text(root, "name") is { } name) fields.Add(new(Strings.ToolFieldSubagentName, name));
		if (Text(root, "context") is { Length: > 0 } context) fields.Add(new(Strings.ToolFieldContext, Strings.ToolContextMode(context)));
		var task = StringValue(root, "task");
		return new ToolCallDetails(fields, Strings.ToolFieldTask, task, [], task is null ? Pretty(root) : null);
	}

	private static ToolCallDetails? SubagentWait(JsonElement root)
	{
		var fields = new List<ToolCallField>();
		if (root.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
		{
			var list = new List<string>(names.GetArrayLength());
			foreach (var item in names.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) list.Add(text);
			}

			if (list.Count > 0) fields.Add(new(Strings.ToolFieldSubagentNames, string.Join(", ", list)));
		}

		if (Integer(root, "timeout_ms") is { } timeout) fields.Add(new(Strings.ToolFieldTimeout, Strings.ToolTimeoutMilliseconds(timeout)));
		return fields.Count > 0 ? new ToolCallDetails(fields, null, null, [], null) : null;
	}

	/// 发给子代理的消息：目标是字段，正文是消息全文。
	private static ToolCallDetails? SubagentSend(JsonElement root)
	{
		if (Text(root, "id") is not { } id) return null;
		var message = StringValue(root, "message");
		return new ToolCallDetails(
			[new(Strings.ToolFieldSubagentTarget, id)],
			Strings.ToolFieldMessage,
			message,
			[],
			message is null ? Pretty(root) : null);
	}

	private static ToolCallDetails? IdField(JsonElement root) =>
		Text(root, "id") is { } id
			? new ToolCallDetails([new(Strings.ToolFieldSubagentTarget, id)], null, null, [], null)
			: null;

	private static ToolCallDetails? Shell(JsonElement root)
	{
		if (StringValue(root, "command") is not { } command) return null;
		var fields = new List<ToolCallField>();
		if (Integer(root, "timeout") is { } timeout) fields.Add(new(Strings.ToolFieldTimeout, Strings.ToolTimeout(timeout)));
		return new ToolCallDetails(fields, Strings.ToolCommandLabel, command, [], null);
	}

	private static ToolCallDetails? Read(JsonElement root)
	{
		if (Text(root, "path") is not { } path) return null;
		var fields = new List<ToolCallField> { new(Strings.ToolFieldPath, path) };
		var offset = Integer(root, "offset");
		var limit = Integer(root, "limit");
		if (offset is not null || limit is not null)
			fields.Add(new(Strings.ToolFieldRange, Strings.ToolReadRange(offset, limit)));
		return new ToolCallDetails(fields, null, null, [], null);
	}

	private static ToolCallDetails? Write(JsonElement root)
	{
		if (Text(root, "path") is not { } path) return null;
		var content = StringValue(root, "content");
		var fields = new List<ToolCallField> { new(Strings.ToolFieldPath, path) };
		if (content is not null) fields.Add(new(Strings.ToolFieldLines, Strings.ToolLineCount(LineCount(content))));
		var details = new ToolCallDetails(fields, Strings.ToolContentLabel, content, [], null);
		// 内容缺失通常是参数坏掉：留一份原始 JSON，别让展开后只剩路径。
		return content is null ? details with { Fallback = Pretty(root) } : details;
	}

	private static ToolCallDetails? Edit(JsonElement root, string? patch)
	{
		if (Text(root, "path") is not { } path) return null;
		var fields = new List<ToolCallField> { new(Strings.ToolFieldPath, path) };
		if (root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array &&
			edits.GetArrayLength() > 0)
		{
			fields.Add(new(Strings.ToolFieldEdits, Strings.ToolEditCount(edits.GetArrayLength())));
		}
		var details = new ToolCallDetails(fields, null, null, [], null);
		// 有 patch 时展开就是看 diff，原始参数里的 oldText/newText 只会重复正文。
		return patch is { Length: > 0 } ? details : details with { Fallback = Pretty(root) };
	}

	private static ToolCallDetails? Grep(JsonElement root)
	{
		if (Text(root, "pattern") is not { } pattern) return null;
		var fields = new List<ToolCallField> { new(Strings.ToolFieldPattern, pattern) };
		Add(fields, Strings.ToolFieldPath, Text(root, "path"));
		Add(fields, Strings.ToolFieldGlob, Text(root, "glob"));
		if (Bool(root, "ignoreCase") == true) fields.Add(new(Strings.ToolFieldIgnoreCase, Strings.AskYes));
		if (Bool(root, "literal") == true) fields.Add(new(Strings.ToolFieldLiteral, Strings.AskYes));
		if (Integer(root, "context") is { } context && context > 0)
			fields.Add(new(Strings.ToolFieldContext, Strings.ToolLineCount((int)context)));
		Add(fields, Strings.ToolFieldLimit, Integer(root, "limit"));
		return new ToolCallDetails(fields, null, null, [], null);
	}

	private static ToolCallDetails? Find(JsonElement root)
	{
		if (Text(root, "pattern") is not { } pattern) return null;
		var fields = new List<ToolCallField> { new(Strings.ToolFieldPattern, pattern) };
		Add(fields, Strings.ToolFieldPath, Text(root, "path"));
		Add(fields, Strings.ToolFieldLimit, Integer(root, "limit"));
		return new ToolCallDetails(fields, null, null, [], null);
	}

	private static ToolCallDetails? Ls(JsonElement root)
	{
		var path = Text(root, "path");
		var limit = Integer(root, "limit");
		if (path is null && limit is null) return null;
		var fields = new List<ToolCallField>();
		Add(fields, Strings.ToolFieldPath, path);
		Add(fields, Strings.ToolFieldLimit, limit);
		return new ToolCallDetails(fields, null, null, [], null);
	}

	private static ToolCallDetails? Todo(JsonElement root)
	{
		if (Text(root, "action") is not { } action) return null;
		var fields = new List<ToolCallField> { new(Strings.ToolFieldAction, Strings.ToolTodoAction(action)) };
		Add(fields, Strings.ToolFieldId, Integer(root, "id"));
		Add(fields, Strings.ToolFieldTodo, Text(root, "text"));
		if (Text(root, "status") is { } status)
			fields.Add(new(Strings.ToolFieldStatus, Strings.ToolTodoStatus(status)));
		var todos = new List<ToolCallTodo>();
		if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in items.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object && Text(item, "text") is { } text)
					todos.Add(new ToolCallTodo(Text(item, "status") ?? "pending", text));
			}
		}
		return new ToolCallDetails(fields, null, null, todos, null);
	}

	private static ToolCallDetails? Ask(JsonElement root, ChatEntry entry)
	{
		var ids = new List<string>();
		var exchanges = new List<ToolCallExchange>();
		if (root.TryGetProperty("questions", out var array) && array.ValueKind == JsonValueKind.Array)
		{
			var position = 0;
			foreach (var item in array.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object && Text(item, "question") is { } text)
				{
					ids.Add(Text(item, "id") ?? $"q{position + 1}");
					exchanges.Add(new ToolCallExchange(text, null));
				}
				position++;
			}
		}
		// 兼容旧式顶层字段，默认 id 与 AskQuestions 一致。
		if (exchanges.Count == 0 && Text(root, "question") is { } single)
		{
			ids.Add(Text(root, "id") ?? "default");
			exchanges.Add(new ToolCallExchange(single, null));
		}
		if (exchanges.Count == 0) return null;

		var answers = Answers(entry.ToolDetails);
		// 只配齐了才并到题目上：缺一半时分开显示会让人以为另一题没问。
		if (answers is not null && ids.TrueForAll(answers.ContainsKey))
		{
			for (var index = 0; index < exchanges.Count; index++)
				exchanges[index] = exchanges[index] with { Answer = answers[ids[index]] };
		}
		return new ToolCallDetails([], Strings.ToolQuestionLabel, null, [], null) { Exchanges = exchanges };
	}

	private static Dictionary<string, string>? Answers(JsonNode? details)
	{
		if (details is not JsonObject root || root["answers"] is not JsonArray array || array.Count == 0) return null;
		var answers = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var node in array)
		{
			if (node is not JsonObject item || item["id"]?.GetValue<string>() is not { Length: > 0 } id) continue;
			// 选项的 value 只是机器 id（diff、one-row）；给人看的是 label，没有 label 的自定义输入/文本题才退回值。
			var text = item["label"]?.GetValue<string>();
			if (text is not { Length: > 0 })
			{
				text = AskQuestions.AnswerText(new AskAnswer(
					id,
					item["type"]?.GetValue<string>() ?? "input",
					item["value"],
					item["label"]?.GetValue<string>(),
					item["wasCustom"]?.GetValue<bool>() == true));
			}
			// 空答案等同没答：取消/未答的题不能算配上，否则结果区会被一行空白替掉。
			if (text.Length > 0) answers[id] = text;
		}
		return answers.Count > 0 ? answers : null;
	}

	private static ToolCallDetails? Code(JsonElement root)
	{
		if (StringValue(root, "code") is not { } code) return null;
		return new ToolCallDetails([], Strings.ToolCodeLabel, code, [], null);
	}

	private static ToolCallDetails Unknown(JsonElement root)
	{
		// 标量参数直接平铺成字段；出现数组或对象时保留 JSON 形状，避免把嵌套结构压成一行。
		var fields = new List<ToolCallField>();
		foreach (var property in root.EnumerateObject())
		{
			var value = property.Value;
			if (value.ValueKind == JsonValueKind.Null)
			{
				fields.Add(new ToolCallField(property.Name, "null"));
				continue;
			}

			if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
			{
				return new ToolCallDetails([], null, null, [], Pretty(root));
			}

			fields.Add(new ToolCallField(property.Name, Scalar(value)));
		}
		return new ToolCallDetails(fields, null, null, [], null);
	}

	private static void Add(List<ToolCallField> fields, string label, string? value)
	{
		if (value is { Length: > 0 }) fields.Add(new ToolCallField(label, value));
	}

	private static void Add(List<ToolCallField> fields, string label, long? value)
	{
		if (value is { } number) fields.Add(new ToolCallField(label, number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
	}

	private static string? Text(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
		value.GetString() is { Length: > 0 } text
			? text
			: null;

	private static string? StringValue(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static long? Integer(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
		if (value.TryGetInt64(out var integer)) return integer;
		if (!value.TryGetDouble(out var number) || !double.IsFinite(number) ||
			number < long.MinValue || number > long.MaxValue) return null;
		return (long)number;
	}

	private static bool? Bool(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value)
			? value.ValueKind switch
			{
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => null,
			}
			: null;

	private static string Scalar(JsonElement value) =>
		value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

	private static string Pretty(JsonElement root)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer, Indented)) root.WriteTo(writer);
		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static int LineCount(string text)
	{
		if (text.Length == 0) return 0;
		var newlines = text.AsSpan().Count('\n');
		// 末行换行不额外算一行："a\n" 是 1 行，不是 2 行。
		return text[^1] == '\n' ? newlines : newlines + 1;
	}
}
