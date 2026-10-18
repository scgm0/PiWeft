using System.Text;
using System.Text.RegularExpressions;
using PiWeft.CodingAgent.Resources;

namespace PiWeft.CodingAgent.Prompts;

/// <summary>从 <c>prompts</c> 目录加载的斜杠命令提示词模板。</summary>
public sealed record PromptTemplate
{
	/// <summary>命令名，取文件名去掉扩展名。</summary>
	public required string Name { get; init; }

	public required string Content { get; init; }

	public required string FilePath { get; init; }

	/// <summary>模板来源（用户、项目或包）。</summary>
	public ResourceOrigin Origin { get; init; }
}

/// <summary>
/// 加载并展开斜杠命令提示词模板。对应 pi 的 <c>prompt-templates.ts</c>。
/// </summary>
public static partial class PromptTemplates
{
	/// <summary>从用户与项目提示词目录加载模板，项目模板优先；包含已安装 pi 包提供的模板。</summary>
	public static IReadOnlyList<PromptTemplate> Discover(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var resolvedAgentDir = agentDir ?? Session.SessionPaths.GetAgentDir();
		var packages = PiPackageStore.Discover(cwd, resolvedAgentDir, includeProject);
		return Discover(ResourceResolver.PromptRoots(cwd, resolvedAgentDir, includeProject, packages));
	}

	/// <summary>从已解析的根目录加载模板；单个根可以是目录，也可以是 <c>.md</c> 文件。</summary>
	public static IReadOnlyList<PromptTemplate> Discover(IReadOnlyList<ResourceRoot> roots)
	{
		var templates = new List<PromptTemplate>();
		var seenNames = new HashSet<string>(StringComparer.Ordinal);

		foreach (var (root, origin) in roots)
		{
			if (File.Exists(root))
			{
				LoadFile(root, origin, templates, seenNames);
				continue;
			}

			LoadDirectory(root, origin, templates, seenNames);
		}

		return templates;
	}

	/// <summary>把 <c>/name args</c> 形式的消息展开成模板正文；不匹配时原样返回。</summary>
	public static string Expand(string text, IReadOnlyList<PromptTemplate> templates)
	{
		if (!text.StartsWith('/'))
		{
			return text;
		}

		var match = CommandRegex().Match(text);
		if (!match.Success)
		{
			return text;
		}

		var name = match.Groups["name"].Value;
		var argsString = match.Groups["args"].Success ? match.Groups["args"].Value : string.Empty;

		foreach (var template in templates)
		{
			if (string.Equals(template.Name, name, StringComparison.Ordinal))
			{
				return SubstituteArgs(template.Content, ParseArgs(argsString));
			}
		}

		return text;
	}

	/// <summary>
	/// 按空白切分参数字符串，并识别单引号与双引号。
	/// 空的引号参数（<c>""</c> / <c>''</c>）是一个位置参数，不能丢弃 ——
	/// 丢掉会让后面的参数错位、默认值失真（pi 1.3.0 修正的正是这个行为）。
	/// </summary>
	public static string[] ParseArgs(string argsString)
	{
		var args = new List<string>();
		var current = new StringBuilder();
		char? quote = null;

		// 当前词里出现过引号或有实际内容；引号即使立刻闭合也代表一个（可能为空的）参数。
		var sawToken = false;

		foreach (var c in argsString)
		{
			if (quote is { } open)
			{
				if (c == open)
				{
					quote = null;
				}
				else
				{
					current.Append(c);
				}
			}
			else if (c is '"' or '\'')
			{
				quote = c;
				sawToken = true;
			}
			else if (char.IsWhiteSpace(c))
			{
				if (current.Length > 0 || sawToken)
				{
					args.Add(current.ToString());
					current.Clear();
					sawToken = false;
				}
			}
			else
			{
				current.Append(c);
				sawToken = true;
			}
		}

		if (current.Length > 0 || sawToken)
		{
			args.Add(current.ToString());
		}

		return [.. args];
	}

	/// <summary>
	/// 在模板内容中替换 <c>$1</c>、<c>$@</c>、<c>$ARGUMENTS</c>、<c>${N:-default}</c>、<c>${@:-default}</c>、
	/// <c>${@:N}</c> 与 <c>${@:N:L}</c>。替换进去的值不会被再次扫描。
	/// </summary>
	public static string SubstituteArgs(string content, IReadOnlyList<string> args)
	{
		var allArgs = string.Join(" ", args);

		return PlaceholderRegex().Replace(content, match =>
		{
			var defaultTarget = match.Groups["defaultTarget"];
			if (defaultTarget.Success)
			{
				var fallback = match.Groups["defaultValue"].Value;
				var value = defaultTarget.Value is "@" or "ARGUMENTS"
					? allArgs
					: Index(args, int.Parse(defaultTarget.Value, System.Globalization.CultureInfo.InvariantCulture));

				return value.Length > 0 ? value : fallback;
			}

			var sliceStart = match.Groups["sliceStart"];
			if (sliceStart.Success)
			{
				var start = int.Parse(sliceStart.Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
				if (start < 0)
				{
					start = 0;
				}

				var sliceLength = match.Groups["sliceLength"];
				if (sliceLength.Success)
				{
					var length = int.Parse(sliceLength.Value, System.Globalization.CultureInfo.InvariantCulture);
					return string.Join(" ", args.Skip(start).Take(length));
				}

				return string.Join(" ", args.Skip(start));
			}

			var simple = match.Groups["simple"].Value;
			if (simple is "ARGUMENTS" or "@")
			{
				return allArgs;
			}

			return Index(args, int.Parse(simple, System.Globalization.CultureInfo.InvariantCulture));
		});
	}

	private static string Index(IReadOnlyList<string> args, int oneBased)
	{
		var index = oneBased - 1;
		return index >= 0 && index < args.Count ? args[index] : string.Empty;
	}

	private static void LoadDirectory(
		string directory,
		ResourceOrigin origin,
		List<PromptTemplate> templates,
		HashSet<string> seenNames)
	{
		if (!Directory.Exists(directory))
		{
			return;
		}

		string[] files;
		try
		{
			// 物化到数组：EnumerateFiles 是惰性的，异常在遍历时才抛，只包住创建等于没有保护。
			files = [.. Directory.EnumerateFiles(directory, "*.md")];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}

		foreach (var file in files.OrderBy(static f => f, StringComparer.Ordinal))
		{
			LoadFile(file, origin, templates, seenNames);
		}
	}

	private static void LoadFile(
		string file,
		ResourceOrigin origin,
		List<PromptTemplate> templates,
		HashSet<string> seenNames)
	{
		var name = Path.GetFileNameWithoutExtension(file);
		if (name.Length == 0 || !seenNames.Add(name))
		{
			return;
		}

		try
		{
			templates.Add(new PromptTemplate
			{
				Name = name,
				Content = File.ReadAllText(file),
				FilePath = Path.GetFullPath(file),
				Origin = origin,
			});
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			seenNames.Remove(name);
		}
	}

	[GeneratedRegex(@"^/(?<name>[^\s]+)(?:\s+(?<args>[\s\S]*))?$")]
	private static partial Regex CommandRegex();

	[GeneratedRegex(@"\$\{(?<defaultTarget>\d+|ARGUMENTS|@):-(?<defaultValue>[^}]*)\}|\$\{@:(?<sliceStart>\d+)(?::(?<sliceLength>\d+))?\}|\$(?<simple>ARGUMENTS|@|\d+)")]
	private static partial Regex PlaceholderRegex();
}
