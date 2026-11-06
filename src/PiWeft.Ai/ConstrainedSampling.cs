using System.Text.Json.Nodes;

namespace PiWeft.Ai;

/// 严格 schema 变换遇到 provider 不接受的形状：这个工具不能走严格模式，
/// 不是请求失败。<c>strict: "prefer"</c> 的工具应回落到普通 schema。
public sealed class UnsupportedStrictJsonSchemaException(string message) : Exception(message);

/// provider 自己的严格模式白名单：返回 true 表示该 keyword/value 会被拒绝（整个请求 400）。
public delegate bool UnsupportedStrictSchemaKeyword(string key, JsonNode? value);

/// 工具声明里的 <c>constrainedSampling</c>：与 pi 的
/// <c>packages/ai/src/api/constrained-sampling.ts</c> 同义。
/// 工具只表达「希望被约束采样」，能不能给由 provider 与模型能力决定：
/// <list type="bullet">
/// <item><c>strict: "prefer"</c>：能严格就严格，schema 不被接受时静默回落；</item>
/// <item><c>strict: "require"</c>：不能严格就报错，不悄悄降低约束。</item>
/// </list>
/// 严格 schema 是 provider 侧的方言：可选属性写成 <c>anyOf: [schema, null]</c> 并全部列进
/// <c>required</c>、<c>additionalProperties: false</c>。重排后模型只能产出符合 schema 的参数，
/// 不必再指望提示词兜住解析失败。
public static class ConstrainedSampling
{
	public const string JsonSchemaType = "json_schema";

	public const string GrammarType = "grammar";

	/// 严格模式一律不接受的组合键（各 provider 相同的那部分）。
	private static readonly string[] UnsupportedStrictSchemaKeys =
	[
		"$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties", "dependentSchemas",
		"dependencies", "unevaluatedProperties", "propertyNames", "contains", "prefixItems", "not",
		"if", "then", "else",
	];

	/// Anthropic 严格工具额外拒绝这些 bounds 关键字（整个请求 400）。
	private static readonly HashSet<string> AnthropicUnsupportedKeywords = new(StringComparer.Ordinal)
	{
		"minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "maxItems",
		"uniqueItems", "minContains", "maxContains", "minProperties", "maxProperties",
	};

	private static readonly HashSet<string> AnthropicStrictStringFormats = new(StringComparer.Ordinal)
	{
		"date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid",
	};

	/// Claude 拒绝严格工具超限时的报错特征（20 个严格工具、16 个可选/联合类型参数等）。
	private static readonly string[] StrictToolLimitErrorMessages =
	[
		"The compiled grammar is too large",
		"Too many strict tools (",
		"Schemas contains too many parameters with union types (",
	];

	/// 工具声明的便捷构造：<paramref name="require"/> 为 true 时不允许回落。
	public static JsonObject JsonSchema(bool require = false) => new()
	{
		["type"] = JsonSchemaType,
		["strict"] = require ? "require" : "prefer",
	};

	/// 只要求「能严格就严格」的严格 JSON schema 工具（pi 的 prefersJsonSchemaStrictSampling）。
	public static bool PrefersStrictJsonSchema(Tool tool) =>
		tool.ConstrainedSampling is JsonObject config &&
		Str(config, "type") == JsonSchemaType &&
		Str(config, "strict") == "prefer";

