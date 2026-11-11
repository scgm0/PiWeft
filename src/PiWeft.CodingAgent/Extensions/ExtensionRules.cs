using System.Text;
using System.Text.RegularExpressions;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Extensions;

/// <summary>
/// pi <c>settings.extensions</c> 的过滤规则：<c>!glob</c> 排除、<c>+path</c> 强制包含、
/// <c>-path</c> 强制排除。全局层按「排除 → 包含 → 排除」求值，项目层按最后一条匹配覆盖。
/// </summary>
public sealed class ExtensionRules
{
	/// <summary>内置 MCP 扩展名。</summary>
	public const string McpBuiltin = "mcp";

	/// <summary>内置 llama.cpp provider 扩展名。</summary>
	public const string LlamaCppBuiltin = "llama.cpp";

	/// <summary>内置 codemode 扩展名。</summary>
	public const string CodemodeBuiltin = "codemode";

	/// <summary>内置 tool_search 扩展名。</summary>
	public const string ToolSearchBuiltin = "tool-search";

	/// <summary>内置子代理扩展名。</summary>
	public const string SubagentBuiltin = "subagent";

	/// <summary>随 CLI 分发的内置扩展，顺序与 pi 的配置界面一致。</summary>
	public static IReadOnlyList<string> BuiltinNames { get; } =
		[McpBuiltin, LlamaCppBuiltin, CodemodeBuiltin, ToolSearchBuiltin, SubagentBuiltin];

	private static readonly StringComparison PathComparison =
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

	/// <summary>没有任何过滤规则。</summary>
	public static ExtensionRules Empty { get; } = new([], []);

	private readonly Rule[] _global;
	private readonly Rule[] _project;

	/// <summary>从已解析的全局与项目规则创建过滤器。</summary>
	public ExtensionRules(IReadOnlyList<string> global, IReadOnlyList<string> project)
	{
		_global = NormalizeRules(global);
		_project = NormalizeRules(project);
	}

	internal bool IsEmpty => _global.Length == 0 && _project.Length == 0;

	/// <summary>判断一个扩展路径或 <c>builtin:name</c> 是否启用；没有匹配规则时返回 <see langword="true"/>。</summary>
	public bool IsEnabled(string value)
	{
		var target = NormalizePath(value.Trim());
		if (target.Length == 0)
		{
			return true;
		}

		var enabled = true;
		if (_global.Length > 0)
		{
			// 全局层固定「排除 → 包含 → 排除」，与顺序无关；一次遍历收集三类命中。
			var excluded = false;
			var included = false;
			var forceExcluded = false;
			foreach (var rule in _global)
			{
				switch (rule.Prefix)
				{
					case '!':
						excluded |= !excluded && Matches(rule, target);
						break;
					case '+':
						included |= !included && SamePath(rule.Value, target);
						break;
					case '-':
						forceExcluded |= !forceExcluded && SamePath(rule.Value, target);
						break;
				}
			}

			if (excluded)
			{
				enabled = false;
			}
			if (included)
			{
				enabled = true;
			}
			if (forceExcluded)
			{
				enabled = false;
			}
		}

		foreach (var rule in _project)
		{
			if (rule.Prefix != '\0' && Matches(rule, target))
			{
				enabled = rule.Prefix is not ('!' or '-');
			}
		}

		return enabled;
	}

	/// <summary>判断一个内置扩展是否启用。</summary>
	public bool IsBuiltinEnabled(string name) => IsEnabled("builtin:" + name);

	/// <summary>
	/// 在原始条目里启用或停用一个扩展：保留显式路径与其他规则，只清理精确指向目标的
	/// <c>+</c>/<c>-</c> 规则，再追加一条新的精确规则。
	/// </summary>
	/// <param name="entries">该层 <c>extensions</c> 的原始条目。</param>
	/// <param name="value">绝对路径或 <c>builtin:name</c>。</param>
	/// <param name="enabled">目标状态。</param>
	/// <param name="baseDirectory">相对路径条目的解析基准；省略时按当前目录解析。</param>
	public static IReadOnlyList<string> SetEnabled(
		IReadOnlyList<string> entries,
		string value,
		bool enabled,
		string? baseDirectory = null)
	{
		var target = value.Trim();
		if (target.Length == 0)
		{
			return [.. entries];
		}

		// 写进 settings 的精确规则用绝对路径（builtin: 标识符原样保留），
		// 否则换工作目录后同一条规则就指向别处。
		target = ResolveExact(target, baseDirectory);

		var result = new List<string>(entries.Count + 1);
		foreach (var raw in entries)
		{
			var entry = raw.Trim();
			if (entry.Length < 2 || entry[0] is not ('+' or '-'))
			{
				result.Add(raw);
				continue;
			}

			var body = entry[1..].Trim();
			var resolved = ResolveExact(body, baseDirectory);
			if (SamePath(resolved, target))
			{
				continue;
			}

			result.Add(raw);
		}

		result.Add((enabled ? "+" : "-") + target);
		return result;
	}

	/// <summary>在原始条目里启用或停用一个内置扩展。</summary>
	public static IReadOnlyList<string> SetBuiltinEnabled(
		IReadOnlyList<string> entries,
		string name,
		bool enabled) => SetEnabled(entries, "builtin:" + name.Trim(), enabled);

	/// <summary>这个条目是不是 <c>+</c>/<c>-</c>/<c>!</c> 过滤规则，而不是显式来源声明。</summary>
	internal static bool IsRule(string entry) => entry.Length > 0 && entry[0] is '+' or '-' or '!';

