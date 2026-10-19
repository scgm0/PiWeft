using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.JavaScript;

/// <summary>
/// 把工具与 JSON Schema 渲染成 TypeScript 声明，供模型阅读脚本 API。
/// 移植自 pi 的 <c>declarations.ts</c>：对象按属性名排序，属性说明变成 <c>//</c> 注释，
/// 类型无法表达的约束变成 <c>@integer @minimum 1</c> 之类的标签。
/// </summary>
public static class CodeModeDeclarations
{
	/// <summary>渲染后的参数类型上限，超过时先丢标签、再退化成 <c>unknown</c>。</summary>
	public const int DefaultInputSchemaMaxChars = 16_000;

	/// <summary>每个 schema 最多展开多少次本地 <c>$ref</c>，避免共享定义撑爆输出。</summary>
	private const int MaxRefExpansions = 32;

	private const string Indent = "  ";

	private static readonly string[] TagKeywords =
	[
		"minimum", "exclusiveMinimum", "maximum", "exclusiveMaximum", "multipleOf",
		"minLength", "maxLength", "pattern", "format", "minItems", "maxItems",
	];

	private static readonly HashSet<string> LowerBounds = new(StringComparer.Ordinal)
	{
		"minimum", "exclusiveMinimum", "minLength", "minItems",
	};

	private static readonly HashSet<string> UpperBounds = new(StringComparer.Ordinal)
	{
		"maximum", "exclusiveMaximum", "maxLength", "maxItems",
	};

	/// <summary>最长属性标签；更长（大 default、长 pattern）直接省略。</summary>
	private const int MaxTagChars = 80;

	/// <summary>渲染 <c>declare const tools: { ... };</c>；没有工具时返回空串。</summary>
	public static string Render(IReadOnlyList<CodeModeTool> tools)
	{
		if (tools.Count == 0) return string.Empty;
		var members = tools.Select(tool =>
			$"{DocComment(tool.Description, Indent)}{Indent}{RenderSignature(tool)}");
		return $"declare const tools: {{\n{string.Join("\n", members)}\n}};";
	}

	/// <summary><c>name(args: T): Promise&lt;R&gt;;</c>，名称使用脚本侧的标识符。</summary>
	public static string RenderSignature(CodeModeTool tool, int inputMaxChars = DefaultInputSchemaMaxChars)
	{
		var input = tool.InputSchema is null
			? "unknown"
			: SchemaToType(tool.InputSchema, inputMaxChars);
		return $"{CodeModeIdentifier.ToIdentifier(tool.Name)}(args: {input}): Promise<{RenderOutputType(tool.OutputSchema)}>;";
	}

	/// <summary>工具调用解析出的类型；没有输出 schema 时是 <c>unknown</c>。</summary>
	public static string RenderOutputType(JsonNode? schema) =>
		schema is null ? "unknown" : SchemaToType(schema);

	/// <summary>工具的完整样例：说明 + 声明代码块，用于工具清单与 <c>ALL_TOOLS</c>。</summary>
	public static string RenderSample(CodeModeTool tool)
	{
		var declaration = $"declare const tools: {{ {RenderSignature(tool)} }};";
		return $"{tool.Description?.Trim() ?? string.Empty}\n\ncodemode tool declaration:\n```ts\n{declaration}\n```";
	}

	/// <summary>
	/// JSON Schema → TypeScript 类型表达式。结果超过 <paramref name="maxChars"/> 时先去掉约束标签；
	/// 仍然超长则退化为 <c>unknown</c>。
	/// </summary>
	public static string SchemaToType(JsonNode? schema, int maxChars = DefaultInputSchemaMaxChars)
	{
		var tagged = ToType(schema, new SchemaContext(schema, tags: true));
		if (tagged.Length <= maxChars) return tagged;
		var untagged = ToType(schema, new SchemaContext(schema, tags: false));
		return untagged.Length <= maxChars ? untagged : "unknown";
	}

