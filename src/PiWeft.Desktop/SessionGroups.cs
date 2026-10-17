using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>
/// 侧栏两个分段的归属规则：工作目录就是聊天目录的会话属于「对话」，其余属于「项目」。
/// 与 PiDeck 的内置 Chat 项目一致 —— 「对话」是一块没有项目上下文的聊天工作区，
/// 不是全部会话的平铺；否则同一批会话会在两个分段里重复出现。
/// </summary>
internal static class SessionGroups
{
	/// <summary>会话是否属于「对话」分段。</summary>
	public static bool IsChat(SessionListItem item, string chatWorkspace) =>
		chatWorkspace.Length > 0 && item.Cwd.Length > 0 && SamePath(item.Cwd, chatWorkspace);

	/// <summary>「对话」分段：聊天目录里的会话，最近的在前。</summary>
	public static IReadOnlyList<SessionListItem> Chats(IEnumerable<SessionListItem> sessions, string chatWorkspace) =>
		[.. sessions.Where(item => IsChat(item, chatWorkspace))];

	/// <summary>「项目」分段：不属于聊天目录的会话，按在项目树里分组展示。</summary>
	public static IReadOnlyList<SessionListItem> Projects(IEnumerable<SessionListItem> sessions, string chatWorkspace) =>
		[.. sessions.Where(item => !IsChat(item, chatWorkspace))];

	/// <summary>
	/// 同一个目录的判定：Windows/macOS 大小写不敏感，Linux 敏感；
	/// 无法解析成绝对路径时退化为原始字符串比较，绝不抛异常打断侧栏刷新。
	/// </summary>
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

	/// <summary>GetFullPath 会保留末尾的分隔符（<c>/a/b/</c>），比较前统一去掉；根目录本身保留。</summary>
	private static string Normalize(string path)
	{
		var full = Path.GetFullPath(path);
		var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return trimmed.Length == 0 ? full : trimmed;
	}
}