	/// <summary>把规则里的相对路径解析成绝对路径；<c>builtin:</c> 标识符保持原样。</summary>
	internal static string ResolveExact(string value, string? baseDirectory)
	{
		var trimmed = value.Trim();
		if (trimmed.StartsWith("builtin:", StringComparison.Ordinal))
		{
			return trimmed;
		}

		var expanded = UserPath.ExpandTilde(trimmed);
		if (Path.IsPathRooted(expanded) || string.IsNullOrEmpty(baseDirectory))
		{
			return NormalizePath(expanded);
		}

		try
		{
			return NormalizePath(Path.GetFullPath(Path.Combine(baseDirectory, expanded)));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return NormalizePath(expanded);
		}
	}

	/// <summary>把 <c>!</c> glob 解析成绝对模式；通配符保持可匹配。</summary>
	internal static string ResolvePattern(string pattern, string baseDirectory)
	{
		var expanded = UserPath.ExpandTilde(pattern.Trim()).Replace('\\', '/');
		if (expanded.StartsWith("builtin:", StringComparison.Ordinal))
		{
			return expanded;
		}

		var normalized = expanded.StartsWith("./", StringComparison.Ordinal) ? expanded[2..] : expanded;
		if (!normalized.Contains('/'))
		{
			// pi 的 minimatch 还会拿文件名单独匹配：没有目录的模式要能命中任意层级的同名文件。
			return "**/" + normalized;
		}

		var wildcard = normalized.IndexOfAny('*', '?');
		if (wildcard < 0)
		{
			return ResolveExact(normalized, baseDirectory);
		}

		// 只把通配符前面的目录部分交给 GetFullPath，避免把 * 当文件名。
		// 这样 foo/../*.js 会先折叠成 base/*.js，而不是留下匹配不到的 ..
		var pathRoot = Path.IsPathRooted(normalized) ? Path.GetPathRoot(normalized) : null;
		var offset = pathRoot?.Length ?? 0;
		var relative = normalized[offset..];
		var relativeWildcard = Math.Max(0, wildcard - offset);
		var separator = relative.LastIndexOf('/', relativeWildcard);
		var relativeRoot = separator < 0 ? "." : relative[..separator];
		var remainder = separator < 0 ? relative : relative[(separator + 1)..];

		try
		{
			var root = Path.GetFullPath(Path.Combine(pathRoot ?? baseDirectory, relativeRoot));
			return NormalizePath(root) + "/" + remainder;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return expanded;
		}
	}

	private static Rule[] NormalizeRules(IReadOnlyList<string> entries)
	{
		if (entries.Count == 0)
		{
			return [];
		}

		var rules = new Rule[entries.Count];
		for (var index = 0; index < entries.Count; index++)
		{
			var entry = entries[index].Trim();
			if (entry.Length > 1 && entry[0] is '+' or '-' or '!')
			{
				var value = NormalizePath(entry[1..]);
				var glob = entry[0] == '!' && value.IndexOfAny('*', '?') >= 0 ? BuildGlob(value) : null;
				rules[index] = new Rule(entry[0], value, glob);
			}
			else
			{
				rules[index] = new Rule('\0', entry, null);
			}
		}

		return rules;
	}

	private static bool Matches(Rule rule, string target)
	{
		if (rule.Glob is not { } glob)
		{
			return SamePath(rule.Value, target);
		}

		try
		{
			return glob.IsMatch(target);
		}
		catch (RegexMatchTimeoutException)
		{
			// 极端路径长度下宁可保留扩展，也不要让一次过滤判定打断发现。
			return false;
		}
	}

	private static bool SamePath(string left, string right) =>
		left.StartsWith("builtin:", StringComparison.Ordinal) || right.StartsWith("builtin:", StringComparison.Ordinal)
			? string.Equals(left, right, StringComparison.Ordinal)
			: string.Equals(left, right, PathComparison);


	private static string NormalizePath(string value)
	{
		if (value.Length == 0)
		{
			return value;
		}

		var length = value.Length;
		while (length > 0 && value[length - 1] == '/')
		{
			length--;
		}

		var span = value.AsSpan(0, length);
		if (span.IndexOf('\\') < 0)
		{
			return length == value.Length ? value : value[..length];
		}

		return string.Create(length, value, static (destination, source) =>
		{
			for (var index = 0; index < destination.Length; index++)
			{
				destination[index] = source[index] == '\\' ? '/' : source[index];
			}
		});
	}

	private static Regex BuildGlob(string pattern)
	{
		// pi 的 ! 规则走 minimatch：* 不跨段，** 跨段。这里只实现资源和扩展路径用得到的子集。
		var builder = new StringBuilder("^");
		for (var index = 0; index < pattern.Length; index++)
		{
			var character = pattern[index];
			if (character == '*')
			{
				if (index + 1 < pattern.Length && pattern[index + 1] == '*')
				{
					// **/ 可以跨零段：minimatch 的 **/*.js 也匹配当前目录的文件。
					if (index + 2 < pattern.Length && pattern[index + 2] == '/')
					{
						builder.Append("(?:.*/)?");
						index += 2;
					}
					else
					{
						builder.Append(".*");
						index++;
					}
				}
				else
				{
					builder.Append("[^/]*");
				}
			}
			else if (character == '?')
			{
				builder.Append("[^/]");
			}
			else
			{
				builder.Append(Regex.Escape(character.ToString()));
			}
		}

		builder.Append("\\z");
		// 规则来自用户配置：非回溯引擎 + 超时，避免宽 glob 演变成回溯爆炸。
		return new Regex(
			builder.ToString(),
			RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Singleline,
			TimeSpan.FromSeconds(1));
	}

	private readonly struct Rule(char prefix, string value, Regex? glob)
	{
		public char Prefix { get; } = prefix;

		public string Value { get; } = value;

		public Regex? Glob { get; } = glob;
	}
}