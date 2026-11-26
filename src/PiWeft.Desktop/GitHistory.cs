using System.Globalization;
using System.Text.RegularExpressions;

namespace PiWeft.Desktop;

public sealed record GitCommitEntry(
	string Hash,
	string AuthorName,
	string AuthorEmail,
	DateTimeOffset AuthorDate,
	IReadOnlyList<string> Parents,
	IReadOnlyList<string> Refs,
	string Subject)
{
	public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;

	public string FullMessage { get; init; } = string.Empty;

	public bool IsHead => Refs.Any(static reference => reference.StartsWith("HEAD ->", StringComparison.Ordinal));
}

public sealed record GitCommitFile(string Status, string Path, string? OriginalPath)
{
	/// 这一个文件的增删行数；二进制、纯改名与读不到统计时为 null。
	internal GitDiffStat? DiffStat { get; init; }

	public string Letter => Status.Length > 0 ? Status[..1] : "M";

	/// 取最后一段名字；未跟踪目录条目以 `/` 结尾时不能切成空串。
	public string FileName => GitPathText.LeafName(Path);
}

public sealed record GitCommitDetail(
	GitCommitEntry Commit,
	IReadOnlyList<GitCommitFile> Files,
	int Insertions,
	int Deletions,
	string? Error = null);

public sealed record GitLogResult(IReadOnlyList<GitCommitEntry> Commits, string? Error = null);

public sealed record GitBranchComparison(
	IReadOnlyList<GitCommitFile> Files,
	int Ahead,
	int Behind,
	string BaseHash,
	string TargetHash,
	string? Error = null);

public sealed record GitFileDiff(string Text, string? Error = null)
{
	public bool Ok => Error is null;
}

/// Git 历史只读查询：log、提交数、提交详情、分支三点比较与提交/比较文件 diff。
/// 所有 ref/hash 都先做形态校验，路径统一放在 <c>--</c> 之后。
public static class GitHistoryReader
{
	private const char FieldSeparator = '\u001f';
	private const char RecordSeparator = '\u001e';
	private const int MaxLogOutput = 2 * 1024 * 1024;
	private const int MaxDiffOutput = 4 * 1024 * 1024;

	private static readonly Regex InsertionsPattern = new(@"(\d+)\s+insertions?\(\+\)", RegexOptions.CultureInvariant);
	private static readonly Regex DeletionsPattern = new(@"(\d+)\s+deletions?\(-\)", RegexOptions.CultureInvariant);

	public static GitLogResult Log(
		string cwd,
		int maxEntries = 40,
		string? reference = null,
		bool allBranches = true)
	{
		maxEntries = Math.Clamp(maxEntries, 1, 200);
		var arguments = new List<string>
		{
			"log",
			$"--format=%H{FieldSeparator}%aN{FieldSeparator}%aE{FieldSeparator}%at{FieldSeparator}%P{FieldSeparator}%D{FieldSeparator}%s{RecordSeparator}",
			"-z",
			"--topo-order",
			$"-n{maxEntries}",
		};

		if (reference is { Length: > 0 })
		{
			if (!IsSafeReference(reference))
			{
				return new GitLogResult([], Strings.GitBranchNameInvalid);
			}

			arguments.Add(reference);
		}
		else if (allBranches)
		{
			arguments.Add($"--exclude={Checkpoints.RefPrefix}*");
			arguments.Add("--exclude=refs/stash");
			arguments.Add("--all");
		}
		else
		{
			arguments.Add("HEAD");
		}

		var (output, error, exitCode) = GitStatusReader.RunDetailed(cwd, [.. arguments], MaxLogOutput);
		if (exitCode != 0)
		{
			return new GitLogResult([], ErrorText(error));
		}

		return new GitLogResult(ParseLog(output), null);
	}

