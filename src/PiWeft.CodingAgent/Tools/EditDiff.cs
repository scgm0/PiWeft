using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

public sealed record EditReplacement(string OldText, string NewText);

public sealed record AppliedEdits(string BaseContent, string NewContent);

public static partial class EditDiff
{
	public static (bool HadBom, string Content) SplitBom(string content) =>
		content.StartsWith('\uFEFF') ? (true, content[1..]) : (false, content);

	public static string DetectLineEnding(string content)
	{
		var crlf = content.IndexOf("\r\n", StringComparison.Ordinal);
		var lf = content.IndexOf('\n');
		if (lf < 0 || crlf < 0)
		{
			return "\n";
		}

		// 与 pi 一致：以文件中第一个换行的样式为准，而不是“出现过 CRLF 就全转 CRLF”。
		return crlf < lf ? "\r\n" : "\n";
	}

	public static string NormalizeToLf(string text) =>
		text.Replace("\r\n", "\n").Replace('\r', '\n');

	public static string RestoreLineEndings(string text, string lineEnding) =>
		lineEnding == "\n" ? text : text.Replace("\n", lineEnding);

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

	/// 应用互不重叠的替换。每个 <c>oldText</c> 都与原始内容匹配，且必须唯一；
	/// 匹配不到时会退化为忽略空白的匹配。
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

		var matches = new List<TextReplacement>(normalized.Length);
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

			matches.Add(new TextReplacement(index, oldText.Length, normalized[i].NewText, i));
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

		// 模糊匹配时，替换发生在归一化视图里；未触碰的行必须从原文件逐字节拷回，
		// 否则 NFKC 的字符展开/收缩会让索引错位（pi 的 applyReplacementsPreservingUnchangedLines）。
		var newContent = usedFuzzyMatch
			? ApplyReplacementsPreservingUnchangedLines(content, searchContent, matches)
			: ApplyReplacements(content, matches, offset: 0);

		if (string.Equals(baseContent, newContent, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				normalized.Length == 1
					? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
					: $"No changes made to {path}. The replacements produced identical content.");
		}

