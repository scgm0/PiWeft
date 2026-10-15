using System.Collections.Frozen;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Resources;

namespace PiWeft.CodingAgent.Themes;

/// <summary>主题的明暗分类。</summary>
public enum ThemeAppearance
{
	Dark,
	Light,
}

/// <summary>
/// 一个已解析的 pi 主题：<c>name</c>、可选 <c>appearance</c>、<c>vars</c> 变量与 <c>colors</c> 角色。
/// 颜色值可能是「终端默认」（<c>""</c>），由渲染侧决定回退。
/// </summary>
public sealed record PiTheme
{
	/// <summary>主题名，即 <c>name</c> 字段；文件名的约定值也是它。</summary>
	public required string Name { get; init; }

	/// <summary>主题文件路径。</summary>
	public required string FilePath { get; init; }

	/// <summary>明暗分类；未声明时按背景角色推断。</summary>
	public ThemeAppearance Appearance { get; init; }

	/// <summary>已解析的颜色角色。</summary>
	public required IReadOnlyDictionary<string, ThemeColor> Colors { get; init; }

	/// <summary>来源（用户/项目/包）。</summary>
	public ResourceOrigin Origin { get; init; }

	/// <summary>解析期间的诊断（未知角色、缺失的回退项等）。</summary>
	public IReadOnlyList<string> Warnings { get; init; } = [];

	public ThemeColor? ExportPageBackground { get; init; }

	public ThemeColor? ExportCardBackground { get; init; }

	public ThemeColor? ExportInfoBackground { get; init; }

	/// <summary>取一个颜色角色，缺失或为「终端默认」时返回 <paramref name="fallback"/>。</summary>
	public ThemeColor GetOrDefault(string token, ThemeColor fallback)
	{
		if (Colors.TryGetValue(token, out var color) && !color.IsTerminalDefault)
		{
			return color;
		}

		if (ThemeTokens.OptionalFallbacks.TryGetValue(token, out var fallbackToken) &&
			Colors.TryGetValue(fallbackToken, out var inherited) &&
			!inherited.IsTerminalDefault)
		{
			return inherited;
		}

		return fallback;
	}