	/// <summary>可变的渲染上下文：<c>Resolving</c>/<c>Expansions</c> 沿递归进出，避免每层拷贝集合。</summary>
	private sealed class SchemaContext(JsonNode? root, bool tags)
	{
		public JsonNode? Root { get; } = root;

		public HashSet<string> Resolving { get; } = new(StringComparer.Ordinal);

		public int Expansions { get; set; }

		public bool Tags { get; } = tags;
	}

	private static string ToType(JsonNode? schema, SchemaContext context)
	{
		if (schema is null) return "unknown";
		if (schema is JsonValue && (IsBoolean(schema, true) || IsBoolean(schema, false)))
		{
			return IsBoolean(schema, true) ? "unknown" : "never";
		}

		if (schema is not JsonObject obj) return "unknown";

		if (Str(obj["$ref"]) is { } reference)
		{
			if (context.Resolving.Contains(reference) || context.Expansions >= MaxRefExpansions) return "unknown";
			var target = ResolveRef(reference, context.Root);
			if (target is null) return "unknown";
			// 沿当前路径进出（而不是每层拷贝集合），深度优先的兄弟分支仍可再次展开。
			if (!context.Resolving.Add(reference)) return "unknown";
			context.Expansions++;
			try
			{
				return ToType(target, context);
			}
			finally
			{
				context.Expansions--;
				context.Resolving.Remove(reference);
			}
		}

		if (obj.ContainsKey("const")) return Json(obj["const"]) ?? "unknown";

		if (obj["enum"] is JsonArray values)
		{
			return Union(values.Select(value => Json(value) ?? "unknown"));
		}

		var variants = obj["anyOf"] as JsonArray ?? obj["oneOf"] as JsonArray;
		if (variants is not null) return Union(variants.Select(variant => ToType(variant, context)));

		if (obj["allOf"] is JsonArray parts)
		{
			var rendered = parts.Select(part => ToType(part, context)).Where(part => part != "unknown").ToList();
			return rendered.Count == 0
				? "unknown"
				: string.Join(" & ", rendered.Select(part => part.Contains(" | ", StringComparison.Ordinal) ? $"({part})" : part));
		}

		var type = obj["type"];
		if (type is JsonArray types)
		{
			// 逐个 union 成员重渲染时只做浅拷贝（共享子节点），避免对整个 schema 反复深拷贝。
			return Union(types.Where(static entry => entry is not null).Select(entry =>
			{
				var variant = new JsonObject();
				foreach (var (key, value) in obj) variant[key] = value;
				variant["type"] = entry!.DeepClone();
				return ToType(variant, context);
			}));
		}

		return Str(type) switch
		{
			"string" => "string",
			"number" or "integer" => "number",
			"boolean" => "boolean",
			"null" => "null",
			"array" => ArrayType(obj, context),
			"object" => ObjectType(obj, context),
			null when obj.ContainsKey("properties") || obj.ContainsKey("additionalProperties") || obj.ContainsKey("required") =>
				ObjectType(obj, context),
			null when obj.ContainsKey("items") || obj.ContainsKey("prefixItems") => ArrayType(obj, context),
			_ => "unknown",
		};
	}

	private static string ArrayType(JsonObject schema, SchemaContext context)
	{
		if (schema["items"] is JsonObject items) return $"Array<{ToType(items, context)}>";

		var tuple = schema["prefixItems"] as JsonArray ?? schema["items"] as JsonArray;
		if (tuple is { Count: > 0 })
		{
			return $"[{string.Join(", ", tuple.Select(item => ToType(item, context)))}]";
		}

		return "unknown[]";
	}

