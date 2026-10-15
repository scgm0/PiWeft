using System.Diagnostics;

namespace PiWeft.Desktop;

/// <summary>工作区里一个被改动的文件。</summary>
public sealed record GitChange(string IndexStatus, string WorkTreeStatus, string Path)
{
	/// <summary>面向用户的单字状态：新增/修改/删除/改名/冲突/未跟踪。</summary>
	public string Label => (IndexStatus, WorkTreeStatus) switch
	{
		("?", "?") => "未跟踪",
		("A", _) or (_, "A") => "新增",
		("D", _) or (_, "D") => "删除",
		("R", _) or (_, "R") => "改名",
		("U", _) or (_, "U") or ("A", "A") or ("D", "D") => "冲突",
		_ => "修改",
	};
}

/// <summary>一次 <c>git status</c> 的结果。</summary>
public sealed record GitStatus
{
	/// <summary>当前分支名；分离头指针时是短哈希。</summary>
	public string Branch { get; init; } = string.Empty;

	public int Ahead { get; init; }

	public int Behind { get; init; }

	public IReadOnlyList<GitChange> Changes { get; init; } = [];

	/// <summary>不在 Git 仓库里时为 false。</summary>
	public bool IsRepository { get; init; }

	public string? Error { get; init; }

	/// <summary>一行摘要，例如「main · 3 个改动 ↑1」；不是仓库时给一句简短说明。</summary>
	public string Summary()
	{
		if (!IsRepository)
		{
			// git 自己的报错很长（带着文件系统边界的解释），这里只留一句结论，细节放详情区。
			// 只有「连 git 命令都起不来」才是环境问题，其余都按「不在仓库里」处理。
			return string.Equals(Error, Strings.GitUnavailable, StringComparison.Ordinal)
				? Strings.GitUnavailable
				: Strings.GitNotRepository;
		}

		var parts = new List<string> { Branch.Length > 0 ? Branch : "HEAD" };

		if (Changes.Count > 0)
		{
			parts.Add(Strings.GitChangeCount(Changes.Count));
		}
		else
		{
			parts.Add(Strings.GitClean);
		}

		if (Ahead > 0)
		{
			parts.Add($"↑{Ahead}");
		}

		if (Behind > 0)
		{
			parts.Add($"↓{Behind}");
		}

		return string.Join(" · ", parts);
	}
}

/// <summary>
/// 只读的 Git 状态：调用系统 <c>git</c> 并解析 <c>--porcelain=v1 -b</c> 的输出。
/// 写操作（暂存、提交、分支）见 <see cref="GitWriter"/>。
/// </summary>
public static class GitStatusReader
{
	/// <summary>读取某个工作目录的 Git 状态。</summary>
	public static GitStatus Read(string cwd, TimeSpan? timeout = null)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
		};

		startInfo.ArgumentList.Add("status");
		startInfo.ArgumentList.Add("--porcelain=v1");
		startInfo.ArgumentList.Add("-b");
		startInfo.ArgumentList.Add("--untracked-files=all");

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return new GitStatus { Error = Strings.GitUnavailable };
			}

			var stdout = process.StandardOutput.ReadToEnd();
			var stderr = process.StandardError.ReadToEnd();

			if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (InvalidOperationException)
				{
					// 已经退出。
				}

				return new GitStatus { Error = Strings.GitTimeout };
			}

			if (process.ExitCode != 0)
			{
				// 最常见的原因是当前目录不是仓库：git 会把原因写在 stderr。
				return new GitStatus { Error = stderr.Trim().Length > 0 ? stderr.Trim() : Strings.GitNotRepository };
			}

			return Parse(stdout);
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return new GitStatus { IsRepository = false, Error = Strings.GitUnavailable };
		}
		catch (InvalidOperationException)
		{
			return new GitStatus { Error = Strings.GitUnavailable };
		}
	}

	/// <summary>
	/// 读取某个文件的 diff 文本。<paramref name="maxLines"/> 用于限制体积（界面里只看前若干行），
	/// 未跟踪文件没有 diff，改为把文件内容当成整段新增显示。
	/// </summary>
	public static string Diff(string cwd, string path, bool untracked, int maxLines = 400)
	{
		var arguments = untracked
			? new[] { "diff", "--no-color", "--no-index", "--", "/dev/null", path }
			: ["diff", "--no-color", "--", path];

		var (output, _) = Run(cwd, arguments);
		return Truncate(output, maxLines);
	}

	/// <summary>把输出裁到前 N 行，超出时补一行说明。</summary>
	internal static string Truncate(string text, int maxLines)
	{
		var lines = text.Replace("\r\n", "\n").Split('\n');

		if (lines.Length <= maxLines)
		{
			return text;
		}

		return string.Join('\n', lines.Take(maxLines)) + $"\n… 还有 {lines.Length - maxLines} 行未显示（在终端运行 git diff 查看完整内容）";
	}

	/// <summary>运行一条 git 命令并返回标准输出与退出码。</summary>
	private static (string Output, int ExitCode) Run(string cwd, string[] arguments)
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

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return (string.Empty, -1);
			}

			var output = process.StandardOutput.ReadToEnd();
			process.StandardError.ReadToEnd();
			process.WaitForExit(10_000);
			return (output, process.ExitCode);
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return (string.Empty, -1);
		}
	}

	/// <summary>解析 <c>git status --porcelain=v1 -b</c> 的输出。</summary>
	public static GitStatus Parse(string output)
	{
		var branch = string.Empty;
		var ahead = 0;
		var behind = 0;
		var changes = new List<GitChange>();

		foreach (var raw in output.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			if (line.Length == 0)
			{
				continue;
			}

			if (line.StartsWith("## ", StringComparison.Ordinal))
			{
				(branch, ahead, behind) = ParseBranchLine(line[3..]);
				continue;
			}

			if (line.Length < 4)
			{
				continue;
			}

			var index = line[0].ToString();
			var workTree = line[1].ToString();
			var path = line[3..];

			// 改名会写成 "old -> new"，展示目标路径更有用。
			var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
			if (arrow >= 0)
			{
				path = path[(arrow + 4)..];
			}

			changes.Add(new GitChange(index, workTree, path));
		}

		return new GitStatus
		{
			IsRepository = true,
			Branch = branch,
			Ahead = ahead,
			Behind = behind,
			Changes = changes,
		};
	}

	/// <summary>解析 <c>## main...origin/main [ahead 1, behind 2]</c>。</summary>
	private static (string Branch, int Ahead, int Behind) ParseBranchLine(string text)
	{
		var branch = text;
		var ahead = 0;
		var behind = 0;

		var bracket = text.IndexOf('[', StringComparison.Ordinal);
		if (bracket >= 0)
		{
			branch = text[..bracket].Trim();

			var close = text.IndexOf(']', bracket);
			var counters = close > bracket ? text[(bracket + 1)..close] : text[(bracket + 1)..];

			foreach (var part in counters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (part.StartsWith("ahead ", StringComparison.Ordinal) &&
					int.TryParse(part[6..], out var aheadCount))
				{
					ahead = aheadCount;
				}
				else if (part.StartsWith("behind ", StringComparison.Ordinal) &&
					int.TryParse(part[7..], out var behindCount))
				{
					behind = behindCount;
				}
			}
		}

		// "main...origin/main" → "main"；无上游时可能是 "main" 或 "(no branch)"。
		var dots = branch.IndexOf("...", StringComparison.Ordinal);
		if (dots > 0)
		{
			branch = branch[..dots];
		}

		return (branch.Trim(), ahead, behind);
	}
}
