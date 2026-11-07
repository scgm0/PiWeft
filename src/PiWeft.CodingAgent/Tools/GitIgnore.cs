using System.Buffers;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// 忽略规则的来源。嵌套仓库只丢掉祖先仓库的本地 VCS 规则；全局 git 忽略与非 VCS 规则保留。
internal enum IgnoreSource
{
	LocalGit,
	GlobalGit,
	Always,
}

/// grep 跟 ripgrep 的 ignore 语义，find 跟 fd 的。
internal enum IgnoreFlavor
{
	Ripgrep,
	Fd,
}

/// 遍历参数：ignore 方言决定读哪些 ignore 文件，也决定嵌套仓库边界怎么处理。
internal sealed record FileWalkOptions
{
	public IgnoreFlavor Flavor { get; init; } = IgnoreFlavor.Fd;

	public bool SkipHidden { get; init; }

	public bool IncludeGitDirectory { get; init; }

	public string FlavorIgnoreFileName => Flavor == IgnoreFlavor.Fd ? ".fdignore" : ".rgignore";
}

/// 遍历结果。带上类型，调用方不必为每条再 stat 一次。
internal readonly record struct FileEntry(string Path, bool IsDirectory);

/// 供 <c>grep</c> 与 <c>find</c> 遍历时使用的 <c>.gitignore</c> 匹配器。pi 把忽略规则交给 ripgrep/fd，
/// 本端口在进程内实现常用子集，因而不需要外部可执行文件。
/// <para>
/// 一个实例只对应一个基准目录（逐目录压栈的自然形态）。规则先按「纯名字 / 扩展名 / 其余」
/// 分流，前两类走字典，其余合并成一条正则：vendored 仓库里 400 行的 .gitignore 也只需
/// 每层一次字典查找加一次正则，而不是 400 次逐条匹配。
/// </para>
public sealed class GitIgnore
{
	private const int FileScope = 1;
	private const int DirectoryScope = 2;

	private readonly List<IgnoreRule> _rules = [];
	private readonly string _baseDirectory;
	private RuleMatcher? _matcher;

	private GitIgnore(string baseDirectory) => _baseDirectory = baseDirectory;

	public static GitIgnore Empty { get; } = new(string.Empty);

	public int Count => _rules.Count;

	/// 基准目录的长度：遍历时用它把绝对路径切成相对路径，避免每条目一次 <c>GetRelativePath</c>。
	internal int BaseLength => _baseDirectory.Length;

	internal string BaseDirectory => _baseDirectory;


	public static GitIgnore FromDirectory(string directory) =>
		FromFile(Path.Combine(directory, ".gitignore"), directory);

	public static GitIgnore FromFile(string path, string baseDirectory) =>
		FromFile(path, baseDirectory, IgnoreSource.LocalGit);

	internal static GitIgnore FromFile(string path, string baseDirectory, IgnoreSource source)
	{
		if (!File.Exists(path))
		{
			return new GitIgnore(baseDirectory);
		}

		return FromExistingFile(path, baseDirectory, source);
	}

	/// 调用方已经从目录列举里知道文件存在（省掉每条目一次 stat）。
	internal static GitIgnore FromExistingFile(string path, string baseDirectory, IgnoreSource source)
	{
		try
		{
			return Parse(File.ReadAllLines(path), baseDirectory, source);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new GitIgnore(baseDirectory);
		}
	}

	/// 搜索起点在子目录时，ripgrep/fd 仍会应用父目录的 ignore 文件。
	/// VCS 规则按仓库根截断（仓库根之上的 .gitignore 不生效），非 VCS 规则
	/// （.ignore / .rgignore / .fdignore）不受仓库边界限制；fd 的 --no-require-git
	/// 还会在非仓库目录里读父级 .gitignore 与全局 git 忽略。
	internal static List<GitIgnore> ForWalkRoot(string root, FileWalkOptions options)
	{
		var layers = new List<GitIgnore>();
		var directory = Directory.Exists(root)
			? Path.GetFullPath(root)
			: Path.GetDirectoryName(Path.GetFullPath(root)) ?? Path.GetFullPath(root);
		if (directory.Length == 0)
		{
			return layers;
		}

		// 非 VCS 规则从文件系统根一路读下来；顺序由低到高，子目录规则后读。
		for (var current = Path.GetDirectoryName(directory); current is { Length: > 0 }; current = Path.GetDirectoryName(current))
		{
			Add(layers, FromFile(Path.Combine(current, ".ignore"), current, IgnoreSource.Always));
			Add(layers, FromFile(Path.Combine(current, options.FlavorIgnoreFileName), current, IgnoreSource.Always));
		}

		if (options.Flavor == IgnoreFlavor.Fd)
		{
			Add(layers, FdGlobal(directory));
		}

		var gitRoot = FindGitRoot(directory);
		// fd 用 --no-require-git：不在仓库里也读 .gitignore / 全局 git 忽略，且不按仓库边界重置。
		var vcsActive = gitRoot is not null || options.Flavor == IgnoreFlavor.Fd;
		if (!vcsActive)
		{
			return layers;
		}

		var baseDirectory = gitRoot ?? directory;
		layers.AddRange(GlobalLayers(baseDirectory));

		if (gitRoot is not null)
		{
			// 仓库根之上的 .gitignore 不生效；仓库根自己的规则留给 Walker 读。
			var chain = new List<string>();
			if (!SamePath(directory, gitRoot))
			{
				for (var current = Path.GetDirectoryName(directory); current is { Length: > 0 }; current = Path.GetDirectoryName(current))
				{
					chain.Add(current);
					if (SamePath(current, gitRoot))
					{
						break;
					}
				}
			}

			if (chain.Count > 0)
			{
				Add(layers, FromFile(Path.Combine(gitRoot, ".git", "info", "exclude"), gitRoot, IgnoreSource.LocalGit));
				for (var index = chain.Count - 1; index >= 0; index--)
				{
					Add(layers, FromFile(Path.Combine(chain[index], ".gitignore"), chain[index], IgnoreSource.LocalGit));
				}
			}
		}
		else
		{
			for (var current = Path.GetDirectoryName(directory); current is { Length: > 0 }; current = Path.GetDirectoryName(current))
			{
				Add(layers, FromFile(Path.Combine(current, ".gitignore"), current, IgnoreSource.LocalGit));
			}
		}

		return layers;

		static void Add(List<GitIgnore> layers, GitIgnore ignore)
		{
			if (ignore.Count > 0)
			{
				layers.Add(ignore);
			}
		}
	}

