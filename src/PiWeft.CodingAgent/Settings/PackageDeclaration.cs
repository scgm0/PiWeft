using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiWeft.CodingAgent.Settings;

/// <summary>
/// settings.packages 的一个条目。字符串形式等价于只填写 <see cref="Source"/>；
/// 对象形式可以关闭自动加载并按资源类型收窄。对应 pi 的 <c>PackageSource</c>。
/// </summary>
public sealed record PackageDeclaration
{
	public required string Source { get; init; }

	/// <summary>false 时不自动加载包内资源，只应用显式过滤器。</summary>
	public bool Autoload { get; init; } = true;

	public List<string>? Extensions { get; init; }

	public List<string>? Skills { get; init; }

	public List<string>? Prompts { get; init; }

	public List<string>? Themes { get; init; }

	public static PackageDeclaration FromSource(string source) => new() { Source = source };

	public static implicit operator PackageDeclaration(string source) => FromSource(source);
}

/// <summary>兼容字符串与对象两种 packages 条目。</summary>
public sealed class PackageDeclarationListJsonConverter : JsonConverter<List<PackageDeclaration>>
{
	public override List<PackageDeclaration> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.StartArray)
		{
			throw new JsonException("packages must be an array");
		}

		var result = new List<PackageDeclaration>();
		while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
		{
			switch (reader.TokenType)
			{
				case JsonTokenType.String:
					result.Add(PackageDeclaration.FromSource(reader.GetString() ?? string.Empty));
					break;

				case JsonTokenType.StartObject:
				{
					using var document = JsonDocument.ParseValue(ref reader);
					var root = document.RootElement;
					var source = root.TryGetProperty("source", out var sourceValue) && sourceValue.ValueKind == JsonValueKind.String
						? sourceValue.GetString() ?? string.Empty
						: string.Empty;
					if (source.Length == 0)
					{
						break;
					}

					result.Add(new PackageDeclaration
					{
						Source = source,
						Autoload = !root.TryGetProperty("autoload", out var autoload) || autoload.ValueKind != JsonValueKind.False,
						Extensions = Strings(root, "extensions"),
						Skills = Strings(root, "skills"),
						Prompts = Strings(root, "prompts"),
						Themes = Strings(root, "themes"),
					});
					break;
				}

				default:
					reader.Skip();
					break;
			}
		}

		return result;
	}

	public override void Write(Utf8JsonWriter writer, List<PackageDeclaration> value, JsonSerializerOptions options)
	{
		writer.WriteStartArray();
		foreach (var package in value)
		{
			if (package.Autoload && package.Extensions is null && package.Skills is null &&
				package.Prompts is null && package.Themes is null)
			{
				writer.WriteStringValue(package.Source);
				continue;
			}

			writer.WriteStartObject();
			writer.WriteString("source", package.Source);
			if (!package.Autoload)
			{
				writer.WriteBoolean("autoload", false);
			}

			WriteList(writer, "extensions", package.Extensions);
			WriteList(writer, "skills", package.Skills);
			WriteList(writer, "prompts", package.Prompts);
			WriteList(writer, "themes", package.Themes);
			writer.WriteEndObject();
		}

		writer.WriteEndArray();
	}

	private static List<string>? Strings(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		var result = new List<string>();
		foreach (var item in value.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
			{
				result.Add(text);
			}
		}

		return result;
	}

	private static void WriteList(Utf8JsonWriter writer, string name, List<string>? values)
	{
		if (values is null)
		{
			return;
		}

		writer.WritePropertyName(name);
		writer.WriteStartArray();
		foreach (var value in values)
		{
			writer.WriteStringValue(value);
		}

		writer.WriteEndArray();
	}
}

/// <summary>兼容旧版 skills 对象（customDirectories）与字符串数组。</summary>
public sealed class FlexibleStringListJsonConverter : JsonConverter<List<string>>
{
	public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.StartObject)
		{
			using var document = JsonDocument.ParseValue(ref reader);
			if (document.RootElement.TryGetProperty("customDirectories", out var directories) &&
				directories.ValueKind == JsonValueKind.Array)
			{
				return ReadArray(directories);
			}

			return [];
		}

		if (reader.TokenType != JsonTokenType.StartArray)
		{
			throw new JsonException("expected an array or skills object");
		}

		var result = new List<string>();
		while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
		{
			if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } text)
			{
				result.Add(text);
			}
		}

		return result;
	}

	public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
	{
		writer.WriteStartArray();
		foreach (var item in value)
		{
			writer.WriteStringValue(item);
		}

		writer.WriteEndArray();
	}

	private static List<string> ReadArray(JsonElement array)
	{
		var result = new List<string>();
		foreach (var item in array.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
			{
				result.Add(text);
			}
		}

		return result;
	}
}
