using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Resources;

/// <summary>
/// 一个够用的 glob 展开器：支持 pi 包清单里的相对路径、<c>*</c>（段内）与 <c>**</c>（跨段）。
/// 只在包资源解析时使用，不追求完整 glob 语义。
/// </summary>
internal static class PathGlob
{
	/// <summary>
	/// 把 <paramref name="pattern"/> 相对 <paramref name="root"/> 展开成绝对路径列表。
	/// 没有通配符时直接返回该路径（无论是否存在）。
	/// </summary>
	public static IReadOnlyList<string> Expand(string root, string pattern)
	{
		var normalized = pattern.Replace('\\', '/').Trim();
		if (normalized.Length == 0)
		{
			return [];
		}

		if (Path.IsPathRooted(normalized))
		{
			return [Path.GetFullPath(normalized)];
		}

		if (!normalized.Contains('*', StringComparison.Ordinal))
		{
			return [Path.GetFullPath(Path.Combine(root, normalized))];
		}

		var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var results = new List<string>();
		Walk(root, segments, 0, results);

		// 保持稳定顺序，便于测试与界面展示。
		results.Sort(StringComparer.Ordinal);
		return results;
	}

	private static void Walk(string directory, string[] segments, int index, List<string> results)
	{
		if (index == segments.Length)
		{
			if (File.Exists(directory) || Directory.Exists(directory))
			{
				results.Add(Path.GetFullPath(directory));
			}

			return;
		}

		var segment = segments[index];
		if (segment == "**")
		{
			// 跨段：既匹配零段，也匹配任意深度。
			Walk(directory, segments, index + 1, results);

			foreach (var child in SafeEnumerateDirectories(directory))
			{
				Walk(child, segments, index, results);
			}

			return;
		}

		if (!segment.Contains('*', StringComparison.Ordinal))
		{
			Walk(Path.Combine(directory, segment), segments, index + 1, results);
			return;
		}

		var regex = SegmentRegex(segment);
		foreach (var name in SafeEnumerateNames(directory))
		{
			if (regex.IsMatch(name))
			{
				Walk(Path.Combine(directory, name), segments, index + 1, results);
			}
		}
	}

	private static IEnumerable<string> SafeEnumerateDirectories(string directory)
	{
		if (!Directory.Exists(directory))
		{
			yield break;
		}

		IEnumerable<string> entries;
		try
		{
			entries = Directory.EnumerateDirectories(directory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			yield break;
		}

		foreach (var entry in entries)
		{
			yield return entry;
		}
	}

	private static IEnumerable<string> SafeEnumerateNames(string directory)
	{
		if (!Directory.Exists(directory))
		{
			yield break;
		}

		IEnumerable<string> entries;
		try
		{
			entries = Directory.EnumerateFileSystemEntries(directory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			yield break;
		}

		foreach (var entry in entries)
		{
			yield return Path.GetFileName(entry);
		}
	}

	private static Regex SegmentRegex(string segment)
	{
		var builder = new StringBuilder("^");
		foreach (var character in segment)
		{
			if (character == '*')
			{
				builder.Append("[^/]*");
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

		builder.Append('$');
		return new Regex(builder.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
	}
}
