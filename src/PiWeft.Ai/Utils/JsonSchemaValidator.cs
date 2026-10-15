using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Utils;

/// <summary>
/// 按工具的 JSON Schema 校验并强制转换模型写出的工具参数。对应 pi 的 <c>utils/validation.ts</c>：
/// 可选的 null 会被删除，基础类型会被转换，失败时给出模型看到的错误工具结果文本。
/// </summary>
public static class JsonSchemaValidator
{
	/// <summary>校验后的参数，或格式化好的校验错误。</summary>
	public sealed record Result(JsonObject? Value, string? Error);

	/// <summary>校验一次工具调用的参数，返回转换后的参数或格式化错误。</summary>
	public static Result ValidateToolArguments(Tool tool, JsonObject arguments)
	{
		ArgumentNullException.ThrowIfNull(tool);

		var clone = (JsonObject)arguments.DeepClone();
		NormalizeOptionalNulls(tool.Parameters, clone);

		var coerced = Coerce(tool.Parameters, clone) as JsonObject ?? clone;

		var errors = new List<string>();
		ValidateNode(tool.Parameters, coerced, string.Empty, errors);

		if (errors.Count == 0)
		{
			return new Result(coerced, null);
		}

		var message =
			$"Validation failed for tool \"{tool.Name}\":\n" +
			string.Join("\n", errors.Select(static error => $"  - {error}")) +
			"\n\nReceived arguments:\n" +
			arguments.ToJsonString(IndentedOptions);

		return new Result(null, message);
	}

	private static readonly JsonSerializerOptions IndentedOptions = new()
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	// ------------------------------------------------------------------ 类型转换

	/// <summary>删除值为 <c>null</c> 的可选属性 —— 模型常以此表示“未设置”。</summary>
	public static void NormalizeOptionalNulls(JsonObject schema, JsonNode? value)
	{
		switch (value)
		{
			case JsonArray array:
			{
				var itemSchema = schema["items"] as JsonObject;
				if (schema["items"] is JsonArray itemSchemas)
				{
					for (var i = 0; i < array.Count && i < itemSchemas.Count; i++)
					{
						if (itemSchemas[i] is JsonObject tupleSchema)
						{
							NormalizeOptionalNulls(tupleSchema, array[i]);
						}
					}
				}
				else if (itemSchema is not null)
				{
					foreach (var item in array)
					{
						NormalizeOptionalNulls(itemSchema, item);
					}
				}

				return;
			}

			case JsonObject obj:
			{
				if (schema["properties"] is not JsonObject properties)
				{
					return;
				}

				var required = RequiredSet(schema);
				foreach (var (key, propertySchema) in properties)
				{
					if (propertySchema is not JsonObject property || !obj.TryGetPropertyValue(key, out var propertyValue))
					{
						continue;
					}

					if (propertyValue is null && !required.Contains(key))
					{
						obj.Remove(key);
						continue;
					}

					NormalizeOptionalNulls(property, propertyValue);
				}

				return;
			}
		}
	}

	/// <summary>把基础类型转换成声明的类型，并递归处理对象、数组与联合类型。</summary>
	public static JsonNode? Coerce(JsonObject schema, JsonNode? value)
	{
		var current = value;

		if (schema["allOf"] is JsonArray allOf)
		{
			foreach (var nested in allOf.OfType<JsonObject>())
			{
				current = Coerce(nested, current);
			}
		}

		foreach (var keyword in UnionKeywords)
		{
			if (schema[keyword] is JsonArray union)
			{
				current = CoerceUnion(union.OfType<JsonObject>().ToList(), current);
			}
		}

		var types = SchemaTypes(schema);
		var matchesUnionMember = types.Count > 1 && types.Any(type => MatchesType(current, type));
		if (types.Count > 0 && !matchesUnionMember)
		{
			foreach (var type in types)
			{
				var candidate = CoercePrimitive(current, type);
				if (!ReferenceEquals(candidate, current))
				{
					current = candidate;
					break;
				}
			}
		}

		if (types.Contains("object") && current is JsonObject obj && schema["properties"] is JsonObject properties)
		{
			foreach (var (key, propertySchema) in properties.ToList())
			{
				if (propertySchema is not JsonObject property || !obj.TryGetPropertyValue(key, out var propertyValue))
				{
					continue;
				}

				var coercedProperty = Coerce(property, propertyValue);
				if (!ReferenceEquals(coercedProperty, propertyValue))
				{
					obj[key] = coercedProperty;
				}
			}

			if (schema["additionalProperties"] is JsonObject additional)
			{
				var defined = properties.Select(static pair => pair.Key).ToHashSet(StringComparer.Ordinal);
				foreach (var (key, propertyValue) in obj.ToList())
				{
					if (defined.Contains(key))
					{
						continue;
					}

					var coercedProperty = Coerce(additional, propertyValue);
					if (!ReferenceEquals(coercedProperty, propertyValue))
					{
						obj[key] = coercedProperty;
					}
				}
			}
		}

		if (types.Contains("array") && current is JsonArray array)
		{
			if (schema["items"] is JsonArray tuple)
			{
				for (var i = 0; i < array.Count && i < tuple.Count; i++)
				{
					if (tuple[i] is not JsonObject tupleSchema)
					{
						continue;
					}

					var coercedItem = Coerce(tupleSchema, array[i]);
					if (!ReferenceEquals(coercedItem, array[i]))
					{
						array[i] = coercedItem;
					}
				}
			}
			else if (schema["items"] is JsonObject itemSchema)
			{
				for (var i = 0; i < array.Count; i++)
				{
					var coercedItem = Coerce(itemSchema, array[i]);
					if (!ReferenceEquals(coercedItem, array[i]))
					{
						array[i] = coercedItem;
					}
				}
			}
		}

		return current;
	}