	public static int Count(string cwd, string? reference = null, bool allBranches = true)
	{
		var arguments = new List<string> { "rev-list", "--count" };
		if (reference is { Length: > 0 })
		{
			if (!IsSafeReference(reference))
			{
				return 0;
			}

			arguments.Add(reference);
		}
		else if (allBranches)
		{
			arguments.Add($"--exclude={Checkpoints.RefPrefix}*");
			arguments.Add("--exclude=refs/stash");
			arguments.Add("--all");
		}
		else
		{
			arguments.Add("HEAD");
		}

		var (output, _, exitCode) = GitStatusReader.RunDetailed(cwd, [.. arguments], 256);
		return exitCode == 0 && int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
			? Math.Max(0, count)
			: 0;
	}

	public static GitCommitDetail? Detail(string cwd, string hash)
	{
		if (!IsHash(hash))
		{
			return null;
		}

		var format = $"%H{FieldSeparator}%aN{FieldSeparator}%aE{FieldSeparator}%at{FieldSeparator}%P{FieldSeparator}%D{FieldSeparator}%B{RecordSeparator}";
		var (output, _, exitCode) = GitStatusReader.RunDetailed(
			cwd,
			["show", "-s", $"--format={format}", "-z", hash, "--"],
			MaxLogOutput);
		if (exitCode != 0)
		{
			return null;
		}

		var record = output.Split('\0', 2)[0].TrimEnd('\r', '\n', RecordSeparator);
		var fields = record.Split(FieldSeparator);
		if (fields.Length < 7)
		{
			return null;
		}

		var fullMessage = string.Join(FieldSeparator, fields[6..]).Trim();
		var commit = new GitCommitEntry(
			fields[0].Trim(),
			fields[1].Trim(),
			fields[2].Trim(),
			ParseDate(fields[3]),
			SplitList(fields[4], ' '),
			SplitList(fields[5], ','),
			FirstLine(fullMessage))
		{
			FullMessage = fullMessage,
		};
		var parent = commit.Parents.FirstOrDefault();
		var fileArguments = parent is { Length: > 0 }
			? new[] { "diff", "--name-status", "-z", "--find-renames", parent, commit.Hash }
			: new[] { "diff-tree", "--root", "--no-commit-id", "--name-status", "-r", "-z", "--find-renames", commit.Hash };
		var (filesOutput, fileError, filesExitCode) = GitStatusReader.RunDetailed(cwd, fileArguments, MaxDiffOutput);
		var (stats, truncated) = NumStat(cwd, parent, commit.Hash);
		var files = filesExitCode == 0 ? ParseNameStatus(filesOutput, stats) : [];
		// numstat 已经跑过一遍完整差异，合计直接从它加出来，不再为 shortstat 再跑一次。
		// 被 4 MiB 上限截断时退回 shortstat：它只有一行汇总，不受这个上限影响。
		var (insertions, deletions) = stats is { } counted && !truncated
			? Sum(counted.Values)
			: ShortStat(cwd, commit.Hash);
		return new GitCommitDetail(
			commit,
			files,
			insertions,
			deletions,
			filesExitCode == 0 ? null : ErrorText(fileError));
	}

	public static GitFileDiff CommitFileDiff(
		string cwd,
		string commitHash,
		string? parentHash,
		string path,
		string? originalPath = null,
		int maxLines = 400)
	{
		if (!IsHash(commitHash) || path.Length == 0)
		{
			return new GitFileDiff(string.Empty, Strings.GitDiffFailed);
		}

		var arguments = parentHash is { Length: > 0 } && IsHash(parentHash)
			? new List<string> { "diff", "--no-color", "--unified=3", parentHash, commitHash, "--" }
			: new List<string> { "show", "--format=", "--no-color", "--unified=3", commitHash, "--" };
		if (!string.IsNullOrEmpty(originalPath) && !string.Equals(originalPath, path, StringComparison.Ordinal))
		{
			arguments.Add(originalPath);
		}

		arguments.Add(path);
		var (output, error, exitCode) = GitStatusReader.RunDetailed(cwd, [.. arguments], 512 * 1024);
		return exitCode == 0
			? new GitFileDiff(GitStatusReader.Truncate(output, maxLines))
			: new GitFileDiff(string.Empty, ErrorText(error));
	}

