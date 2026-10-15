using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.CodingAgent.Resources;

/// <summary>
/// <c>package.json</c> 里的 <c>pi</c> 字段：包声明的技能、提示词模板、主题与扩展路径。
/// 与 pi 的 <c>PiManifest</c> 一致 —— 只有「全为字符串的数组」才生效，否则该字段被忽略。
/// </summary>
public sealed record PiPackageManifest
{
	public IReadOnlyList<string> Extensions { get; init; } = [];

	public IReadOnlyList<string> Skills { get; init; } = [];

	public IReadOnlyList<string> Prompts { get; init; } = [];

	public IReadOnlyList<string> Themes { get; init; } = [];

	/// <summary>没有声明任何资源；此时按包的约定目录自动发现。</summary>
	public bool IsEmpty =>
		Extensions.Count == 0 && Skills.Count == 0 && Prompts.Count == 0 && Themes.Count == 0;

	/// <summary>解析 <c>package.json</c> 文本；格式不对时返回空清单。</summary>
	public static PiPackageManifest Parse(string json)
	{
		JsonObject? document;
		try
		{
			document = JsonNode.Parse(json) as JsonObject;
		}
		catch (JsonException)
		{
			return new PiPackageManifest();
		}

		if (document?["pi"] is not JsonObject pi)
		{
			return new PiPackageManifest();
		}

		return new PiPackageManifest
		{
			Extensions = Strings(pi["extensions"]),
			Skills = Strings(pi["skills"]),
			Prompts = Strings(pi["prompts"]),
			Themes = Strings(pi["themes"]),
		};
	}

	/// <summary>读取包根目录下的 <c>package.json</c>。</summary>
	public static PiPackageManifest Load(string packageRoot)
	{
		var path = Path.Combine(packageRoot, ResourcePaths.PackageManifestName);
		if (!File.Exists(path))
		{
			return new PiPackageManifest();
		}

		try
		{
			return Parse(File.ReadAllText(path));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new PiPackageManifest();
		}
	}

	private static IReadOnlyList<string> Strings(JsonNode? node)
	{
		if (node is not JsonArray array || array.Count == 0)
		{
			return [];
		}

		var values = new List<string>(array.Count);
		foreach (var item in array)
		{
			// pi：数组里出现非字符串时整个字段不生效。
			if (item is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
			{
				return [];
			}

			values.Add(value.GetValue<string>());
		}

		return values;
	}
}
