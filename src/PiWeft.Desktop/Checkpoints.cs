using System.Diagnostics;
using System.Globalization;

namespace PiWeft.Desktop;

public sealed record Checkpoint(string Id, string Sha, DateTimeOffset Created, string Label);

/// D-05：工作区检查点与回滚（移植 pi-rewind 的思路）。
/// 快照写入 git 低层引用 <c>refs/pi-checkpoints/&lt;id&gt;</c>，不产生用户可见的分支或提交：
/// 用临时索引把工作区（含未跟踪文件，尊从 .gitignore）写成 tree，再 <c>commit-tree</c> 成对象。
/// 恢复只回写文件（<c>git restore --worktree</c>），不改变索引、HEAD 或分支；
/// 快照之后新增的未跟踪文件会保留，需要时应自行清理。
public static class Checkpoints
{
	public const string RefPrefix = "refs/pi-checkpoints/";

	public const int DefaultRetention = 50;

	/// 变更概览最多渲染的行数。`git diff --stat` 正常是「文件数」那么多行；
	/// 遇到几千个文件的生成物提交时，仍然要给文本控件一个上界。
	public const int MaxStatLines = 2000;

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> LastAuto = new(StringComparer.Ordinal);

	/// 发送提示前的自动快照：同一个工作目录每 <paramref name="minInterval"/> 最多一次，
	/// 且只在工作区确实有改动时创建，避免为干净仓库堆一组空检查点。
	public static Checkpoint? AutoSnapshot(string cwd, TimeSpan minInterval)
	{
		var full = Path.GetFullPath(cwd);
		var now = DateTimeOffset.UtcNow;
		LastAuto.TryGetValue(full, out var previous);
		if (previous != default && now - previous < minInterval)
		{
			return null;
		}

		// 干净工作区不消耗限频窗口：下一次真有改动时仍应快照。
		var status = GitStatusReader.Read(full, TimeSpan.FromSeconds(5));
		if (!status.IsRepository || status.Changes.Count == 0)
		{
			return null;
		}

		LastAuto[full] = now;

		var checkpoint = Create(full, "auto");
		if (checkpoint is not null)
		{
			Prune(full);
		}

		return checkpoint;
	}

	public static Checkpoint? Create(string cwd, string label = "")
	{
		var index = Path.Combine(Path.GetTempPath(), "piweft-index-" + Guid.NewGuid().ToString("N")[..12]);
		var environment = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["GIT_INDEX_FILE"] = index,
			// 仓库可能没配 user.name/email；检查点不该因此失败。
			["GIT_AUTHOR_NAME"] = "PiWeft",
			["GIT_AUTHOR_EMAIL"] = "piweft@local",
			["GIT_COMMITTER_NAME"] = "PiWeft",
			["GIT_COMMITTER_EMAIL"] = "piweft@local",
		};

