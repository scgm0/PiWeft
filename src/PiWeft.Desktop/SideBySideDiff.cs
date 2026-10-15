namespace PiWeft.Desktop;

/// <summary>并排 diff 中一行的类型。</summary>
public enum DiffRowKind
{
	/// <summary>上下文行，两侧相同。</summary>
	Context,

	/// <summary>仅左侧（删除）。</summary>
	Deletion,

	/// <summary>仅右侧（新增）。</summary>
	Addition,

	/// <summary>左删右增配对。</summary>
	Change,

	/// <summary>hunk 头，作为分隔行。</summary>
	Header,
}

/// <summary>并排 diff 的一行。</summary>
public sealed record DiffRow(
	int? OldNumber,
	string OldText,
	int? NewNumber,
	string NewText,
	DiffRowKind Kind,
	string Header = "");

/// <summary>
/// 把统一 diff 解析成并排的两列。删除与新增按顺序配对，缺少一侧的行用空行占位，
/// 因此两列的行数始终一致、逐行对齐。不做语法处理，只负责行结构。
/// </summary>
public static class SideBySideDiff
{
	public static IReadOnlyList<DiffRow> Parse(string patch)
	{
		var rows = new List<DiffRow>();
		var normalized = patch.Replace("\r\n", "\n").Replace('\r', '\n');
		var lines = normalized.Split('\n');
		var oldLine = 0;
		var newLine = 0;

		for (var index = 0; index < lines.Length; index++)
		{
			var line = lines[index];
			if (line.StartsWith("@@", StringComparison.Ordinal))
			{
				(oldLine, newLine) = ParseHunkHeader(line, oldLine, newLine);
				rows.Add(new DiffRow(null, string.Empty, null, string.Empty, DiffRowKind.Header, line));
				continue;
			}

			if (IsMetadata(line) || index == lines.Length - 1 && line.Length == 0)
			{
				continue;
			}

			if (line.StartsWith('-'))
			{
				// 收集连续的删除与紧接着的新增，按序号配对。
				var deletions = new List<(int Number, string Text)>();
				while (index < lines.Length && lines[index].StartsWith('-') && !IsMetadata(lines[index]))
				{
					deletions.Add((oldLine++, lines[index][1..]));
					index++;
				}

				var additions = new List<(int Number, string Text)>();
				while (index < lines.Length && lines[index].StartsWith('+') && !IsMetadata(lines[index]))
				{
					additions.Add((newLine++, lines[index][1..]));
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
				rows.Add(new DiffRow(null, string.Empty, newLine++, line[1..], DiffRowKind.Addition));
				continue;
			}

			// 上下文行：两侧都有，行号各自递增。
			var text = line.StartsWith(' ') ? line[1..] : line;
			rows.Add(new DiffRow(oldLine++, text, newLine++, text, DiffRowKind.Context));
		}

		return rows;
	}

	private static bool IsMetadata(string line) => line.StartsWith("+++", StringComparison.Ordinal) ||
		line.StartsWith("---", StringComparison.Ordinal) ||
		line.StartsWith("diff ", StringComparison.Ordinal) ||
		line.StartsWith("index ", StringComparison.Ordinal) ||
		line.StartsWith('\\');

	private static (int OldLine, int NewLine) ParseHunkHeader(string line, int fallbackOld, int fallbackNew)
	{
		var match = System.Text.RegularExpressions.Regex.Match(line, @"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
		return match.Success && int.TryParse(match.Groups[1].Value, out var oldLine) && int.TryParse(match.Groups[2].Value, out var newLine)
			? (oldLine, newLine)
			: (fallbackOld, fallbackNew);
	}
}
