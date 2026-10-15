using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// <summary>文件中的一次定点替换。</summary>
public sealed record EditReplacement(string OldText, string NewText);

/// <summary>实际应用的变化：原始内容与替换后内容，均已归一化为 LF。</summary>
public sealed record AppliedEdits(string BaseContent, string NewContent);

/// <summary>对应 pi 的 <c>edit-diff.ts</c>：BOM 与行尾处理、唯一性检查、统一 diff 补丁。</summary>
public static partial class EditDiff
{
	public static (bool HadBom, string Content) SplitBom(string content) =>
		content.StartsWith('\uFEFF') ? (true, content[1..]) : (false, content);

	public static string DetectLineEnding(string content) => content.Contains("\r\n") ? "\r\n" : "\n";

	public static string NormalizeToLf(string text) =>
		text.Replace("\r\n", "\n").Replace('\r', '\n');

	public static string RestoreLineEndings(string text, string lineEnding) =>
		lineEnding == "\n" ? text : text.Replace("\n", lineEnding);

	/// <summary>用于匹配“空白与文件不同”的文本的归一化方式。</summary>
	public static string NormalizeForFuzzyMatch(string text)
	{
		var normalized = text.Normalize(NormalizationForm.FormKC);
		var lines = normalized.Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			lines[i] = lines[i].TrimEnd();
		}

		var joined = string.Join('\n', lines);
		var builder = new StringBuilder(joined.Length);
		foreach (var c in joined)
		{
			builder.Append(c switch
			{
				'\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
				'\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
				'\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
				'\u00A0' or '\u202F' or '\u205F' or '\u3000' => ' ',
				>= '\u2002' and <= '\u200A' => ' ',
				_ => c,
			});
		}