	private static JsonNode? CoerceUnion(IReadOnlyList<JsonObject> branches, JsonNode? value)
	{
		if (branches.Count == 0)
		{
			return value;
		}

		// 已在某处合法：保持原样。
		foreach (var branch in branches)
		{
			if (IsValid(branch, value))
			{
				return value;
			}
		}

		// 否则用第一个转换后能通过校验的分支。
		foreach (var branch in branches)
		{
			var candidate = Coerce(branch, value?.DeepClone());
			if (IsValid(branch, candidate))
			{
				return candidate;
			}
		}

		return value;
	}

	private static JsonNode? CoercePrimitive(JsonNode? value, string type)
	{
		var kind = value?.GetValueKind();

		switch (type)
		{
			case "number":
			case "integer":
			{
				if (kind is null)
				{
					return JsonValue.Create(0);
				}

				if (kind == JsonValueKind.String && value!.GetValue<string>().Trim().Length > 0 &&
					double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
					(type == "number" || Math.Floor(parsed) == parsed))
				{
					// 分开返回：写成单个条件表达式会把 long 提升为 double。
					if (type == "integer")
					{
						return JsonValue.Create((long)parsed);
					}

					return JsonValue.Create(parsed);
				}

				if (kind is JsonValueKind.True or JsonValueKind.False)
				{
					return JsonValue.Create(value!.GetValue<bool>() ? 1 : 0);
				}

				return value;
			}

			case "boolean":
			{
				if (kind is null)
				{
					return JsonValue.Create(false);
				}

				if (kind == JsonValueKind.String)
				{
					return value!.GetValue<string>() switch
					{
						"true" => JsonValue.Create(true),
						"false" => JsonValue.Create(false),
						_ => value,
					};
				}

				if (kind == JsonValueKind.Number && TryNumber(value, out var number))
				{
					return number switch
					{
						1 => JsonValue.Create(true),
						0 => JsonValue.Create(false),
						_ => value,
					};
				}

				return value;
			}

			case "string":
			{
				if (kind is null)
				{
					return JsonValue.Create(string.Empty);
				}

				return kind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
					? JsonValue.Create(value!.ToJsonString(PiJson.NodeOptions).Trim('"'))
					: value;
			}

			case "null":
				return kind switch
				{
					JsonValueKind.String when value!.GetValue<string>().Length == 0 => null,
					JsonValueKind.Number when value!.GetValue<double>() == 0 => null,
					JsonValueKind.False => null,
					_ => value,
				};

			default:
				return value;
		}
	}

	// ---------------------------------------------------------------- 校验

	/// <summary>值是否满足该 schema。</summary>
	public static bool IsValid(JsonObject schema, JsonNode? value)
	{
		var errors = new List<string>();
		ValidateNode(schema, value, string.Empty, errors);
		return errors.Count == 0;
	}

