namespace PiWeft.Desktop;

/// <summary>变更列表的一行：目录行或文件行。</summary>
internal enum GitTreeRowKind
{
	Directory,
	File,
}

/// <summary>
/// 变更列表的树形行。<see cref="Changes"/> 在目录行里是整棵子树的资源（目录级暂存/取消暂存
/// 因此对整棵子树生效），在文件行里只有一个元素。
/// </summary>
internal sealed record GitTreeRow(
	GitTreeRowKind Kind,
	string Path,
	string Label,
	int Depth,
	IReadOnlyList<GitChange> Changes)
{
	public bool IsDirectory => Kind == GitTreeRowKind.Directory;

	public GitChange? File => Kind == GitTreeRowKind.File ? Changes[0] : null;
}

/// <summary>
/// 把扁平的 <see cref="GitChange"/> 列表折成目录树（纯函数，便于单测）：
/// 目录优先、同级按名称排序；单子目录且无直属文件时合并成 <c>a.b.c</c> 一行，
/// 避免 <c>src/main/pi/...</c> 这类链把窄面板挤成竖条。
/// </summary>
internal static class GitChangeTree
{
	private sealed class Node
	{
		public Dictionary<string, Node> Directories { get; } = new(StringComparer.Ordinal);

		public List<GitChange> Files { get; } = [];
	}

	public static IReadOnlyList<GitTreeRow> Build(
		IReadOnlyList<GitChange> changes,
		ISet<string>? collapsedDirectories = null)
	{
		var root = new Node();
		foreach (var change in changes)
		{
			var parts = Normalize(change.Path).Split('/', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
			{
				continue;
			}

			var node = root;
			for (var index = 0; index < parts.Length - 1; index++)
			{
				if (!node.Directories.TryGetValue(parts[index], out var child))
				{
					child = new Node();
					node.Directories[parts[index]] = child;
				}

				node = child;
			}

			node.Files.Add(change);
		}

		var rows = new List<GitTreeRow>();
		Walk(root, string.Empty, 0, collapsedDirectories, rows);
		return rows;
	}

	/// <summary>树里所有可折叠目录的完整路径；用于「全部收起」。</summary>
	public static IReadOnlyList<string> AllDirectoryPaths(IReadOnlyList<GitChange> changes) =>
		[.. Build(changes).Where(static row => row.IsDirectory).Select(static row => row.Path)];

	private static void Walk(
		Node node,
		string directory,
		int depth,
		ISet<string>? collapsed,
		List<GitTreeRow> rows)
	{
		foreach (var name in node.Directories.Keys.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
		{
			var (label, segments, leaf) = CompactChain(name, node.Directories[name]);
			var path = directory.Length == 0 ? string.Join('/', segments) : directory + "/" + string.Join('/', segments);
			rows.Add(new GitTreeRow(GitTreeRowKind.Directory, path, label, depth, Collect(leaf)));
			if (collapsed?.Contains(path) != true)
			{
				Walk(leaf, path, depth + 1, collapsed, rows);
			}
		}

		foreach (var file in node.Files.OrderBy(static change => change.FileName, StringComparer.OrdinalIgnoreCase))
		{
			rows.Add(new GitTreeRow(GitTreeRowKind.File, Normalize(file.Path), file.FileName, depth, [file]));
		}
	}

	private static (string Label, string[] Segments, Node Leaf) CompactChain(string name, Node node)
	{
		var segments = new List<string> { name };
		var current = node;
		while (current.Files.Count == 0 && current.Directories.Count == 1)
		{
			var (childName, child) = current.Directories.First();
			segments.Add(childName);
			current = child;
		}

		return (string.Join('.', segments), [.. segments], current);
	}

	private static IReadOnlyList<GitChange> Collect(Node node)
	{
		var changes = new List<GitChange>();
		CollectInto(node, changes);
		return changes;
	}

	private static void CollectInto(Node node, List<GitChange> changes)
	{
		changes.AddRange(node.Files);
		foreach (var child in node.Directories.Values)
		{
			CollectInto(child, changes);
		}
	}

	private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