	/// 这次请求能不能用严格 schema。<c>null</c> 表示按普通 schema 发送。
	/// <para>
	/// 注意：这里只判断「工具希望严格 + provider 支持 + schema 能被变换」，
	/// 具体怎么发（<c>strict: true</c> 字段、输入 schema 换成严格版）由 provider 自己决定。
	/// </para>
	public static bool? ResolveStrictJsonSchema(
		Tool tool,
		bool supportsStrictMode,
		UnsupportedStrictSchemaKeyword? unsupportedKeyword = null)
	{
		if (tool.ConstrainedSampling is not JsonObject config || Str(config, "type") != JsonSchemaType)
		{
			return null;
		}

		var require = Str(config, "strict") == "require";
		if (supportsStrictMode)
		{
			try
			{
				MakeStrictJsonSchema(tool.Parameters, unsupportedKeyword);
				return true;
			}
			catch (UnsupportedStrictJsonSchemaException exception)
			{
				if (!require) return null;
				throw new InvalidOperationException(
					$"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but {exception.Message}.");
			}
		}

		if (require)
		{
			throw new InvalidOperationException(
				$"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
		}

		return null;
	}

	/// 发请求用的参数 schema：严格时换成分量完整的严格版，否则原样发送。
	public static JsonObject GetJsonSchemaToolParameters(Tool tool, bool? strict) =>
		strict == true ? MakeStrictJsonSchema(tool.Parameters) : tool.Parameters;

	/// 把工具 schema 改写成 provider 严格模式接受的子集（pi 的 makeStrictJsonSchema）：
	/// 可选属性包成 <c>anyOf: [schema, null]</c>、全部属性进 <c>required</c>、
	/// 关闭 <c>additionalProperties</c>，并在遇到无法表达的形状时抛出
	/// <see cref="UnsupportedStrictJsonSchemaException"/>。
	public static JsonObject MakeStrictJsonSchema(
		JsonObject schema,
		UnsupportedStrictSchemaKeyword? unsupportedKeyword = null)
	{
		var cloned = (JsonObject)schema.DeepClone();
		MakeNodeStrict(cloned, unsupportedKeyword);
		if (Str(cloned, "type") != "object")
		{
			throw new UnsupportedStrictJsonSchemaException("root schema must have type object");
		}

		return cloned;
	}

	/// 严格工具额度/文法过大时的报错特征，用于把请求降级成普通 schema 重发一次。
	public static bool IsStrictToolLimitError(string? message) =>
		message is { Length: > 0 } text &&
		StrictToolLimitErrorMessages.Any(limit => text.Contains(limit, StringComparison.Ordinal));

	/// Anthropic 严格工具的额外限制：bounds 关键字与未知的字符串 format 都会被整请求拒绝。
	public static bool IsAnthropicStrictUnsupportedKeyword(string key, JsonNode? value) =>
		AnthropicUnsupportedKeywords.Contains(key) ||
		(key == "minItems" && !(TryIntegral(value, out var items) && (items == 0 || items == 1))) ||
		(key == "format" && !(value is JsonValue formatValue &&
			formatValue.TryGetValue<string>(out var format) && AnthropicStrictStringFormats.Contains(format)));

	/// 数值 node 的整型读数。schema 里的数字可能来自代码建的 <see cref="JsonValue"/>（int/long），
	/// 也可能来自 JSON 解析的 JsonElement，两种都要能读。
	private static bool TryIntegral(JsonNode? node, out long value)
	{
		value = 0;
		if (node is not JsonValue json)
		{
			return false;
		}

		if (json.TryGetValue<System.Text.Json.JsonElement>(out var element))
		{
			return element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetInt64(out value);
		}

		if (json.TryGetValue<long>(out value))
		{
			return true;
		}

		if (json.TryGetValue<int>(out var int32))
		{
			value = int32;
			return true;
		}

		if (json.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) < 9e15)
		{
			value = (long)real;
			return true;
		}

		return false;
	}

