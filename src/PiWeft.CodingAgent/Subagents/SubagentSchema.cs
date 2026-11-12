using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;

namespace PiWeft.CodingAgent.Subagents;

/// 子代理结构化输出的 JSON Schema 子集：只接受能完整校验的关键字，遇到不认识的直接拒绝，
/// 避免「schema 写了但实际没生效」。
internal static class SubagentSchema
{
	/// 定义与取值允许的最大嵌套深度。
	public const int MaxDepth = 16;

	/// 结构化结果进上下文的上限；超过视为失败，避免一段 JSON 把父会话撑爆。
	public const int MaxStructuredChars = 12_000;

	private static readonly FrozenSet<string> AllowedKeywords = FrozenSet.ToFrozenSet(
	[
		"type",
		"properties",
		"required",
		"items",
		"enum",
		"additionalProperties",
		"description",
		"title",
		"default",
		"examples",
		"$schema",
	], StringComparer.Ordinal);

	private static readonly FrozenSet<string> Types = FrozenSet.ToFrozenSet(
	[
		"object",
		"array",
		"string",
		"number",
		"integer",
		"boolean",
		"null",
	], StringComparer.Ordinal);

	/// 校验 schema 本身；返回 null 表示可用，否则是一句给模型的错误。
	public static string? ValidateDefinition(JsonObject schema)
	{
		var errors = new List<string>(4);
		ValidateDefinitionCore(schema, "$", 0, errors);
		if (schema["type"] is JsonValue rootType &&
			rootType.TryGetValue<string>(out var root) &&
			!string.Equals(root, "object", StringComparison.Ordinal))
		{
			errors.Add("$: root type must be object");
		}

		return errors.Count == 0 ? null : string.Join("; ", errors.Take(8));
	}

