using System.Text;

namespace PiWeft.CodingAgent.Tools;

public static partial class ToolPaths
{
	public static string ResolveToCwd(string path, string cwd)
	{
		var expanded = Expand(path);
		return Path.IsPathRooted(expanded)
			? Path.GetFullPath(expanded)
			: Path.GetFullPath(Path.Combine(cwd, expanded));
	}

	public static string Expand(string path)
	{
		var value = path;
		if (value.StartsWith('@'))
		{
			value = value[1..];
		}

		value = NormalizeUnicodeSpaces(value);
		value = NormalizeWindowsShellPath(value);

		if (value == "~")
		{
			return HomeDirectory;
		}

		if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
		{
			return Path.Combine(HomeDirectory, value[2..]);
		}

		// pi 的 normalizePath 接受 file:// URL；Path.GetFullPath 会把它当成相对路径。
		if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
			Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
			uri.IsFile)
		{
			return uri.LocalPath;
		}

		return value;
	}

	/// Git Bash / MSYS / WSL 的 /c/foo、/mnt/c/foo、/cygdrive/c/foo 在 Windows 上
	/// 需要转成 C:\foo；pi 的 normalizeWindowsShellPath 同义。
	private static string NormalizeWindowsShellPath(string value)
	{
		if (!OperatingSystem.IsWindows() ||
			!value.StartsWith('/') ||
			value.StartsWith("//", StringComparison.Ordinal) ||
			value.Contains('\\'))
		{
			return value;
		}

		var match = WindowsShellPath().Match(value);
		if (!match.Success)
		{
			return value;
		}

		var drive = char.ToUpperInvariant(match.Groups[1].Value[0]);
		var suffix = match.Groups[2].Value.Replace('/', '\\');
		return $"{drive}:\\{suffix}";
	}

	[System.Text.RegularExpressions.GeneratedRegex(
		"^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?$",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex WindowsShellPath();

	public static string HomeDirectory =>
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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

	public static string RelativeTo(string path, string root)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative.Replace('\\', '/');
	}

	public static void EnsureParentDirectory(string path)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}
	}

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
