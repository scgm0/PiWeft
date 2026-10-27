namespace PiWeft.Desktop;

/// <summary>diff 面板当前展示的内容类型。</summary>
internal enum GitDiffKind
{
	WorkingTree,
	Staged,
	Commit,
	Compare,
}

/// <summary>
/// 底部 diff 面板的请求。工作区/暂存、提交内文件与分支比较都走同一块面板，
/// 切换来源时只换请求，不重建第二套 diff UI。
/// </summary>
internal sealed record GitDiffRequest(
	GitDiffKind Kind,
	string Path,
	string Title,
	string? CommitHash = null,
	string? BaseHash = null,
	string? TargetHash = null,
	string? OriginalPath = null,
	bool Untracked = false)
{
	public string ShortCommit => CommitHash is { Length: > 7 } hash ? hash[..7] : CommitHash ?? string.Empty;

	public static GitDiffRequest WorkingTree(string path, bool staged, bool untracked = false) => new(
		staged ? GitDiffKind.Staged : GitDiffKind.WorkingTree,
		path,
		Strings.GitDiff,
		Untracked: untracked);

	public static GitDiffRequest Commit(string hash, string? parentHash, string path, string? originalPath = null) => new(
		GitDiffKind.Commit,
		path,
		$"提交 {(hash.Length > 7 ? hash[..7] : hash)}",
		CommitHash: hash,
		BaseHash: parentHash,
		OriginalPath: originalPath);

	public static GitDiffRequest Compare(string baseHash, string targetHash, string label, string path, string? originalPath = null) => new(
		GitDiffKind.Compare,
		path,
		label,
		BaseHash: baseHash,
		TargetHash: targetHash,
		OriginalPath: originalPath);

	public bool MatchesWorkingTree(string path, bool staged) =>
		(Kind is GitDiffKind.WorkingTree or GitDiffKind.Staged) &&
		(Kind == GitDiffKind.Staged) == staged &&
		string.Equals(Path, path, StringComparison.Ordinal);

	public bool MatchesCommit(string hash, string path) =>
		Kind == GitDiffKind.Commit &&
		string.Equals(CommitHash, hash, StringComparison.Ordinal) &&
		string.Equals(Path, path, StringComparison.Ordinal);

	public bool MatchesCompare(string baseHash, string targetHash, string path) =>
		Kind == GitDiffKind.Compare &&
		string.Equals(BaseHash, baseHash, StringComparison.Ordinal) &&
		string.Equals(TargetHash, targetHash, StringComparison.Ordinal) &&
		string.Equals(Path, path, StringComparison.Ordinal);
}
