namespace PiWeft.Mcp;

/// MCP 工具的暴露方式，与 pi 同名同义：
/// <list type="bullet">
/// <item><c>codemode</c>：只在 codemode 脚本里可调用，不直接声明给模型（pi 默认）。</item>
/// <item><c>deferred</c>：等 <c>tool_search</c> 加载后再由模型直接调用。</item>
/// <item><c>direct</c>：像普通工具一样直接声明给模型。</item>
/// <item><c>hidden</c>：注册但不可达（等于移除）。</item>
/// </list>
/// 默认值与 pi 一致：<c>codemode</c>。会话会把这类工具留给脚本调用（必要时自动启用 codemode），
/// <c>deferred</c> 等 <c>tool_search</c> 加载后再声明给模型，<c>hidden</c> 直接丢弃。
/// 仍沿用 pi 的默认别名替换规则。
public static class McpExposure
{
	public const string Codemode = "codemode";
	public const string Deferred = "deferred";
	public const string Direct = "direct";
	public const string Hidden = "hidden";

	public const string Default = Codemode;

	private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
	{
		["codemode-deferred"] = Codemode,
	};

	public static string? Normalize(string? value)	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		var trimmed = value.Trim();
		if (Aliases.TryGetValue(trimmed, out var alias))
		{
			return alias;
		}

		return trimmed is Codemode or Deferred or Direct or Hidden ? trimmed : null;
	}

	public static bool IsKnown(string? value) => Normalize(value) is not null;

	public static bool IsVisible(string exposure) => exposure != Hidden;

	/// 单个工具的有效暴露方式：精确名优先，其次按声明顺序取第一个匹配的通配符，最后回落到服务器默认。
	/// 未知值按默认值处理，避免一个拼写错误让整个服务器消失。
	public static string Resolve(McpServerConfig config, string toolName)
	{
		if (config.ToolExposure is { Count: > 0 } overrides)
		{
			if (overrides.TryGetValue(toolName, out var exact))
			{
				return Normalize(exact) ?? Default;
			}

			foreach (var (pattern, value) in overrides)
			{
				if (pattern.Contains('*') && MatchesWildcard(pattern, toolName))
				{
					return Normalize(value) ?? Default;
				}
			}
		}

		return Normalize(config.Exposure) ?? Default;
	}

	public static bool IsFullyHidden(McpServerConfig config) =>
		Normalize(config.Exposure) == Hidden && config.ToolExposure is not { Count: > 0 };

	internal static bool MatchesWildcard(string pattern, string value)
	{
		var patternIndex = 0;
		var valueIndex = 0;
		var starIndex = -1;
		var starValueIndex = 0;

		while (valueIndex < value.Length)
		{
			if (patternIndex < pattern.Length && pattern[patternIndex] == value[valueIndex])
			{
				patternIndex++;
				valueIndex++;
			}
			else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
			{
				starIndex = patternIndex;
				starValueIndex = valueIndex;
				patternIndex++;
			}
			else if (starIndex >= 0)
			{
				patternIndex = starIndex + 1;
				starValueIndex++;
				valueIndex = starValueIndex;
			}
			else
			{
				return false;
			}
		}

		while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
		{
			patternIndex++;
		}

		return patternIndex == pattern.Length;
	}
}
