using System.Text;
using System.Text.RegularExpressions;
using PiWeft.CodingAgent.Resources;

namespace PiWeft.CodingAgent.Prompts;

public sealed record PromptTemplate
{
	public required string Name { get; init; }

	public required string Content { get; init; }

	public string? Description { get; init; }

	public required string FilePath { get; init; }

	public ResourceOrigin Origin { get; init; }
}

/// 加载并展开斜杠命令提示词模板。对应 pi 的 <c>prompt-templates.ts</c>。
public static partial class PromptTemplates
{
	public static IReadOnlyList<PromptTemplate> Discover(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var resolvedAgentDir = agentDir ?? Session.SessionPaths.GetAgentDir();
		var packages = PiPackageStore.Discover(cwd, resolvedAgentDir, includeProject);
		return Discover(ResourceResolver.PromptRoots(cwd, resolvedAgentDir, includeProject, packages));
	}

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

	/// 按空白切分参数字符串，并识别单引号与双引号。
	/// 空的引号参数（<c>""</c> / <c>''</c>）是一个位置参数，不能丢弃；
	/// 丢掉会让后面的参数错位、默认值失真（pi 1.3.0 修正的正是这个行为）。
	public static string[] ParseArgs(string argsString)
	{
		var args = new List<string>();
		var current = new StringBuilder();
		char? quote = null;
		var quoteStart = -1;
		var hasToken = false;

		for (var i = 0; i < argsString.Length; i++)
		{
			var c = argsString[i];
			if (quote is { } open)
			{
				if (c == '\\' && i + 1 < argsString.Length && argsString[i + 1] == open)
				{
					current.Append(open);
					i++;
				}
				else if (c == open)
				{
					quote = null;
				}
				else
				{
					current.Append(c);
				}
			}
			else if (c is '"' or '\'' && !hasToken)
			{
				// 引号只在参数开头生效：doesn't 里的撇号按普通字符处理。
				quote = c;
				quoteStart = i;
				hasToken = true;
			}
			else if (char.IsWhiteSpace(c))
			{
				if (hasToken)
				{
					args.Add(current.ToString());
					current.Clear();
					hasToken = false;
				}
			}
			else
			{
				current.Append(c);
				hasToken = true;
			}

			if (quote is not null && i == argsString.Length - 1)
			{
				// 未闭合的引号按普通字符处理，从引号位置重新当作无引号文本扫描。
				quote = null;
				current.Clear();
				current.Append(argsString[quoteStart]);
				hasToken = true;
				i = quoteStart;
			}
		}

		if (hasToken)
		{
			args.Add(current.ToString());
		}

		return [.. args];
	}

	/// 在模板内容中替换 <c>$1</c>、<c>$@</c>、<c>$ARGUMENTS</c>、<c>${N:-default}</c>、<c>${@:-default}</c>、
	/// <c>${@:N}</c> 与 <c>${@:N:L}</c>。替换进去的值不会被再次扫描。
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
			var raw = File.ReadAllText(file);
			var (fields, body) = Skills.Skills.ParseFrontmatter(raw);
			templates.Add(new PromptTemplate
			{
				Name = name,
				Content = body,
				Description = fields is not null && fields.TryGetValue("description", out var description) &&
					description.Length > 0 ? description : null,
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
