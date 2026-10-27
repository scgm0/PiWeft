using System.Globalization;
using System.Text.RegularExpressions;

namespace PiWeft.Desktop;

/// <summary>一条提交历史。日志只带 subject；展开详情时再读完整消息与文件。</summary>
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

	/// <summary>详情查询时拿到的完整提交消息；日志列表里为空串。</summary>
	public string FullMessage { get; init; } = string.Empty;

	/// <summary>当前 HEAD 指向的提交（<c>%D</c> 里的 <c>HEAD -> branch</c>）。</summary>
	public bool IsHead => Refs.Any(static reference => reference.StartsWith("HEAD ->", StringComparison.Ordinal));
}

/// <summary>提交或分支比较里的一个文件。</summary>
public sealed record GitCommitFile(string Status, string Path, string? OriginalPath)
{
	public string Letter => Status.Length > 0 ? Status[..1] : "M";

	public string FileName => Path.Length == 0
		? Path
		: Path[(Path.Replace('\\', '/').LastIndexOf('/') + 1)..];
}

/// <summary>一次提交详情：完整消息、改动文件与增删统计。</summary>
public sealed record GitCommitDetail(
	GitCommitEntry Commit,
	IReadOnlyList<GitCommitFile> Files,
	int Insertions,
	int Deletions,
	string? Error = null);

/// <summary><c>git log</c> 的结果；失败时 <see cref="Error"/> 非空。</summary>
public sealed record GitLogResult(IReadOnlyList<GitCommitEntry> Commits, string? Error = null);

/// <summary>两个分支的三点比较结果。</summary>
public sealed record GitBranchComparison(
	IReadOnlyList<GitCommitFile> Files,
	int Ahead,
	int Behind,
	string BaseHash,
	string TargetHash,
	string? Error = null);

/// <summary>提交/比较文件 diff 的结果；空文本不一定代表失败，<see cref="Error"/> 才代表 git 失败。</summary>
public sealed record GitFileDiff(string Text, string? Error = null)
{
	public bool Ok => Error is null;
}

/// <summary>
/// Git 历史只读查询：log、提交数、提交详情、分支三点比较与提交/比较文件 diff。
/// 所有 ref/hash 都先做形态校验，路径统一放在 <c>--</c> 之后。
/// </summary>
public static class GitHistoryReader
{
	private const char FieldSeparator = '\u001f';
	private const char RecordSeparator = '\u001e';
	private const int MaxLogOutput = 2 * 1024 * 1024;
	private const int MaxDiffOutput = 4 * 1024 * 1024;

	private static readonly Regex InsertionsPattern = new(@"(\d+)\s+insertions?\(\+\)", RegexOptions.CultureInvariant);
	private static readonly Regex DeletionsPattern = new(@"(\d+)\s+deletions?\(-\)", RegexOptions.CultureInvariant);

	/// <summary>最近提交；<paramref name="reference"/> 为空时看全部分支（排除内部检查点引用）。</summary>
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

	/// <summary>与 <see cref="Log"/> 同过滤条件的提交总数。</summary>
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

	/// <summary>完整提交详情；hash 非法、commit 不存在或读取失败时返回 <c>null</c>。</summary>
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
		var files = filesExitCode == 0 ? ParseNameStatus(filesOutput) : [];
		var (insertions, deletions) = ShortStat(cwd, commit.Hash);
		return new GitCommitDetail(
			commit,
			files,
			insertions,
			deletions,
			filesExitCode == 0 ? null : ErrorText(fileError));
	}

	/// <summary>提交内单个文件的 diff；重命名时把原路径一起交给 pathspec。</summary>
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

	/// <summary>三点比较（merge-base → target）的文件列表与 ahead/behind。</summary>
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

		return new GitBranchComparison(ParseNameStatus(filesOutput), ahead, behind, baseHash, targetHash);
	}

	/// <summary>比较结果里单个文件的 diff（base...target）。</summary>
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

	/// <summary>解析 <c>--name-status -z</c> 输出；重命名/复制包含原路径与目标路径。</summary>
	internal static IReadOnlyList<GitCommitFile> ParseNameStatus(string output)
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
				files.Add(new GitCommitFile(status, target, original));
				index += 2;
			}
			else
			{
				files.Add(new GitCommitFile(status, fields[index + 1], null));
				index++;
			}
		}

		return files;
	}

	/// <summary>解析 <c>git log</c> 的 <c>-z</c> 记录，供测试复用。</summary>
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
