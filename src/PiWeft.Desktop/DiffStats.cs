namespace PiWeft.Desktop;

/// 统一补丁的增删行数统计。Git 面板与轮末文件汇总共用同一口径。
internal static class DiffStats
{
	/// 统计补丁里新增与删除的内容行。
	/// 按 hunk 头的行数逐行消费，不按前缀猜：内容行本身以 <c>--</c> / <c>++</c> 开头时，
	/// 前缀与文件头无法区分。碰到认不出的行就结束当前 hunk，后面按文件头忽略。
	public static (int Added, int Removed) Count(string patch)
	{
		var added = 0;
		var removed = 0;
		var oldLines = 0;
		var newLines = 0;
		foreach (var line in patch.EnumerateLines())
		{
			if (line.StartsWith("@@", StringComparison.Ordinal))
			{
				(oldLines, newLines) = DiffSyntax.HunkLengths(line);
				continue;
			}

			if (oldLines <= 0 && newLines <= 0) continue;
			switch (line.IsEmpty ? '\0' : line[0])
			{
				case '+' when newLines > 0:
					added++;
					newLines--;
					break;
				case '-' when oldLines > 0:
					removed++;
					oldLines--;
					break;
				case ' ' when oldLines > 0 && newLines > 0:
					oldLines--;
					newLines--;
					break;
				case '\\':
					// "\ No newline at end of file"：不属于任何一侧的行数。
					break;
				default:
					oldLines = 0;
					newLines = 0;
					break;
			}
		}

		return (added, removed);
	}
}