	/// 从最终回复里取 JSON 并按 schema 校验；成功返回值和 null，失败返回 null 和错误。
	public static (JsonNode? Value, string? Error) ParseResult(string text, JsonObject schema)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return (null, "the final reply is empty");
		}

		var value = Extract(text);
		if (value is null)
		{
			return (null, "no JSON value found in the final reply");
		}

		var size = value.ToJsonString(PiJson.NodeOptions).Length;
		if (size > MaxStructuredChars)
		{
			return (null, $"structured output is too large ({size} chars; limit {MaxStructuredChars})");
		}

		var error = ValidateValue(value, schema);
		return error is null ? (value, null) : (null, error);
	}

	/// 按 schema 校验一个 JSON 值；返回 null 表示通过。
	public static string? ValidateValue(JsonNode? value, JsonObject schema)
	{
		var errors = new List<string>(4);
		ValidateValueCore(value, schema, "$", errors, 0);
		return errors.Count == 0 ? null : string.Join("; ", errors.Take(8));
	}

	private static void ValidateDefinitionCore(JsonObject schema, string path, int depth, List<string> errors)
	{
		if (errors.Count >= 16)
		{
			return;
		}

		if (depth > MaxDepth)
		{
			errors.Add($"{path}: nesting is too deep");
			return;
		}

		if (schema.Count > 64)
		{
			errors.Add($"{path}: too many keys");
			return;
		}

		foreach (var (key, _) in schema)
		{
			if (!AllowedKeywords.Contains(key))
			{
				errors.Add($"{path}: unsupported keyword \"{key}\"");
			}
		}

		if (schema["type"] is not JsonValue typeValue ||
			!typeValue.TryGetValue<string>(out var type) ||
			!Types.Contains(type))
		{
			errors.Add($"{path}: type must be one of {string.Join(", ", Types)}");
			return;
		}

		if (schema["enum"] is { } enumNode)
		{
			if (enumNode is not JsonArray enums || enums.Count is 0 or > 64)
			{
				errors.Add($"{path}.enum: expected 1-64 values");
			}
			else
			{
				foreach (var item in enums)
				{
					if (item is JsonArray or JsonObject)
					{
						errors.Add($"{path}.enum: only primitive values are supported");
						break;
					}
				}
			}
		}

		switch (type)
		{
			case "object":
				if (schema["items"] is not null)
				{
					errors.Add($"{path}.items: only allowed for arrays");
				}

				JsonObject? properties = null;
				if (schema["properties"] is { } propertiesNode)
				{
					if (propertiesNode is not JsonObject objectProperties)
					{
						errors.Add($"{path}.properties: expected an object");
					}
					else
					{
						properties = objectProperties;
						if (objectProperties.Count > 64)
						{
							errors.Add($"{path}.properties: too many properties");
						}

						foreach (var (name, value) in objectProperties)
						{
							if (value is not JsonObject child)
							{
								errors.Add($"{path}.properties.{name}: expected a schema object");
								continue;
							}

							ValidateDefinitionCore(child, $"{path}.{name}", depth + 1, errors);
						}
					}
				}

				if (schema["required"] is { } requiredNode)
				{
					if (requiredNode is not JsonArray required)
					{
						errors.Add($"{path}.required: expected an array");
					}
					else
					{
						var seen = new HashSet<string>(StringComparer.Ordinal);
						foreach (var item in required)
						{
							if (item is not JsonValue nameValue ||
								!nameValue.TryGetValue<string>(out var name) ||
								name.Length == 0)
							{
								errors.Add($"{path}.required: expected property names");
								continue;
							}

							if (!seen.Add(name))
							{
								errors.Add($"{path}.required: duplicate \"{name}\"");
							}

							if (properties is not null && !properties.ContainsKey(name))
							{
								errors.Add($"{path}.required: \"{name}\" has no property schema");
							}
						}
					}
				}

				if (schema["additionalProperties"] is { } additional)
				{
					if (additional is JsonObject additionalSchema)
					{
						ValidateDefinitionCore(additionalSchema, $"{path}.additionalProperties", depth + 1, errors);
					}
					else if (additional is not JsonValue booleanValue ||
						!booleanValue.TryGetValue<bool>(out _))
					{
						errors.Add($"{path}.additionalProperties: expected a boolean or schema");
					}
				}

				break;

			case "array":
				if (schema["properties"] is not null || schema["required"] is not null)
				{
					errors.Add($"{path}: properties/required are only allowed for objects");
				}

				if (schema["items"] is { } itemsNode)
				{
					if (itemsNode is not JsonObject items)
					{
						errors.Add($"{path}.items: expected a schema object");
					}
					else
					{
						ValidateDefinitionCore(items, $"{path}[]", depth + 1, errors);
					}
				}

				break;

			default:
				if (schema["properties"] is not null || schema["required"] is not null ||
					schema["items"] is not null || schema["additionalProperties"] is not null)
				{
					errors.Add($"{path}: object/array keywords are only allowed for object/array types");
				}

				break;
		}
	}

	private static void ValidateValueCore(
		JsonNode? value,
		JsonObject schema,
		string path,
		List<string> errors,
		int depth)
	{
		if (errors.Count >= 16)
		{
			return;
		}

		if (depth > MaxDepth)
		{
			errors.Add($"{path}: nesting is too deep");
			return;
		}

		if (schema["enum"] is JsonArray allowed &&
			!allowed.Any(item => JsonNode.DeepEquals(item, value)))
		{
			errors.Add($"{path}: not one of the allowed values");
			return;
		}

		if (schema["type"] is not JsonValue typeValue ||
			!typeValue.TryGetValue<string>(out var type))
		{
			errors.Add($"{path}: schema has no type");
			return;
		}

		switch (type)
		{
			case "object":
			{
				if (value is not JsonObject obj)
				{
					errors.Add($"{path}: expected object");
					return;
				}

				if (schema["properties"] is JsonObject properties)
				{
					foreach (var (name, childSchema) in properties)
					{
						if (childSchema is JsonObject child && obj.TryGetPropertyValue(name, out var childValue))
						{
							ValidateValueCore(childValue, child, $"{path}.{name}", errors, depth + 1);
						}
					}
				}

				if (schema["required"] is JsonArray required)
				{
					foreach (var item in required)
					{
						if (item is JsonValue nameValue &&
							nameValue.TryGetValue<string>(out var name) &&
							!obj.ContainsKey(name))
						{
							errors.Add($"{path}.{name}: required property is missing");
						}
					}
				}

				if (schema["additionalProperties"] is JsonValue additionalValue &&
					additionalValue.TryGetValue<bool>(out var additionalAllowed) &&
					!additionalAllowed)
				{
					foreach (var (name, _) in obj)
					{
						if (schema["properties"] is not JsonObject known || !known.ContainsKey(name))
						{
							errors.Add($"{path}.{name}: additional property is not allowed");
						}
					}
				}
				else if (schema["additionalProperties"] is JsonObject additionalSchema)
				{
					foreach (var (name, childValue) in obj)
					{
						if (schema["properties"] is JsonObject known && known.ContainsKey(name))
						{
							continue;
						}

						ValidateValueCore(childValue, additionalSchema, $"{path}.{name}", errors, depth + 1);
					}
				}

				return;
			}

			case "array":
			{
				if (value is not JsonArray array)
				{
					errors.Add($"{path}: expected array");
					return;
				}

				if (schema["items"] is JsonObject itemSchema)
				{
					for (var index = 0; index < array.Count; index++)
					{
						ValidateValueCore(array[index], itemSchema, $"{path}[{index}]", errors, depth + 1);
					}
				}

				return;
			}

			case "string":
				if (!IsKind(value, JsonValueKind.String))
				{
					errors.Add($"{path}: expected string");
				}

				return;

			case "number":
				if (!IsKind(value, JsonValueKind.Number))
				{
					errors.Add($"{path}: expected number");
				}

				return;

			case "integer":
				if (value is not JsonValue numberValue ||
					numberValue.GetValueKind() != JsonValueKind.Number ||
					!numberValue.TryGetValue<long>(out _))
				{
					errors.Add($"{path}: expected integer");
				}

				return;

			case "boolean":
				if (!IsKind(value, JsonValueKind.True) && !IsKind(value, JsonValueKind.False))
				{
					errors.Add($"{path}: expected boolean");
				}

				return;

			case "null":
				if (value is not null && !IsKind(value, JsonValueKind.Null))
				{
					errors.Add($"{path}: expected null");
				}

				return;
		}
	}

	private static bool IsKind(JsonNode? value, JsonValueKind kind) =>
		value is not null && value.GetValueKind() == kind;

	private static JsonNode? Extract(string text)
	{
		var trimmed = text.Trim();
		if (TryParse(trimmed) is { } whole)
		{
			return whole;
		}

		// 最后一个围栏块优先：模型常把契约 JSON 放在末尾。
		var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
		if (end > 0)
		{
			var start = trimmed.LastIndexOf("```", end - 1, StringComparison.Ordinal);
			if (start >= 0)
			{
				var block = trimmed[(start + 3)..end].Trim();
				if (block.StartsWith("json", StringComparison.OrdinalIgnoreCase))
				{
					block = block[4..].Trim();
				}

				if (TryParse(block) is { } fenced)
				{
					return fenced;
				}
			}
		}

		foreach (var (open, close) in new[] { ('{', '}'), ('[', ']') })
		{
			var first = trimmed.IndexOf(open);
			var last = trimmed.LastIndexOf(close);
			if (first >= 0 && last > first && TryParse(trimmed[first..(last + 1)]) is { } embedded)
			{
				return embedded;
			}
		}

		return null;
	}

	private static JsonNode? TryParse(string text)
	{
		try
		{
			return JsonNode.Parse(text);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
