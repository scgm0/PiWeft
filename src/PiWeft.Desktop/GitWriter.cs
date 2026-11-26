using System.Diagnostics;

namespace PiWeft.Desktop;

/// 一次 Git 写操作的结果。<see cref="Message"/> 是给人看的一句话结论（成功摘要或分类后的失败原因），
/// <see cref="Detail"/> 是 git 的原始输出尾部，只在排查时展示。把
/// <c>fatal: could not read Username for 'https://github.com': terminal prompts disabled</c>
/// 直接甩到状态行里，用户只知道「失败了」。
public sealed record GitResult(bool Ok, string Message, string? Detail = null)
{
	public static GitResult Success(string message = "") => new(true, message);

	public static GitResult Failure(string message, string? detail = null) => new(false, message, detail);
}

public sealed record GitStashEntry(string Reference, string Description);

/// <summary>一条 git 工作树记录（<c>git worktree list --porcelain</c>）。</summary>
public sealed record GitWorktree(string Path, string Head, string Branch, bool Detached, bool IsMain)
{
	/// <summary>分支短名；分离头指针时为空。</summary>
	public string BranchName => Branch.Length > 0 && Branch.StartsWith("refs/heads/", StringComparison.Ordinal)
		? Branch["refs/heads/".Length..]
		: Branch;
}

/// 把 git 的原始输出翻成一句能照着做的中文。git 的错误文本是稳定的英文，也被脚本广泛依赖，
/// 按特征串分类可靠；分类不出来就退回 output 首行，至少不吞信息。
/// 要区分「工作区状态不对」与「环境/认证/网络不通」：前者用户自己能解决，后者要看凭据或代理。
/// 两类提示混在一起，用户会往错误的方向折腾。
public static class GitFailure
{
	public static string? Classify(string output)
	{
		var text = output.Trim();
		if (text.Length == 0)
		{
			return null;
		}

		return text switch
		{
			_ when Contains(text, "not a git repository") => Strings.GitNotRepository,

			// 认证：终端提示被关掉（我们设了 GIT_TERMINAL_PROMPT=0）或凭据被拒。
			_ when Contains(text, "terminal prompts disabled")
				|| Contains(text, "could not read Username")
				|| Contains(text, "could not read Password")
				|| Contains(text, "Authentication failed")
				|| Contains(text, "Permission denied (publickey)")
				|| Contains(text, "Invalid username or password")
				|| Contains(text, "Support for password authentication was removed") =>
				Strings.GitAuthFailed,

			// 网络：DNS、连接、超时、TLS。
			_ when Contains(text, "Could not resolve host")
				|| Contains(text, "unable to access")
				|| Contains(text, "Connection refused")
				|| Contains(text, "Connection reset")
				|| Contains(text, "Operation timed out")
				|| Contains(text, "SSL certificate problem")
				|| Contains(text, "Failed to connect") =>
				Strings.GitNetworkFailed,

			// 远端领先：push 被拒。
			_ when Contains(text, "non-fast-forward")
				|| Contains(text, "fetch first")
				|| Contains(text, "[rejected]")
				|| Contains(text, "Updates were rejected") =>
				Strings.GitPushRejected,

			// 远端名不存在（首次推送挑错远端时会走到这里）。
			_ when Contains(text, "does not appear to be a git repository") => Strings.GitUnknownRemote,

			// 隔离副本：同名分支或目录已经存在。
			_ when Contains(text, "already exists") => Strings.GitWorktreeExists,

			// 提交操作：上一个 revert / cherry-pick 还没收尾。
			_ when Contains(text, "is already in progress") || Contains(text, "sequencer") =>
				Strings.GitCommitSequenceRunning,

			// 无上游 / 无远端。
			_ when Contains(text, "has no upstream branch")
				|| Contains(text, "no upstream configured")
				|| Contains(text, "no configured push destination") =>
				Strings.GitNoUpstream,

			// pull：本地改动会被覆盖，或不能快进。
			_ when Contains(text, "would be overwritten by merge")
				|| Contains(text, "Please commit your changes or stash them")
				|| Contains(text, "Please, commit your changes or stash them")
				|| Contains(text, "Your local changes to the following files") =>
				Strings.GitLocalChangesBlock,

			_ when Contains(text, "not possible to fast-forward")
				|| Contains(text, "Not possible to fast-forward")
				|| Contains(text, "diverging branches")
				|| Contains(text, "need to specify how to reconcile divergent branches") =>
				Strings.GitNotFastForward,

			// 冲突。
			_ when Contains(text, "CONFLICT")
				|| Contains(text, "Automatic merge failed")
				|| Contains(text, "Merge conflict") =>
				Strings.GitConflict,

			_ when Contains(text, "nothing to commit") || Contains(text, "no changes added to commit") =>
				Strings.GitNothingToCommit,

			_ when Contains(text, "Everything up-to-date") => Strings.GitUpToDate,

			_ when Contains(text, "No local changes to save") => Strings.GitStashNothing,

			// 全新仓库还没有 HEAD：stash 需要至少一个提交作为基线。
			_ when Contains(text, "You do not have the initial commit yet") => Strings.GitNoInitialCommit,

			_ => null,
		};
	}

