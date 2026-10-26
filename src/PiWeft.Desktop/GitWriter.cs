using System.Diagnostics;

namespace PiWeft.Desktop;

/// <summary>
/// 一次 Git 写操作的结果。<see cref="Message"/> 是给人看的一句话结论（成功摘要或分类后的失败原因），
/// <see cref="Detail"/> 是 git 的原始输出尾部，只在需要排查时展示 —— 直接把
/// <c>fatal: could not read Username for 'https://github.com': terminal prompts disabled</c>
/// 甩到状态行里，用户只知道「失败了」。
/// </summary>
public sealed record GitResult(bool Ok, string Message, string? Detail = null)
{
	public static GitResult Success(string message = "") => new(true, message);

	public static GitResult Failure(string message, string? detail = null) => new(false, message, detail);
}

/// <summary>一个 stash 条目。</summary>
public sealed record GitStashEntry(string Reference, string Description);

/// <summary>
/// 把 git 的原始输出翻成一句能照着做的中文。git 的错误文本是稳定的英文（且被脚本广泛依赖），
/// 所以按特征串分类是可靠的；分类不出来就退回 output 的首行，至少不吞信息。
/// <para>
/// 注意区分「工作区状态不对」与「环境/认证/网络不通」：前者用户能自己解决，
/// 后者要看凭据或代理 —— 提示必须不同，否则用户会去错误的方向折腾。
/// </para>
/// </summary>
public static class GitFailure
{
	/// <summary>按输出特征分类；未命中时返回 <see langword="null"/>。</summary>
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

/// <summary>
/// Git 写操作：暂存、取消暂存、提交、分支、远端同步（fetch/pull/push）与 stash。
/// 全部通过 <see cref="ProcessStartInfo.ArgumentList"/> 传参，不拼 shell；名称以 <c>-</c> 开头的一律拒绝，
/// 避免被 git 当成选项。输出有界、命令有超时，不让一次卡住的 git 拖垮界面。
/// </summary>
public static class GitWriter
{
	/// <summary>本地操作的上限：超过这个时间基本是 index.lock 残留或磁盘打嗝。</summary>
	private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(30);

	/// <summary>网络操作的上限：克隆大仓或慢链路正常也要几十秒。</summary>
	private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(120);

	public static GitResult Stage(string cwd, IReadOnlyList<string> paths)
	{
		if (!HasPaths(paths))
		{
			return GitResult.Failure(Strings.GitNoSelection);
		}

		return Run(cwd, ["add", "--", .. paths]);
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
		var result = Run(cwd, [.. arguments]);
		if (result.Ok)
		{
			return result;
		}

		var reset = new List<string> { "reset", "--" };
		reset.AddRange(paths);
		return Run(cwd, [.. reset]);
	}

	public static GitResult Commit(string cwd, string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return GitResult.Failure(Strings.GitCommitMessageRequired);
		}

		return Run(cwd, ["commit", "-m", message]);
	}

	/// <summary>本地分支名列表，当前分支排在最前。</summary>
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

	/// <summary>只更新远端跟踪分支，不动工作区。拉取前先看清有没有新提交时用。</summary>
	public static GitResult Fetch(string cwd)
	{
		if (!HasRemotes(cwd))
		{
			return GitResult.Failure(Strings.GitNoRemote);
		}

		return RunNetwork(cwd, ["fetch", "--prune"], Strings.GitFetchDone);
	}

	/// <summary>
	/// 拉取。<c>--ff-only</c> 是刻意选的：这里没有冲突解决界面，自动 merge 或 rebase 会把仓库
	/// 推进到一个用户无法从界面收拾的状态；不能快进时宁可报错并提示用户去终端处理。
	/// </summary>
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

	/// <summary>
	/// 推送。没有上游分支时自动 <c>push -u</c> 建立跟踪 —— 首次推送是最常见的动作，
	/// 而 <c>git push</c> 在无上游时的报错恰好是新手最容易卡住的地方。
	/// </summary>
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

