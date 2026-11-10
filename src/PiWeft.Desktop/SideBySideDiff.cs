namespace PiWeft.Desktop;

public enum DiffRowKind
{
	Context,

	Deletion,

	Addition,

	Change,

	Header,
}

public sealed record DiffRow(
	int? OldNumber,
	string OldText,
	int? NewNumber,
	string NewText,
	DiffRowKind Kind,
	string Header = "");

/// 把统一 diff 解析成并排的两列。删除与新增按顺序配对，缺少一侧的行用空行占位，
/// 因此两列的行数始终一致、逐行对齐。不做语法处理，只负责行结构。
public static class SideBySideDiff
{
	public static IReadOnlyList<DiffRow> Parse(string patch)
	{
		var rows = new List<DiffRow>();
		var normalized = patch.Replace("\r\n", "\n").Replace('\r', '\n');
		var span = normalized.AsSpan();
		var lines = SplitLines(span);
		var oldLine = 0;
		var newLine = 0;

		for (var index = 0; index < lines.Count; index++)
		{
			var line = span[lines[index]];
			if (line.StartsWith("@@"))
			{
				(oldLine, newLine) = ParseHunkHeader(line, oldLine, newLine);
				rows.Add(new DiffRow(null, string.Empty, null, string.Empty, DiffRowKind.Header, line.ToString()));
				continue;
			}

			if (IsMetadata(line) || index == lines.Count - 1 && line.IsEmpty)
			{
				continue;
			}

			if (line.StartsWith('-'))
			{
				// 收集连续的删除与紧接着的新增，按序号配对。
				var deletions = new List<(int Number, string Text)>();
				while (index < lines.Count && span[lines[index]].StartsWith('-') && !IsMetadata(span[lines[index]]))
				{
					deletions.Add((oldLine++, span[lines[index]][1..].ToString()));
					index++;
				}

				var additions = new List<(int Number, string Text)>();
				while (index < lines.Count && span[lines[index]].StartsWith('+') && !IsMetadata(span[lines[index]]))
				{
					additions.Add((newLine++, span[lines[index]][1..].ToString()));
					index++;
				}

				index--; // 抵消循环自增

				for (var pair = 0; pair < Math.Max(deletions.Count, additions.Count); pair++)
				{
					var hasOld = pair < deletions.Count;
					var hasNew = pair < additions.Count;
					rows.Add(new DiffRow(
						hasOld ? deletions[pair].Number : null,
						hasOld ? deletions[pair].Text : string.Empty,
						hasNew ? additions[pair].Number : null,
						hasNew ? additions[pair].Text : string.Empty,
						hasOld && hasNew ? DiffRowKind.Change : hasOld ? DiffRowKind.Deletion : DiffRowKind.Addition));
				}

				continue;
			}

			if (line.StartsWith('+'))
			{
				rows.Add(new DiffRow(null, string.Empty, newLine++, line[1..].ToString(), DiffRowKind.Addition));
				continue;
			}

			// 上下文行：两侧都有，行号各自递增。
			var text = (line.StartsWith(' ') ? line[1..] : line).ToString();
			rows.Add(new DiffRow(oldLine++, text, newLine++, text, DiffRowKind.Context));
		}

		return rows;
	}

	/// 用 span 切行，不产生每行的字符串；行文本只在需要进 <see cref="DiffRow"/> 时 ToString。
	/// 切分结果与 <c>Split('\n')</c> 一致（含结尾空行）。
	private static List<Range> SplitLines(ReadOnlySpan<char> span)
	{
		var lines = new List<Range>(span.Count('\n') + 1);
		foreach (var range in span.Split('\n'))
		{
			lines.Add(range);
		}

		return lines;
	}

	// 行分类与 hunk 头解析与统一视图共用：两边各留一份就会在一次修正后只修好一边。
	private static bool IsMetadata(ReadOnlySpan<char> line) => DiffSyntax.IsMetadata(line);

	private static (int OldLine, int NewLine) ParseHunkHeader(ReadOnlySpan<char> line, int fallbackOld, int fallbackNew) =>
		DiffSyntax.ParseHunkHeader(line, fallbackOld, fallbackNew);
}