	private static bool Contains(string text, string needle) =>
		text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// Git 写操作：暂存、取消暂存、提交、分支、远端同步（fetch/pull/push）与 stash。
/// 全部通过 <see cref="ProcessStartInfo.ArgumentList"/> 传参，不拼 shell；名称以 <c>-</c> 开头的一律拒绝，
/// 避免被 git 当成选项。输出有界、命令有超时，不让一次卡住的 git 拖垮界面。
public static class GitWriter
{
	private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(30);

	private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(120);

	public static GitResult Init(string cwd) => Run(cwd, ["init"], Strings.GitInitDone);

	public static GitResult Stage(string cwd, IReadOnlyList<string> paths)
	{
		if (!HasPaths(paths))
		{
			return GitResult.Failure(Strings.GitNoSelection);
		}

		return Run(cwd, ["add", "--", .. paths], literalPathspecs: true);
	}

	public static GitResult Unstage(string cwd, IReadOnlyList<string> paths)
	{
		if (!HasPaths(paths))
		{
			return GitResult.Failure(Strings.GitNoSelection);
		}

		// HEAD 不存在（全新仓库）时 restore --staged 会失败，改用 reset。
		var arguments = new List<string> { "restore", "--staged", "--" };
		arguments.AddRange(paths);
		var result = Run(cwd, [.. arguments], literalPathspecs: true);
		if (result.Ok)
		{
			return result;
		}

		var reset = new List<string> { "reset", "--" };
		reset.AddRange(paths);
		var resetResult = Run(cwd, [.. reset], literalPathspecs: true);
		if (resetResult.Ok)
		{
			return resetResult;
		}

		// 还没有首个提交（unborn 分支）时 reset 也解析不到 HEAD：从索引里撤掉文件。
		var cached = new List<string> { "rm", "--cached", "--" };
		cached.AddRange(paths);
		return Run(cwd, [.. cached], literalPathspecs: true);
	}

	public static GitResult Commit(string cwd, string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return GitResult.Failure(Strings.GitCommitMessageRequired);
		}

		return Run(cwd, ["commit", "-m", message]);
	}

	public static IReadOnlyList<string> Branches(string cwd)
	{
		var capture = RunCapture(cwd, ["branch", "--format=%(HEAD)\t%(refname:short)"], LocalTimeout);
		if (!capture.Ok)
		{
			return [];
		}

		var current = string.Empty;
		var others = new List<string>();
		foreach (var raw in capture.Stdout.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			if (line.Length == 0)
			{
				continue;
			}

			var tab = line.IndexOf('\t');
			if (tab < 0)
			{
				continue;
			}

			var name = line[(tab + 1)..].Trim();
			if (name.Length == 0)
			{
				continue;
			}

			if (line[..tab].Trim() == "*")
			{
				current = name;
			}
			else
			{
				others.Add(name);
			}
		}

		if (current.Length > 0)
		{
			others.Insert(0, current);
		}

		return others;
	}

