using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;

using PiWeft.CodingAgent.Compatibility;

namespace PiWeft.CodingAgent;

public sealed record AskOption(string Label, string Value, string? Description);

public sealed record AskQuestion(
	string Id,
	string Type,
	string Question,
	IReadOnlyList<AskOption>? Options,
	string? Placeholder,
	string? Prefill);

/// 一条答案。<see cref="Skipped"/> 为 true 表示用户在卡片上主动跳过本题，
/// 此时 <see cref="Value"/> 为 null，但不能当成「没答」——它已经处理完了。
public sealed record AskAnswer(string Id, string Type, JsonNode? Value, string? Label, bool WasCustom, bool Skipped = false);

/// 一次问询的结果。PiDeck 0.12 把「关闭卡片但答了一部分」与「整单取消」分开：
/// <see cref="Closed"/> 为 true 时 <see cref="Answers"/> 里的内容有效，
/// 未出现的题目按未作答处理。
public sealed record AskResponse(IReadOnlyList<AskAnswer> Answers, bool Cancelled, bool Closed)
{
	/// 保留旧调用点的两元解构；新代码应读 <see cref="Closed"/>。
	public void Deconstruct(out IReadOnlyList<AskAnswer> answers, out bool cancelled)
	{
		answers = Answers;
		cancelled = Cancelled;
	}
}

/// <c>ask_question</c> 的线协议编解码：与 PiDeck 的 <c>pi-deck-ask-question.ts</c> 完全一致。
/// 无论单个还是批量问题，都通过一次 <c>ui.input</c> 提交：<c>title</c> 是 JSON 信封
/// （键 <c>__piDeckBatchAsk</c>），<c>placeholder</c> 是固定哨兵值 <c>__piDeckBatchAsk__</c>。
/// PiDeck 的渲染层据此展开为问询卡片，回答以同构 JSON 字符串返回。
public static class AskQuestions
{
	public const string EnvelopeKey = PiDeckProtocol.AskEnvelopeKey;

	public const string EnvelopePlaceholder = PiDeckProtocol.AskEnvelopePlaceholder;

	public static readonly string[] Types = ["select", "multi_select", "confirm", "input", "editor"];

	/// 模型写多选的别名键：PiDeck 0.12 把这些意图键当 multi_select，而不是硬失败。
	private static readonly string[] MultiIntentKeys = ["multi_select", "multiSelect", "multiselect", "multi", "multiple"];

	/// 模型写多行文本的别名键。
	private static readonly string[] EditorIntentKeys = ["multiline", "multiLine", "multi_line"];

	private static readonly HashSet<string> QuestionKeys = new(StringComparer.Ordinal)
	{
		"id", "type", "question", "options", "placeholder", "prefill", "allowOther",
	};

	/// JS 原型链上的名字：PiDeck 用对象按 id 索引答案，q1 之类的 id 撞上会污染原型。
	private static readonly HashSet<string> PrototypeNames = new(StringComparer.Ordinal)
	{
		"__proto__", "constructor", "prototype", "toString", "valueOf", "hasOwnProperty",
		"isPrototypeOf", "propertyIsEnumerable", "toLocaleString",
	};

	public static IReadOnlyList<AskQuestion> FromToolArguments(JsonObject arguments)
	{
		var raw = arguments["questions"] as JsonArray;
		var questions = new List<AskQuestion>();
		if (raw is { Count: > 0 })
		{
			var ids = AssignIds(raw);
			// 外层 type / 意图键是各题的默认 type（pi 0.5.x 的批量提问偶尔这么写）。
			var outerType = DeclaredType(arguments, optionsIsShape: false);
			for (var index = 0; index < raw.Count; index++)
			{
				if (raw[index] is JsonObject item)
				{
					questions.Add(Normalize(item, ids[index], outerType));
				}
			}

			return questions;
		}

		// 兼容旧模型直接传顶层字段。
		if (arguments["question"] is not null)
		{
			// PiDeck 对旧式单问题使用 "default"；批量问题才用 q1/q2。
			questions.Add(Normalize(arguments, "default", fallbackType: null));
		}

		return questions;
	}

