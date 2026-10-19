using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Utils;

/// <summary>
/// 宽容的 JSON 读取：修复模型常见的非法转义与控制字符，并在失败时保留原文。
/// 对应 pi 的 <c>utils/json-parse.ts</c>。
/// </summary>
public static class JsonRepair
{
	private static readonly HashSet<char> ValidEscapes = ['"', '\\', '/', 'b', 'f', 'n', 'r', 't'];

	/// <summary>转义字符串里的控制字符，并给非法转义补一个反斜杠。</summary>
	public static string Repair(string json)
	{
		var repaired = new StringBuilder(json.Length + 16);
		var inString = false;

		for (var index = 0; index < json.Length; index++)
		{
			var current = json[index];
			if (!inString)
			{
				repaired.Append(current);
				if (current == '"')
				{
					inString = true;
				}

				continue;
			}

			if (current == '"')
			{
				repaired.Append(current);
				inString = false;
				continue;
			}

			if (current != '\\')
			{
				repaired.Append(IsControl(current) ? EscapeControl(current) : current);
				continue;
			}

			if (index + 1 >= json.Length)
			{
				repaired.Append("\\\\");
				continue;
			}

			var next = json[index + 1];
			if (next == 'u')
			{
				var digits = index + 2 + 4 <= json.Length ? json.Substring(index + 2, 4) : string.Empty;
				if (digits.Length == 4 && digits.All(static c => char.IsAsciiHexDigit(c)))
				{
					repaired.Append("\\u").Append(digits);
					index += 5;
					continue;
				}

				repaired.Append("\\\\u");
				index++;
				continue;
			}

			if (ValidEscapes.Contains(next))
			{
				repaired.Append('\\').Append(next);
				index++;
				continue;
			}

			repaired.Append("\\\\");
		}

		return repaired.ToString();
	}

	/// <summary>解析一个 JSON 对象；不是对象或无法修复时返回错误文本。</summary>
	public static (JsonObject? Value, string? Error) ParseObject(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return (new JsonObject(), null);
		}

		try
		{
			return (ParseObjectCore(json), null);
		}
		catch (JsonException first)
		{
			try
			{
				var repaired = Repair(json);
				if (!string.Equals(repaired, json, StringComparison.Ordinal))
				{
					return (ParseObjectCore(repaired), null);
				}
			}
			catch (JsonException)
			{
				// 使用第一次的错误文本，它更接近模型实际发送的内容。
			}

			return (null, $"{first.Message}\n\nReceived arguments:\n{json}");
		}
	}

	private static JsonObject ParseObjectCore(string json)
	{
		var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip,
		});

		return node switch
		{
			JsonObject value => value,
			null => new JsonObject(),
			_ => throw new JsonException("Tool arguments must be a JSON object"),
		};
	}

	private static bool IsControl(char value) => value is >= '\u0000' and <= '\u001f';

	private static string EscapeControl(char value) => value switch
	{
		'\b' => "\\b",
		'\f' => "\\f",
		'\n' => "\\n",
		'\r' => "\\r",
		'\t' => "\\t",
		_ => $"\\u{(int)value:x4}",
	};
}