	public static GitResult CreateBranch(string cwd, string name)
	{
		if (!IsValidName(name))
		{
			return GitResult.Failure(Strings.GitBranchNameInvalid);
		}

		return Run(cwd, ["switch", "-c", name]);
	}

	public static GitResult SwitchBranch(string cwd, string name)
	{
		if (!IsValidName(name))
		{
			return GitResult.Failure(Strings.GitBranchNameInvalid);
		}

		return Run(cwd, ["switch", name]);
	}

	public static GitResult Fetch(string cwd)
	{
		if (!HasRemotes(cwd))
		{
			return GitResult.Failure(Strings.GitNoRemote);
		}

		return RunNetwork(cwd, ["fetch", "--prune"], Strings.GitFetchDone);
	}

	/// 拉取。<c>--ff-only</c> 是刻意选的：这里没有冲突解决界面，自动 merge 或 rebase 会把仓库
	/// 推进到无法从界面收拾的状态；不能快进就报错，提示用户去终端。
	public static GitResult Pull(string cwd)
	{
		if (!HasRemotes(cwd))
		{
			return GitResult.Failure(Strings.GitNoRemote);
		}

		if (!HasUpstream(cwd))
		{
			return GitResult.Failure(Strings.GitNoUpstream);
		}

		return RunNetwork(cwd, ["pull", "--ff-only"], Strings.GitPullDone);
	}

	/// 推送。没有上游分支时自动 <c>push -u</c> 建立跟踪：首次推送是最常见的动作，
	/// 而 <c>git push</c> 在无上游时的报错正是新手最容易卡住的地方。
	public static GitResult Push(string cwd)
	{
		if (!HasRemotes(cwd))
		{
			return GitResult.Failure(Strings.GitNoRemote);
		}

		if (HasUpstream(cwd))
		{
			return RunNetwork(cwd, ["push"], Strings.GitPushDone);
		}

		// 分离头指针下建跟踪分支会产生一个用户没预期的分支名，明确拒绝。
		var branch = GitStatusReader.Upstream(cwd).Branch;
		if (branch.Length == 0 || string.Equals(branch, "HEAD", StringComparison.Ordinal))
		{
			return GitResult.Failure(Strings.GitDetachedHead);
		}

		// 首次推送要挑远端，不能写死 origin：不少仓库的远端叫 upstream / github，
		// 写死会让一次本该成功的推送报成「'origin' does not appear to be a git repository」。
		if (ResolvePushRemote(cwd) is not { } remote)
		{
			return GitResult.Failure(Strings.GitAmbiguousRemote);
		}

		return RunNetwork(cwd, ["push", "--set-upstream", remote, branch], Strings.GitPushUpstreamCreated);
	}

	/// 首次推送的远端选择：只有一个远端就用它；有 <c>origin</c> 用 <c>origin</c>；
	/// 多个且都不是 origin 时不猜（推错远端比不推难查得多），返回 <see langword="null"/> 让界面提示用户指定。
	public static string? ResolvePushRemote(string cwd)
	{
		var capture = RunCapture(cwd, ["remote"], LocalTimeout);
		if (!capture.Ok)
		{
			return null;
		}

		var remotes = capture.Stdout
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();

		if (remotes.Count == 1)
		{
			return remotes[0];
		}

		return remotes.FirstOrDefault(static name => string.Equals(name, "origin", StringComparison.Ordinal));
	}