		return new AppliedEdits(baseContent, newContent);
	}

	private sealed record TextReplacement(int Index, int Length, string NewText, int EditIndex);

	private sealed record LineSpan(int Start, int End);

	private sealed class ReplacementGroup(int startLine, int endLine, List<TextReplacement> replacements)
	{
		public int StartLine { get; } = startLine;

		public int EndLine { get; set; } = endLine;

		public List<TextReplacement> Replacements { get; } = replacements;
	}

	private static string ApplyReplacements(string content, IReadOnlyList<TextReplacement> replacements, int offset)
	{
		var result = content;
		for (var i = replacements.Count - 1; i >= 0; i--)
		{
			var replacement = replacements[i];
			var index = replacement.Index - offset;
			result = string.Concat(
				result.AsSpan(0, index),
				replacement.NewText,
				result.AsSpan(index + replacement.Length));
		}

		return result;
	}

	private static string ApplyReplacementsPreservingUnchangedLines(
		string originalContent,
		string baseContent,
		IReadOnlyList<TextReplacement> replacements)
	{
		var originalLines = SplitLinesWithEndings(originalContent);
		var baseLines = GetLineSpans(baseContent);
		if (originalLines.Count != baseLines.Count)
		{
			throw new InvalidOperationException(
				"Cannot preserve unchanged lines because the base content has a different line count.");
		}

		var groups = new List<ReplacementGroup>();
		foreach (var replacement in replacements.OrderBy(static replacement => replacement.Index))
		{
			var range = GetReplacementLineRange(baseLines, replacement);
			if (groups.Count > 0 && range.StartLine < groups[^1].EndLine)
			{
				var current = groups[^1];
				current.Replacements.Add(replacement);
				current.EndLine = Math.Max(current.EndLine, range.EndLine);
				continue;
			}

			groups.Add(new ReplacementGroup(range.StartLine, range.EndLine, [replacement]));
		}

		var builder = new StringBuilder(originalContent.Length);
		var originalLineIndex = 0;
		foreach (var group in groups)
		{
			for (var index = originalLineIndex; index < group.StartLine; index++)
			{
				builder.Append(originalLines[index]);
			}

			var groupStart = baseLines[group.StartLine].Start;
			var groupEnd = baseLines[group.EndLine - 1].End;
			builder.Append(ApplyReplacements(baseContent[groupStart..groupEnd], group.Replacements, groupStart));
			originalLineIndex = group.EndLine;
		}

		for (var index = originalLineIndex; index < originalLines.Count; index++)
		{
			builder.Append(originalLines[index]);
		}

		return builder.ToString();
	}

	private static List<string> SplitLinesWithEndings(string content)
	{
		var lines = new List<string>();
		var start = 0;
		for (var index = 0; index < content.Length; index++)
		{
			if (content[index] != '\n')
			{
				continue;
			}

			lines.Add(content[start..(index + 1)]);
			start = index + 1;
		}

		if (start < content.Length)
		{
			lines.Add(content[start..]);
		}

		return lines;
	}

	private static List<LineSpan> GetLineSpans(string content)
	{
		var spans = new List<LineSpan>();
		var offset = 0;
		foreach (var line in SplitLinesWithEndings(content))
		{
			spans.Add(new LineSpan(offset, offset + line.Length));
			offset += line.Length;
		}

		return spans;
	}

	private static (int StartLine, int EndLine) GetReplacementLineRange(
		IReadOnlyList<LineSpan> lines,
		TextReplacement replacement)
	{
		var replacementStart = replacement.Index;
		var replacementEnd = replacement.Index + replacement.Length;
		var startLine = -1;
		for (var index = 0; index < lines.Count; index++)
		{
			var line = lines[index];
			if (replacementStart >= line.Start && replacementStart < line.End)
			{
				startLine = index;
				break;
			}
		}

		if (startLine < 0)
		{
			throw new InvalidOperationException("Replacement range is outside the base content.");
		}

		var endLine = startLine;
		while (endLine < lines.Count && lines[endLine].End < replacementEnd)
		{
			endLine++;
		}

		if (endLine >= lines.Count)
		{
			throw new InvalidOperationException("Replacement range is outside the base content.");
		}

		return (startLine, endLine + 1);
	}

	/// pi 的 display diff：带新旧行号、最多 4 行上下文。用于 edit.details.diff。
	public static (string Diff, int? FirstChangedLine) DisplayDiff(
		string baseContent,
		string newContent,
		int contextLines = 4)
	{
		var before = SplitKeepingTrailing(baseContent);
		var after = SplitKeepingTrailing(newContent);
		var edits = DiffLines(before, after);
		if (edits.All(static edit => edit.Kind == DiffKind.Same))
		{
			return (string.Empty, null);
		}

		var width = Math.Max(before.Length, after.Length).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
		var output = new List<string>();
		var oldLine = 1;
		var newLine = 1;
		int? firstChangedLine = null;
		var lastWasChange = false;

		for (var index = 0; index < edits.Count; index++)
		{
			var edit = edits[index];
			if (edit.Kind == DiffKind.Same)
			{
				var nextIsChange = index + 1 < edits.Count && edits[index + 1].Kind != DiffKind.Same;
				if (!lastWasChange && !nextIsChange)
				{
					oldLine++;
					newLine++;
					continue;
				}

				var run = new List<string>();
				var runIndex = index;
				while (runIndex < edits.Count && edits[runIndex].Kind == DiffKind.Same)
				{
					run.Add(edits[runIndex].Text);
					runIndex++;
				}

				void EmitSame(string line)
				{
					output.Add($" {oldLine.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width)} {line}");
					oldLine++;
					newLine++;
				}

				void SkipSame(int count)
				{
					oldLine += count;
					newLine += count;
				}

				if (lastWasChange && nextIsChange)
				{
					if (run.Count <= contextLines * 2)
					{
						foreach (var line in run) EmitSame(line);
					}
					else
					{
						foreach (var line in run.Take(contextLines)) EmitSame(line);
						output.Add($" {"".PadLeft(width)} ...");
						SkipSame(run.Count - contextLines * 2);
						foreach (var line in run.TakeLast(contextLines)) EmitSame(line);
					}
				}
				else if (lastWasChange)
				{
					foreach (var line in run.Take(contextLines)) EmitSame(line);
					SkipSame(Math.Max(0, run.Count - contextLines));
				}
				else if (nextIsChange)
				{
					SkipSame(Math.Max(0, run.Count - contextLines));
					foreach (var line in run.TakeLast(contextLines)) EmitSame(line);
				}
				else
				{
					SkipSame(run.Count);
				}

				lastWasChange = false;
				index = runIndex - 1;
				continue;
			}

			firstChangedLine ??= newLine;
			if (edit.Kind == DiffKind.Remove)
			{
				output.Add($"-{oldLine.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width)} {edit.Text}");
				oldLine++;
			}
			else
			{
				output.Add($"+{newLine.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width)} {edit.Text}");
				newLine++;
			}

			lastWasChange = true;
		}

		return (string.Join("\n", output), firstChangedLine);
	}

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
			var header = $"--- {path}\n+++ {path}\n@@ -1,{before.Length} +1,{after.Length} @@\n";
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
		output.Append("--- ").Append(path).Append('\n');
		output.Append("+++ ").Append(path).Append('\n');

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
					if (contextAfter > 8)
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

			var hunkStart = Math.Max(0, start - 4);
			var hunkEnd = Math.Min(edits.Count - 1, end + 4);

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
}
