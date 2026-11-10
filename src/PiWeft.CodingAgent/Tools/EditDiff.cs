using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

public sealed record EditReplacement(string OldText, string NewText);

public sealed record AppliedEdits(string BaseContent, string NewContent, string FinalContent);

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

		// 与 pi 一致：以文件中第一个换行的样式为准，不是「出现过 CRLF 就全转 CRLF」。
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
	public static AppliedEdits ApplyEdits(string content, IReadOnlyList<EditReplacement> edits, string path) =>
		ApplyEdits(content, NormalizeToLf(content), edits, path);

	/// <param name="originalContent">未归一化换行的原文，用来保留未被编辑行的 CRLF/LF/CR。</param>
	/// <param name="content">LF 归一化后的内容，替换索引与模糊匹配都在它上面计算。</param>
	public static AppliedEdits ApplyEdits(
		string originalContent,
		string content,
		IReadOnlyList<EditReplacement> edits,
		string path)
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
		var normalizedNewContent = usedFuzzyMatch
			? ApplyReplacementsPreservingUnchangedLines(content, searchContent, matches)
			: ApplyReplacements(content, matches, offset: 0);
		var finalContent = usedFuzzyMatch
			? normalizedNewContent
			: ApplyReplacementsToOriginal(originalContent, matches);

		if (string.Equals(baseContent, normalizedNewContent, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				normalized.Length == 1
					? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
					: $"No changes made to {path}. The replacements produced identical content.");
		}

		return new AppliedEdits(baseContent, normalizedNewContent, finalContent);
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

	/// 把归一化坐标里的替换映射回原文件，保留未触碰行的原始换行；新文本使用插入点的行尾。
	private static string ApplyReplacementsToOriginal(string originalContent, IReadOnlyList<TextReplacement> replacements)
	{
		var crlfBefore = 0;
		var nextCrlf = originalContent.IndexOf("\r\n", StringComparison.Ordinal);
		int ToOriginal(int offset)
		{
			while (nextCrlf != -1 && nextCrlf - crlfBefore < offset)
			{
				crlfBefore++;
				nextCrlf = originalContent.IndexOf("\r\n", nextCrlf + 2, StringComparison.Ordinal);
			}

			return offset + crlfBefore;
		}

		var builder = new StringBuilder(originalContent.Length);
		var endsWithCr = false;
		void Push(string part)
		{
			if (part.Length == 0)
			{
				return;
			}

			// 接缝左侧是单独的 CR、右侧以 LF 开头时，直接拼接会合成 CRLF。补一个 LF 保持两段行尾。
			if (endsWithCr && part[0] == '\n')
			{
				builder.Append('\n');
			}

			builder.Append(part);
			endsWithCr = part[^1] == '\r';
		}

		var position = 0;
		foreach (var replacement in replacements)
		{
			var start = ToOriginal(replacement.Index);
			Push(originalContent[position..start]);
			var ending = GetLineEndingAt(originalContent, start);
			Push(ending == "\n" ? replacement.NewText : replacement.NewText.Replace("\n", ending, StringComparison.Ordinal));
			position = ToOriginal(replacement.Index + replacement.Length);
		}

		Push(originalContent[position..]);
		return builder.ToString();
	}

	private static string GetLineEndingAt(string content, int offset)
	{
		if (content.Length == 0)
		{
			return "\n";
		}

		var searchFrom = Math.Min(offset, content.Length - 1);
		var lineFeed = content.IndexOf('\n', searchFrom);
		if (lineFeed == -1)
		{
			lineFeed = content.LastIndexOf('\n', searchFrom);
		}

		if (lineFeed == -1)
		{
			return content.Contains('\r') ? "\r" : "\n";
		}

		return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";
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
		var result = new List<DiffLine>(before.Length + after.Length);
		DiffRange(before, 0, before.Length, after, 0, after.Length, result, depth: 0);
		return result;
	}

	/// <summary>行级 diff 的递归单元：先削公共前后缀，再按锚点行切分，最后退化为整段替换。</summary>
	/// <remarks>
	/// 不用 LCS 表：2 万行两侧要 1.6 GB，20 万行直接挂起。锚点法的内存与行数线性，
	/// 单处修改经前后缀削减后中段只剩改动处。
	/// </remarks>
	private static void DiffRange(
		string[] before, int beforeStart, int beforeEnd,
		string[] after, int afterStart, int afterEnd,
		List<DiffLine> result, int depth)
	{
		while (beforeStart < beforeEnd && afterStart < afterEnd
			&& string.Equals(before[beforeStart], after[afterStart], StringComparison.Ordinal))
		{
			result.Add(new DiffLine(DiffKind.Same, before[beforeStart]));
			beforeStart++;
			afterStart++;
		}

		var suffix = 0;
		while (beforeStart < beforeEnd - suffix && afterStart < afterEnd - suffix
			&& string.Equals(before[beforeEnd - 1 - suffix], after[afterEnd - 1 - suffix], StringComparison.Ordinal))
		{
			suffix++;
		}

		var beforeTail = beforeEnd - suffix;
		var afterTail = afterEnd - suffix;

		var anchors = depth < MaxDiffDepth
			? FindAnchors(before, beforeStart, beforeTail, after, afterStart, afterTail)
			: [];

		if (anchors.Count == 0)
		{
			for (var i = beforeStart; i < beforeTail; i++)
			{
				result.Add(new DiffLine(DiffKind.Remove, before[i]));
			}

			for (var i = afterStart; i < afterTail; i++)
			{
				result.Add(new DiffLine(DiffKind.Add, after[i]));
			}
		}
		else
		{
			var previousBefore = beforeStart;
			var previousAfter = afterStart;
			foreach (var (beforeIndex, afterIndex) in anchors)
			{
				DiffRange(before, previousBefore, beforeIndex, after, previousAfter, afterIndex, result, depth + 1);
				result.Add(new DiffLine(DiffKind.Same, before[beforeIndex]));
				previousBefore = beforeIndex + 1;
				previousAfter = afterIndex + 1;
			}

			DiffRange(before, previousBefore, beforeTail, after, previousAfter, afterTail, result, depth + 1);
		}

		for (var i = 0; i < suffix; i++)
		{
			result.Add(new DiffLine(DiffKind.Same, before[beforeEnd - suffix + i]));
		}
	}

	private const int MaxDiffDepth = 64;

	/// <summary>两侧都只出现一次的行，取最长递增子序列作为单调锚点。</summary>
	private static List<(int Before, int After)> FindAnchors(
		string[] before, int beforeStart, int beforeEnd,
		string[] after, int afterStart, int afterEnd)
	{
		if (beforeEnd - beforeStart < 2 || afterEnd - afterStart < 2)
		{
			return [];
		}

		var counts = new Dictionary<string, (int Before, int After, int BeforeIndex, int AfterIndex)>(StringComparer.Ordinal);
		for (var i = beforeStart; i < beforeEnd; i++)
		{
			ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, before[i], out _);
			entry.Before++;
			entry.BeforeIndex = i;
		}

		for (var i = afterStart; i < afterEnd; i++)
		{
			if (!counts.TryGetValue(after[i], out var entry))
			{
				continue;
			}

			entry.After++;
			entry.AfterIndex = i;
			counts[after[i]] = entry;
		}

		var ordered = new List<(int Before, int After)>(counts.Count);
		foreach (var entry in counts.Values)
		{
			if (entry.Before == 1 && entry.After == 1)
			{
				ordered.Add((entry.BeforeIndex, entry.AfterIndex));
			}
		}

		if (ordered.Count == 0)
		{
			return [];
		}

		ordered.Sort(static (left, right) => left.Before.CompareTo(right.Before));
		return LongestIncreasingAfter(ordered);
	}

	/// <summary>按 <c>After</c> 求最长递增子序列，保证锚点在两侧同时单调。</summary>
	private static List<(int Before, int After)> LongestIncreasingAfter(List<(int Before, int After)> candidates)
	{
		var tails = new int[candidates.Count];
		var previous = new int[candidates.Count];
		var length = 0;
		for (var i = 0; i < candidates.Count; i++)
		{
			var value = candidates[i].After;
			int low = 0, high = length;
			while (low < high)
			{
				var middle = (low + high) >> 1;
				if (candidates[tails[middle]].After < value)
				{
					low = middle + 1;
				}
				else
				{
					high = middle;
				}
			}

			previous[i] = low > 0 ? tails[low - 1] : -1;
			tails[low] = i;
			if (low == length)
			{
				length++;
			}
		}

		if (length == 0)
		{
			return [];
		}

		var chain = new List<(int Before, int After)>(length);
		for (var index = tails[length - 1]; index >= 0; index = previous[index])
		{
			chain.Add(candidates[index]);
		}

		chain.Reverse();
		return chain;
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