		return RunNetwork(cwd, ["push", "--set-upstream", "origin", branch], Strings.GitPushUpstreamCreated);
	}

	/// <summary>把工作区（含未跟踪文件）收进 stash，得到一个干净的工作区。</summary>
	public static GitResult StashPush(string cwd)
	{
		var capture = RunCapture(cwd, ["stash", "push", "--include-untracked"], LocalTimeout);
		if (!capture.Ok)
		{
			return Failure(capture);
		}

		// 没有改动时 git 也返回 0，只在输出里说 "No local changes to save"。
		// 这不是错误，但必须说出来 —— 否则用户会以为东西已经收进去了。
		var output = Combine(capture.Stdout, capture.Stderr);
		return output.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase)
			? GitResult.Failure(Strings.GitStashNothing)
			: GitResult.Success(Strings.GitStashCreated);
	}

	/// <summary>应用最近一个 stash。冲突时 git 会保留该条目，界面据此提示用户。</summary>
	public static GitResult StashPop(string cwd)
	{
		return Run(cwd, ["stash", "pop"], Strings.GitStashPopped);
	}

	public static GitResult StashDrop(string cwd, string reference)
	{
		if (!IsValidStashReference(reference))
		{
			return GitResult.Failure(Strings.GitStashReferenceInvalid);
		}

		return Run(cwd, ["stash", "drop", reference]);
	}

	/// <summary>stash 列表，最近的在前。</summary>
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

	/// <summary>分支名不能为空、不能以 <c>-</c> 开头（会被当成选项），也不能含空白。</summary>
	private static bool IsValidName(string name) =>
		name.Length > 0 && name[0] != '-' && name.Trim() == name && !name.Any(char.IsWhiteSpace);

	/// <summary>只接受 <c>stash@{n}</c> 形态，避免把一个任意字符串当 git 选项传进去。</summary>
	private static bool IsValidStashReference(string reference) =>
		reference.StartsWith("stash@{", StringComparison.Ordinal) && reference.EndsWith('}') && reference.Length > 7;

	private static GitResult Run(string cwd, string[] arguments, string? successMessage = null) =>
		Finish(RunCapture(cwd, arguments, LocalTimeout), successMessage ?? Strings.GitDone);

	/// <summary>
	/// 网络操作：关掉终端提示。<c>GIT_TERMINAL_PROMPT=0</c> 把「等用户输入密码」从挂死
	/// （要等满 120 秒超时）变成立刻返回可分类的认证错误，凭据管理器与 askpass 不受影响。
	/// </summary>
	private static GitResult RunNetwork(string cwd, string[] arguments, string successMessage) =>
		Finish(RunCapture(cwd, arguments, NetworkTimeout, disableTerminalPrompt: true), successMessage);

	/// <summary>成功时的摘要：能分类就用分类结果（如「已经是最新的」），否则用操作自己的中文结论。</summary>
	private static GitResult Finish(GitCapture capture, string successMessage)
	{
		if (!capture.Ok)
		{
			return Failure(capture);
		}

		var text = Combine(capture.Stdout, capture.Stderr);
		return GitResult.Success(text.Length > 0 ? GitFailure.Classify(text) ?? successMessage : successMessage);
	}

	/// <summary>失败时优先给分类后的可操作提示，原始输出留在 <see cref="GitResult.Detail"/> 里。</summary>
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

	/// <summary>合并两路输出：git 把进度与错误都写在 stderr，成功摘要写在 stdout。</summary>
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

	/// <summary>
	/// 捕获一次 git 调用。stdout/stderr 并发读：顺序读时子进程某个缓冲（约 64KB）写满就会阻塞，
	/// 进而永远不退出 —— 那 WaitForExit 的超时也等不到，UI 会冻结。
	/// </summary>
	private static GitCapture RunCapture(
		string cwd,
		string[] arguments,
		TimeSpan timeout,
		bool disableTerminalPrompt = false)
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