	public static GitBranchComparison Compare(string cwd, string baseReference, string targetReference)
	{
		var baseHash = Resolve(cwd, baseReference);
		var targetHash = Resolve(cwd, targetReference);
		if (baseHash is null || targetHash is null)
		{
			return new GitBranchComparison([], 0, 0, string.Empty, string.Empty, Strings.GitCompareFailed);
		}

		var range = $"{baseHash}...{targetHash}";
		var (filesOutput, filesError, filesExitCode) = GitStatusReader.RunDetailed(
			cwd,
			["diff", "--name-status", "-z", "--diff-filter=ADMR", range],
			MaxDiffOutput);
		if (filesExitCode != 0)
		{
			return new GitBranchComparison([], 0, 0, baseHash, targetHash, ErrorText(filesError));
		}

		var (countOutput, _, countExitCode) = GitStatusReader.RunDetailed(
			cwd,
			["rev-list", "--left-right", "--count", range],
			256);
		var ahead = 0;
		var behind = 0;
		if (countExitCode == 0)
		{
			var parts = countOutput.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2)
			{
				_ = int.TryParse(parts[0], out behind);
				_ = int.TryParse(parts[1], out ahead);
			}
		}

		var (statOutput, _, statExitCode) = GitStatusReader.RunDetailed(
			cwd,
			["diff", "--no-relative", "--numstat", "-z", "--diff-filter=ADMR", range],
			MaxDiffOutput);
		var stats = statExitCode == 0 ? GitDiffStatsReader.Parse(statOutput) : null;
		return new GitBranchComparison(ParseNameStatus(filesOutput, stats), ahead, behind, baseHash, targetHash);
	}

	/// 工作区相对分叉点的全貌：base 与 HEAD 的 merge-base 之后，再加上未提交与未跟踪文件。
	/// dsh 的本地改动视图就是这一段，PiWeft 用它回答「这个分支连同没提交的东西相对 main 变了什么」。
	public static GitBranchComparison CompareWorkingTree(string cwd, string baseReference)
	{
		var baseHash = Resolve(cwd, baseReference);
		if (baseHash is null)
		{
			return new GitBranchComparison([], 0, 0, string.Empty, WorktreeTarget, Strings.GitCompareFailed);
		}

		// 分叉点优先：直接对 base 的 HEAD 比会把 base 分支后来的提交算成「回退」。
		var fork = MergeBase(cwd, baseHash, "HEAD") ?? baseHash;
		var (filesOutput, filesError, filesExitCode) = GitStatusReader.RunDetailed(
			cwd,
			["diff", "--name-status", "-z", "--diff-filter=ADMR", fork],
			MaxDiffOutput);
		if (filesExitCode != 0)
		{
			return new GitBranchComparison([], 0, 0, fork, WorktreeTarget, ErrorText(filesError));
		}

		var (statOutput, _, statExitCode) = GitStatusReader.RunDetailed(
			cwd,
			["diff", "--no-relative", "--numstat", "-z", "--diff-filter=ADMR", fork],
			MaxDiffOutput);
		var stats = statExitCode == 0 ? GitDiffStatsReader.Parse(statOutput) : null;
		var files = ParseNameStatus(filesOutput, stats).ToList();

		// 未跟踪文件不在 git diff 里：单独补上，字母用问号，与改动列表同一套读法。
		var (untrackedOutput, _, untrackedExitCode) = GitStatusReader.RunDetailed(
			cwd,
			["ls-files", "--others", "--exclude-standard", "-z"],
			MaxDiffOutput);
		// 输出被上限截断时末尾是标记行，不是路径；宁可少列未跟踪项，也不把标记当文件名。
		if (untrackedExitCode == 0 && !GitStatusReader.IsTruncated(untrackedOutput))
		{
			foreach (var path in untrackedOutput.Split('\0'))
			{
				if (path.Length > 0)
				{
					files.Add(new GitCommitFile("?", path, null));
				}
			}
		}

		var ahead = 0;
		var (countOutput, _, countExitCode) = GitStatusReader.RunDetailed(
			cwd,
			["rev-list", "--count", $"{fork}..HEAD"],
			256);
		if (countExitCode == 0)
		{
			_ = int.TryParse(countOutput.Trim(), out ahead);
		}

		return new GitBranchComparison(files, ahead, 0, fork, WorktreeTarget);
	}

	/// 工作区相对分叉点的单文件补丁；未跟踪文件交给调用方用合成 diff。
	public static GitFileDiff WorkingTreeFileDiff(string cwd, string forkHash, string path, int maxLines = 400)
	{
		if (!IsHash(forkHash) || path.Length == 0)
		{
			return new GitFileDiff(string.Empty, Strings.GitDiffFailed);
		}

		var (output, error, exitCode) = GitStatusReader.RunDetailed(
			cwd,
			["diff", "--no-color", "--unified=3", forkHash, "--", path],
			512 * 1024);
		return exitCode == 0
			? new GitFileDiff(GitStatusReader.Truncate(output, maxLines))
			: new GitFileDiff(string.Empty, ErrorText(error));
	}

	/// 「目标 = 工作区」在比较结果里的代号：分支名不允许控制字符，不会撞名。
	internal const string WorktreeTarget = "\u0001worktree";

	public static GitFileDiff CompareFileDiff(
		string cwd,
		string baseHash,
		string targetHash,
		string path,
		string? originalPath = null,
		int maxLines = 400)
	{
		if (!IsHash(baseHash) || !IsHash(targetHash) || path.Length == 0)
		{
			return new GitFileDiff(string.Empty, Strings.GitDiffFailed);
		}

		var arguments = new List<string>
		{
			"diff", "--no-color", "--unified=3", $"{baseHash}...{targetHash}", "--",
		};
		if (!string.IsNullOrEmpty(originalPath) && !string.Equals(originalPath, path, StringComparison.Ordinal))
		{
			arguments.Add(originalPath);
		}

		arguments.Add(path);
		var (output, error, exitCode) = GitStatusReader.RunDetailed(cwd, [.. arguments], 512 * 1024);
		return exitCode == 0
			? new GitFileDiff(GitStatusReader.Truncate(output, maxLines))
			: new GitFileDiff(string.Empty, ErrorText(error));
	}

	/// 一个提交的逐文件增删数：与详情共用父提交的选取，合并提交按第一父比较。
	private static (IReadOnlyDictionary<string, GitDiffStat>? Stats, bool Truncated) NumStat(
		string cwd,
		string? parent,
		string hash)
	{
		string[] arguments = parent is { Length: > 0 }
			? ["diff", "--no-relative", "--numstat", "-z", "--find-renames", parent, hash]
			: ["diff-tree", "--no-relative", "--root", "--no-commit-id", "--numstat", "-r", "-z", "--find-renames", hash];
		var (output, _, exitCode) = GitStatusReader.RunDetailed(cwd, arguments, MaxDiffOutput);
		return exitCode == 0 ? (GitDiffStatsReader.Parse(output), GitStatusReader.IsTruncated(output)) : (null, false);
	}

	internal static IReadOnlyList<GitCommitFile> ParseNameStatus(
		string output,
		IReadOnlyDictionary<string, GitDiffStat>? stats = null)
	{
		var fields = output.Split('\0');
		var files = new List<GitCommitFile>();
		for (var index = 0; index < fields.Length; index++)
		{
			var status = fields[index].Trim();
			if (status.Length == 0 || index + 1 >= fields.Length)
			{
				continue;
			}

			if (status[0] is 'R' or 'C')
			{
				if (index + 2 >= fields.Length)
				{
					break;
				}

				var original = fields[index + 1];
				var target = fields[index + 2];
				files.Add(new GitCommitFile(status, target, original) { DiffStat = StatOf(stats, target) });
				index += 2;
			}
			else
			{
				var path = fields[index + 1];
				files.Add(new GitCommitFile(status, path, null) { DiffStat = StatOf(stats, path) });
				index++;
			}
		}

		return files;
	}

	private static GitDiffStat? StatOf(IReadOnlyDictionary<string, GitDiffStat>? stats, string path) =>
		stats is not null && stats.TryGetValue(path, out var stat) ? stat : null;

	private static (int Insertions, int Deletions) Sum(IEnumerable<GitDiffStat> stats)
	{
		var insertions = 0;
		var deletions = 0;
		foreach (var stat in stats)
		{
			insertions += stat.Added;
			deletions += stat.Removed;
		}

		return (insertions, deletions);
	}

	internal static IReadOnlyList<GitCommitEntry> ParseLog(string output)
	{
		var commits = new List<GitCommitEntry>();
		foreach (var raw in output.Split('\0'))
		{
			var record = raw.TrimEnd('\r', '\n', RecordSeparator);
			if (record.Length == 0)
			{
				continue;
			}

			var fields = record.Split(FieldSeparator);
			if (fields.Length < 7)
			{
				continue;
			}

			commits.Add(new GitCommitEntry(
				fields[0].Trim(),
				fields[1].Trim(),
				fields[2].Trim(),
				ParseDate(fields[3]),
				SplitList(fields[4], ' '),
				SplitList(fields[5], ','),
				fields[6].Trim()));
		}

		return commits;
	}

	internal static string RelativeTime(DateTimeOffset date, DateTimeOffset now)
	{
		var age = now - date;
		if (age <= TimeSpan.FromSeconds(45)) return Strings.GitJustNow;
		if (age < TimeSpan.FromHours(1)) return Strings.GitMinutesAgo(Math.Max(1, (int)age.TotalMinutes));
		if (age < TimeSpan.FromDays(1)) return Strings.GitHoursAgo(Math.Max(1, (int)age.TotalHours));
		if (age < TimeSpan.FromDays(30)) return Strings.GitDaysAgo(Math.Max(1, (int)age.TotalDays));
		if (age < TimeSpan.FromDays(365)) return Strings.GitMonthsAgo(Math.Max(1, (int)(age.TotalDays / 30)));
		return Strings.GitYearsAgo(Math.Max(1, (int)(age.TotalDays / 365)));
	}

	private static string? MergeBase(string cwd, string left, string right)
	{
		var (output, _, exitCode) = GitStatusReader.RunDetailed(cwd, ["merge-base", left, right], 256);
		var hash = output.Trim();
		return exitCode == 0 && IsHash(hash) ? hash : null;
	}

	private static string? Resolve(string cwd, string reference)
	{
		if (!IsSafeReference(reference))
		{
			return null;
		}

		var (output, _, exitCode) = GitStatusReader.RunDetailed(
			cwd,
			["rev-parse", "--verify", $"{reference}^{{commit}}"],
			256);
		var hash = output.Trim();
		return exitCode == 0 && IsHash(hash) ? hash : null;
	}

	private static (int Insertions, int Deletions) ShortStat(string cwd, string hash)
	{
		var (output, _, exitCode) = GitStatusReader.RunDetailed(
			cwd,
			["show", "-s", "--shortstat", "--format=", hash],
			4096);
		if (exitCode != 0)
		{
			return (0, 0);
		}

		var insertions = InsertionsPattern.Match(output);
		var deletions = DeletionsPattern.Match(output);
		return (
			insertions.Success && int.TryParse(insertions.Groups[1].Value, out var added) ? added : 0,
			deletions.Success && int.TryParse(deletions.Groups[1].Value, out var removed) ? removed : 0);
	}

	private static DateTimeOffset ParseDate(string value) =>
		long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
			? DateTimeOffset.FromUnixTimeSeconds(seconds)
			: DateTimeOffset.MinValue;

	private static IReadOnlyList<string> SplitList(string value, char separator) =>
		value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private static string FirstLine(string text)
	{
		var trimmed = text.Trim();
		var newline = trimmed.IndexOf('\n');
		return (newline < 0 ? trimmed : trimmed[..newline]).Trim();
	}

	private static bool IsSafeReference(string value) =>
		value.Length > 0 &&
		value[0] != '-' &&
		value.Trim() == value &&
		!value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character));

	private static bool IsHash(string value) =>
		value.Length is 40 or 64 && value.All(static character => Uri.IsHexDigit(character));

	private static string ErrorText(string error) =>
		error.Trim() is { Length: > 0 } message ? message : Strings.GitFailed;
}