	/// git 的全局忽略源：<c>$XDG_CONFIG_HOME/git/ignore</c>（缺省 <c>~/.config/git/ignore</c>）
	/// 与 <c>core.excludesFile</c> 指到的文件。ripgrep 与 fd 都读它们，进程内实现也要读，
	/// 否则「本机早就忽略掉的构建产物」会重新出现在 grep/find 的结果里。
	public static GitIgnore Global(string baseDirectory) => Merge(GlobalLayers(baseDirectory), baseDirectory);

	internal static List<GitIgnore> GlobalLayers(string baseDirectory)
	{
		var layers = new List<GitIgnore>();
		foreach (var path in GlobalFiles())
		{
			var ignore = FromFile(path, baseDirectory, IgnoreSource.GlobalGit);
			if (ignore.Count > 0)
			{
				layers.Add(ignore);
			}
		}

		return layers;
	}

	/// fd 自己的全局忽略文件：<c>$XDG_CONFIG_HOME/fd/ignore</c>（缺省 <c>~/.config/fd/ignore</c>）。
	internal static GitIgnore FdGlobal(string baseDirectory)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (home.Length == 0)
		{
			return new GitIgnore(baseDirectory);
		}

		var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var xdg = string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome;
		return FromFile(Path.Combine(xdg, "fd", "ignore"), baseDirectory, IgnoreSource.Always);
	}

	private static GitIgnore Merge(List<GitIgnore> layers, string baseDirectory)
	{
		if (layers.Count == 1)
		{
			return layers[0];
		}

		if (layers.Count == 0)
		{
			return new GitIgnore(baseDirectory);
		}

		var merged = new GitIgnore(baseDirectory);
		foreach (var layer in layers)
		{
			merged.AddRange(layer);
		}

		return merged;
	}

	/// 嵌套仓库边界：丢掉从祖先仓库继承的本地 VCS 规则，保留全局 git 忽略与非 VCS 规则。
	internal GitIgnore WithoutLocalGit()
	{
		if (!_rules.Any(static rule => rule.Source == IgnoreSource.LocalGit))
		{
			return this;
		}

		var copy = new GitIgnore(_baseDirectory);
		copy._rules.AddRange(_rules.Where(static rule => rule.Source != IgnoreSource.LocalGit));
		foreach (var rule in copy._rules)
		{
			rule.BaseDirectory = copy._baseDirectory;
		}

		return copy;
	}

	private static IEnumerable<string> GlobalFiles()
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (home.Length == 0)
		{
			yield break;
		}

		var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var xdg = string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome;
		yield return Path.Combine(xdg, "git", "ignore");

		foreach (var configFile in new[] { Path.Combine(xdg, "git", "config"), Path.Combine(home, ".gitconfig") })
		{
			if (ReadCoreExcludesFile(configFile) is { } excludes)
			{
				yield return excludes;
			}
		}
	}

	/// 只读 <c>[core] excludesfile</c> 一行：不必为了一个路径去实现整套 git 配置语法。
	private static string? ReadCoreExcludesFile(string configFile)
	{
		if (!File.Exists(configFile))
		{
			return null;
		}

		try
		{
			var section = string.Empty;
			foreach (var raw in File.ReadLines(configFile))
			{
				var line = raw.Trim();
				if (line.Length == 0 || line[0] is '#' or ';')
				{
					continue;
				}

				if (line[0] == '[' && line[^1] == ']')
				{
					section = line[1..^1].Trim();
					continue;
				}

				var separator = line.IndexOf('=');
				if (separator <= 0 || !string.Equals(section, "core", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var key = line[..separator].Trim();
				if (!string.Equals(key, "excludesfile", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var value = line[(separator + 1)..].Trim().Trim('"');
				if (value.StartsWith('~'))
				{
					var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
					value = value.Length > 1 && value[1] == '/'
						? Path.Combine(home, value[2..])
						: home;
				}

				// 相对路径的语义随 cwd 变，这里只接受绝对路径（git 文档也不推荐相对路径）。
				return Path.IsPathRooted(value) ? value : null;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 读不到全局配置就当没有：忽略规则缺失只会多列文件，不能因此让工具失败。
		}

		return null;
	}

	internal static string? FindGitRoot(string directory)
	{
		for (var current = directory; current is { Length: > 0 }; current = Path.GetDirectoryName(current))
		{
			if (HasGitMarker(current))
			{
				return current;
			}
		}

		return null;
	}

	/// 仓库标记既可能是目录（普通仓库），也可能是文件（worktree / submodule）。
	internal static bool HasGitMarker(string directory)
	{
		var marker = Path.Combine(directory, ".git");
		return Directory.Exists(marker) || File.Exists(marker);
	}

	internal static bool SamePath(string left, string right) =>
		string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	public static GitIgnore Parse(IEnumerable<string> lines, string baseDirectory) =>
		Parse(lines, baseDirectory, IgnoreSource.LocalGit);

	internal static GitIgnore Parse(IEnumerable<string> lines, string baseDirectory, IgnoreSource source)
	{
		var ignore = new GitIgnore(baseDirectory);
		foreach (var raw in lines)
		{
			var rule = Compile(raw, baseDirectory, source);
			if (rule is not null)
			{
				ignore._rules.Add(rule);
			}
		}

		return ignore;
	}

	/// 只能合并同一基准目录的层：匹配时整层共用一个相对路径切片。
	public void AddRange(GitIgnore other)
	{
		if (ReferenceEquals(other, this) || other._rules.Count == 0)
		{
			return;
		}

		if (!string.Equals(other._baseDirectory, _baseDirectory, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Cannot merge ignore layers with different base directories.");
		}

		_rules.AddRange(other._rules);
		_matcher = null;
	}

	public bool IsIgnored(string absolutePath, bool isDirectory)
	{
		if (_rules.Count == 0)
		{
			return false;
		}

		var relative = Path.GetRelativePath(_baseDirectory, absolutePath);
		if (relative.StartsWith("..", StringComparison.Ordinal))
		{
			return false;
		}

		if (OperatingSystem.IsWindows())
		{
			relative = relative.Replace('\\', '/');
		}

		var name = relative.AsSpan();
		var slash = name.LastIndexOf('/');
		if (slash >= 0)
		{
			name = name[(slash + 1)..];
		}

		return Apply(relative, name, isDirectory, ignored: false);
	}

	/// 把本层规则叠加到已有判定上（跨层时后读的规则覆盖先读的，不能每层各自返回 true 后短路）。
	/// <paramref name="relative"/> 是相对本层基准目录的路径，调用方按前缀切片给出，不额外分配。
	internal bool Apply(ReadOnlySpan<char> relative, ReadOnlySpan<char> name, bool isDirectory, bool ignored)
	{
		if (_rules.Count == 0)
		{
			return ignored;
		}

		var matcher = _matcher ??= BuildMatcher();
		if (matcher.HasPositive && MatchesPositive(matcher, relative, name, isDirectory))
		{
			return ApplyOrdered(relative, name, isDirectory, ignored);
		}

		// 正向没命中时，只有「前面已被忽略、本层又可能放回来」才需要逐条求值。
		if (!ignored || matcher.Negatives is null || !MatchesNegatives(matcher, relative, name, isDirectory))
		{
			return ignored;
		}

		return ApplyOrdered(relative, name, isDirectory, ignored);
	}

	private static bool MatchesPositive(
		RuleMatcher matcher,
		ReadOnlySpan<char> relative,
		ReadOnlySpan<char> name,
		bool isDirectory)
	{
		var scope = isDirectory ? DirectoryScope : FileScope;
		if (matcher.Names is not null
			&& matcher.NamesLookup.TryGetValue(name, out var nameScope)
			&& (nameScope & scope) != 0)
		{
			return true;
		}

		if (matcher.Suffixes is not null)
		{
			for (var start = 0; start < name.Length; start++)
			{
				if (name[start] == '.'
					&& matcher.SuffixesLookup.TryGetValue(name[start..], out var suffixScope)
					&& (suffixScope & scope) != 0)
				{
					return true;
				}
			}
		}

		if (matcher.NameGlobs is { } nameGlobs)
		{
			foreach (var (pattern, patternScope) in nameGlobs)
			{
				if ((patternScope & scope) != 0 && pattern.MatchesName(name))
				{
					return true;
				}
			}
		}

		if (matcher.PathByHead is not null || matcher.PathOpen is not null)
		{
			var separator = relative.IndexOf('/');
			var head = separator < 0 ? relative : relative[..separator];
			if (matcher.PathByHead is { } byHead && byHead.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(head, out var bucket))
			{
				foreach (var (pattern, patternScope) in bucket)
				{
					if ((patternScope & scope) != 0 && pattern.MatchesPath(relative))
					{
						return true;
					}
				}
			}

			if (matcher.PathOpen is { } open)
			{
				foreach (var (pattern, patternScope) in open)
				{
					if ((patternScope & scope) != 0 && pattern.MatchesPath(relative))
					{
						return true;
					}
				}
			}
		}

		return false;
	}

	private static bool MatchesNegatives(
		RuleMatcher matcher,
		ReadOnlySpan<char> relative,
		ReadOnlySpan<char> name,
		bool isDirectory)
	{
		foreach (var rule in matcher.Negatives!)
		{
			if (rule.DirectoryOnly && !isDirectory)
			{
				continue;
			}

			if (rule.Matches(relative, name))
			{
				return true;
			}
		}

		return false;
	}

	private bool ApplyOrdered(ReadOnlySpan<char> relative, ReadOnlySpan<char> name, bool isDirectory, bool ignored)
	{
		foreach (var rule in _rules)
		{
			if (rule.DirectoryOnly && !isDirectory)
			{
				continue;
			}

			if (rule.Matches(relative, name))
			{
				ignored = !rule.Negated;
			}
		}

		return ignored;
	}

	private RuleMatcher BuildMatcher()
	{
		var matcher = new RuleMatcher();
		foreach (var rule in _rules)
		{
			if (rule.Negated)
			{
				(matcher.Negatives ??= []).Add(rule);
				continue;
			}

			matcher.HasPositive = true;
			var scope = rule.DirectoryOnly ? DirectoryScope : FileScope | DirectoryScope;
			if (rule.Literal is { } literal)
			{
				matcher.Names ??= new Dictionary<string, int>(StringComparer.Ordinal);
				matcher.Names[literal] = matcher.Names.GetValueOrDefault(literal) | scope;
			}
			else if (rule.Suffix is { } suffix)
			{
				matcher.Suffixes ??= new Dictionary<string, int>(StringComparer.Ordinal);
				matcher.Suffixes[suffix] = matcher.Suffixes.GetValueOrDefault(suffix) | scope;
			}
			else if (rule.Path is { } path)
			{
				// `**/x` 与「任意深度的 x」等价：字面量与 `*.ext` 直接进字典，只有带通配的才逐条试。
				if (path.AnyDepthSegment is { } segment && TryFastPath(segment, out var innerLiteral, out var innerSuffix))
				{
					if (innerLiteral is not null)
					{
						matcher.Names ??= new Dictionary<string, int>(StringComparer.Ordinal);
						matcher.Names[innerLiteral] = matcher.Names.GetValueOrDefault(innerLiteral) | scope;
					}
					else
					{
						matcher.Suffixes ??= new Dictionary<string, int>(StringComparer.Ordinal);
						matcher.Suffixes[innerSuffix!] = matcher.Suffixes.GetValueOrDefault(innerSuffix!) | scope;
					}
				}
				else if (path.AnyDepthSegment is not null)
				{
					(matcher.NameGlobs ??= []).Add((path, scope));
				}
				else
				{
					matcher.AddPath(path, scope);
				}
			}
			else if (rule.NameGlob is { } nameGlob)
			{
				(matcher.NameGlobs ??= []).Add((nameGlob, scope));
			}
		}

		matcher.NamesLookup = matcher.Names is null ? default : matcher.Names.GetAlternateLookup<ReadOnlySpan<char>>();
		matcher.SuffixesLookup = matcher.Suffixes is null ? default : matcher.Suffixes.GetAlternateLookup<ReadOnlySpan<char>>();
		return matcher;
	}

	/// 规则索引。纯名字与扩展名走字典；路径规则按首段分桶，
	/// 这样一次条目匹配只需要 1–2 次字典查找加上少量段匹配，跟规则总数无关。
	private sealed class RuleMatcher
	{
		public Dictionary<string, int>? Names;

		public Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> NamesLookup;

		public Dictionary<string, int>? Suffixes;

		public Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> SuffixesLookup;

		public List<(SegmentPattern Pattern, int Scope)>? NameGlobs;

		/// 首段是字面量的路径规则（`src/bin`、`obj/` 这类）。
		public Dictionary<string, List<(SegmentPattern Pattern, int Scope)>>? PathByHead;


		/// 首段是 '**' 或带通配的路径规则，只能逐条试。
		public List<(SegmentPattern Pattern, int Scope)>? PathOpen;

		public List<IgnoreRule>? Negatives;

		public bool HasPositive;

		public void AddPath(SegmentPattern pattern, int scope)
		{
			if (pattern.Head is { } head)
			{
				PathByHead ??= new Dictionary<string, List<(SegmentPattern, int)>>(StringComparer.Ordinal);
				if (!PathByHead.TryGetValue(head, out var bucket))
				{
					bucket = [];
					PathByHead[head] = bucket;
				}

				bucket.Add((pattern, scope));
				return;
			}

			(PathOpen ??= []).Add((pattern, scope));
		}
	}

	private static IgnoreRule? Compile(string raw, string baseDirectory, IgnoreSource source)
	{
		var line = raw;
		if (line.Length == 0 || line[0] == '#')
		{
			return null;
		}

		// 除非被转义，否则忽略行尾空格。
		var end = line.Length;
		while (end > 0 && line[end - 1] == ' ' && (end < 2 || line[end - 2] != '\\'))
		{
			end--;
		}

		line = line[..end];
		if (line.Length == 0)
		{
			return null;
		}

		var negated = line[0] == '!';
		if (negated)
		{
			line = line[1..];
		}

		if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
		{
			line = line[1..];
		}

		var directoryOnly = line.EndsWith('/');
		line = line.TrimEnd('/');

		if (line.Length == 0)
		{
			return null;
		}

		var anchored = line.StartsWith('/') || line.Contains('/');
		var pattern = line.TrimStart('/');
		// 绝大多数规则是 `name`、`dir/`、`*.ext`：直接比文件名，别为每个条目跑一次通配匹配。
		if (!anchored && TryFastPath(pattern, out var literal, out var suffix))
		{
			return new IgnoreRule(baseDirectory)
			{
				Negated = negated,
				DirectoryOnly = directoryOnly,
				Source = source,
				Literal = literal,
				Suffix = suffix,
			};
		}

		return new IgnoreRule(baseDirectory)
		{
			Negated = negated,
			DirectoryOnly = directoryOnly,
			Source = source,
			Path = anchored ? SegmentPattern.ParsePath(pattern) : null,
			NameGlob = anchored ? null : SegmentPattern.ParseName(pattern),
		};
	}

	/// 无通配的普通名字，或只有前缀 `*.` 的扩展名规则。返回 true 时 <paramref name="literal"/>
	/// 或 <paramref name="suffix"/> 恰好有一个非空。
	private static readonly SearchValues<char> Wildcards =
		SearchValues.Create("*?[\\");

	private static bool TryFastPath(string pattern, out string? literal, out string? suffix)
	{
		literal = null;
		suffix = null;
		if (pattern.Length == 0 || pattern.Contains('/'))
		{
			return false;
		}

		if (pattern[0] == '*' && pattern.Length > 1 && pattern[1] == '.')
		{
			var rest = pattern[1..];
			if (rest.AsSpan().IndexOfAny(Wildcards) < 0)
			{
				suffix = rest;
				return true;
			}

			return false;
		}

		if (pattern.AsSpan().IndexOfAny(Wildcards) < 0)
		{
			literal = pattern;
			return true;
		}

		return false;
	}

	/// <summary>一条 glob 规则编译后的形态：按 '/' 切成段，段内支持 <c>*</c>、<c>?</c>、<c>[...]</c>、<c>\</c>。</summary>
	/// <remarks>
	/// 以前把规则翻成正则再匹配，2 万文件的仓库里光是合并正则的编译就占 400 ms，
	/// 每条目再走一次正则还要几微秒。glob 段匹配是纯字符比较，命中与否不依赖规则总数。
	/// </remarks>
	private sealed class SegmentPattern
	{
		/// Windows 与上一个实现（正则 + IgnoreCase）保持一致：忽略规则不分大小写。
		private static readonly bool IgnoreCase = OperatingSystem.IsWindows();

		private static bool CharEquals(char left, char right) =>
			IgnoreCase ? char.ToUpperInvariant(left) == char.ToUpperInvariant(right) : left == right;

		/// '**' 段用 null 表示（跨任意多段）。
		private readonly string?[] _segments;

		private SegmentPattern(string?[] segments, string? head) => (_segments, Head) = (segments, head);

		/// 首段是字面量时的值，供按首段分桶；'**' 或有通配时是 null。
		public string? Head { get; }

		/// `**/x` 形态等价于「任意深度的 x」，可以退化成按名字匹配。
		public string? AnyDepthSegment =>
			_segments is [null, { } segment] ? segment : null;

		public static SegmentPattern ParseName(string pattern) =>
			new([pattern], null);

		public static SegmentPattern ParsePath(string pattern)
		{
			var segments = new List<string?>();
			var start = 0;
			while (start <= pattern.Length)
			{
				var slash = pattern.IndexOf('/', start);
				var end = slash < 0 ? pattern.Length : slash;
				var segment = pattern[start..end];
				if (segment.Length == 0)
				{
					// 连续 '/' 或末尾 '/'：gitignore 里没有意义，忽略。
				}
				else if (segment == "**")
				{
					segments.Add(null);
				}
				else
				{
					segments.Add(segment);
				}

				if (slash < 0)
				{
					break;
				}

				start = slash + 1;
			}

			if (segments.Count == 0)
			{
				segments.Add("**");
			}

			var array = segments.ToArray();
			var head = array[0] is { } first && first.AsSpan().IndexOfAny(WildcardCharacters) < 0 ? first : null;
			return new SegmentPattern(array, head);
		}

		public bool MatchesName(ReadOnlySpan<char> name) =>
			MatchSegment(_segments[^1] ?? "*", name);

		public bool MatchesPath(ReadOnlySpan<char> relative) => MatchFrom(0, relative, exhausted: false);

		/// <param name="exhausted">路径已经没有下一段了（与「下一段是空串」不同，后者不存在）。</param>
		private bool MatchFrom(int segmentIndex, ReadOnlySpan<char> path, bool exhausted)
		{
			if (segmentIndex == _segments.Length)
			{
				return exhausted;
			}

			var segment = _segments[segmentIndex];
			if (segment is null)
			{
				if (segmentIndex == _segments.Length - 1)
				{
					// 末尾的 '**' 与 git 一致：匹配 a 之下的一切，不含 a 自己。
					return !exhausted;
				}

				// 中间的 '**'：先试「吃 0 段」，再逐段尝试。
				if (MatchFrom(segmentIndex + 1, path, exhausted))
				{
					return true;
				}

				var rest = path;
				while (!rest.IsEmpty)
				{
					var slash = rest.IndexOf('/');
					if (slash < 0)
					{
						return false;
					}

					rest = rest[(slash + 1)..];
					if (MatchFrom(segmentIndex + 1, rest, exhausted: false))
					{
						return true;
					}
				}

				return false;
			}

			if (exhausted)
			{
				return false;
			}

			var separator = path.IndexOf('/');
			if (separator < 0)
			{
				return MatchSegment(segment, path) && MatchFrom(segmentIndex + 1, default, exhausted: true);
			}

			return MatchSegment(segment, path[..separator]) && MatchFrom(segmentIndex + 1, path[(separator + 1)..], exhausted: false);
		}

		private static readonly SearchValues<char> WildcardCharacters = SearchValues.Create("*?[\\");

		/// 段内 glob：'*' 不跨 '/'（段本身不含 '/'），'?' 单字符，'[...]' 字符类。
		private static bool MatchSegment(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
		{
			var p = 0;
			var t = 0;
			var starPattern = -1;
			var starText = 0;
			while (t < text.Length)
			{
				if (p < pattern.Length && pattern[p] == '*')
				{
					starPattern = ++p;
					starText = t;
					continue;
				}

				if (p < pattern.Length && MatchToken(pattern, ref p, text[t]))
				{
					t++;
					continue;
				}

				if (starPattern >= 0)
				{
					p = starPattern;
					t = ++starText;
					continue;
				}

				return false;
			}

			while (p < pattern.Length && pattern[p] == '*')
			{
				p++;
			}

			return p == pattern.Length;
		}

		private static bool MatchToken(ReadOnlySpan<char> pattern, ref int p, char c)
		{
			switch (pattern[p])
			{
				case '?':
					p++;
					return true;
				case '[':
					return MatchClass(pattern, ref p, c);
				case '\\':
					if (p + 1 < pattern.Length)
					{
						var escaped = pattern[p + 1];
						p += 2;
						return CharEquals(escaped, c);
					}

					break;
			}

			var literal = pattern[p];
			p++;
			return CharEquals(literal, c);
		}

		private static bool MatchClass(ReadOnlySpan<char> pattern, ref int p, char c)
		{
			var index = p + 1;
			var negated = false;
			if (index < pattern.Length && (pattern[index] == '!' || pattern[index] == '^'))
			{
				negated = true;
				index++;
			}

			var matched = false;
			var closed = false;
			while (index < pattern.Length)
			{
				var current = pattern[index];
				if (current == ']' && index > p + 1)
				{
					closed = true;
					index++;
					break;
				}

				if (current == '\\' && index + 1 < pattern.Length)
				{
					current = pattern[index + 1];
					index += 2;
				}
				else
				{
					index++;
				}

				// 区间：a-z
				if (index + 1 < pattern.Length && pattern[index] == '-' && pattern[index + 1] != ']')
				{
					var high = pattern[index + 1];
					index += 2;
					if (c >= current && c <= high)
					{
						matched = true;
					}
				}
				else if (CharEquals(current, c))
				{
					matched = true;
				}
			}

			if (!closed)
			{
				// 没闭上的 '[' 按字面处理，与 git 一致。
				return false;
			}

			p = index;
			return matched != negated;
		}
	}

	private sealed class IgnoreRule
	{
		private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

		public IgnoreRule(string baseDirectory) => BaseDirectory = baseDirectory;

		/// 合并层会改写它（WithoutLocalGit 重建的是另一个基准目录的层）。
		public string BaseDirectory { get; set; }

		public bool Negated { get; init; }

		public bool DirectoryOnly { get; init; }

		public IgnoreSource Source { get; init; }

		/// 纯名字规则。
		public string? Literal { get; init; }

		/// `*.ext` 规则。
		public string? Suffix { get; init; }

		/// 含 '/' 的规则（相对基准目录锚定）。
		public SegmentPattern? Path { get; init; }

		/// 不含 '/' 但带通配的规则（比文件名）。
		public SegmentPattern? NameGlob { get; init; }

		/// <param name="relative">相对规则基准目录的路径，已统一成 '/' 分隔。</param>
		/// <param name="name">条目自身的名字。</param>
		public bool Matches(ReadOnlySpan<char> relative, ReadOnlySpan<char> name)
		{
			if (Literal is { } literal)
			{
				return name.Equals(literal, Comparison);
			}

			if (Suffix is { } suffix)
			{
				return name.EndsWith(suffix, Comparison);
			}

			if (Path is { } path)
			{
				return path.MatchesPath(relative);
			}

			return NameGlob is { } glob && glob.MatchesName(name);
		}
	}
}

/// 遍历目录：单趟 <see cref="FileSystemEnumerable{TResult}"/> 取名字与类型（Linux 上是 d_type，
/// 不必为每条 stat），忽略规则按前缀切片匹配，不产生中间字符串。
internal static class FileWalker
{
	public static IEnumerable<string> EnumerateFiles(string root, FileWalkOptions options)
	{
		foreach (var entry in Enumerate(root, options, directoriesFirst: false))
		{
			if (!entry.IsDirectory)
			{
				yield return entry.Path;
			}
		}
	}

	public static IEnumerable<FileEntry> EnumerateEntries(string root, FileWalkOptions options) =>
		Enumerate(root, options, directoriesFirst: true);

	private static IEnumerable<FileEntry> Enumerate(string root, FileWalkOptions options, bool directoriesFirst)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}

		var state = CreateState(root, options);
		foreach (var entry in Walk(state.Root, state, directoriesFirst))
		{
			yield return entry;
		}
	}

	private sealed class WalkState
	{
		public required string Root { get; init; }

		public required FileWalkOptions Options { get; init; }

		public List<GitIgnore> Layers { get; } = [];

		/// 当前是否处于某个 git 仓库内（grep 在进入仓库前不读 .gitignore）。
		public bool RepositoryActive { get; set; }

		/// 进入嵌套仓库时是否丢掉祖先仓库的本地规则。
		public bool ResetAtNestedRepository { get; set; }

		public bool GlobalGitLoaded { get; set; }

	}

	private readonly record struct DirectoryEntry(string Name, bool IsDirectory);

	private static WalkState CreateState(string root, FileWalkOptions options)
	{
		var full = Path.GetFullPath(root);
		var gitRoot = GitIgnore.FindGitRoot(full);
		var state = new WalkState
		{
			Root = full,
			Options = options,
			// fd 的 --no-require-git：不在仓库里也按仓库规则读 .gitignore 与全局忽略。
			RepositoryActive = gitRoot is not null || options.Flavor == IgnoreFlavor.Fd,
			// 从非仓库目录开始搜索时，ripgrep 遇到仓库才启用 VCS 规则，且之后仍按仓库边界重置。
			ResetAtNestedRepository = gitRoot is not null || options.Flavor == IgnoreFlavor.Ripgrep,
			GlobalGitLoaded = gitRoot is not null || options.Flavor == IgnoreFlavor.Fd,
		};

		state.Layers.AddRange(GitIgnore.ForWalkRoot(full, options));
		return state;
	}

	private static IEnumerable<FileEntry> Walk(string directory, WalkState state, bool directoriesFirst)
	{
		var previousRepositoryActive = state.RepositoryActive;
		// 深度取在 EnterRepository 之前：它压入的全局 git 规则要随本目录一起弹掉，
		// 否则兄弟目录会带着上一个仓库的基准目录做切片。
		var depth = state.Layers.Count;
		var previousGlobalGitLoaded = state.GlobalGitLoaded;
		var previousLayers = EnterRepository(directory, state);

		try
		{
			var listing = ListDirectory(directory);
			if (listing is null)
			{
				yield break;
			}

			PushLocalLayer(directory, state, listing);

			if (directoriesFirst)
			{
				foreach (var entry in YieldDirectories(directory, listing, state))
				{
					yield return entry;
				}

				foreach (var entry in YieldFiles(directory, listing, state))
				{
					yield return entry;
				}

				foreach (var entry in Descend(directory, listing, state, directoriesFirst))
				{
					yield return entry;
				}
			}
			else
			{
				foreach (var entry in YieldFiles(directory, listing, state))
				{
					yield return entry;
				}

				foreach (var entry in Descend(directory, listing, state, directoriesFirst))
				{
					yield return entry;
				}
			}
		}
		finally
		{
			RestoreLayers(state, depth, previousLayers);

			// 离开仓库后 VCS 规则不再生效（ripgrep 按文件所在仓库判断）。
			state.RepositoryActive = previousRepositoryActive;
			state.GlobalGitLoaded = previousGlobalGitLoaded;
		}
	}

	private static IEnumerable<FileEntry> YieldFiles(string directory, List<DirectoryEntry> listing, WalkState state)
	{
		foreach (var entry in listing)
		{
			if (entry.IsDirectory)
			{
				continue;
			}

			var path = string.Concat(directory, "/", entry.Name);
			if (IsIgnored(state, path, entry.Name, isDirectory: false))
			{
				continue;
			}

			yield return new FileEntry(path, IsDirectory: false);
		}
	}

	private static IEnumerable<FileEntry> YieldDirectories(string directory, List<DirectoryEntry> listing, WalkState state)
	{
		foreach (var entry in listing)
		{
			if (!entry.IsDirectory || !IsCandidate(state, entry.Name))
			{
				continue;
			}

			var path = string.Concat(directory, "/", entry.Name);
			if (IsIgnored(state, path, entry.Name, isDirectory: true))
			{
				continue;
			}

			yield return new FileEntry(path, IsDirectory: true);
		}
	}

	private static IEnumerable<FileEntry> Descend(string directory, List<DirectoryEntry> listing, WalkState state, bool directoriesFirst)
	{
		foreach (var entry in listing)
		{
			if (!entry.IsDirectory || !IsCandidate(state, entry.Name))
			{
				continue;
			}

			var path = string.Concat(directory, "/", entry.Name);
			if (IsIgnored(state, path, entry.Name, isDirectory: true))
			{
				continue;
			}

			// 不跟随符号链接目录：d_type 已把链接标成非目录，这里再挡一次 GetDirectories 语义的旧路径。
			if (IsReparsePoint(path))
			{
				continue;
			}

			foreach (var child in Walk(path, state, directoriesFirst))
			{
				yield return child;
			}
		}
	}

	private static bool IsCandidate(WalkState state, string name)
	{
		if (!state.Options.IncludeGitDirectory && string.Equals(name, ".git", StringComparison.Ordinal))
		{
			return false;
		}

		return !state.Options.SkipHidden || !name.StartsWith('.');
	}

	private static bool IsIgnored(WalkState state, string path, ReadOnlySpan<char> name, bool isDirectory)
	{
		var ignored = false;
		foreach (var layer in state.Layers)
		{
			// 层的基准目录一定是当前目录的祖先：直接切片，省掉每条目一次 GetRelativePath。
			// 前缀比较只是保险，代价是每条目几十次字符比较。
			var baseLength = layer.BaseLength;
			if (path.Length <= baseLength || !path.AsSpan(0, baseLength).SequenceEqual(layer.BaseDirectory))
			{
				continue;
			}

			var relative = path.AsSpan(baseLength + 1);
			ignored = layer.Apply(relative, name, isDirectory, ignored);
		}

		return ignored;
	}

	/// 单趟列举：名字与类型一次到手。返回 null 表示目录读不了（与 rg/fd 一样跳过）。
	private static List<DirectoryEntry>? ListDirectory(string directory)
	{
		var options = new EnumerationOptions
		{
			RecurseSubdirectories = false,
			AttributesToSkip = 0,
			IgnoreInaccessible = true,
			ReturnSpecialDirectories = false,
		};

		var entries = new List<DirectoryEntry>(64);
		try
		{
			foreach (var entry in new FileSystemEnumerable<DirectoryEntry>(
				directory,
				static (ref FileSystemEntry source) => new DirectoryEntry(source.FileName.ToString(), source.IsDirectory),
				options))
			{
				entries.Add(entry);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
		return entries;
	}

	private static void RestoreLayers(WalkState state, int depth, List<GitIgnore>? previousLayers)
	{
		if (previousLayers is not null)
		{
			state.Layers.Clear();
			state.Layers.AddRange(previousLayers);
			return;
		}

		while (state.Layers.Count > depth)
		{
			state.Layers.RemoveAt(state.Layers.Count - 1);
		}
	}

	/// 进入一个仓库根：嵌套仓库先丢掉祖先仓库的本地规则，再按自身规则重新加载。
	/// 返回进入前的整层快照；没有改动共享栈时返回 null，调用方省一次拷贝。
	private static List<GitIgnore>? EnterRepository(string directory, WalkState state)
	{
		if (!GitIgnore.HasGitMarker(directory))
		{
			return null;
		}

		List<GitIgnore>? previousLayers = null;
		if (state.ResetAtNestedRepository && state.RepositoryActive && !GitIgnore.SamePath(directory, state.Root))
		{
			previousLayers = [.. state.Layers];
			for (var index = 0; index < state.Layers.Count; index++)
			{
				state.Layers[index] = state.Layers[index].WithoutLocalGit();
			}

			state.Layers.RemoveAll(static layer => layer.Count == 0);
			state.RepositoryActive = false;
		}

		state.RepositoryActive = true;

		// ripgrep 从非仓库目录进入仓库后才开始读全局 git 忽略；fd 在起点已经读过。
		if (!state.GlobalGitLoaded)
		{
			state.Layers.AddRange(GitIgnore.GlobalLayers(directory));
			state.GlobalGitLoaded = true;
		}

		return previousLayers;
	}

	/// 当前目录自己的规则：info/exclude → .gitignore → .ignore → 方言 ignore 文件（后者覆盖前者）。
	/// 文件是否存在从目录列举里就能看出来，不再逐目录探测。
	private static void PushLocalLayer(string directory, WalkState state, List<DirectoryEntry> listing)
	{
		var hasGitIgnore = false;
		var hasIgnore = false;
		var hasFlavorIgnore = false;
		var hasGitDirectory = false;
		foreach (var entry in listing)
		{
			if (entry.IsDirectory)
			{
				if (string.Equals(entry.Name, ".git", StringComparison.Ordinal))
				{
					hasGitDirectory = true;
				}

				continue;
			}

			if (string.Equals(entry.Name, ".gitignore", StringComparison.Ordinal))
			{
				hasGitIgnore = true;
			}
			else if (string.Equals(entry.Name, ".ignore", StringComparison.Ordinal))
			{
				hasIgnore = true;
			}
			else if (string.Equals(entry.Name, state.Options.FlavorIgnoreFileName, StringComparison.Ordinal))
			{
				hasFlavorIgnore = true;
			}
		}

		if (state.RepositoryActive && hasGitDirectory)
		{
			Add(GitIgnore.FromFile(
				string.Concat(directory, "/.git/info/exclude"), directory, IgnoreSource.LocalGit));
		}

		if (state.RepositoryActive && hasGitIgnore)
		{
			Add(GitIgnore.FromExistingFile(string.Concat(directory, "/.gitignore"), directory, IgnoreSource.LocalGit));
		}

		if (hasIgnore)
		{
			Add(GitIgnore.FromExistingFile(string.Concat(directory, "/.ignore"), directory, IgnoreSource.Always));
		}

		if (hasFlavorIgnore)
		{
			Add(GitIgnore.FromExistingFile(
				string.Concat(directory, "/", state.Options.FlavorIgnoreFileName), directory, IgnoreSource.Always));
		}

		void Add(GitIgnore ignore)
		{
			if (ignore.Count > 0)
			{
				state.Layers.Add(ignore);
			}
		}
	}

	/// 是否是符号链接/重解析点。读不到属性时按「不进入」处理：宁可少扫一点，也不让
	/// 自指软链接把 grep/find 拖进无限递归。Windows 上的可重解析点不只有链接（OneDrive
	/// 占位符也是普通目录），所以拿到 ReparsePoint 还要看 LinkTarget。
	private static bool IsReparsePoint(string path)
	{
		try
		{
			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
			{
				return false;
			}

			return new DirectoryInfo(path).LinkTarget is not null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}
