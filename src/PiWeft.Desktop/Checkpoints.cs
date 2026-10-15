using System.Diagnostics;
using System.Globalization;

namespace PiWeft.Desktop;

/// <summary>一个工作区检查点。</summary>
public sealed record Checkpoint(string Id, string Sha, DateTimeOffset Created, string Label);

/// <summary>
/// D-05：工作区检查点与回滚（移植 pi-rewind 的思路）。
/// <para>
/// 快照写入 git 低层引用 <c>refs/pi-checkpoints/&lt;id&gt;</c>，不产生用户可见的分支或提交：
/// 用临时索引把工作区（含未跟踪文件，尊从 .gitignore）写成 tree，再 <c>commit-tree</c> 成对象。
/// 恢复只回写文件（<c>git restore --worktree</c>），不改变索引、HEAD 或分支；
/// 快照之后新增的未跟踪文件会保留，需要时应自行清理。
/// </para>
/// </summary>
public static class Checkpoints
{
	public const string RefPrefix = "refs/pi-checkpoints/";

	/// <summary>默认保留的检查点数量，超出后按时间清理最旧的。</summary>
	public const int DefaultRetention = 50;

	/// <summary>每个工作目录最近一次自动快照的时间，用于限频。</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> LastAuto = new(StringComparer.Ordinal);

	/// <summary>
	/// 发送提示前的自动快照：同一个工作目录每 <paramref name="minInterval"/> 最多一次，
	/// 且只在工作区确实有改动时创建，避免为干净仓库堆一组空检查点。
	/// </summary>
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

	/// <summary>创建一个覆盖当前工作区的检查点；失败时返回 <c>null</c>。</summary>
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

			var id = $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid().ToString("N")[..8]}";
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

	/// <summary>列出检查点，最新的在前。</summary>
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

		checkpoints.Sort(static (a, b) => b.Created.CompareTo(a.Created));
		return checkpoints.Count <= limit ? checkpoints : checkpoints.Take(limit).ToList();
	}

	/// <summary>把工作区恢复成某个检查点的内容；返回错误信息，成功时为 <c>null</c>。</summary>
	public static string? Restore(string cwd, string sha)
	{
		if (sha.Length == 0)
		{
			return Strings.CheckpointMissing;
		}

		var result = Run(cwd, ["restore", "--source", sha, "--worktree", "--", "."]);
		return result.Ok ? null : result.Output;
	}

	/// <summary>按保留上限清理最旧的检查点，返回删除数量。</summary>
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

	/// <summary>运行一条 git 命令，合并 stdout/stderr，超时与输出都有界。</summary>
	private static (string Output, bool Ok) Run(string cwd, string[] arguments, IReadOnlyDictionary<string, string>? environment = null)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
		};

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

			var stdout = process.StandardOutput.ReadToEnd();
			var stderr = process.StandardError.ReadToEnd();
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

			var output = stdout.Length > 0 ? stdout : stderr;
			return (output.Length <= 4096 ? output : output[..4096], process.ExitCode == 0);
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