	/// <summary>
	/// 解析一个主题 JSON。<paramref name="error"/> 非空表示主题无效（缺少 name/colors、
	/// 颜色写法不合法或存在未定义的变量），此时返回 <c>null</c>。
	/// </summary>
	public static PiTheme? Parse(
		string json,
		string filePath,
		ResourceOrigin origin,
		out string? error,
		out IReadOnlyList<string> warnings)
	{
		error = null;
		warnings = [];

		JsonNode? root;
		try
		{
			root = JsonNode.Parse(json);
		}
		catch (System.Text.Json.JsonException exception)
		{
			error = $"主题 JSON 无法解析：{exception.Message}";
			return null;
		}

		if (root is not JsonObject document)
		{
			error = "主题文件必须是一个 JSON 对象";
			return null;
		}

		// 类型不符时 GetValue<string> 抛的是 InvalidOperationException（不是 JsonException），
		// 会逃出发现流程让整个会话构造失败，所以只接受真正的字符串。
		var name = document["name"] is JsonValue nameValue && nameValue.GetValueKind() == System.Text.Json.JsonValueKind.String
			? nameValue.GetValue<string>().Trim()
			: null;
		if (string.IsNullOrEmpty(name))
		{
			error = "主题缺少必需的 name 字段";
			return null;
		}

		if (name.Contains('/', StringComparison.Ordinal))
		{
			error = $"主题名不能包含 '/'：{name}";
			return null;
		}

		if (document["colors"] is not JsonObject colorObject)
		{
			error = "主题缺少必需的 colors 对象";
			return null;
		}

		var variables = ReadVariables(document["vars"]);
		var diagnostics = new List<string>();
		var colors = new Dictionary<string, ThemeColor>(colorObject.Count, StringComparer.Ordinal);

		foreach (var (token, node) in colorObject)
		{
			// 只接受字符串或整数两种写法；小数等其它数字形态按「值不合法」记诊断，
			// 而不是让 GetValue<int> 抛出逃逸到发现流程之外。
			var raw = node is JsonValue value
				? value.GetValueKind() switch
				{
					System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
					System.Text.Json.JsonValueKind.Number when value.TryGetValue<int>(out var number) =>
						number.ToString(System.Globalization.CultureInfo.InvariantCulture),
					_ => null,
				}
				: null;

			if (raw is null)
			{
				diagnostics.Add($"颜色角色 {token} 的值必须是字符串或数字，已忽略");
				continue;
			}

			if (ThemeColorParser.TryParse(raw, variables, out var color, out var parseError))
			{
				colors[token] = color;
			}
			else
			{
				// pi 把未定义变量与循环引用视为主题无效 —— 颜色值写错同样如此。
				error = $"颜色角色 {token} 无法解析：{parseError}";
				return null;
			}
		}

		if (colors.Count == 0)
		{
			error = "主题的 colors 为空";
			return null;
		}

		foreach (var core in ThemeTokens.CoreTokens)
		{
			if (!colors.ContainsKey(core))
			{
				diagnostics.Add($"缺少常用角色 {core}，桌面配色将使用默认值");
			}
		}

		var export = document["export"] as JsonObject;
		warnings = diagnostics;

		return new PiTheme
		{
			Name = name,
			FilePath = filePath,
			Origin = origin,
			Appearance = ReadAppearance(document["appearance"], colors),
			Colors = colors.ToFrozenDictionary(StringComparer.Ordinal),
			Warnings = diagnostics,
			ExportPageBackground = ReadOptionalColor(export?["pageBg"], variables),
			ExportCardBackground = ReadOptionalColor(export?["cardBg"], variables),
			ExportInfoBackground = ReadOptionalColor(export?["infoBg"], variables),
		};
	}

	private static FrozenDictionary<string, string> ReadVariables(JsonNode? node)
	{
		var variables = new Dictionary<string, string>(StringComparer.Ordinal);
		if (node is not JsonObject vars)
		{
			return variables.ToFrozenDictionary(StringComparer.Ordinal);
		}

		foreach (var (key, value) in vars)
		{
			if (value is JsonValue jsonValue && jsonValue.GetValueKind() == System.Text.Json.JsonValueKind.String)
			{
				variables[key] = jsonValue.GetValue<string>();
			}
		}

		return variables.ToFrozenDictionary(StringComparer.Ordinal);
	}

	private static ThemeColor? ReadOptionalColor(JsonNode? node, IReadOnlyDictionary<string, string> variables)
	{
		if (node is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.String)
		{
			return null;
		}

		return ThemeColorParser.TryParse(value.GetValue<string>(), variables, out var color, out _)
			? color
			: null;
	}

	private static ThemeAppearance ReadAppearance(JsonNode? node, IReadOnlyDictionary<string, ThemeColor> colors)
	{
		if (node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String)
		{
			var declared = value.GetValue<string>();
			if (declared.Equals("light", StringComparison.OrdinalIgnoreCase))
			{
				return ThemeAppearance.Light;
			}

			if (declared.Equals("dark", StringComparison.OrdinalIgnoreCase))
			{
				return ThemeAppearance.Dark;
			}
		}

		// 未声明时按背景角色推断，与 pi 的做法一致。
		foreach (var token in new[]
		{
			ThemeTokens.ToolPendingBg,
			ThemeTokens.SelectedBg,
			ThemeTokens.UserMessageBg,
			ThemeTokens.CustomMessageBg,
		})
		{
			if (colors.TryGetValue(token, out var color) && !color.IsTerminalDefault)
			{
				var luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;
				return luminance > 0.5 ? ThemeAppearance.Light : ThemeAppearance.Dark;
			}
		}

		return ThemeAppearance.Dark;
	}
}
