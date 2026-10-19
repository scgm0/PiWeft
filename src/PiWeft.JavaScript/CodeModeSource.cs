using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.JavaScript;

/// <summary>脚本首行 <c>// @options:</c> 声明的执行选项。</summary>
public sealed record CodeModeSourceOptions
{
	/// <summary>脚本输出的 token 预算。</summary>
	public int? MaxOutputTokens { get; init; }

	/// <summary>整个脚本的硬性时限（毫秒）。</summary>
	public int? TimeoutMs { get; init; }
}

/// <summary>解析后的脚本：<see cref="Code"/> 的选项行已替换成空行，行号与输入一致。</summary>
public sealed record ParsedCodeModeSource(string Code, CodeModeSourceOptions Options);

/// <summary>脚本源码格式错误；与脚本自身抛错区分，调用方可以直接展示。</summary>
public sealed class CodeModeSourceException : Exception
{
	public CodeModeSourceException(string message) : base(message) { }
}

/// <summary>
/// 解析 codemode 源码：可选的 <c>// @options: {"max_output_tokens": 2000, "timeout_ms": 30000}</c>
/// 首行，其后是 JavaScript。与 pi 的 <c>parseCodemodeSource()</c> 规则一致。
/// </summary>
public static class CodeModeSource
{
	public const string OptionsPrefix = "// @options:";

	private const string SupportedFieldsText = "`max_output_tokens` 和 `timeout_ms`";

	/// <summary>最大 <c>timeout_ms</c>，与 <see cref="int.MaxValue"/> 一致。</summary>
	private const int MaxTimeoutMs = int.MaxValue;

	public static ParsedCodeModeSource Parse(string input)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (string.IsNullOrWhiteSpace(input))
		{
			throw new CodeModeSourceException(
				"Expected JavaScript source text (non-empty). Provide JS only, optionally with a first line `// @options: {\"max_output_tokens\": 1000}`.");
		}

		var lines = input.Split('\n');
		for (var index = 1; index < lines.Length; index++)
		{
			if (lines[index].TrimStart().StartsWith(OptionsPrefix, StringComparison.Ordinal))
			{
				throw new CodeModeSourceException(
					$"The {OptionsPrefix} line must be the first line of the input; found one on line {index + 1}");
			}
		}

		var firstLine = lines[0].TrimEnd('\r');
		var trimmed = firstLine.TrimStart();
		if (!trimmed.StartsWith(OptionsPrefix, StringComparison.Ordinal))
		{
			return new ParsedCodeModeSource(input, new CodeModeSourceOptions());
		}

		var newline = input.IndexOf('\n');
		var code = newline < 0 ? string.Empty : input[newline..];
		if (string.IsNullOrWhiteSpace(code))
		{
			throw new CodeModeSourceException("The @options line must be followed by JavaScript source on subsequent lines");
		}

		return new ParsedCodeModeSource(
			code,
			ParseOptions(trimmed[OptionsPrefix.Length..].Trim()));
	}

	private static CodeModeSourceOptions ParseOptions(string directive)
	{
		if (directive.Length == 0)
		{
			throw new CodeModeSourceException($"@options must be a JSON object with supported fields {SupportedFieldsText}");
		}

		JsonNode? value;
		try
		{
			value = JsonNode.Parse(directive);
		}
		catch (JsonException ex)
		{
			throw new CodeModeSourceException(
				$"@options must be valid JSON with supported fields {SupportedFieldsText}: {ex.Message}");
		}

		if (value is not JsonObject fields)
		{
			throw new CodeModeSourceException($"@options must be a JSON object with supported fields {SupportedFieldsText}");
		}

		foreach (var (key, _) in fields)
		{
			if (key is not ("max_output_tokens" or "timeout_ms"))
			{
				throw new CodeModeSourceException($"@options only supports {SupportedFieldsText}; got `{key}`");
			}
		}

		int? maxOutputTokens = null;
		int? timeoutMs = null;
		if (fields.TryGetPropertyValue("max_output_tokens", out var tokens))
		{
			var parsed = SafeInteger(tokens);
			if (parsed is null or < 0)
			{
				throw new CodeModeSourceException("@options field `max_output_tokens` must be a non-negative safe integer");
			}

			maxOutputTokens = parsed;
		}

		if (fields.TryGetPropertyValue("timeout_ms", out var timeout))
		{
			var parsed = SafeInteger(timeout);
			if (parsed is null or <= 0 or > MaxTimeoutMs)
			{
				throw new CodeModeSourceException(
					$"@options field `timeout_ms` must be a positive integer up to {MaxTimeoutMs.ToString(CultureInfo.InvariantCulture)}");
			}

			timeoutMs = parsed;
		}

		return new CodeModeSourceOptions { MaxOutputTokens = maxOutputTokens, TimeoutMs = timeoutMs };
	}

	/// <summary>整数节点 → int；非整数、缺失与超出 safe integer 范围都返回 <c>null</c>。</summary>
	private static int? SafeInteger(JsonNode? node)
	{
		if (node is not JsonValue value) return null;
		long number;
		if (value.TryGetValue<JsonElement>(out var element))
		{
			if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out number)) return null;
		}
		else if (!value.TryGetValue<long>(out number))
		{
			if (!value.TryGetValue<int>(out var small)) return null;
			number = small;
		}

		return number is >= int.MinValue and <= int.MaxValue ? (int)number : null;
	}
}