	private static void MakeNodeStrict(JsonNode? node, UnsupportedStrictSchemaKeyword? unsupportedKeyword)
	{
		if (node is not JsonObject schema)
		{
			throw new UnsupportedStrictJsonSchemaException("boolean schemas are unsupported");
		}

		foreach (var key in UnsupportedStrictSchemaKeys)
		{
			if (schema.ContainsKey(key))
			{
				throw new UnsupportedStrictJsonSchemaException($"{key} schemas are unsupported");
			}
		}

		if (unsupportedKeyword is not null)
		{
			foreach (var pair in schema)
			{
				if (unsupportedKeyword(pair.Key, pair.Value))
				{
					throw new UnsupportedStrictJsonSchemaException(
						$"{pair.Key}: {pair.Value?.ToJsonString() ?? "null"} is unsupported");
				}
			}
		}

		if (schema["anyOf"] is { } anyOf)
		{
			if (anyOf is not JsonArray variants || variants.Count == 0)
			{
				throw new UnsupportedStrictJsonSchemaException("anyOf must contain at least one schema");
			}

			foreach (var variant in variants)
			{
				if (IsStructuredSchema(variant))
				{
					throw new UnsupportedStrictJsonSchemaException("object and array unions are unsupported");
				}

				MakeNodeStrict(variant, unsupportedKeyword);
			}
		}

		if (schema["items"] is { } items)
		{
			if (items is JsonArray)
			{
				throw new UnsupportedStrictJsonSchemaException("tuple schemas are unsupported");
			}

			MakeNodeStrict(items, unsupportedKeyword);
		}

		var isObjectSchema = Str(schema, "type") == "object";
		if (schema.ContainsKey("properties") && !isObjectSchema)
		{
			throw new UnsupportedStrictJsonSchemaException("properties require type object");
		}

		if (!isObjectSchema)
		{
			return;
		}

		if (schema.TryGetPropertyValue("additionalProperties", out var additional) && additional is not null &&
			!(additional is JsonValue value && value.TryGetValue<bool>(out var allowed) && !allowed))
		{
			throw new UnsupportedStrictJsonSchemaException("schema-valued or true additionalProperties is unsupported");
		}

		if (schema.TryGetPropertyValue("properties", out var propertiesNode) && propertiesNode is not null &&
			propertiesNode is not JsonObject)
		{
			throw new UnsupportedStrictJsonSchemaException("object properties must be a schema map");
		}

		var required = new HashSet<string>(StringComparer.Ordinal);
		if (schema.TryGetPropertyValue("required", out var requiredNode) && requiredNode is not null)
		{
			if (requiredNode is not JsonArray requiredArray)
			{
				throw new UnsupportedStrictJsonSchemaException("object required must be a string array");
			}

			foreach (var name in requiredArray)
			{
				if (name is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var text))
				{
					throw new UnsupportedStrictJsonSchemaException("object required must be a string array");
				}

				required.Add(text);
			}
		}

		if (propertiesNode is not JsonObject properties)
		{
			// 没有 properties 的对象 schema：只需要拒绝额外键，不必造一个空表出来。
			if (required.Count > 0)
			{
				throw new UnsupportedStrictJsonSchemaException("required contains an unknown property");
			}

			schema["additionalProperties"] = false;
			return;
		}

		var names = new List<string>(properties.Count);
		foreach (var pair in properties)
		{
			names.Add(pair.Key);
		}

		foreach (var name in required)
		{
			if (!properties.ContainsKey(name))
			{
				throw new UnsupportedStrictJsonSchemaException("required contains an unknown property");
			}
		}

		foreach (var name in names)
		{
			var property = properties[name];
			MakeNodeStrict(property, unsupportedKeyword);
			if (!required.Contains(name) && !AllowsNull(property))
			{
				properties[name] = new JsonObject
				{
					["anyOf"] = new JsonArray(property?.DeepClone(), new JsonObject { ["type"] = "null" }),
				};
			}
		}

		var allRequired = new JsonArray();
		foreach (var name in names)
		{
			allRequired.Add((JsonNode?)JsonValue.Create(name));
		}

		schema["required"] = allRequired;
		schema["additionalProperties"] = false;
	}

	private static bool IsStructuredSchema(JsonNode? node)
	{
		if (node is not JsonObject schema)
		{
			return false;
		}

		var types = schema["type"] switch
		{
			JsonValue value when value.TryGetValue<string>(out var single) => new[] { single },
			JsonArray array => [.. array.Select(static item =>
				item is JsonValue entry && entry.TryGetValue<string>(out var text) ? text : string.Empty)],
			_ => [],
		};
		return types.Contains("object", StringComparer.Ordinal) ||
			types.Contains("array", StringComparer.Ordinal) ||
			schema.ContainsKey("properties") ||
			schema.ContainsKey("items");
	}

	private static bool AllowsNull(JsonNode? node)
	{
		if (node is not JsonObject schema)
		{
			return false;
		}

		if (Str(schema, "type") == "null")
		{
			return true;
		}

		if (schema["type"] is JsonArray types &&
			types.Any(static item => item is JsonValue value && value.TryGetValue<string>(out var text) && text == "null"))
		{
			return true;
		}

		if (schema["const"] is null && schema.ContainsKey("const"))
		{
			return true;
		}

		if (schema["enum"] is JsonArray choices && choices.Any(static item => item is null))
		{
			return true;
		}

		return schema["anyOf"] is JsonArray variants && variants.Any(AllowsNull);
	}

	private static string? Str(JsonObject node, string key) =>
		node.TryGetPropertyValue(key, out var value) && value is JsonValue jsonValue &&
		jsonValue.TryGetValue<string>(out var text)
			? text
			: null;
}