	private static void ValidateNode(JsonObject schema, JsonNode? value, string path, List<string> errors)
	{
		var displayPath = path.Length == 0 ? "root" : path;

		if (schema["allOf"] is JsonArray allOf)
		{
			foreach (var nested in allOf.OfType<JsonObject>())
			{
				ValidateNode(nested, value, path, errors);
			}
		}

		if (schema["anyOf"] is JsonArray anyOf &&
			!anyOf.OfType<JsonObject>().Any(branch => IsValid(branch, value)))
		{
			errors.Add($"{displayPath}: Expected value to match at least one of the allowed schemas");
			return;
		}

		if (schema["oneOf"] is JsonArray oneOf &&
			oneOf.OfType<JsonObject>().Count(branch => IsValid(branch, value)) != 1)
		{
			errors.Add($"{displayPath}: Expected value to match exactly one of the allowed schemas");
			return;
		}

		var types = SchemaTypes(schema);
		if (types.Count > 0 && !types.Any(type => MatchesType(value, type)))
		{
			errors.Add($"{displayPath}: Expected {string.Join(" or ", types)}");
			return;
		}

		if (schema["enum"] is JsonArray enumValues && !enumValues.Any(candidate => JsonNode.DeepEquals(candidate, value)))
		{
			errors.Add($"{displayPath}: Expected one of {enumValues.ToJsonString(PiJson.NodeOptions)}");
		}

		if (schema["const"] is { } constValue && !JsonNode.DeepEquals(constValue, value))
		{
			errors.Add($"{displayPath}: Expected {constValue.ToJsonString(PiJson.NodeOptions)}");
		}

		ValidateNumberConstraints(schema, value, displayPath, errors);
		ValidateStringConstraints(schema, value, displayPath, errors);
		ValidateArrayConstraints(schema, value, path, displayPath, errors);
		ValidateObjectConstraints(schema, value, path, displayPath, errors);
	}

	private static void ValidateNumberConstraints(JsonObject schema, JsonNode? value, string path, List<string> errors)
	{
		if (value is null || value.GetValueKind() != JsonValueKind.Number)
		{
			return;
		}

		if (!TryNumber(value, out var number))
		{
			return;
		}

		if (AsDouble(schema["minimum"]) is { } minimum && number < minimum)
		{
			errors.Add($"{path}: Expected number greater than or equal to {Format(minimum)}");
		}

		if (AsDouble(schema["maximum"]) is { } maximum && number > maximum)
		{
			errors.Add($"{path}: Expected number less than or equal to {Format(maximum)}");
		}

		if (AsDouble(schema["exclusiveMinimum"]) is { } exclusiveMinimum && number <= exclusiveMinimum)
		{
			errors.Add($"{path}: Expected number greater than {Format(exclusiveMinimum)}");
		}

		if (AsDouble(schema["exclusiveMaximum"]) is { } exclusiveMaximum && number >= exclusiveMaximum)
		{
			errors.Add($"{path}: Expected number less than {Format(exclusiveMaximum)}");
		}

		if (AsDouble(schema["multipleOf"]) is { } multipleOf && multipleOf > 0)
		{
			var quotient = number / multipleOf;
			if (Math.Abs(quotient - Math.Round(quotient)) > 1e-9)
			{
				errors.Add($"{path}: Expected number to be a multiple of {Format(multipleOf)}");
			}
		}
	}

	private static void ValidateStringConstraints(JsonObject schema, JsonNode? value, string path, List<string> errors)
	{
		if (value is null || value.GetValueKind() != JsonValueKind.String)
		{
			return;
		}

		var text = value.GetValue<string>();
		if (AsDouble(schema["minLength"]) is { } minLength && text.Length < minLength)
		{
			errors.Add($"{path}: Expected string length greater than or equal to {(int)minLength}");
		}

		if (AsDouble(schema["maxLength"]) is { } maxLength && text.Length > maxLength)
		{
			errors.Add($"{path}: Expected string length less than or equal to {(int)maxLength}");
		}

		if (schema["pattern"] is JsonValue patternNode &&
			patternNode.TryGetValue<string>(out var pattern) &&
			!SafeRegexMatch(pattern, text))
		{
			errors.Add($"{path}: Expected string to match '{pattern}'");
		}
	}

	private static void ValidateArrayConstraints(
		JsonObject schema,
		JsonNode? value,
		string path,
		string displayPath,
		List<string> errors)
	{
		if (value is not JsonArray array)
		{
			return;
		}

		if (AsDouble(schema["minItems"]) is { } minItems && array.Count < minItems)
		{
			errors.Add($"{displayPath}: Expected at least {(int)minItems} item(s)");
		}

		if (AsDouble(schema["maxItems"]) is { } maxItems && array.Count > maxItems)
		{
			errors.Add($"{displayPath}: Expected at most {(int)maxItems} item(s)");
		}

		if (schema["uniqueItems"]?.GetValueKind() == JsonValueKind.True)
		{
			for (var i = 0; i < array.Count; i++)
			{
				for (var j = i + 1; j < array.Count; j++)
				{
					if (JsonNode.DeepEquals(array[i], array[j]))
					{
						errors.Add($"{displayPath}: Expected items to be unique");
						i = array.Count;
						break;
					}
				}
			}
		}

		if (schema["items"] is JsonArray tuple)
		{
			for (var i = 0; i < array.Count && i < tuple.Count; i++)
			{
				if (tuple[i] is JsonObject tupleSchema)
				{
					ValidateNode(tupleSchema, array[i], $"{path}[{i}]", errors);
				}
			}
		}
		else if (schema["items"] is JsonObject itemSchema)
		{
			for (var i = 0; i < array.Count; i++)
			{
				ValidateNode(itemSchema, array[i], $"{path}[{i}]", errors);
			}
		}
	}

