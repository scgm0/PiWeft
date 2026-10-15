using System.Text;

namespace PiWeft.CodingAgent.Tools;

/// <summary>内置工具共用的路径处理，对应 pi 的 <c>path-utils</c> 与路径配置。</summary>
public static class ToolPaths
{
	/// <summary>展开 <c>~</c>、开头的 <c>@</c> 引用与 Unicode 空格，再相对 <paramref name="cwd"/> 解析。</summary>
	public static string ResolveToCwd(string path, string cwd)
	{
		var expanded = Expand(path);
		return Path.IsPathRooted(expanded)
			? Path.GetFullPath(expanded)
			: Path.GetFullPath(Path.Combine(cwd, expanded));
	}

	/// <summary>展开用户给出的路径，但不相对工作目录解析。</summary>
	public static string Expand(string path)
	{
		var value = path;
		if (value.StartsWith('@'))
		{
			value = value[1..];
		}

		value = NormalizeUnicodeSpaces(value);

		if (value == "~")
		{
			return HomeDirectory;
		}

		if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
		{
			return Path.Combine(HomeDirectory, value[2..]);
		}

		return value;
	}

	public static string HomeDirectory =>
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	/// <summary>把各种特殊空格（NBSP、窄 NBSP、表意空格等）替换为 U+0020。</summary>
	public static string NormalizeUnicodeSpaces(string value)
	{
		StringBuilder? builder = null;
		for (var i = 0; i < value.Length; i++)
		{
			var c = value[i];
			var replace = c == '\u00A0' ||
				c == '\u202F' ||
				c == '\u205F' ||
				c == '\u3000' ||
				(c >= '\u2002' && c <= '\u200A');

			if (!replace)
			{
				builder?.Append(c);
				continue;
			}

			builder ??= new StringBuilder(value.Length).Append(value, 0, i);
			builder.Append(' ');
		}

		return builder?.ToString() ?? value;
	}

	/// <summary>输出相对 <paramref name="root"/> 且使用正斜杠的路径，与 ripgrep/fd 的输出一致。</summary>
	public static string RelativeTo(string path, string root)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative.Replace('\\', '/');
	}

	/// <summary>文件路径的父目录不存在时创建它。</summary>
	public static void EnsureParentDirectory(string path)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}
	}

	/// <summary>读取失败时 pi 会重试的四种 macOS 文件名变体（AM/PM 空格、NFD、弯引号）。</summary>
	public static IReadOnlyList<string> ReadVariants(string resolved)
	{
		var variants = new List<string>(4);

		var amPm = System.Text.RegularExpressions.Regex.Replace(
			resolved,
			" (AM|PM)\\.",
			"\u202F$1.",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (amPm != resolved)
		{
			variants.Add(amPm);
		}

		var nfd = resolved.Normalize(NormalizationForm.FormD);
		if (nfd != resolved)
		{
			variants.Add(nfd);
		}

		var curly = resolved.Replace('\'', '\u2019');
		if (curly != resolved)
		{
			variants.Add(curly);
		}

		if (nfd != resolved)
		{
			variants.Add(nfd.Replace('\'', '\u2019'));
		}

		return variants;
	}

	public static string ResolveReadPath(string path, string cwd)
	{
		var resolved = ResolveToCwd(path, cwd);
		if (File.Exists(resolved) || Directory.Exists(resolved))
		{
			return resolved;
		}

		foreach (var variant in ReadVariants(resolved))
		{
			if (File.Exists(variant) || Directory.Exists(variant))
			{
				return variant;
			}
		}

		return resolved;
	}
}
