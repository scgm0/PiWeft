namespace PiWeft.Desktop;

internal enum GitChangeRowKind
{
	Section,
	Directory,
	File,
}

/// 更改列表的一行。<see cref="Tree"/> 为 null 表示分节标题行。
/// 三段（合并冲突 / 已暂存 / 未暂存）折进同一份扁平行表：列表要虚拟化，
/// 一行一个模板控件，不能再按「分节容器」把子元素一次性全建出来。
internal sealed record GitChangeRow(
	GitChangeRowKind Kind,
	string Title,
	GitTreeRow? Tree,
	GitDiffStat? Stat,
	IReadOnlyList<GitChange> SectionChanges,
	bool Staged,
	bool Conflict,
	bool AllowActions)
{
	/// 行身份：折叠目录、暂存操作后同一条改动的行还在原处，高度缓存与滚动位置靠它复用。
	/// 同一路径可能同时出现在「已暂存」与「未暂存」（部分暂存），标识要带上所在的一侧。
	public string Key => $"{(int)Kind}:{(Staged ? 1 : 0)}:{(Conflict ? 1 : 0)}:{Tree?.Path ?? Title}";

	/// 本行作用的改动：目录行是整棵子树，文件行是那一个文件，分节标题行是整节。
	public IReadOnlyList<GitChange> Changes => Tree?.Changes ?? SectionChanges;
}

/// 把 <see cref="GitStatus"/> 折成更改列表的扁平行表（纯函数，便于单测）。
internal static class GitChangeList
{
	public static List<GitChangeRow> Build(
		GitStatus status,
		ISet<string>? collapsedDirectories = null,
		GitChangeStats? stats = null)
	{
		// 预留分节标题行；超大改动集（上万文件）下这段的分配也不该是瓶颈。
		var rows = new List<GitChangeRow>(status.Changes.Count + 8);
		// 一次划成三份：部分暂存的文件同时属于「已暂存」与「未暂存」，两条判断不能写成 else if。
		var conflicts = new List<GitChange>();
		var stagedChanges = new List<GitChange>();
		var unstagedChanges = new List<GitChange>();
		foreach (var change in status.Changes)
		{
			if (change.IsConflict)
			{
				conflicts.Add(change);
				continue;
			}

			if (change.HasStaged)
			{
				stagedChanges.Add(change);
			}

			if (change.HasWorkTree)
			{
				unstagedChanges.Add(change);
			}
		}

		Append(rows, Strings.GitMergeChanges, conflicts,
			staged: false, conflict: true, allowActions: false, collapsedDirectories, stats);
		Append(rows, Strings.GitStaged, stagedChanges,
			staged: true, conflict: false, allowActions: true, collapsedDirectories, stats);
		Append(rows, Strings.GitUnstaged, unstagedChanges,
			staged: false, conflict: false, allowActions: true, collapsedDirectories, stats);
		return rows;
	}

	private static void Append(
		List<GitChangeRow> rows,
		string title,
		IReadOnlyList<GitChange> changes,
		bool staged,
		bool conflict,
		bool allowActions,
		ISet<string>? collapsedDirectories,
		GitChangeStats? stats)
	{
		if (changes.Count == 0)
		{
			return;
		}

		rows.Add(new GitChangeRow(GitChangeRowKind.Section, title, null, null, changes, staged, conflict, allowActions));
		foreach (var tree in GitChangeTree.Build(changes, collapsedDirectories))
		{
			rows.Add(new GitChangeRow(
				tree.IsDirectory ? GitChangeRowKind.Directory : GitChangeRowKind.File,
				string.Empty,
				tree,
				// 冲突文件没有可比较的一侧，按工作区算。目录行不显示行数（合计一份会让人以为是可点的一条）。
				tree.IsDirectory ? null : stats?.For(tree.Path, staged),
				[],
				staged,
				conflict,
				allowActions));
		}
	}
}
