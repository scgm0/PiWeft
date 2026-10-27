using System.Diagnostics;

namespace PiWeft.Desktop;

/// <summary>工作区里一个被改动的文件。</summary>
public sealed record GitChange(string IndexStatus, string WorkTreeStatus, string Path)
{
	/// <summary>索引里有这个文件的改动（已暂存）。<c>MM</c> 这种「已暂存又改过」两边都算。</summary>
	public bool HasStaged => IndexStatus is not (" " or "?");

	/// <summary>工作区里有这个文件的改动（未暂存）；未跟踪文件只落在这一侧。</summary>
	public bool HasWorkTree => WorkTreeStatus is not " " || IndexStatus == "?";

	/// <summary>合并冲突：两边都不能直接提交，界面把它单独成节。</summary>
	public bool IsConflict => (IndexStatus, WorkTreeStatus) switch
	{
		("U", _) or (_, "U") or ("A", "A") or ("D", "D") => true,
		_ => false,
	};

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

	/// <summary>变更列表行首的单字母状态；<paramref name="staged"/> 选择索引侧或工作区侧。</summary>
	public string StatusLetter(bool staged)
	{
		if (IndexStatus == "?" && WorkTreeStatus == "?") return "?";
		if (IsConflict) return "U";
		var code = (staged ? IndexStatus : WorkTreeStatus).Trim();
		return code switch
		{
			"A" => "A",
			"D" => "D",
			"R" => "R",
			"C" => "C",
			"T" => "T",
			"M" => "M",
			_ => "M",
		};
	}

	/// <summary>文件名（不含目录），用于紧凑树行。</summary>
	public string FileName => Path.Length == 0
		? Path
		: Path[(Path.Replace('\\', '/').LastIndexOf('/') + 1)..];

	/// <summary>目录部分；根目录文件返回空串。</summary>
	public string Directory
	{
		get
		{
			var slash = Path.Replace('\\', '/').LastIndexOf('/');
			return slash >= 0 ? Path[..slash] : string.Empty;
		}
	}
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

	/// <summary>当前分支是否已配置上游（<c>branch.&lt;name&gt;.merge</c>）。推送/拉取的可用性判据。</summary>
	public bool HasUpstream { get; init; }

	/// <summary>仓库是否配置了任何远端。<c>false</c> 时推送/拉取/获取都没有意义。</summary>
	public bool HasRemotes { get; init; }

	/// <summary>分离头指针：没有分支可推，也没有上游。</summary>
	public bool Detached { get; init; }

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

/// <summary>当前分支与它的上游跟踪分支。</summary>
public sealed record GitUpstream(string Branch, string Upstream)
{
	public bool HasUpstream => Upstream.Length > 0;
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

		ApplyLocale(startInfo);
		startInfo.ArgumentList.Add("status");
		startInfo.ArgumentList.Add("--porcelain=v1");
		startInfo.ArgumentList.Add("-b");
		startInfo.ArgumentList.Add("-z");
		startInfo.ArgumentList.Add("--untracked-files=all");

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return new GitStatus { Error = Strings.GitUnavailable };
			}

			// 两路并发读：顺序读时子进程写满 stderr 缓冲会阻塞，进而永远不退出（超时也等不到）。
			var stdoutTask = process.StandardOutput.ReadToEndAsync();
			var stderrTask = process.StandardError.ReadToEndAsync();

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

			var stdout = stdoutTask.GetAwaiter().GetResult();
			var stderr = stderrTask.GetAwaiter().GetResult();

			if (process.ExitCode != 0)
			{
				// 最常见的原因是当前目录不是仓库：git 会把原因写在 stderr。
				return new GitStatus { Error = stderr.Trim().Length > 0 ? stderr.Trim() : Strings.GitNotRepository };
			}

			var status = Parse(stdout);
			// 远端列表要多一次进程调用，但它决定「推送/拉取」按钮的可用性；面板刷新是用户动作
			// 驱动的，这点开销可以接受。探测失败按「没有远端」处理，按钮退化成不可用而不是报错。
			return status with { HasRemotes = HasRemotes(cwd) };
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

