using System.Diagnostics;

namespace PiWeft.Desktop;

/// <summary>一次 Git 写操作的结果。</summary>
public sealed record GitResult(bool Ok, string Message)
{
	public static GitResult Success(string message = "") => new(true, message);

	public static GitResult Failure(string message) => new(false, message);
}

/// <summary>
/// Git 写操作：暂存、取消暂存、提交、分支列举/创建/切换。
/// 全部通过 <see cref="ProcessStartInfo.ArgumentList"/> 传参，不拼 shell；名称以 <c>-</c> 开头的一律拒绝，
/// 避免被 git 当成选项。输出有界、命令有超时，不让一次卡住的 git 拖垮界面。
/// </summary>
public static class GitWriter
{
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
		var (output, ok) = RunCapture(cwd, ["branch", "--format=%(HEAD)\t%(refname:short)"]);
		if (!ok)
		{
			return [];
		}

		var current = string.Empty;
		var others = new List<string>();
		foreach (var raw in output.Split('\n'))
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

	private static bool HasPaths(IReadOnlyList<string> paths) => paths.Count > 0 && paths.Any(static path => path.Length > 0);

	/// <summary>分支名不能为空、不能以 <c>-</c> 开头（会被当成选项），也不能含空白。</summary>
	private static bool IsValidName(string name) =>
		name.Length > 0 && name[0] != '-' && name.Trim() == name && !name.Any(char.IsWhiteSpace);

	private static GitResult Run(string cwd, string[] arguments)
	{
		var (output, ok) = RunCapture(cwd, arguments);
		if (ok)
		{
			// commit 之类的成功输出在 stdout；stdout 为空时退回一句通用提示。
			var firstLine = FirstLine(output);
			return GitResult.Success(firstLine.Length > 0 ? firstLine : Strings.GitDone);
		}

		return GitResult.Failure(FirstLine(output));
	}

	/// <summary>合并 stdout 与 stderr，返回是否成功；输出截断到有界长度。</summary>
	private static (string Output, bool Ok) RunCapture(string cwd, string[] arguments)
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
				return (Strings.GitUnavailable, false);
			}

			// 两路并发读：顺序读 stderr 时，子进程的 stdout 缓冲（约 64KB）一旦写满会阻塞，
			// 进而永远不退出 —— 那 WaitForExit 的超时也救不了，UI 会冻结。
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

			// 只去掉尾部换行，保留行首空格：git branch 用前导空格表示非当前分支。
			var output = stdout.TrimEnd('\r', '\n');
			if (output.Length == 0)
			{
				output = stderr.TrimEnd('\r', '\n');
			}

			return (Limit(output), process.ExitCode == 0);
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

	private static string Limit(string text) => text.Length <= 4096 ? text : text[..4096] + "\n…";

	private static string FirstLine(string text)
	{
		var index = text.IndexOf('\n');
		return (index < 0 ? text : text[..index]).Trim();
	}
}