	private static void ValidateObjectConstraints(
		JsonObject schema,
		JsonNode? value,
		string path,
		string displayPath,
		List<string> errors)
	{
		if (value is not JsonObject obj)
		{
			return;
		}

		var properties = schema["properties"] as JsonObject;

		foreach (var required in RequiredSet(schema))
		{
			if (!obj.ContainsKey(required))
			{
				var requiredPath = path.Length == 0 ? required : $"{path}.{required}";
				errors.Add($"{requiredPath}: Expected required property");
			}
		}

		if (AsDouble(schema["minProperties"]) is { } minProperties && obj.Count < minProperties)
		{
			errors.Add($"{displayPath}: Expected at least {(int)minProperties} propert(y|ies)");
		}

		if (AsDouble(schema["maxProperties"]) is { } maxProperties && obj.Count > maxProperties)
		{
			errors.Add($"{displayPath}: Expected at most {(int)maxProperties} propert(y|ies)");
		}

		foreach (var (key, propertyValue) in obj)
		{
			var childPath = path.Length == 0 ? key : $"{path}.{key}";

			if (properties?[key] is JsonObject propertySchema)
			{
				ValidateNode(propertySchema, propertyValue, childPath, errors);
				continue;
			}

			switch (schema["additionalProperties"])
			{
				case JsonObject additional when !IsValid(additional, propertyValue):
					errors.Add($"{childPath}: Unexpected value for an unknown property");
					break;
				case JsonValue { } flag when flag.GetValueKind() == JsonValueKind.False:
					errors.Add($"{childPath}: Unexpected property");
					break;
			}
		}
	}

	// -------------------------------------------------------------------- 辅助

	private static readonly string[] UnionKeywords = ["anyOf", "oneOf"];

	private static HashSet<string> RequiredSet(JsonObject schema) =>
		schema["required"] is JsonArray required
			? [.. required.Select(static node => node?.GetValue<string>() ?? string.Empty)]
			: new HashSet<string>(StringComparer.Ordinal);

	private static List<string> SchemaTypes(JsonObject schema)
	{
		var types = new List<string>();
		switch (schema["type"])
		{
			case JsonValue value when value.GetValueKind() == JsonValueKind.String:
				types.Add(value.GetValue<string>());
				break;
			case JsonArray array:
				types.AddRange(array
					.Where(static node => node?.GetValueKind() == JsonValueKind.String)
					.Select(static node => node!.GetValue<string>()));
				break;
		}

		return types;
	}

	private static bool MatchesType(JsonNode? value, string type)
	{
		var kind = value?.GetValueKind();
		return type switch
		{
			"number" => kind == JsonValueKind.Number,
			"integer" => TryNumber(value, out var number) && Math.Floor(number) == number,
			"boolean" => kind is JsonValueKind.True or JsonValueKind.False,
			"string" => kind == JsonValueKind.String,
			"null" => kind is null,
			"array" => kind == JsonValueKind.Array,
			"object" => kind == JsonValueKind.Object,
			_ => false,
		};
	}

	private static double? AsDouble(JsonNode? node) => TryNumber(node, out var value) ? value : null;

	/// <summary>不论底层 CLR 类型是哪种都读出数值：解析得到的 JSON，或由 int/long/decimal 构建的节点。</summary>
	private static bool TryNumber(JsonNode? node, out double value)
	{
		value = 0;
		if (node is null || node.GetValueKind() != JsonValueKind.Number)
		{
			return false;
		}

		var jsonValue = node.AsValue();
		if (jsonValue.TryGetValue<double>(out value))
		{
			return true;
		}

		if (jsonValue.TryGetValue<long>(out var asLong))
		{
			value = asLong;
			return true;
		}

		if (jsonValue.TryGetValue<int>(out var asInt))
		{
			value = asInt;
			return true;
		}

		if (jsonValue.TryGetValue<decimal>(out var asDecimal))
		{
			value = (double)asDecimal;
			return true;
		}

		return false;
	}

	private static string Format(double value) =>
		value == Math.Floor(value) && Math.Abs(value) < 1e15
			? ((long)value).ToString(CultureInfo.InvariantCulture)
			: value.ToString("0.######", CultureInfo.InvariantCulture);

	private static bool SafeRegexMatch(string pattern, string text)
	{
		try
		{
			return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
		}
		catch (ArgumentException)
		{
			return true; // schema 里的非法正则不能让整次调用失败
		}
	}
}
