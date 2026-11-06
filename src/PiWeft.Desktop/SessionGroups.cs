using PiWeft.Cli;

namespace PiWeft.Desktop;

/// 侧栏两个分段的归属规则：工作目录就是聊天目录的会话属于「对话」，其余属于「项目」。
/// 与 PiDeck 的内置 Chat 项目一致：「对话」是一块没有项目上下文的聊天工作区，
/// 不是全部会话的平铺；否则同一批会话会在两个分段里重复出现。
internal static class SessionGroups
{
	public static bool IsChat(SessionListItem item, string chatWorkspace) =>
		chatWorkspace.Length > 0 && item.Cwd.Length > 0 && SamePath(item.Cwd, chatWorkspace);

	public static IReadOnlyList<SessionListItem> Chats(IEnumerable<SessionListItem> sessions, string chatWorkspace) =>
		[.. sessions.Where(item => IsChat(item, chatWorkspace))];

	public static IReadOnlyList<SessionListItem> Projects(IEnumerable<SessionListItem> sessions, string chatWorkspace) =>
		[.. sessions.Where(item => !IsChat(item, chatWorkspace))];

	/// 同一个目录的判定：Windows/macOS 大小写不敏感，Linux 敏感；
	/// 无法解析成绝对路径时退化为原始字符串比较，绝不抛异常打断侧栏刷新。
	public static bool SamePath(string left, string right)
	{
		var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

		try
		{
			return string.Equals(Normalize(left), Normalize(right), comparison);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return string.Equals(left, right, comparison);
		}
	}

	private static string Normalize(string path)
	{
		var full = Path.GetFullPath(path);
		var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return trimmed.Length == 0 ? full : trimmed;
	}
}
