using PiWeft.Cli;

namespace PiWeft.Desktop;

/// 侧栏「项目」的一行：规范化后的工作目录，以及它下面的会话。刚添加、还没开过任务的目录会话为空。
internal sealed record ProjectGroup(string Cwd, IReadOnlyList<SessionListItem> Sessions);

/// 侧栏里的一棵会话树：子会话挂在父会话下面，父会话缺失时子会话自己当根。
internal sealed record SessionTreeEntry(SessionListItem Item, IReadOnlyList<SessionTreeEntry> Children);

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

	/// 把子会话挂到父会话下面；父会话不在列表里时子会话保持顶层。
	/// 用 visited 兜住手改文件造成的环，最坏退化成平铺，不递归到栈溢出。
	public static IReadOnlyList<SessionTreeEntry> Nest(IReadOnlyList<SessionListItem> sessions)
	{
		if (sessions.Count == 0)
		{
			return [];
		}

		// 子会话头的 parentSession 可能写父会话 id，也可能写父会话文件路径
		//（桌面建子会话时写的是 ParentFile）。两种都要认，否则侧栏挂不上去。
		var byKey = new Dictionary<string, string>(sessions.Count * 2, StringComparer.Ordinal);
		foreach (var item in sessions)
		{
			byKey[item.Id] = item.Id;
			if (item.Path.Length > 0)
			{
				byKey[item.Path] = item.Id;
			}
		}

		var byParent = new Dictionary<string, List<SessionListItem>>(StringComparer.Ordinal);
		var roots = new List<SessionListItem>();
		foreach (var item in sessions)
		{
			if (item.Origin == "subagent" &&
				item.ParentSession.Length > 0 &&
				byKey.TryGetValue(item.ParentSession, out var parentId) &&
				parentId != item.Id)
			{
				if (!byParent.TryGetValue(parentId, out var children))
				{
					byParent[parentId] = children = [];
				}

				children.Add(item);
			}
			else
			{
				roots.Add(item);
			}
		}

		var visited = new HashSet<string>(StringComparer.Ordinal);
		var result = new List<SessionTreeEntry>(sessions.Count);
		foreach (var item in roots)
		{
			result.Add(Build(item, 0, []));
		}

		// 环或父会话缺失时也要有入口：没访问过的一律当根。
		foreach (var item in sessions)
		{
			if (!visited.Contains(item.Id))
			{
				result.Add(Build(item, 0, []));
			}
		}

		return result;

		SessionTreeEntry Build(SessionListItem item, int depth, HashSet<string> path)
		{
			visited.Add(item.Id);
			if (depth >= 8 || !byParent.TryGetValue(item.Id, out var children) || children.Count == 0)
			{
				return new SessionTreeEntry(item, []);
			}

			var branch = new HashSet<string>(path, StringComparer.Ordinal) { item.Id };
			var nested = new List<SessionTreeEntry>(children.Count);
			foreach (var child in children)
			{
				if (!branch.Contains(child.Id) && !visited.Contains(child.Id))
				{
					nested.Add(Build(child, depth + 1, branch));
				}
			}

			return new SessionTreeEntry(item, nested);
		}
	}

	/// 侧栏「项目」的完整清单：会话分组加「添加项目」记录，同一个目录只出现一行。
	/// 空目录也要留下，否则添加一个还没有会话的文件夹时侧栏没有任何变化。
	/// <paramref name="dismissedProjects"/> 里的目录连同会话一起隐藏：移除是显式动作，
	/// 不能被会话扫描又加回来（对齐 PiDeck 的 dismissed-project-paths）。
	public static IReadOnlyList<ProjectGroup> ProjectGroups(
		IEnumerable<SessionListItem> sessions,
		string chatWorkspace,
		IReadOnlyList<string> addedProjects,
		IReadOnlyList<string> dismissedProjects)
	{
		var dismissed = ToKeys(dismissedProjects);
		var groups = new Dictionary<string, List<SessionListItem>>(PathComparer);
		var order = new List<string>();
		// 会话通常只来自几个工作目录：每次刷新每个目录只规范化一次，省掉每条会话一次 GetFullPath。
		var keys = new Dictionary<string, string>(PathComparer);

		// 直接走一遍全部会话：不先物化「项目会话」列表，刷新时少一次分配与遍历。
		foreach (var item in sessions.DistinctBy(static item => item.Path))
		{
			if (IsChat(item, chatWorkspace))
			{
				continue;
			}

			// 没有 cwd 的旧会话聚成「?」分组：它不是一个目录，不参与移除过滤。
			var key = item.Cwd.Length == 0 ? string.Empty : PathKey(item.Cwd);
			if (key.Length > 0 && dismissed.Contains(key))
			{
				continue;
			}

			if (!groups.TryGetValue(key, out var list))
			{
				groups[key] = list = new List<SessionListItem>(2);
				order.Add(key);
			}

			list.Add(item);
		}

		foreach (var path in addedProjects)
		{
			var key = PathKey(path);
			if (dismissed.Contains(key))
			{
				continue;
			}

			if (groups.TryAdd(key, []))
			{
				order.Add(key);
			}
		}

		// 有会话的项目按最近修改排；刚添加、还没有会话的排在后面（OrderBy 稳定，保持添加顺序）。
		return
		[
			.. order
				.Select(key => new ProjectGroup(key, groups[key]))
				.OrderByDescending(static group => group.Sessions.Count > 0
					? group.Sessions.Max(static item => item.Modified)
					: DateTimeOffset.MinValue),
		];

		string PathKey(string path)
		{
			if (keys.TryGetValue(path, out var cached))
			{
				return cached;
			}

			var key = SafeProjectPath(path);
			keys[path] = key;
			return key;
		}
	}

	/// 同一个目录的判定：Windows/macOS 大小写不敏感，Linux 敏感；
	/// 无法解析成绝对路径时退化为原始字符串比较，不抛异常打断侧栏刷新。
	public static bool SamePath(string left, string right) => SameKey(SafeProjectPath(left), SafeProjectPath(right));

	/// 两个已规范化的项目路径键是否同一个目录。
	public static bool SameKey(string left, string right) => PathComparer.Equals(left, right);

	/// 项目路径的比较器：Windows/macOS 不区分大小写，Linux 区分。
	/// 侧栏的字典、集合与展开记录都用它，避免同一个目录因大小写不同占两行。
	public static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;

	/// 项目路径的统一写法：绝对路径，去掉结尾分隔符。同一目录的 `/x`、`/x/`、`/x/../x`
	/// 落到同一个项目，也落到同一个单实例键。根目录（`/`、`C:\`）保持原样。
	public static string ProjectPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

	/// <see cref="ProjectPath"/> 的宽松版：手改的偏好或旧会话里解析不了的 cwd 按原串返回。
	public static string SafeProjectPath(string path)
	{
		try
		{
			return ProjectPath(path);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return path;
		}
	}

	private static HashSet<string> ToKeys(IReadOnlyList<string> paths)
	{
		var keys = new HashSet<string>(paths.Count, PathComparer);
		foreach (var path in paths)
		{
			keys.Add(SafeProjectPath(path));
		}

		return keys;
	}
}