		try
		{
			// 优先从 HEAD 建索引；还没有首个提交时用空索引。
			if (!Run(cwd, ["read-tree", "HEAD"], environment).Ok)
			{
				Run(cwd, ["read-tree", "--empty"], environment);
			}

			if (!Run(cwd, ["add", "-A"], environment).Ok)
			{
				return null;
			}

			var tree = Run(cwd, ["write-tree"], environment).Output.Trim();
			if (tree.Length == 0)
			{
				return null;
			}

			var message = label.Length > 0 ? label : "piweft checkpoint";
			var commit = Run(cwd, ["commit-tree", tree, "-m", message], environment).Output.Trim();
			if (commit.Length == 0)
			{
				return null;
			}

			// 带毫秒：git 的 creatordate 只精确到秒，同一秒内连按两次「新建检查点」时
			// 仅靠时间排序会让 Prune 删掉较新的那个；id 里的毫秒是稳定的次级排序键。
			var id = $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{Guid.NewGuid().ToString("N")[..8]}";
			if (!Run(cwd, ["update-ref", RefPrefix + id, commit]).Ok)
			{
				return null;
			}

			return new Checkpoint(id, commit, DateTimeOffset.UtcNow, label);
		}
		finally
		{
			try
			{
				File.Delete(index);
			}
			catch (IOException)
			{
			}
		}
	}

	public static IReadOnlyList<Checkpoint> List(string cwd, int limit = DefaultRetention)
	{
		var result = Run(cwd, ["for-each-ref", "--format=%(refname)%09%(objectname)%09%(creatordate:iso-strict)%09%(subject)", RefPrefix]);
		if (!result.Ok)
		{
			return [];
		}

		var checkpoints = new List<Checkpoint>();
		foreach (var raw in result.Output.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			if (line.Length == 0)
			{
				continue;
			}

			var columns = line.Split('\t');
			if (columns.Length < 3)
			{
				continue;
			}

			var id = columns[0].StartsWith(RefPrefix, StringComparison.Ordinal) ? columns[0][RefPrefix.Length..] : columns[0];
			var created = DateTimeOffset.TryParse(columns[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
				? parsed
				: DateTimeOffset.MinValue;
			var label = columns.Length > 3 && columns[3] != "piweft checkpoint" ? columns[3] : string.Empty;
			checkpoints.Add(new Checkpoint(id, columns[1], created, label));
		}

		// creatordate 只有秒级精度：同一秒内创建的快照用 id（含毫秒）倒序兜底。
		checkpoints.Sort(static (a, b) =>
		{
			var byTime = b.Created.CompareTo(a.Created);
			return byTime != 0 ? byTime : string.CompareOrdinal(b.Id, a.Id);
		});
		return checkpoints.Count <= limit ? checkpoints : checkpoints.Take(limit).ToList();
	}

	/// 检查点相对当前工作区的逐文件变更概览（`git diff --stat`）。
	/// 回退预览默认展示它而不是完整补丁：340 个文件的补丁有 5 万多行，
	/// 一次性建文本控件会把面板拖死；概览只有文件数那么多行，完整补丁交给编辑器。
	public static (string Stat, string? Error) Stat(string cwd, string sha)
	{
		if (sha.Length == 0)
		{
			return (string.Empty, Strings.CheckpointMissing);
		}

		// 固定宽度：右侧面板最窄 360 DIP，默认的 ~80 列 stat 会把 `++++` 条推到面板外。
		// 40 列 = 26 列路径 + 10 列图形 + 分隔符；长路径由 git 自己截成 `...名字`。
		var result = Run(
			cwd,
			[
				"diff", "--stat", "--stat-width=40", "--stat-name-width=26", "--stat-graph-width=10",
				"--no-ext-diff", sha, "--", ".",
			],
			maxOutput: 256 * 1024);
		return result.Ok ? (ClampLines(result.Output, MaxStatLines), null) : (string.Empty, result.Output);
	}

	private static string ClampLines(string text, int maxLines)
	{
		var lines = text.Split('\n');
		if (lines.Length <= maxLines)
		{
			return text;
		}

		return string.Join('\n', lines.Take(maxLines)) + $"\n…（仅显示前 {maxLines} 行）";
	}

	public static (string Patch, string? Error) Patch(string cwd, string sha)
	{
		if (sha.Length == 0)
		{
			return (string.Empty, Strings.CheckpointMissing);
		}

		var result = Run(cwd, ["diff", "--no-color", "--no-ext-diff", "--no-textconv", sha, "--", "."], maxOutput: 16 * 1024 * 1024);
		return result.Ok ? (result.Output, null) : (string.Empty, result.Output);
	}

	public static (string Summary, string? Error) ShortStat(string cwd, string sha)
	{
		if (sha.Length == 0)
		{
			return (string.Empty, Strings.CheckpointMissing);
		}

		var result = Run(cwd, ["diff", "--shortstat", "--no-ext-diff", sha, "--", "."]);
		return result.Ok ? (result.Output.Trim(), null) : (string.Empty, result.Output);
	}

	public static string? Restore(string cwd, string sha)
	{
		if (sha.Length == 0)
		{
			return Strings.CheckpointMissing;
		}

		var result = Run(cwd, ["restore", "--source", sha, "--worktree", "--", "."]);
		return result.Ok ? null : result.Output;
	}

	public static int Prune(string cwd, int keep = DefaultRetention)
	{
		var all = List(cwd, int.MaxValue);
		var removed = 0;
		foreach (var checkpoint in all.Skip(Math.Max(0, keep)))
		{
			if (Run(cwd, ["update-ref", "-d", RefPrefix + checkpoint.Id]).Ok)
			{
				removed++;
			}
		}

		return removed;
	}

	private static (string Output, bool Ok) Run(
		string cwd,
		string[] arguments,
		IReadOnlyDictionary<string, string>? environment = null,
		int maxOutput = 4096)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
		};

		// 与其它 git 子进程同一约定：输出与报错都按英文解析，提示文案不会被系统语言改掉。
		startInfo.Environment["LC_ALL"] = "C";
		startInfo.Environment["LANG"] = "C";

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		if (environment is not null)
		{
			foreach (var (key, value) in environment)
			{
				startInfo.Environment[key] = value;
			}
		}

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return (Strings.GitUnavailable, false);
			}

			// 两路并发读：顺序读时子进程写满另一路缓冲会阻塞，进而永远不退出（超时也等不到）。
			var stdoutTask = process.StandardOutput.ReadToEndAsync();
			var stderrTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(30_000))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (InvalidOperationException)
				{
				}

				return (Strings.GitTimeout, false);
			}

			var stdout = stdoutTask.GetAwaiter().GetResult();
			var stderr = stderrTask.GetAwaiter().GetResult();
			var output = stdout.Length > 0 ? stdout : stderr;
			if (output.Length <= maxOutput)
			{
				return (output, process.ExitCode == 0);
			}

			// 截断也要告诉调用方：半份 diff 看起来像「只有这些改动」。
			return (maxOutput > 4096
				? output[..maxOutput] + "\n…（差异过大，已截断）"
				: output[..4096], process.ExitCode == 0);
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return (Strings.GitUnavailable, false);
		}
		catch (InvalidOperationException)
		{
			return (Strings.GitUnavailable, false);
		}
	}
}
