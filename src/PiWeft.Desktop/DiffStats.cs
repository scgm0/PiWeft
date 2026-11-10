namespace PiWeft.Desktop;

/// 统一补丁的增删行数统计。Git 面板与轮末文件汇总共用同一口径：
/// 跳过文件头（---/+++）与 hunk 头，只数内容行；两处各写一份迟早会算得不一样。
internal static class DiffStats
{
	/// 统计补丁里以 <c>+</c> / <c>-</c> 开头的内容行。
	public static (int Added, int Removed) Count(string patch)
	{
		var added = 0;
		var removed = 0;
		foreach (var line in patch.AsSpan().EnumerateLines())
		{
			if (line.Length == 0) continue;
			if (line.StartsWith("@@", StringComparison.Ordinal)) continue;
			if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal)) continue;
			if (line[0] == '+') added++;
			else if (line[0] == '-') removed++;
		}

		return (added, removed);
	}
}