	public static string BuildEnvelope(IReadOnlyList<AskQuestion> questions, bool review)
	{
		var array = new List<JsonNode?>(questions.Count);
		foreach (var question in questions)
		{
			var item = new JsonObject
			{
				["id"] = question.Id,
				["type"] = question.Type,
				["question"] = question.Question,
			};

			if (question.Options is { Count: > 0 } options)
			{
				item["options"] = OptionsNode(options);
			}

			if (question.Type == "select")
			{
				item["allowOther"] = true;
			}

			if (question.Placeholder is { Length: > 0 } placeholder)
			{
				item["placeholder"] = placeholder;
			}

			if (question.Prefill is { Length: > 0 } prefill)
			{
				item["prefill"] = prefill;
			}

			array.Add(item);
		}

		return new JsonObject
		{
			[EnvelopeKey] = 1,
			["review"] = review,
			["questions"] = new JsonArray([.. array]),
		}.ToJsonString(PiJson.NodeOptions);
	}

	public static IReadOnlyList<AskQuestion>? ParseEnvelope(string? title)
	{
		if (title is null || title.Length == 0)
		{
			return null;
		}

		try
		{
			if (JsonNode.Parse(title) is not JsonObject obj || obj[EnvelopeKey] is null)
			{
				return null;
			}

			return ParseQuestionArray(obj["questions"] as JsonArray);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static bool ReviewRequested(string? title)
	{
		if (string.IsNullOrEmpty(title))
		{
			return false;
		}

		try
		{
			return JsonNode.Parse(title) is JsonObject obj
				&& obj[EnvelopeKey] is not null
				&& obj["review"] is JsonValue value
				&& value.TryGetValue<bool>(out var review)
				&& review;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	public static AskResponse ParseResponse(string? raw, IReadOnlyList<AskQuestion> questions)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return new AskResponse([], Cancelled: true, Closed: false);
		}

		JsonObject? obj;
		try
		{
			obj = JsonNode.Parse(raw) as JsonObject;
		}
		catch (JsonException)
		{
			return new AskResponse([], Cancelled: true, Closed: false);
		}

		if (obj is null)
		{
			return new AskResponse([], Cancelled: true, Closed: false);
		}

		var answers = new List<AskAnswer>();
		if (obj["answers"] is JsonArray array)
		{
			foreach (var node in array)
			{
				if (node is not JsonObject item)
				{
					continue;
				}

				answers.Add(new AskAnswer(
					item["id"]?.GetValue<string>() ?? string.Empty,
					item["type"]?.GetValue<string>() ?? "input",
					item["value"]?.DeepClone(),
					item["label"]?.GetValue<string>(),
					item["wasCustom"]?.GetValue<bool>() == true,
					item["skipped"]?.GetValue<bool>() == true));
			}
		}

		var cancelledFlag = obj["cancelled"]?.GetValue<bool>() == true;
		var answeredAny = answers.Any(HasAnswerValue);
		if (cancelledFlag)
		{
			// PiDeck 0.12：带答案的 cancelled 信封 = 答了一部分后关闭卡片，已答内容有效。
			return new AskResponse(answers, Cancelled: !answeredAny, Closed: answeredAny);
		}

		// 主动跳过算已处理；答案条数不足（旧桌面端或异常信封）仍算没答完。
		var complete = answers.Count >= questions.Count &&
			answers.All(static answer => answer.Skipped || HasAnswerValue(answer));
		return complete
			? new AskResponse(answers, Cancelled: false, Closed: false)
			: new AskResponse(answers, Cancelled: !answeredAny, Closed: answeredAny);
	}

	public static string EncodeResponse(IReadOnlyList<AskAnswer> answers, bool cancelled)
	{
		var array = new List<JsonNode?>(answers.Count);
		foreach (var answer in answers)
		{
			var item = new JsonObject
			{
				["id"] = answer.Id,
				["type"] = answer.Type,
				// 跳过题也写 value:null，与 PiDeck 面板的信封同形。
				["value"] = answer.Value?.DeepClone(),
			};

			if (answer.Label is { Length: > 0 } label)
			{
				item["label"] = label;
			}

			if (answer.WasCustom)
			{
				item["wasCustom"] = true;
			}

			if (answer.Skipped)
			{
				item["skipped"] = true;
			}

			array.Add(item);
		}

		return new JsonObject { ["cancelled"] = cancelled, ["answers"] = new JsonArray([.. array]) }.ToJsonString(PiJson.NodeOptions);
	}

	/// 模型可见的结果文本：PiDeck 0.12 在关闭卡片时逐题列出「未作答 / 跳过」，
	/// 不让模型把空答案当成用户的选择。
	public static string ResultText(
		IReadOnlyList<AskQuestion> questions,
		IReadOnlyList<AskAnswer> answers,
		bool cancelled,
		bool closed = false)
	{
		var answered = answers.Where(HasAnswerValue).ToList();
		if (cancelled && !closed && answered.Count == 0)
		{
			return "用户取消了提问";
		}

		if (questions.Count == 0 && answers.Count == 0)
		{
			return "无答案";
		}

		var byId = new Dictionary<string, AskAnswer>(StringComparer.Ordinal);
		foreach (var answer in answers)
		{
			byId[answer.Id] = answer;
		}

		var lines = new List<string>(questions.Count + answers.Count);
		foreach (var question in questions)
		{
			if (byId.TryGetValue(question.Id, out var answer))
			{
				lines.Add($"{question.Id}: {LineOf(answer)}");
			}
			else
			{
				lines.Add($"{question.Id}: (未作答)");
			}
		}

		foreach (var answer in answers)
		{
			if (!questions.Any(question => string.Equals(question.Id, answer.Id, StringComparison.Ordinal)))
			{
				lines.Add($"{answer.Id}: {LineOf(answer)}");
			}
		}

		var body = lines.Count > 0 ? string.Join("\n", lines) : "无答案";
		return closed
			? "用户关闭了提问卡，没有答完全部问题。下面逐题列出作答情况（「否」也是有效作答）；标为未作答或跳过的题不要替用户假设答案：\n" + body
			: body;
	}

	/// 一条答案的展示文本：自行输入带前缀，多选以顿号连接，布尔写「是/否」。
	public static string AnswerText(AskAnswer answer)
	{
		if (answer.Skipped)
		{
			return "(用户跳过，未作答)";
		}

		if (!IsAnswered(answer.Value))
		{
			return "(未作答)";
		}

		return $"{(answer.WasCustom ? "(自行输入) " : string.Empty)}{FormatValue(answer.Value)}";
	}

	public static JsonObject ResultDetails(
		IReadOnlyList<AskQuestion> questions,
		IReadOnlyList<AskAnswer> answers,
		bool cancelled,
		bool closed = false)
	{
		var details = new JsonObject
		{
			["questions"] = new JsonArray([.. questions.Select(static question =>
			{
				var item = new JsonObject
				{
					["id"] = question.Id,
					["type"] = question.Type,
					["question"] = question.Question,
				};

				if (question.Options is { Count: > 0 } options)
				{
					item["options"] = OptionsNode(options);
				}

				if (question.Type == "select")
				{
					item["allowOther"] = true;
				}

				if (question.Placeholder is { Length: > 0 } placeholder)
				{
					item["placeholder"] = placeholder;
				}

				if (question.Prefill is { Length: > 0 } prefill)
				{
					item["prefill"] = prefill;
				}

				return (JsonNode?)item;
			})]),
			["answers"] = new JsonArray([.. answers.Select(static answer =>
			{
				var item = new JsonObject
				{
					["id"] = answer.Id,
					["type"] = answer.Type,
					["value"] = answer.Value?.DeepClone(),
					["wasCustom"] = answer.WasCustom,
				};
				if (answer.Label is { Length: > 0 } label)
				{
					item["label"] = label;
				}

				if (answer.Skipped)
				{
					item["skipped"] = true;
				}

				return (JsonNode?)item;
			})]),
			["cancelled"] = cancelled,
		};

		if (closed)
		{
			details["closed"] = true;
		}

		return details;
	}

	private static IReadOnlyList<AskQuestion> ParseQuestionArray(JsonArray? array)
	{
		var questions = new List<AskQuestion>();
		if (array is null)
		{
			return questions;
		}

		var ids = AssignIds(array);
		for (var index = 0; index < array.Count; index++)
		{
			if (array[index] is JsonObject item)
			{
				questions.Add(Normalize(item, ids[index], fallbackType: null));
			}
		}

		return questions;
	}

	/// 与 PiDeck 的 assignQuestionIds 同口径：缺 id 用 q1/q2，重名加 -2/-3，
	/// 并绕开 JS 原型链上的名字。
	private static IReadOnlyList<string> AssignIds(JsonArray array)
	{
		var declared = new string?[array.Count];
		var reserved = new HashSet<string>(StringComparer.Ordinal);
		for (var index = 0; index < array.Count; index++)
		{
			var id = (array[index] as JsonObject)?["id"];
			var text = id is null ? null : id.ToString();
			declared[index] = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
			if (declared[index] is { } value)
			{
				reserved.Add(value);
			}
		}

		var used = new HashSet<string>(StringComparer.Ordinal);
		var assigned = new string[array.Count];
		for (var index = 0; index < array.Count; index++)
		{
			var id = declared[index];
			var basis = id ?? $"q{index + 1}";
			var candidate = basis;
			var suffix = 2;
			while (used.Contains(candidate) ||
				PrototypeNames.Contains(candidate) ||
				(candidate != id && reserved.Contains(candidate)))
			{
				candidate = $"{basis}-{suffix++}";
			}

			used.Add(candidate);
			assigned[index] = candidate;
		}

		return assigned;
	}

	private static AskQuestion Normalize(JsonObject item, string id, string? fallbackType)
	{
		var options = ParseOptions(item["options"]);
		var type = ResolveType(item, options, fallbackType);
		// 与 PiDeck 一样：非选择题不把 options 带进卡片，避免各端对错位字段做不同解释。
		var effectiveOptions = IsPickType(type) ? options : null;

		return new AskQuestion(
			id,
			type,
			item["question"]?.GetValue<string>() ?? string.Empty,
			effectiveOptions,
			item["placeholder"]?.GetValue<string>(),
			item["prefill"]?.GetValue<string>());
	}

	private static string ResolveType(JsonObject item, IReadOnlyList<AskOption>? options, string? fallbackType)
	{
		var declared = DeclaredType(item, optionsIsShape: true);
		if (declared is not null)
		{
			return declared;
		}

		var hasOptions = options is { Count: > 0 };
		if (fallbackType is not null &&
			!string.Equals(fallbackType, "confirm", StringComparison.Ordinal) &&
			IsPickType(fallbackType) == hasOptions)
		{
			return fallbackType;
		}

		// 按形状兜底：带选项推断为选择题，否则是文本输入。
		return hasOptions ? "select" : "input";
	}

	/// 模型写了哪种题型：规范 type、别名、多选/多行意图键都算。
	private static string? DeclaredType(JsonObject item, bool optionsIsShape)
	{
		var canonical = CanonicalType(item["type"]);
		if (canonical is not null)
		{
			// `type:"select"` 旁边写 `multi_select:true` 时按多选处理（PiDeck 0.12 同义）。
			if (canonical == "select" && MultiIntentKeys.Any(key => IsIntentOn(item[key])))
			{
				return "multi_select";
			}

			return canonical;
		}

		if (MultiIntentKeys.Any(key => IsIntentOn(item[key])))
		{
			return "multi_select";
		}

		if (EditorIntentKeys.Any(key => IsIntentOn(item[key])))
		{
			// 带 options 时多行意图键没意义：按选择题形状走，不折成 editor。
			return optionsIsShape && item["options"] is JsonArray { Count: > 0 } ? null : "editor";
		}

		// 未声明键里出现 multiSelect / multipleChoice 这类写法：按多选意图处理。
		foreach (var pair in item)
		{
			if (!QuestionKeys.Contains(pair.Key) &&
				!MultiIntentKeys.Contains(pair.Key, StringComparer.Ordinal) &&
				!EditorIntentKeys.Contains(pair.Key, StringComparer.Ordinal) &&
				IsIntentOn(pair.Value) &&
				IsMultiLikeToken(pair.Key) &&
				!IsLineLikeToken(pair.Key))
			{
				return "multi_select";
			}
		}

		return null;
	}

	private static string? CanonicalType(JsonNode? node)
	{
		if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
		{
			return null;
		}

		var token = NormalizeToken(value.GetValue<string>());
		foreach (var type in Types)
		{
			if (string.Equals(NormalizeToken(type), token, StringComparison.Ordinal))
			{
				return type;
			}
		}

		if (IsLineLikeToken(token))
		{
			return "editor";
		}

		// `multi-select` / `multiSelect` / `multiple` 这类别名。
		if (IsMultiLikeToken(token))
		{
			return "multi_select";
		}

		return null;
	}

	private static string NormalizeToken(string value) =>
		string.Concat(value.Trim().ToLowerInvariant().Where(static c => c is not (' ' or '_' or '-')));

	private static bool IsMultiLikeToken(string token)
	{
		var normalized = NormalizeToken(token);
		return normalized is "multi" or "multiple" ||
			normalized.Contains("multi", StringComparison.Ordinal) ||
			normalized.Contains("multiple", StringComparison.Ordinal) ||
			normalized.Contains("checkbox", StringComparison.Ordinal) ||
			normalized.Contains("checklist", StringComparison.Ordinal);
	}

	private static bool IsLineLikeToken(string token)
	{
		var normalized = NormalizeToken(token);
		return normalized.Contains("multiline", StringComparison.Ordinal) ||
			normalized.Contains("multipleline", StringComparison.Ordinal);
	}

	/// 意图键的真假值。PiDeck 会区分 "single"/"off"/"否" 等反义写法；
	/// 这里只兜住常见的布尔与字符串，避免把 `multi_select:false` 当成多选。
	private static bool IsIntentOn(JsonNode? node) => node switch
	{
		null => false,
		JsonValue value when value.GetValueKind() == JsonValueKind.True => true,
		JsonValue value when value.GetValueKind() == JsonValueKind.False => false,
		JsonValue value when value.GetValueKind() == JsonValueKind.Number =>
			value.TryGetValue<double>(out var number) && number != 0,
		JsonValue value when value.GetValueKind() == JsonValueKind.String =>
			IsTruthyText(value.GetValue<string>()),
		_ => false,
	};

	private static bool IsTruthyText(string text)
	{
		var trimmed = text.Trim();
		if (trimmed.Length == 0)
		{
			return false;
		}

		var token = NormalizeToken(trimmed);
		return token is not ("false" or "no" or "off" or "none" or "null" or "0" or "n"
			or "否" or "不" or "非" or "无" or "单行" or "单选" or "single" or "singlechoice" or "singleselect"
			or "singleonly" or "onlyone" or "one" or "oneof" or "exclusive");
	}

	private static bool IsPickType(string type) => type is "select" or "multi_select";

	private static bool HasAnswerValue(AskAnswer answer) =>
		!answer.Skipped && IsAnswered(answer.Value);

	private static string LineOf(AskAnswer answer) => AnswerText(answer);

	private static JsonArray OptionsNode(IReadOnlyList<AskOption> options) =>
		new([.. options.Select(static option =>
		{
			var node = new JsonObject { ["label"] = option.Label, ["value"] = option.Value };
			if (option.Description is { Length: > 0 } description)
			{
				node["description"] = description;
			}

			return (JsonNode?)node;
		})]);

	private static IReadOnlyList<AskOption>? ParseOptions(JsonNode? node)
	{
		if (node is not JsonArray array || array.Count == 0)
		{
			return null;
		}

		var options = new List<AskOption>(array.Count);
		foreach (var raw in array)
		{
			if (raw is JsonValue value && value.GetValueKind() == JsonValueKind.String)
			{
				var label = value.GetValue<string>();
				options.Add(new AskOption(label, label, null));
				continue;
			}

			if (raw is JsonObject obj)
			{
				var label = obj["label"]?.GetValue<string>() ?? string.Empty;
				options.Add(new AskOption(
					label,
					obj["value"]?.GetValue<string>() ?? label,
					obj["description"]?.GetValue<string>()));
			}
		}

		return options.Count > 0 ? options : null;
	}

	private static bool IsAnswered(JsonNode? value)
	{
		if (value is null)
		{
			return false;
		}

		return value switch
		{
			JsonArray array => array.Count > 0,
			JsonValue json => json.GetValueKind() != JsonValueKind.Null,
			_ => true,
		};
	}

	private static string FormatValue(JsonNode? value) => value switch
	{
		null => string.Empty,
		JsonArray array => string.Join("、", array.Select(static item => item?.ToString() ?? string.Empty)),
		JsonValue json when json.GetValueKind() == JsonValueKind.True => "是",
		JsonValue json when json.GetValueKind() == JsonValueKind.False => "否",
		_ => value.ToString(),
	};
}
