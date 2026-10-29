using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.CodingAgent;

/// <summary>选择题的一个选项。</summary>
public sealed record AskOption(string Label, string Value, string? Description);

/// <summary>归一化后的一个问题。</summary>
public sealed record AskQuestion(
	string Id,
	string Type,
	string Question,
	IReadOnlyList<AskOption>? Options,
	string? Placeholder,
	string? Prefill);

/// <summary>用户对一个问题的回答；<c>Value</c> 可能是字符串、布尔或字符串数组。</summary>
public sealed record AskAnswer(string Id, string Type, JsonNode? Value, string? Label, bool WasCustom);

/// <summary>
/// <c>ask_question</c> 的线协议编解码：与 PiDeck 的 <c>pi-deck-ask-question.ts</c> 完全一致。
/// <para>
/// 无论单个还是批量问题，都通过一次 <c>ui.input</c> 提交：<c>title</c> 是 JSON 信封
/// （键 <c>__piDeckBatchAsk</c>），<c>placeholder</c> 是固定哨兵值 <c>__piDeckBatchAsk__</c>。
/// PiDeck 的渲染层据此展开为问询卡片，回答以同构 JSON 字符串返回。
/// </para>
/// </summary>
public static class AskQuestions
{
	/// <summary>批量信封的键，PiDeck 用它识别问询卡片。</summary>
	public const string EnvelopeKey = "__piDeckBatchAsk";

	/// <summary>PiDeck 用来识别批量问询的占位符哨兵。</summary>
	public const string EnvelopePlaceholder = "__piDeckBatchAsk__";

	/// <summary>允许的问题类型。</summary>
	public static readonly string[] Types = ["select", "multi_select", "confirm", "input", "editor"];

	/// <summary>把工具参数归一化成问题列表；缺少 <c>questions</c> 时回退到顶层单问题字段。</summary>
	public static IReadOnlyList<AskQuestion> FromToolArguments(JsonObject arguments)
	{
		var raw = arguments["questions"] as JsonArray;
		var questions = new List<AskQuestion>();
		if (raw is { Count: > 0 })
		{
			for (var index = 0; index < raw.Count; index++)
			{
				if (raw[index] is JsonObject item)
				{
					questions.Add(Normalize(item, index));
				}
			}

			return questions;
		}

		// 兼容旧模型直接传顶层字段。
		if (arguments["question"] is not null)
		{
			questions.Add(Normalize(arguments, 0));
		}

		return questions;
	}

	/// <summary>构造提交给宿主 UI 的信封 JSON。</summary>
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
				item["options"] = new JsonArray([.. options.Select(static option =>
				{
					var node = new JsonObject { ["label"] = option.Label, ["value"] = option.Value };
					if (option.Description is { Length: > 0 } description)
					{
						node["description"] = description;
					}

					return (JsonNode?)node;
				})]);
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
		}.ToJsonString();
	}

	/// <summary>宿主端解析信封；不是批量问询时返回 <c>null</c>。</summary>
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

	/// <summary>信封里的「批量审阅」标志：true 时界面在最后一题后加一页「确认全部答案」。</summary>
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

	/// <summary>解析宿主返回的回答。</summary>
	public static (IReadOnlyList<AskAnswer> Answers, bool Cancelled) ParseResponse(string? raw, IReadOnlyList<AskQuestion> questions)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return ([], true);
		}

		JsonObject? obj;
		try
		{
			obj = JsonNode.Parse(raw) as JsonObject;
		}
		catch (JsonException)
		{
			return ([], true);
		}

		if (obj is null)
		{
			return ([], true);
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

				var value = item["value"];
				answers.Add(new AskAnswer(
					item["id"]?.GetValue<string>() ?? string.Empty,
					item["type"]?.GetValue<string>() ?? "input",
					value?.DeepClone(),
					item["label"]?.GetValue<string>(),
					item["wasCustom"]?.GetValue<bool>() == true));
			}
		}

		var cancelled = obj["cancelled"]?.GetValue<bool>() == true;
		var complete = !cancelled && answers.Count >= questions.Count && answers.All(static answer => IsAnswered(answer.Value));
		return (answers, !complete);
	}

	/// <summary>构造宿主返回值。</summary>
	public static string EncodeResponse(IReadOnlyList<AskAnswer> answers, bool cancelled)
	{
		var array = new List<JsonNode?>(answers.Count);
		foreach (var answer in answers)
		{
			var item = new JsonObject
			{
				["id"] = answer.Id,
				["type"] = answer.Type,
			};

			if (answer.Value is not null)
			{
				item["value"] = answer.Value.DeepClone();
			}

			if (answer.Label is { Length: > 0 } label)
			{
				item["label"] = label;
			}

			if (answer.WasCustom)
			{
				item["wasCustom"] = true;
			}

			array.Add(item);
		}

		return new JsonObject { ["cancelled"] = cancelled, ["answers"] = new JsonArray([.. array]) }.ToJsonString();
	}

	/// <summary>模型可见的回答文本，与 PiDeck 的 <c>batchResult</c> 一致。</summary>
	public static string ResultText(IReadOnlyList<AskAnswer> answers, bool cancelled)
	{
		if (cancelled && answers.Count == 0)
		{
			return "用户取消了提问";
		}

		if (answers.Count == 0)
		{
			return "无答案";
		}

		return string.Join("\n", answers.Select(static answer =>
			$"{answer.Id}: {(answer.WasCustom ? "(自行输入) " : string.Empty)}{FormatValue(answer.Value)}"));
	}

	/// <summary>写入工具结果的 <c>details</c>，供界面渲染。</summary>
	public static JsonObject ResultDetails(IReadOnlyList<AskQuestion> questions, IReadOnlyList<AskAnswer> answers, bool cancelled) => new()
	{
		["questions"] = new JsonArray([.. questions.Select(static question => (JsonNode?)new JsonObject
		{
			["id"] = question.Id,
			["type"] = question.Type,
			["question"] = question.Question,
		})]),
		["answers"] = new JsonArray([.. answers.Select(static answer => (JsonNode?)new JsonObject
		{
			["id"] = answer.Id,
			["type"] = answer.Type,
			["value"] = answer.Value?.DeepClone(),
			["wasCustom"] = answer.WasCustom,
		})]),
		["cancelled"] = cancelled,
	};

	private static IReadOnlyList<AskQuestion> ParseQuestionArray(JsonArray? array)
	{
		var questions = new List<AskQuestion>();
		if (array is null)
		{
			return questions;
		}

		for (var index = 0; index < array.Count; index++)
		{
			if (array[index] is JsonObject item)
			{
				questions.Add(Normalize(item, index));
			}
		}

		return questions;
	}

	private static AskQuestion Normalize(JsonObject item, int index)
	{
		var options = ParseOptions(item["options"]);
		var type = item["type"]?.GetValue<string>();
		if (type is null || !Types.Contains(type, StringComparer.Ordinal))
		{
			// 按形状兜底：带选项推断为选择题，否则是文本输入。
			type = options is { Count: > 0 } ? "select" : "input";
		}

		return new AskQuestion(
			item["id"]?.GetValue<string>() is { Length: > 0 } id ? id : $"q{index + 1}",
			type,
			item["question"]?.GetValue<string>() ?? string.Empty,
			options,
			item["placeholder"]?.GetValue<string>(),
			item["prefill"]?.GetValue<string>());
	}

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
			JsonValue json => json.GetValueKind() switch
			{
				JsonValueKind.Null => false,
				JsonValueKind.String => json.GetValue<string>().Length > 0,
				_ => true,
			},
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
