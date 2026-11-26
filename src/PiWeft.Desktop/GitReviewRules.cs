using System.Text;

namespace PiWeft.Desktop;

/// 仓库根 `.gitattributes` 里的审查层规则：团队可以在仓库内声明的约定优先于内置路径规则。
/// 只认两个属性：`piweft-review-layer=test|interface|implementation` 与 `linguist-generated`。
/// 后出现的匹配行覆盖先前的（git 的同一套语义）；带 `!` 的模式跳过，不反向取消前面的规则。
internal sealed class GitReviewRules
{
	public static GitReviewRules Empty { get; } = new([]);

	private const int MaxFileBytes = 256 * 1024;

	private readonly Rule[] _rules;

	private GitReviewRules(Rule[] rules) => _rules = rules;

	public bool IsEmpty => _rules.Length == 0;

	/// 读仓库根的 `.gitattributes`；文件缺失、过大或读不到都退回空规则（只用内置规则）。
	public static GitReviewRules Load(string repository)
	{
		try
		{
			var path = Path.Combine(repository, ".gitattributes");
			var info = new FileInfo(path);
			if (!info.Exists || info.Length > MaxFileBytes)
			{
				return Empty;
			}

			return Parse(File.ReadAllText(path));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return Empty;
		}
	}

	internal static GitReviewRules Parse(string text)
	{
		var rules = new List<Rule>();
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.TrimEnd('\r').Trim();
			if (line.Length == 0 || line[0] == '#')
			{
				continue;
			}

			var (pattern, rest) = SplitPattern(line);
			if (pattern.Length == 0 || pattern[0] == '!')
			{
				// 取反模式不反向取消规则：本实现只按顺序覆盖，跳过比做半套更安全。
				continue;
			}

			// 结尾带 / 的模式在 gitattributes 里只匹配目录本身；文件列表里没有目录，跳过。
			if (pattern.EndsWith('/'))
			{
				continue;
			}

			var rule = Rule.Parse(pattern, rest);
			if (rule is { } parsed)
			{
				rules.Add(parsed);
			}
		}

		return rules.Count == 0 ? Empty : new GitReviewRules([.. rules]);
	}

	/// 返回自定义层；null 表示没有命中，交给内置规则。`linguist-generated` 归到测试层。
	public GitReviewLayer? LayerFor(string path)
	{
		if (_rules.Length == 0)
		{
			return null;
		}

		var normalized = path.Replace('\\', '/');
		GitReviewLayer? layer = null;
		var generated = false;
		foreach (var rule in _rules)
		{
			if (!rule.Matches(normalized))
			{
				continue;
			}

			if (rule.SetsGenerated)
			{
				generated = true;
			}
			else if (rule.UnsetsGenerated)
			{
				generated = false;
			}

			if (rule.Layer is { } assigned)
			{
				layer = assigned;
			}
			else if (rule.UnsetsLayer)
			{
				layer = null;
			}
		}

		return layer ?? (generated ? GitReviewLayer.Tests : null);
	}

	/// 模式与属性之间用空白分隔；模式可以用双引号包住含空格的路径。
	private static (string Pattern, string Attributes) SplitPattern(string line)
	{
		if (line[0] == '"')
		{
			var end = line.IndexOf('"', 1);
			if (end > 0)
			{
				return (line[1..end], line[(end + 1)..].Trim());
			}
		}

		var space = line.IndexOf(' ');
		return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
	}

	private readonly record struct Rule(string Pattern, GitReviewLayer? Layer, bool UnsetsLayer, bool SetsGenerated, bool UnsetsGenerated)
	{
		public static Rule? Parse(string pattern, string attributes)
		{
			GitReviewLayer? layer = null;
			var unsetsLayer = false;
			var setsGenerated = false;
			var unsetsGenerated = false;
			var matched = false;

			foreach (var attribute in attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				if (attribute is "linguist-generated" or "linguist-generated=true")
				{
					setsGenerated = true;
					matched = true;
					continue;
				}

				if (attribute == "-linguist-generated")
				{
					unsetsGenerated = true;
					matched = true;
					continue;
				}

				if (attribute == "-piweft-review-layer")
				{
					unsetsLayer = true;
					matched = true;
					continue;
				}

				const string prefix = "piweft-review-layer=";
				if (!attribute.StartsWith(prefix, StringComparison.Ordinal))
				{
					continue;
				}

				var value = attribute[prefix.Length..].Trim();
				var parsed = value switch
				{
					"test" or "tests" => GitReviewLayer.Tests,
					"interface" => GitReviewLayer.Interface,
					"implementation" or "code" => GitReviewLayer.Implementation,
					_ => GitReviewLayer.All,
				};
				if (parsed == GitReviewLayer.All)
				{
					// 不认识的值不改变分层，避免把拼错的值静默当成某一层。
					continue;
				}

				layer = parsed;
				matched = true;
			}

			return matched
				? new Rule(pattern, layer, unsetsLayer, setsGenerated, unsetsGenerated)
				: null;
		}

		public bool Matches(string path)
		{
			var pattern = Pattern;
			var anchored = pattern.StartsWith('/');
			if (anchored)
			{
				pattern = pattern[1..];
			}

			// 没有斜杠的模式按 basename 匹配（gitattributes 的规则）；有斜杠的按仓库相对路径。
			var target = !anchored && !pattern.Contains('/') ? GitPathText.LeafName(path) : path;
			return Glob.Match(pattern, target);
		}
	}

	/// 只实现 gitattributes 里常用的三种通配：`*`（不跨 `/`）、`**`（跨 `/`）、`?`（一个非 `/` 字符）。
	/// 方括号类与转义不在本版范围内：写错模式时退回内置规则，不会静默分错层。
	private static class Glob
	{
		public static bool Match(string pattern, string text)
		{
			var memo = new Dictionary<(int Pattern, int Text), bool>();
			return Match(pattern, 0, text, 0, memo);
		}

		private static bool Match(
			string pattern,
			int patternIndex,
			string text,
			int textIndex,
			Dictionary<(int, int), bool> memo)
		{
			if (memo.TryGetValue((patternIndex, textIndex), out var cached))
			{
				return cached;
			}

			var result = MatchCore(pattern, patternIndex, text, textIndex, memo);
			memo[(patternIndex, textIndex)] = result;
			return result;
		}

		private static bool MatchCore(
			string pattern,
			int patternIndex,
			string text,
			int textIndex,
			Dictionary<(int, int), bool> memo)
		{
			while (patternIndex < pattern.Length)
			{
				var current = pattern[patternIndex];
				if (current == '*')
				{
					var doubleStar = patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*';
					if (doubleStar)
					{
						// `**` 吃掉任意数量字符（含 /），后面的模式逐个位置尝试。
						for (var skip = textIndex; skip <= text.Length; skip++)
						{
							if (Match(pattern, patternIndex + 2, text, skip, memo))
							{
								return true;
							}
						}

						return false;
					}

					for (var skip = textIndex; ; skip++)
					{
						if (Match(pattern, patternIndex + 1, text, skip, memo))
						{
							return true;
						}

						if (skip >= text.Length || text[skip] == '/')
						{
							return false;
						}
					}
				}

				if (textIndex >= text.Length)
				{
					return false;
				}

				if (current == '?')
				{
					if (text[textIndex] == '/')
					{
						return false;
					}
				}
				else if (current != text[textIndex])
				{
					return false;
				}

				patternIndex++;
				textIndex++;
			}

			return textIndex == text.Length;
		}
	}
}