		return builder.ToString();
	}

	/// <summary>
	/// 应用互不重叠的替换。每个 <c>oldText</c> 都与原始内容匹配，且必须唯一；
	/// 匹配不到时会退化为忽略空白的匹配。
	/// </summary>
	public static AppliedEdits ApplyEdits(string content, IReadOnlyList<EditReplacement> edits, string path)
	{
		if (edits.Count == 0)
		{
			throw new InvalidOperationException($"No edits provided for {path}.");
		}

		var normalized = edits
			.Select(edit => new EditReplacement(NormalizeToLf(edit.OldText), NormalizeToLf(edit.NewText)))
			.ToArray();

		for (var i = 0; i < normalized.Length; i++)
		{
			if (normalized[i].OldText.Length == 0)
			{
				throw new InvalidOperationException(
					normalized.Length == 1
						? $"oldText must not be empty in {path}."
						: $"edits[{i}].oldText must not be empty in {path}.");
			}
		}

		var usedFuzzyMatch = normalized.Any(edit => IndexOf(content, edit.OldText) < 0);
		var baseContent = content;
		var searchContent = usedFuzzyMatch ? NormalizeForFuzzyMatch(content) : content;
		var map = usedFuzzyMatch ? BuildIndexMap(content) : null;

		var matches = new List<(int Index, int Length, int EditIndex, string NewText)>(normalized.Length);
		for (var i = 0; i < normalized.Length; i++)
		{
			var oldText = usedFuzzyMatch ? NormalizeForFuzzyMatch(normalized[i].OldText) : normalized[i].OldText;
			var index = IndexOf(searchContent, oldText);
			if (index < 0)
			{
				throw new InvalidOperationException(
					normalized.Length == 1
						? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
						: $"Could not find edits[{i}] in {path}. The oldText must match exactly including all whitespace and newlines.");
			}

			var occurrences = CountOccurrences(searchContent, oldText);
			if (occurrences > 1)
			{
				throw new InvalidOperationException(
					normalized.Length == 1
						? $"Found {occurrences} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique."
						: $"Found {occurrences} occurrences of edits[{i}] in {path}. Each oldText must be unique. Please provide more context to make it unique.");
			}

			var originalIndex = map is null ? index : map[index];
			var originalEnd = map is null ? index + oldText.Length : map[index + oldText.Length];
			matches.Add((originalIndex, originalEnd - originalIndex, i, normalized[i].NewText));
		}

		matches.Sort(static (a, b) => a.Index.CompareTo(b.Index));
		for (var i = 1; i < matches.Count; i++)
		{
			var previous = matches[i - 1];
			var current = matches[i];
			if (previous.Index + previous.Length > current.Index)
			{
				throw new InvalidOperationException(
					$"edits[{previous.EditIndex}] and edits[{current.EditIndex}] overlap in {path}. Merge them into one edit or target disjoint regions.");
			}
		}

		var builder = new StringBuilder(content.Length);
		var cursor = 0;
		foreach (var match in matches)
		{
			builder.Append(content, cursor, match.Index - cursor);
			builder.Append(match.NewText);
			cursor = match.Index + match.Length;
		}

		builder.Append(content, cursor, content.Length - cursor);
		var newContent = builder.ToString();

		if (string.Equals(baseContent, newContent, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				normalized.Length == 1
					? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
					: $"No changes made to {path}. The replacements produced identical content.");
		}

		return new AppliedEdits(baseContent, newContent);
	}

	/// <summary>新内容中第一处变化的行号（从 1 开始）。</summary>
	public static int? FirstChangedLine(string baseContent, string newContent)
	{
		var before = baseContent.Split('\n');
		var after = newContent.Split('\n');
		var limit = Math.Min(before.Length, after.Length);

		for (var i = 0; i < limit; i++)
		{
			if (!string.Equals(before[i], after[i], StringComparison.Ordinal))
			{
				return i + 1;
			}
		}

		return before.Length == after.Length ? null : limit + 1;
	}

	/// <summary>带三行上下文的标准统一补丁，作为工具的 <c>patch</c> 细节返回。</summary>
	public static string UnifiedPatch(string baseContent, string newContent, string path)
	{
		if (string.Equals(baseContent, newContent, StringComparison.Ordinal))
		{
			return string.Empty;
		}

		var before = SplitKeepingTrailing(baseContent);
		var after = SplitKeepingTrailing(newContent);

		if (before.Length > 20_000 || after.Length > 20_000)
		{
			// 输入过大时退化为整文件替换。
			var header = $"--- a/{path}\n+++ b/{path}\n@@ -1,{before.Length} +1,{after.Length} @@\n";
			var body = new StringBuilder(header);
			foreach (var line in before)
			{
				body.Append('-').Append(line).Append('\n');
			}

			foreach (var line in after)
			{
				body.Append('+').Append(line).Append('\n');
			}

			return body.ToString();
		}

		var edits = DiffLines(before, after);
		var output = new StringBuilder();
		output.Append("--- a/").Append(path).Append('\n');
		output.Append("+++ b/").Append(path).Append('\n');

		var index = 0;
		while (index < edits.Count)
		{
			if (edits[index].Kind == DiffKind.Same)
			{
				index++;
				continue;
			}

			var start = index;
			var end = index;
			var contextAfter = 0;
			for (var i = index; i < edits.Count; i++)
			{
				if (edits[i].Kind == DiffKind.Same)
				{
					contextAfter++;
					if (contextAfter > 6)
					{
						break;
					}
				}
				else
				{
					contextAfter = 0;
					end = i;
				}
			}

			var hunkStart = Math.Max(0, start - 3);
			var hunkEnd = Math.Min(edits.Count - 1, end + 3);

			var oldLines = 0;
			var newLines = 0;
			for (var i = hunkStart; i <= hunkEnd; i++)
			{
				switch (edits[i].Kind)
				{
					case DiffKind.Same:
						oldLines++;
						newLines++;
						break;
					case DiffKind.Remove:
						oldLines++;
						break;
					case DiffKind.Add:
						newLines++;
						break;
				}
			}

			var oldStart = 1;
			for (var i = 0; i < hunkStart; i++)
			{
				if (edits[i].Kind != DiffKind.Add)
				{
					oldStart++;
				}
			}

			var newStart = 1;
			for (var i = 0; i < hunkStart; i++)
			{
				if (edits[i].Kind != DiffKind.Remove)
				{
					newStart++;
				}
			}

			output.Append("@@ -").Append(oldStart).Append(',').Append(oldLines)
				.Append(" +").Append(newStart).Append(',').Append(newLines).Append(" @@\n");

			for (var i = hunkStart; i <= hunkEnd; i++)
			{
				var edit = edits[i];
				output.Append(edit.Kind switch
				{
					DiffKind.Same => ' ',
					DiffKind.Remove => '-',
					_ => '+',
				}).Append(edit.Text).Append('\n');
			}

			index = hunkEnd + 1;
		}

		return output.ToString();
	}

	private enum DiffKind
	{
		Same,
		Remove,
		Add,
	}

	private sealed record DiffLine(DiffKind Kind, string Text);

	private static List<DiffLine> DiffLines(string[] before, string[] after)
	{
		// 经典 LCS 表；调用前已对输入规模做了上限约束。
		var n = before.Length;
		var m = after.Length;
		var lcs = new int[n + 1, m + 1];

		for (var i = n - 1; i >= 0; i--)
		{
			for (var j = m - 1; j >= 0; j--)
			{
				lcs[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
					? lcs[i + 1, j + 1] + 1
					: Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
			}
		}

		var result = new List<DiffLine>(n + m);
		var x = 0;
		var y = 0;
		while (x < n && y < m)
		{
			if (string.Equals(before[x], after[y], StringComparison.Ordinal))
			{
				result.Add(new DiffLine(DiffKind.Same, before[x]));
				x++;
				y++;
			}
			else if (lcs[x + 1, y] >= lcs[x, y + 1])
			{
				result.Add(new DiffLine(DiffKind.Remove, before[x]));
				x++;
			}
			else
			{
				result.Add(new DiffLine(DiffKind.Add, after[y]));
				y++;
			}
		}

		while (x < n)
		{
			result.Add(new DiffLine(DiffKind.Remove, before[x]));
			x++;
		}

		while (y < m)
		{
			result.Add(new DiffLine(DiffKind.Add, after[y]));
			y++;
		}

		return result;
	}

	private static string[] SplitKeepingTrailing(string content)
	{
		var lines = content.Split('\n');
		if (lines.Length > 0 && lines[^1].Length == 0)
		{
			return lines[..^1];
		}

		return lines;
	}

	private static int IndexOf(string haystack, string needle) =>
		haystack.IndexOf(needle, StringComparison.Ordinal);

	private static int CountOccurrences(string haystack, string needle)
	{
		var count = 0;
		var index = 0;
		while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += needle.Length;
		}

		return count;
	}

	/// <summary>
	/// 把归一化后的字符下标映射回原始下标，这样模糊匹配的结果可以作用到原始文本上。
	/// </summary>
	private static int[] BuildIndexMap(string original)
	{
		var map = new List<int>(original.Length + 1);
		var normalized = new StringBuilder(original.Length);
		var index = 0;

		while (index < original.Length)
		{
			var lineStart = index;
			while (index < original.Length && original[index] != '\n')
			{
				index++;
			}

			var line = original.AsSpan(lineStart, index - lineStart);
			var trimmed = line.TrimEnd();

			for (var i = 0; i < trimmed.Length; i++)
			{
				map.Add(lineStart + i);
				normalized.Append(trimmed[i]);
			}

			if (index < original.Length)
			{
				map.Add(index);
				normalized.Append('\n');
				index++;
			}
		}

		map.Add(original.Length);
		return [.. map];
	}
}