	/// <summary>当前分支与它的上游；没有上游时 <see cref="GitUpstream.Upstream"/> 为空。</summary>
	public static GitUpstream Upstream(string cwd)
	{
		// rev-parse 在没有上游时以非 0 退出，这是正常情况而不是错误。
		var (branchOutput, _) = Run(cwd, ["rev-parse", "--abbrev-ref", "HEAD"]);
		var branch = branchOutput.Trim();
		var (upstreamOutput, exitCode) = Run(cwd, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"]);
		var upstream = exitCode == 0 ? upstreamOutput.Trim() : string.Empty;
		return new GitUpstream(branch, upstream);
	}

	/// <summary>仓库是否配置了任何远端。</summary>
	public static bool HasRemotes(string cwd)
	{
		var (output, exitCode) = Run(cwd, ["remote"]);
		return exitCode == 0 && output.Trim().Length > 0;
	}

	/// <summary>一次 diff 读取的结果；<see cref="Error"/> 非空表示 git 调用失败而不是「没有差异」。</summary>
	public sealed record DiffContent(string Text, string? Error)
	{
		public bool Ok => Error is null;
	}

	/// <summary>某个文件的暂存区 diff（HEAD ↔ index）。</summary>
	public static string StagedDiff(string cwd, string path, int maxLines = 400)
	{
		var (output, _, exitCode) = RunDetailed(cwd, ["diff", "--staged", "--no-color", "--", path]);
		return exitCode == 0 ? Truncate(output, maxLines) : string.Empty;
	}

	/// <summary>
	/// 整个暂存区的 diff，供 AI 生成提交摘要。空暂存区返回空串且 <see cref="DiffContent.Ok"/> 为 true；
	/// git 调用失败通过 <see cref="DiffContent.Error"/> 区分，避免把失败误报成「模型没返回」。
	/// </summary>
	public static DiffContent StagedDiffAll(string cwd, int maxCharacters = 8000)
	{
		var (output, error, exitCode) = RunDetailed(
			cwd,
			["diff", "--staged", "--no-color", "--unified=3"],
			maxOutputCharacters: maxCharacters + 1024);
		if (exitCode != 0)
		{
			return new DiffContent(string.Empty, error.Length > 0 ? error.Trim() : Strings.GitFailed);
		}

		if (output.Length == 0)
		{
			return new DiffContent(string.Empty, null);
		}

		return new DiffContent(
			output.Length <= maxCharacters ? output : output[..maxCharacters] + "\n… (diff truncated)",
			null);
	}

	/// <summary>
	/// 读取某个文件的 diff 文本。<paramref name="maxLines"/> 用于限制体积（界面里只看前若干行），
	/// 未跟踪文件没有 diff，改为把文件内容当成整段新增显示。
	/// </summary>
	public static string Diff(string cwd, string path, bool untracked, int maxLines = 400)
	{
		// 未跟踪文件没有 git diff 可比的对象。以前用 `git diff --no-index /dev/null <path>`，
		// 但 `/dev/null` 只在 POSIX 上存在（Git for Windows 是否认这个字面量取决于版本与配置），
		// 在 Windows 上会直接失败、界面显示「没有可显示的 diff」。改为自己合成一份全新增的
		// unified diff：跨平台一致，也不用为只读一份文本多起一个进程。
		if (untracked)
		{
			return UntrackedDiff(cwd, path, maxLines);
		}

		var (output, _) = Run(cwd, ["diff", "--no-color", "--", path]);
		return Truncate(output, maxLines);
	}

	/// <summary>把未跟踪文件渲染成「整份新增」的 unified diff；二进制或读不到时给一句说明。</summary>
	private static string UntrackedDiff(string cwd, string path, int maxLines)
	{
		var full = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(cwd, path);
		var display = path.Replace('\\', '/');

		string text;
		try
		{
			if (!File.Exists(full))
			{
				return string.Empty;
			}

			// 只读前若干行：界面本来就只显示前 maxLines 行，没必要把几 MB 读进来。
			using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			var builder = new System.Text.StringBuilder();
			var read = 0;
			while (read <= maxLines && reader.ReadLine() is { } line)
			{
				if (line.Contains('\0', StringComparison.Ordinal))
				{
					return $"二进制文件：{display}（无法以文本方式预览）";
				}

				builder.Append('+').Append(line).Append('\n');
				read++;
			}

			text = builder.ToString();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return string.Empty;
		}

		var lines = text.Length == 0 ? 0 : text.Count(static c => c == '\n');
		var header = $"diff --git a/{display} b/{display}\nnew file mode 100644\n--- /dev/null\n+++ b/{display}\n@@ -0,0 +1,{lines} @@\n";
		return Truncate(header + text.TrimEnd('\n'), maxLines);
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
		var (output, _, exitCode) = RunDetailed(cwd, arguments);
		return (output, exitCode);
	}

	/// <summary>运行一条 git 命令并同时保留 stderr；读取层需要区分「空结果」与「命令失败」。</summary>
	internal static (string Output, string Error, int ExitCode) RunDetailed(
		string cwd,
		string[] arguments,
		int maxOutputCharacters = 0)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
		};