	private static string ObjectType(JsonObject schema, SchemaContext context)
	{
		var properties = schema["properties"] as JsonObject ?? new JsonObject();
		var required = schema["required"] is JsonArray requiredArray
			? new HashSet<string>(requiredArray.Select(Str).OfType<string>(), StringComparer.Ordinal)
			: new HashSet<string>(StringComparer.Ordinal);
		var names = properties.Select(pair => pair.Key).OrderBy(static name => name, StringComparer.Ordinal).ToList();
		var members = names.Select(name =>
			$"{PropertyKey(name)}{(required.Contains(name) ? string.Empty : "?")}: {ToType(properties[name], context)};").ToList();

		var additional = schema["additionalProperties"];
		if (additional is not null && !IsBoolean(additional, false))
		{
			var additionalType = IsBoolean(additional, true) ? "unknown" : ToType(additional, context);
			members.Add($"[key: string]: {additionalType};");
		}
		else if (additional is null && names.Count == 0)
		{
			members.Add("[key: string]: unknown;");
		}

		if (members.Count == 0) return "{}";

		var comments = names.Select(name =>
		{
			var lines = DescriptionOf(properties[name])
				.Split(['\r', '\n', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries)
				.Select(static line => line.Trim())
				.Where(static line => line.Length > 0)
				.ToList();
			var tags = context.Tags ? ConstraintTags(properties[name]) : string.Empty;
			if (tags.Length > 0) lines.Add(tags);
			return lines;
		}).ToList();

		if (comments.All(static comment => comment.Count == 0)) return $"{{ {string.Join(" ", members)} }}";

		var lines = new List<string> { "{" };
		for (var index = 0; index < comments.Count; index++)
		{
			foreach (var line in comments[index]) lines.Add($"{Indent}// {line}");
			lines.Add($"{Indent}{members[index].Replace("\n", $"\n{Indent}", StringComparison.Ordinal)}");
		}

		foreach (var member in members.Skip(names.Count)) lines.Add($"{Indent}{member}");
		lines.Add("}");
		return string.Join("\n", lines);
	}

	private static string PropertyKey(string name) =>
		IsIdentifier(name) ? name : JsonValue.Create(name)?.ToJsonString() ?? "\"\"";

	private static bool IsIdentifier(string name)
	{
		if (name.Length == 0) return false;
		if (!(char.IsAsciiLetter(name[0]) || name[0] is '_' or '$')) return false;
		return name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');
	}

	private static string Union(IEnumerable<string> types)
	{
		var unique = new List<string>();
		foreach (var type in types)
		{
			if (type == "unknown") return "unknown";
			if (!unique.Contains(type, StringComparer.Ordinal)) unique.Add(type);
		}

		return unique.Count == 0 ? "never" : string.Join(" | ", unique);
	}

	private static string DocComment(string? description, string indent)
	{
		var text = description?.Trim();
		if (string.IsNullOrEmpty(text)) return string.Empty;
		var lines = text.Replace("*/", "*\\/", StringComparison.Ordinal).Split(['\r', '\n'], StringSplitOptions.None);
		if (lines.Length == 1) return $"{indent}/** {lines[0]} */\n";
		var body = string.Join("\n", lines.Select(line => $"{indent} *{(line.Length > 0 ? $" {line}" : string.Empty)}"));
		return $"{indent}/**\n{body}\n{indent} */\n";
	}

	private static string DescriptionOf(JsonNode? property) =>
		property is JsonObject obj ? Str(obj["description"])?.Trim() ?? string.Empty : string.Empty;

	/// <summary>
	/// 类型表达不了的约束，写成 JSDoc 标签：<c>@integer @minimum 1 @maximum 100 @default 20</c>。
	/// </summary>
	private static string ConstraintTags(JsonNode? schema)
	{
		if (schema is not JsonObject property) return string.Empty;

		// Pydantic 的 `int | None` 把约束放在非 null 分支，两者要一起读。
		var variants = property["anyOf"] as JsonArray ?? property["oneOf"] as JsonArray;
		var nonNull = variants?.Where(variant => Str((variant as JsonObject)?["type"]) != "null").ToList() ?? [];
		var branches = nonNull is [{ } single] && single is JsonObject branch
			? new List<JsonObject> { property, branch }
			: [property];

		var tags = new List<string>();
		if (branches.Any(branchSchema => IsInteger(branchSchema["type"]))) tags.Add("@integer");

		foreach (var keyword in TagKeywords)
		{
			var values = branches
				.Select(branchSchema => branchSchema[keyword])
				.Where(static value => value is not null)
				.ToList();
			if (LowerBounds.Contains(keyword) || UpperBounds.Contains(keyword))
			{
				var numbers = values.Select(Number).OfType<double>().ToList();
				if (numbers.Count == 0) continue;
				tags.Add($"@{keyword} {(LowerBounds.Contains(keyword) ? numbers.Max() : numbers.Min())}");
				continue;
			}

			foreach (var value in values.Distinct(JsonNodeComparer.Instance))
			{
				if (Number(value) is { } number) tags.Add($"@{keyword} {number}");
				else if (Str(value) is { } text) tags.Add($"@{keyword} {text}");
			}
		}

		if (branches.Any(static branchSchema => branchSchema["uniqueItems"] is JsonValue value && IsBoolean(value, true)))
		{
			tags.Add("@uniqueItems");
		}

		if (property["default"] is { } defaultValue && !IsNull(defaultValue))
		{
			tags.Add($"@default {Json(defaultValue)}");
		}

		if (property["deprecated"] is JsonValue deprecated && IsBoolean(deprecated, true)) tags.Add("@deprecated");

		return string.Join(" ", tags.Where(tag => tag.Length <= MaxTagChars).Select(EscapeTag));
	}

	private static string EscapeTag(string tag) =>
		tag.Replace("\r", "\\u000d", StringComparison.Ordinal)
			.Replace("\n", "\\u000a", StringComparison.Ordinal)
			.Replace("\u2028", "\\u2028", StringComparison.Ordinal)
			.Replace("\u2029", "\\u2029", StringComparison.Ordinal);

	private static bool IsInteger(JsonNode? type) =>
		Str(type) == "integer" || (type is JsonArray array && array.Any(entry => Str(entry) == "integer"));

	private static JsonNode? ResolveRef(string reference, JsonNode? root)
	{
		if (root is null) return null;
		if (reference == "#") return root;
		if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;

		JsonNode? current = root;
		foreach (var raw in reference[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			var key = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
			if (current is not JsonObject obj || obj[key] is not { } next) return null;
			current = next;
		}

		return current;
	}

	private static string? Str(JsonNode? node)
	{
		if (node is not JsonValue value) return null;
		if (value.TryGetValue<JsonElement>(out var element))
		{
			return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
		}

		return value.TryGetValue<string>(out var text) ? text : null;
	}

	private static double? Number(JsonNode? node)
	{
		if (node is not JsonValue value) return null;
		if (value.TryGetValue<JsonElement>(out var element))
		{
			return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) ? number : null;
		}

		if (value.TryGetValue<double>(out var raw)) return raw;
		if (value.TryGetValue<int>(out var integer)) return integer;
		if (value.TryGetValue<long>(out var longInteger)) return longInteger;
		return value.TryGetValue<decimal>(out var decimalValue) ? (double)decimalValue : null;
	}

	private static bool IsBoolean(JsonNode? node, bool expected)
	{
		if (node is not JsonValue value) return false;
		if (value.TryGetValue<JsonElement>(out var element))
		{
			return element.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
		}

		return value.TryGetValue<bool>(out var raw) && raw == expected;
	}

	private static bool IsNull(JsonNode? node)
	{
		if (node is null) return true;
		if (node is not JsonValue value) return false;
		if (value.TryGetValue<JsonElement>(out var element)) return element.ValueKind == JsonValueKind.Null;
		return false;
	}

	private static string? Json(JsonNode? node) => node?.ToJsonString();

	private sealed class JsonNodeComparer : IEqualityComparer<JsonNode?>
	{
		public static JsonNodeComparer Instance { get; } = new();

		public bool Equals(JsonNode? x, JsonNode? y) => JsonNode.DeepEquals(x, y);

		public int GetHashCode(JsonNode? obj) => obj?.ToJsonString().GetHashCode(StringComparison.Ordinal) ?? 0;
	}
}