	public static GitResult StashPush(string cwd)
	{
		var capture = RunCapture(cwd, ["stash", "push", "--include-untracked"], LocalTimeout);
		if (!capture.Ok)
		{
			return Failure(capture);
		}

		// 没有改动时 git 也返回 0，只在输出里说 "No local changes to save"。
		// 这不是错误，但要说出来，否则用户会以为东西已经收进去了。
		var output = Combine(capture.Stdout, capture.Stderr);
		return output.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase)
			? GitResult.Failure(Strings.GitStashNothing)
			: GitResult.Success(Strings.GitStashCreated);
	}

	public static GitResult StashPop(string cwd, string reference = "stash@{0}")
	{
		if (!IsValidStashReference(reference))
		{
			return GitResult.Failure(Strings.GitStashReferenceInvalid);
		}

		return Run(cwd, ["stash", "pop", reference], Strings.GitStashPopped);
	}

	/// <summary>
	/// 放弃工作区改动：已跟踪文件回到索引里的内容，未跟踪文件从磁盘删除。
	/// 调用方负责先确认与打检查点：这一步不可撤销。
	/// </summary>
	public static GitResult Discard(string cwd, IReadOnlyList<string> tracked, IReadOnlyList<string> untracked)
	{
		if (!HasPaths(tracked) && !HasPaths(untracked))
		{
			return GitResult.Failure(Strings.GitNoSelection);
		}

		lock (RepoLockFor(cwd))
		{
			var failed = new List<string>();
			var details = new List<string>();
			if (HasPaths(tracked))
			{
				var restore = RunCapture(
					cwd,
					["restore", "--worktree", "--", .. tracked],
					LocalTimeout,
					literalPathspecs: true);
				if (!restore.Ok)
				{
					// 老 git 或索引里没有该路径时 restore 会失败，退回 checkout 的等价语义。
					var checkout = RunCapture(
						cwd,
						["checkout", "--", .. tracked],
						LocalTimeout,
						literalPathspecs: true);
					if (!checkout.Ok)
					{
						failed.Add(Failure(checkout).Message);
						details.Add(Limit(Combine(checkout.Stdout, checkout.Stderr)));
					}
				}
			}

			if (HasPaths(untracked))
			{
				// git clean 对含 .git 的目录只提示 "Skipping repository" 并返回 0：面板会报成功，
				// 目录却原样还在。这里先拒绝，避免“已放弃”与实际不符（也避免 -ff 删掉整棵仓库）。
				foreach (var path in untracked)
				{
					if (IsNestedRepository(cwd, path))
					{
						return GitResult.Failure(Strings.GitDiscardNestedRepository(path));
					}
				}

				// 用 git clean 而不是直接删文件：它只清未跟踪项，不会顺着路径删到被忽略或已跟踪的内容。
				var clean = RunCapture(
					cwd,
					["clean", "-f", "-d", "--", .. untracked],
					LocalTimeout,
					literalPathspecs: true);
				if (!clean.Ok)
				{
					failed.Add(Failure(clean).Message);
					details.Add(Limit(Combine(clean.Stdout, clean.Stderr)));
				}
			}

			return failed.Count == 0
				? GitResult.Success(Strings.GitDiscardDone)
				: GitResult.Failure(failed[0], details.Count > 0 ? details[0] : null);
		}
	}

	/// <summary>撤销一个提交：生成一个新提交把它的改动反向应用。</summary>
	public static GitResult RevertCommit(string cwd, string hash)
	{
		if (!IsFullCommitHash(hash))
		{
			return GitResult.Failure(Strings.GitCommitHashInvalid);
		}

		return Run(cwd, ["revert", "--no-edit", hash], Strings.GitRevertDone);
	}

	/// <summary>把某个提交拣选到当前分支（生成一个新提交）。</summary>
	public static GitResult CherryPickCommit(string cwd, string hash)
	{
		if (!IsFullCommitHash(hash))
		{
			return GitResult.Failure(Strings.GitCommitHashInvalid);
		}

		return Run(cwd, ["cherry-pick", hash], Strings.GitCherryPickDone);
	}

	/// <summary>软重置：HEAD 移到目标提交，改动留在暂存区，不碰工作区文件。</summary>
	public static GitResult ResetSoft(string cwd, string hash)
	{
		if (!IsFullCommitHash(hash))
		{
			return GitResult.Failure(Strings.GitCommitHashInvalid);
		}

		return Run(cwd, ["reset", "--soft", hash], Strings.GitResetSoftDone);
	}

	/// <summary>应用一条 stash 但保留条目；与 pop 分清，误点还有退路。</summary>
	public static GitResult StashApply(string cwd, string reference = "stash@{0}")
	{
		if (!IsValidStashReference(reference))
		{
			return GitResult.Failure(Strings.GitStashReferenceInvalid);
		}

		var capture = RunCapture(cwd, ["stash", "apply", reference], LocalTimeout);
		if (!capture.Ok)
		{
			return Failure(capture);
		}

		var output = Combine(capture.Stdout, capture.Stderr);
		return output.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
			? GitResult.Failure(Strings.GitConflict)
			: GitResult.Success(Strings.GitStashApplied);
	}

	/// <summary>列出仓库的全部工作树（含主工作区与子代理的隔离副本）。</summary>
	public static IReadOnlyList<GitWorktree> Worktrees(string cwd)
	{
		var capture = RunCapture(cwd, ["worktree", "list", "--porcelain"], LocalTimeout);
		if (!capture.Ok)
		{
			return [];
		}

		var entries = new List<GitWorktree>();
		var path = string.Empty;
		var head = string.Empty;
		var branch = string.Empty;
		var detached = false;

		void Flush()
		{
			if (path.Length == 0)
			{
				return;
			}

			// porcelain 的第一条就是主工作区：git 保证顺序，不靠路径猜测。
			entries.Add(new GitWorktree(path, head, branch, detached, entries.Count == 0));
			path = string.Empty;
			head = string.Empty;
			branch = string.Empty;
			detached = false;
		}

		foreach (var raw in capture.Stdout.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			if (line.Length == 0)
			{
				Flush();
				continue;
			}

			var space = line.IndexOf(' ');
			var key = space < 0 ? line : line[..space];
			var value = space < 0 ? string.Empty : line[(space + 1)..].Trim();
			switch (key)
			{
				case "worktree":
					path = value;
					break;
				case "HEAD":
					head = value;
					break;
				case "branch":
					branch = value;
					break;
				case "detached":
					detached = true;
					break;
			}
		}

		Flush();
		return entries;
	}

	/// <summary>清理目录已经不在的工作树登记（<c>git worktree prune</c>），分支不受影响。</summary>
	public static GitResult WorktreePrune(string repository) =>
		Run(repository, ["worktree", "prune"], Strings.GitWorktreePruneDone);

	/// <summary>只删工作树目录，保留分支：面板里删别人的副本时分支可能有内容。</summary>
	public static GitResult WorktreeRemoveOnly(string repository, string path)
	{
		lock (RepoLockFor(repository))
		{
			var removed = RunCapture(repository, ["worktree", "remove", "--force", "--", path], LocalTimeout);
			if (!removed.Ok)
			{
				return Failure(removed);
			}

			RunCapture(repository, ["worktree", "prune"], LocalTimeout);
			return GitResult.Success(Strings.GitWorktreeRemoved);
		}
	}

	public static GitResult StashDrop(string cwd, string reference)
	{
		if (!IsValidStashReference(reference))
		{
			return GitResult.Failure(Strings.GitStashReferenceInvalid);
		}

		return Run(cwd, ["stash", "drop", reference]);
	}

	public static IReadOnlyList<GitStashEntry> Stashes(string cwd)
	{
		var capture = RunCapture(cwd, ["stash", "list", "--format=%gd\t%gs"], LocalTimeout);
		if (!capture.Ok)
		{
			return [];
		}

		var entries = new List<GitStashEntry>();
		foreach (var raw in capture.Stdout.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			if (line.Length == 0)
			{
				continue;
			}

			var tab = line.IndexOf('\t');
			if (tab <= 0)
			{
				continue;
			}

			entries.Add(new GitStashEntry(line[..tab].Trim(), line[(tab + 1)..].Trim()));
		}

		return entries;
	}

	public static bool HasRemotes(string cwd) =>
		RunCapture(cwd, ["remote"], LocalTimeout).Stdout.Trim().Length > 0;

	public static bool HasUpstream(string cwd) => GitStatusReader.Upstream(cwd).Upstream.Length > 0;

	private static bool HasPaths(IReadOnlyList<string> paths) => paths.Count > 0 && paths.Any(static path => path.Length > 0);

	private static bool IsValidName(string name) =>
		name.Length > 0 && name[0] != '-' && name.Trim() == name && !name.Any(char.IsWhiteSpace);

	/// 未跟踪的目录里还套着一个仓库（含 gitfile 形态）时不能交给 clean：它要么静默跳过，
	/// 要么在 -ff 下整棵删掉，两种都不是“放弃一个未跟踪文件”的意思。
	private static bool IsNestedRepository(string cwd, string path)
	{
		try
		{
			var full = Path.Combine(cwd, path.TrimEnd('/', '\\'));
			if (!Directory.Exists(full))
			{
				return false;
			}

			var dotGit = Path.Combine(full, ".git");
			return Directory.Exists(dotGit) || File.Exists(dotGit);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return false;
		}
	}

	/// 提交操作只接受完整对象名：短引用可能命中同名分支或前缀歧义，以 `-` 开头也会被当成选项。
	private static bool IsFullCommitHash(string value) =>
		value.Length is 40 or 64 && value.All(static character => char.IsAsciiHexDigit(character));

	private static bool IsValidStashReference(string reference) =>
		reference.StartsWith("stash@{", StringComparison.Ordinal) && reference.EndsWith('}') && reference.Length > 7;

	/// <summary>解析仓库根与基线 commit；worktree 创建前先取这两个值，diff 才能钉在创建时的基线。</summary>
	public static (bool Ok, string Repository, string BaseCommit) WorktreeBase(string cwd, string reference)
	{
		var top = RunCapture(cwd, ["rev-parse", "--show-toplevel"], LocalTimeout);
		if (!top.Ok)
		{
			return (false, string.Empty, Combine(top.Stdout, top.Stderr));
		}

		var repository = top.Stdout.Trim();
		if (repository.Length == 0)
		{
			return (false, string.Empty, Strings.GitNotRepository);
		}

		var baseRef = string.IsNullOrWhiteSpace(reference) ? "HEAD" : reference.Trim();
		var commit = RunCapture(repository, ["rev-parse", "--verify", "--end-of-options", baseRef + "^{commit}"], LocalTimeout);
		return commit.Ok
			? (true, repository, commit.Stdout.Trim())
			: (false, repository, Combine(commit.Stdout, commit.Stderr));
	}

	/// <summary>在仓库里建一个隔离副本；失败时由调用方清理目录与分支。</summary>
	public static GitResult WorktreeAdd(string repository, string path, string branch, string baseCommit)
	{
		// 三个参数都会进 argv：分支名以 `-` 开头会被 git 当成选项，路径必须写绝对路径。
		// 子代理的副本由代码生成，面板里的是用户输入，两条路都从同一处收口。
		if (!IsValidName(branch))
		{
			return GitResult.Failure(Strings.GitBranchNameInvalid);
		}

		if (baseCommit.Length == 0 || baseCommit[0] == '-')
		{
			return GitResult.Failure(Strings.GitWorktreeBaseMissing(baseCommit));
		}

		if (path.Length == 0 || path[0] == '-' || !Path.IsPathRooted(path))
		{
			return GitResult.Failure(Strings.GitWorktreePathInvalid(path));
		}

		// .git/worktrees 是共享元数据：两个子代理同时创建副本会互相踩。
		lock (RepoLockFor(repository))
		{
			var capture = RunCapture(
				repository,
				["worktree", "add", path, "-b", branch, baseCommit],
				LocalTimeout);
			return capture.Ok ? GitResult.Success(Strings.GitWorktreeAdded) : Failure(capture);
		}
	}

	/// <summary>删掉隔离副本与分支；副本已经不在了也算删掉目录这一步失败，调用方按提示处理。</summary>
	public static GitResult WorktreeRemove(string repository, string path, string branch)
	{
		// 两个值来自登记表与会话文件，可能被外部改写：以 `-` 开头会被 git 当成选项。
		if (path.Length == 0 || path[0] == '-' || !Path.IsPathRooted(path))
		{
			return GitResult.Failure(Strings.GitWorktreePathInvalid(path));
		}

		if (!IsValidName(branch))
		{
			return GitResult.Failure(Strings.GitBranchNameInvalid);
		}

		lock (RepoLockFor(repository))
		{
			var removed = RunCapture(repository, ["worktree", "remove", "--force", "--", path], LocalTimeout);
			if (!removed.Ok)
			{
				return Failure(removed);
			}

			// 分支可能已经被合并或手动删掉：删不掉不算整体失败。
			var deleted = RunCapture(repository, ["branch", "-D", "--", branch], LocalTimeout);
			RunCapture(repository, ["worktree", "prune"], LocalTimeout);
			return deleted.Ok
				? GitResult.Success(Strings.GitWorktreeRemoved)
				: GitResult.Failure(Strings.GitWorktreeBranchKept, Limit(Combine(deleted.Stdout, deleted.Stderr)));
		}
	}

	/// <summary>隔离副本相对基线的完整 patch（含二进制）。</summary>
	public static (bool Ok, string Text) WorktreePatch(string worktree, string baseCommit)
	{
		var capture = RunCapture(
			worktree,
			[
				"diff", baseCommit, "--binary", "--no-color", "--no-ext-diff", "--no-textconv",
				"--src-prefix=a/", "--dst-prefix=b/", "--line-prefix=", "--no-relative",
			],
			LocalTimeout);
		return (capture.Ok, capture.Ok ? capture.Stdout : Combine(capture.Stdout, capture.Stderr));
	}

	/// <summary>隔离副本的 diff stat；卡片上的一行摘要用。</summary>
	public static (bool Ok, string Text) WorktreeStat(string worktree, string baseCommit)
	{
		var capture = RunCapture(worktree, ["diff", baseCommit, "--stat", "--no-color"], LocalTimeout);
		return (capture.Ok, capture.Ok ? capture.Stdout.Trim() : Combine(capture.Stdout, capture.Stderr));
	}

	/// <summary>把 patch 应用到主工作区；先 --check，冲突时不动文件。</summary>
	public static GitResult WorktreeApply(string repository, string patch)
	{
		var path = Path.Combine(Path.GetTempPath(), "piweft-worktree-" + Guid.NewGuid().ToString("N") + ".patch");
		try
		{
			File.WriteAllText(path, patch);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return GitResult.Failure(Strings.GitPatchWriteFailed, exception.Message);
		}

		try
		{
			lock (RepoLockFor(repository))
			{
				var check = RunCapture(repository, ["apply", "--check", "--binary", path], LocalTimeout);
				if (!check.Ok)
				{
					return GitResult.Failure(Strings.GitPatchConflict, Limit(Combine(check.Stdout, check.Stderr)));
				}

				var applied = RunCapture(repository, ["apply", "--binary", path], LocalTimeout);
				return applied.Ok ? GitResult.Success(Strings.GitPatchApplied) : Failure(applied);
			}
		}
		finally
		{
			try
			{
				File.Delete(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 临时文件删不掉不影响结果。
			}
		}
	}

	private static GitResult Run(
		string cwd,
		string[] arguments,
		string? successMessage = null,
		bool literalPathspecs = false)
	{
		lock (RepoLockFor(cwd))
		{
			return Finish(
				RunCapture(cwd, arguments, LocalTimeout, literalPathspecs: literalPathspecs),
				successMessage ?? Strings.GitDone);
		}
	}

	/// 网络操作：关掉终端提示。<c>GIT_TERMINAL_PROMPT=0</c> 把「等用户输入密码」从挂死
	/// （要等满 120 秒超时）变成立刻返回可分类的认证错误，凭据管理器与 askpass 不受影响。
	private static GitResult RunNetwork(string cwd, string[] arguments, string successMessage)
	{
		lock (RepoLockFor(cwd))
		{
			return Finish(RunCapture(cwd, arguments, NetworkTimeout, disableTerminalPrompt: true), successMessage);
		}
	}

	/// 同一仓库的写操作串行：另一个会话的 stash/checkout 会把这边的未提交改动卷走。
	/// 按规范化 cwd 归到固定条带，不建无界字典。仓库根与子目录可能落在不同条带，
	/// 桌面会话通常以仓库根为 cwd；<c>bash</c> 里的 git 不经过这里。
	private static readonly Lock[] RepoStripes = CreateRepoStripes();

	private static Lock[] CreateRepoStripes()
	{
		var stripes = new Lock[64];
		for (var index = 0; index < stripes.Length; index++)
		{
			stripes[index] = new Lock();
		}

		return stripes;
	}

	private static Lock RepoLockFor(string cwd)
	{
		string key;
		try
		{
			key = Path.GetFullPath(cwd);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			key = cwd;
		}

		if (OperatingSystem.IsWindows())
		{
			key = key.ToUpperInvariant();
		}

		return RepoStripes[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)RepoStripes.Length];
	}

	private static GitResult Finish(GitCapture capture, string successMessage)
	{
		if (!capture.Ok)
		{
			return Failure(capture);
		}

		var text = Combine(capture.Stdout, capture.Stderr);
		return GitResult.Success(text.Length > 0 ? GitFailure.Classify(text) ?? successMessage : successMessage);
	}

	private static GitResult Failure(GitCapture capture)
	{
		if (capture.TimedOut)
		{
			return GitResult.Failure(Strings.GitTimeout, null);
		}

		var output = Combine(capture.Stdout, capture.Stderr);
		if (output.Length == 0)
		{
			return GitResult.Failure(Strings.GitFailed);
		}

		var friendly = GitFailure.Classify(output) ?? FirstLine(output);
		return GitResult.Failure(friendly.Length > 0 ? friendly : Strings.GitFailed, Limit(output));
	}

	private static string Combine(string stdout, string stderr)
	{
		var trimmedOut = stdout.TrimEnd('\r', '\n');
		var trimmedErr = stderr.TrimEnd('\r', '\n');
		if (trimmedOut.Length == 0)
		{
			return trimmedErr;
		}

		return trimmedErr.Length == 0 ? trimmedOut : trimmedOut + "\n" + trimmedErr;
	}

	/// 捕获一次 git 调用。stdout/stderr 并发读：顺序读时子进程某个缓冲（约 64KB）写满就会阻塞，
	/// 导致进程不退出，WaitForExit 的超时也等不到，UI 会冻结。
	private static GitCapture RunCapture(
		string cwd,
		string[] arguments,
		TimeSpan timeout,
		bool disableTerminalPrompt = false,
		bool literalPathspecs = false)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
		};

		// git 的消息是本地化的：这台机器上 `git stash push` 说的是「没有要保存的本地修改」。
		// 分类表按英文特征串写，不钉住语言就会静默全部失配（失败被当成成功、或者只报出
		// 一句「来自 /tmp/remote」这种半截输出）。钉住 C 语言环境后，用户看到的是我们翻译过的
		// 中文提示，原始英文输出留在 GitResult.Detail 里。
		GitStatusReader.ApplyLocale(startInfo);

		if (disableTerminalPrompt)
		{
			startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		}

		if (literalPathspecs)
		{
			// 路径来自 git status 的原始输出，可能含 * ? [ 这类字符；`--` 之后必须按字面量
			// 匹配，否则名为 `a*.txt` 的文件会连带命中 `ab.txt`。不能全局设：stash 内部
			// 依赖普通 pathspec 语义，全局钉住会让 --include-untracked 漏掉未跟踪文件。
			startInfo.Environment["GIT_LITERAL_PATHSPECS"] = "1";
		}

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return new GitCapture(string.Empty, Strings.GitUnavailable, false, false);
			}

			var stdoutTask = process.StandardOutput.ReadToEndAsync();
			var stderrTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit((int)timeout.TotalMilliseconds))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (InvalidOperationException)
				{
					// 已经退出。
				}

				return new GitCapture(string.Empty, Strings.GitTimeout, false, true);
			}

			return new GitCapture(
				stdoutTask.GetAwaiter().GetResult(),
				stderrTask.GetAwaiter().GetResult(),
				process.ExitCode == 0,
				false);
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return new GitCapture(string.Empty, Strings.GitUnavailable, false, false);
		}
		catch (InvalidOperationException)
		{
			return new GitCapture(string.Empty, Strings.GitUnavailable, false, false);
		}
	}

	private static string Limit(string text) => text.Length <= 4096 ? text : text[..4096] + "\n…";

	private static string FirstLine(string text)
	{
		var trimmed = text.Trim();
		var index = trimmed.IndexOf('\n');
		return (index < 0 ? trimmed : trimmed[..index]).Trim();
	}

	private readonly record struct GitCapture(string Stdout, string Stderr, bool Ok, bool TimedOut);
}