		ApplyLocale(startInfo);
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return (string.Empty, Strings.GitUnavailable, -1);
			}

			// 两路并发读：先 stdout 再 stderr 时，子进程写满 stderr 缓冲会阻塞，WaitForExit 永远等不到。
			var stdoutTask = ReadBoundedAsync(process.StandardOutput, maxOutputCharacters);
			var stderrTask = process.StandardError.ReadToEndAsync();
			process.WaitForExit(10_000);
			var output = stdoutTask.GetAwaiter().GetResult();
			var error = stderrTask.GetAwaiter().GetResult();
			return (output, error, process.ExitCode);
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return (string.Empty, Strings.GitUnavailable, -1);
		}
	}

	/// <summary>
	/// 读完 stdout，但最多保留 <paramref name="cap"/> 个字符。超限后继续排空管道，
	/// 子进程不会因为写满缓冲而卡到超时；调用方拿到的文本已带截断标记。
	/// </summary>
	private static async Task<string> ReadBoundedAsync(StreamReader reader, int cap)
	{
		if (cap <= 0)
		{
			return await reader.ReadToEndAsync().ConfigureAwait(false);
		}

		var buffer = new char[8192];
		var builder = new System.Text.StringBuilder(Math.Min(cap, 8192));
		var truncated = false;
		while (true)
		{
			var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}

			var room = cap - builder.Length;
			if (room > 0)
			{
				builder.Append(buffer, 0, Math.Min(room, read));
			}

			if (read > room)
			{
				truncated = true;
			}
		}

		return truncated ? builder.ToString() + "\n… (truncated)" : builder.ToString();
	}

	/// <summary>
	/// 让 git 说英文。失败分类（<see cref="GitFailure"/>）按英文特征串匹配，用户机器上的
	/// 本地化输出会让它静默失配 —— 中文环境里 <c>git pull</c> 的报错是「无法快进」而不是
	/// "Not possible to fast-forward"。这里只影响 git 自己的消息，不影响路径与内容字节。
	/// </summary>
	internal static void ApplyLocale(ProcessStartInfo startInfo)
	{
		startInfo.Environment["LC_ALL"] = "C";
		startInfo.Environment["LANG"] = "C";
		startInfo.Environment["LANGUAGE"] = "C";
	}

	/// <summary>
	/// 解析 <c>git status --porcelain=v1 -b -z</c> 的输出（字段以 NUL 分隔）。
	/// <para>
	/// 必须是 <c>-z</c> 形态：非 <c>-z</c> 时 git 会对非 ASCII 路径做八进制转义
	/// （<c>"\344\270\255..."</c>），给带空格的路径加引号，而解析按固定列取子串 ——
	/// 中文或 emoji 文件名会拿到一串转义文本，后续「暂存 / 打开」全部指向不存在的路径。
	/// <c>-z</c> 输出原始 UTF-8 字节且不带引号，是 git 唯一保证机器可读的形态。
	/// </para>
	/// </summary>
	public static GitStatus Parse(string output)
	{
		var branch = string.Empty;
		var ahead = 0;
		var behind = 0;
		var hasUpstream = false;
		var detached = false;
		var changes = new List<GitChange>();

		// 尾随 NUL 会切出一个空字段；改名条目多出一个「原路径」字段，按需跳过。
		var fields = output.Split('\0');
		for (var position = 0; position < fields.Length; position++)
		{
			var line = fields[position];
			if (line.Length == 0)
			{
				continue;
			}

			if (line.StartsWith("## ", StringComparison.Ordinal))
			{
				(branch, ahead, behind, hasUpstream, detached) = ParseBranchLine(line[3..]);
				continue;
			}

			if (line.Length < 4)
			{
				continue;
			}

			var index = line[0].ToString();
			var workTree = line[1].ToString();
			var path = line[3..];

			// -z 形态下改名/复制把原路径放在紧随其后的独立字段里（顺序与文本形态的 "old -> new" 相反，
			// 目标路径反而在前）。展示目标路径更有用，原路径直接跳过。
			if (line[0] is 'R' or 'C' || line[1] is 'R' or 'C')
			{
				position++;
			}

			changes.Add(new GitChange(index, workTree, path));
		}

		return new GitStatus
		{
			IsRepository = true,
			Branch = branch,
			Ahead = ahead,
			Behind = behind,
			HasUpstream = hasUpstream,
			Detached = detached,
			Changes = changes,
		};
	}

	/// <summary>
	/// 解析 <c>## main...origin/main [ahead 1, behind 2]</c>。
	/// 无上游时是 <c>## main</c>；分离头指针是 <c>## HEAD (no branch)</c>。
	/// </summary>
	private static (string Branch, int Ahead, int Behind, bool HasUpstream, bool Detached) ParseBranchLine(string text)
	{
		var branch = text;
		var ahead = 0;
		var behind = 0;
		var hasUpstream = false;
		var detached = false;

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

		// "main...origin/main" → "main"，且说明存在上游；无上游时只有 "main"。
		var dots = branch.IndexOf("...", StringComparison.Ordinal);
		if (dots > 0)
		{
			hasUpstream = branch[(dots + 3)..].Trim().Length > 0;
			branch = branch[..dots];
		}

		branch = branch.Trim();

		// 分离头指针时提交头写的是 "HEAD (no branch)"。以前这里原样当成分支名，结果它出现在
		// 分支下拉里还能被「切换」，而推送按钮也照样可点。统一成空分支名（界面回落到 "HEAD"）。
		if (branch == "HEAD (no branch)" || branch == "(no branch)")
		{
			detached = true;
			branch = string.Empty;
		}
		else if (branch.StartsWith("No commits yet on ", StringComparison.Ordinal))
		{
			// 全新仓库：分支还不存在，但名字已经定了。不当作分离头指针。
			branch = branch["No commits yet on ".Length..].Trim();
		}

		return (branch, ahead, behind, hasUpstream, detached);
	}
}
