using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// 删除前备份：PiDeck 的 trash guard 在 bash 工具执行前把字面量删除目标复制进系统回收站。
/// 只处理「命令字 + 静态路径」这一层；`find -delete`、`git clean`、变量展开都不解析。
/// 任何内部错误都不能阻断原命令，删除照常发生。
internal static partial class TrashGuard
{
	private const long MaxFileBytes = 100L * 1024 * 1024;

	private const long MaxTotalBytes = 500L * 1024 * 1024;

	private const int MaxDirectoryDepth = 24;

	private static readonly TimeSpan TrashTimeout = TimeSpan.FromSeconds(20);

	private static readonly FrozenSet<string> PosixDeleteCommands =
		FrozenSet.Create(StringComparer.Ordinal, "rm", "unlink");

	private static readonly FrozenSet<string> WindowsDeleteCommands =
		FrozenSet.Create(StringComparer.Ordinal, "del", "erase", "rd", "rmdir");

	private static readonly FrozenSet<string> PowerShellDeleteCommands =
		FrozenSet.Create(StringComparer.Ordinal, "remove-item", "ri");

	private static readonly FrozenSet<string> CommandPrefixes =
		FrozenSet.Create(StringComparer.Ordinal, "sudo", "doas", "nohup", "command", "exec");

	private static readonly FrozenSet<string> Redirections =
		FrozenSet.Create(StringComparer.Ordinal, ">", ">>", "<", "2>", "2>>", "&>", "&>>");

	/// 命令里出现的删除动词。大多数 bash 调用一条都不含，用一次向量化搜索跳过词法分析。
	private static readonly SearchValues<string> DeleteVerbs = SearchValues.Create(
		["rm", "del", "erase", "unlink", "remove-item", "ri"],
		StringComparison.OrdinalIgnoreCase);

	/// 在删除命令真正执行前备份目标。只认字面量目标，失败静默放行。
	internal static async Task BackupAsync(
		string toolName,
		string command,
		string cwd,
		CancellationToken cancellationToken)
	{
		if (!IsEnabled() || toolName is not ("bash" or "powershell"))
		{
			return;
		}

		if (!HasDeleteCommandWord(command))
		{
			return;
		}

		var stagingRoot = Path.Combine(Path.GetTempPath(), "piweft-trash-guard", Guid.NewGuid().ToString("N"));
		try
		{
			var targets = CollectTargets(command, cwd, toolName == "powershell");
			if (targets.Count == 0)
			{
				return;
			}

			long total = 0;
			Directory.CreateDirectory(stagingRoot);
			var staged = new List<(string Original, string Staging)>();
			for (var index = 0; index < targets.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var target = targets[index];
				if (!IsBackupCandidate(target, total, out var size))
				{
					continue;
				}

				if (total + size > MaxTotalBytes)
				{
					break;
				}

				var destination = Path.Combine(stagingRoot, index.ToString(CultureInfo.InvariantCulture));
				if (!CopyToStaging(target, destination))
				{
					continue;
				}

				total += size;
				staged.Add((target, destination));
			}

			foreach (var (original, staging) in staged)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await MoveToTrashAsync(staging, original, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
		{
			// 备份失败不影响删除本身，与 PiDeck 的「永不阻断 agent」一致。
		}
		catch (Exception)
		{
			// 解析/平台命令的意外错误同样不能拦住用户的命令。
		}
		finally
		{
			try
			{
				if (Directory.Exists(stagingRoot))
				{
					Directory.Delete(stagingRoot, recursive: true);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static bool IsEnabled()
	{
		var value = (Environment.GetEnvironmentVariable("PIDECK_TRASH_GUARD") ?? string.Empty).Trim().ToLowerInvariant();
		return value is not ("off" or "0" or "false" or "no");
	}

	private static bool HasDeleteCommandWord(string command) =>
		command.AsSpan().ContainsAny(DeleteVerbs);

	/// 从一次命令里收集仍然存在的删除目标（已绝对化、去重、展开最后一段 glob）。
	internal static IReadOnlyList<string> CollectTargets(string command, string cwd, bool powerShell)
	{
		var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
		var result = new List<string>();
		foreach (var segment in SplitShellSegments(command))
		{
			foreach (var raw in ExtractDeleteTargets(segment, powerShell))
			{
				var absolute = Path.IsPathRooted(raw) ? raw : Path.Combine(cwd, raw);
				foreach (var expanded in ExpandGlobTarget(absolute))
				{
					var key = expanded.TrimEnd('/', '\\');
					if (key.Length == 0 || !seen.Add(key) || !(File.Exists(expanded) || Directory.Exists(expanded)))
					{
						continue;
					}

					result.Add(expanded);
				}
			}
		}

		return result;
	}

	/// 按 `&&`、`||`、`;`、`|`、`&`、换行与括号切段；引号内的分隔符不切。
	internal static IReadOnlyList<string> SplitShellSegments(string command)
	{
		var segments = new List<string>();
		var current = new StringBuilder();
		char? quote = null;
		var escape = false;

		void Push()
		{
			var text = current.ToString().Trim();
			if (text.Length > 0)
			{
				segments.Add(text);
			}

			current.Clear();
		}

		foreach (var character in command)
		{
			if (escape)
			{
				current.Append(character);
				escape = false;
				continue;
			}

			if (character == '\\' && quote != '\'')
			{
				current.Append(character);
				escape = true;
				continue;
			}

			if (quote is { } open)
			{
				if (character == open)
				{
					quote = null;
				}

				current.Append(character);
				continue;
			}

			if (character is '"' or '\'')
			{
				quote = character;
				current.Append(character);
				continue;
			}

			if (character is ';' or '|' or '&' or '\n' or '(' or ')')
			{
				Push();
				continue;
			}

			current.Append(character);
		}

		Push();
		return segments;
	}

	/// 引号感知分词。bash 语序里 `\x` 转义成 `x`；PowerShell 语序里反斜杠是路径字符。
	internal static IReadOnlyList<string> TokenizeSegment(string segment, bool powerShell)
	{
		var tokens = new List<string>();
		var token = new StringBuilder();
		var hasToken = false;
		char? quote = null;
		var escape = false;

		void Flush()
		{
			if (hasToken)
			{
				tokens.Add(token.ToString());
				token.Clear();
				hasToken = false;
			}
		}

		for (var index = 0; index < segment.Length; index++)
		{
			var character = segment[index];
			if (escape)
			{
				token.Append(character);
				hasToken = true;
				escape = false;
				continue;
			}

			if (quote == '\'')
			{
				if (character == '\'')
				{
					quote = null;
				}
				else
				{
					token.Append(character);
					hasToken = true;
				}

				continue;
			}

			if (quote == '"')
			{
				if (character == '"')
				{
					quote = null;
					continue;
				}

				if (!powerShell && character == '\\' && index + 1 < segment.Length &&
					segment[index + 1] is '"' or '\\' or '$' or '`')
				{
					token.Append(segment[++index]);
				}
				else
				{
					token.Append(character);
				}

				hasToken = true;
				continue;
			}

			if (character == '\\' && !powerShell)
			{
				escape = true;
				hasToken = true;
				continue;
			}

			if (character is '"' or '\'')
			{
				quote = character;
				hasToken = true;
				continue;
			}

			if (char.IsWhiteSpace(character))
			{
				Flush();
				continue;
			}

			token.Append(character);
			hasToken = true;
		}

		Flush();
		return tokens;
	}

	internal static IReadOnlyList<string> ExtractDeleteTargets(string segment, bool powerShell)
	{
		var tokens = TokenizeSegment(segment, powerShell);
		if (tokens.Count == 0)
		{
			return [];
		}

		var index = 0;
		while (index < tokens.Count && CommandPrefixes.Contains(tokens[index]))
		{
			index++;
		}

		if (index >= tokens.Count)
		{
			return [];
		}

		var rawCommand = tokens[index].Replace('\\', '/');
		var command = rawCommand.Contains('/')
			? rawCommand[(rawCommand.LastIndexOf('/') + 1)..].ToLowerInvariant()
			: rawCommand.ToLowerInvariant();
		var posix = PosixDeleteCommands.Contains(command);
		var windows = WindowsDeleteCommands.Contains(command);
		var shell = PowerShellDeleteCommands.Contains(command);
		if (!posix && !windows && !shell)
		{
			return [];
		}

		index++;
		var targets = new List<string>();
		var flagsEnded = false;
		while (index < tokens.Count)
		{
			var token = tokens[index++];
			if (!flagsEnded && token == "--")
			{
				flagsEnded = true;
				continue;
			}

			if (!flagsEnded && IsFlagToken(command, token, posix, windows, shell))
			{
				if (shell)
				{
					var lower = token.ToLowerInvariant();
					if (lower is "-path" or "-literalpath")
					{
						continue; // 值留给下一轮当目标
					}

					if (lower == "-filter" && index < tokens.Count)
					{
						index++;
					}
				}

				continue;
			}

			if (IsRedirectToken(token))
			{
				if (index < tokens.Count && Redirections.Contains(token))
				{
					index++;
				}

				continue;
			}

			targets.Add(token);
		}

		return targets;
	}

	private static bool IsFlagToken(string command, string token, bool posix, bool windows, bool shell)
	{
		if (token.Length <= 1)
		{
			return false;
		}

		if (posix || shell)
		{
			return token[0] == '-';
		}

		if (!windows)
		{
			return false;
		}

		// rmdir 在 POSIX（coreutils）里用 '-' 标志，在 Windows cmd 里用 '/'；
		// 其它 Windows 命令只有 '/' 风格。
		if (command == "rmdir" && !OperatingSystem.IsWindows())
		{
			return token[0] == '-';
		}

		return token[0] == '/';
	}

	private static bool IsRedirectToken(string token) =>
		Redirections.Contains(token) || RedirectToken().IsMatch(token);

	[GeneratedRegex(@"^[0-9]*&?>", RegexOptions.CultureInvariant)]
	private static partial Regex RedirectToken();

	/// 只展开最后一段的 `*` / `?`；`**` 与目录段通配不支持，按无匹配处理。
	internal static IReadOnlyList<string> ExpandGlobTarget(string target)
	{
		if (!target.Any(static character => character is '*' or '?'))
		{
			return [target];
		}

		if (target.Contains("**", StringComparison.Ordinal))
		{
			return [];
		}

		var directory = Path.GetDirectoryName(target);
		var pattern = Path.GetFileName(target);
		if (directory is null || pattern.Length == 0)
		{
			return [];
		}

		try
		{
			if (!Directory.Exists(directory))
			{
				return [];
			}

			var regex = GlobPattern(pattern);
			var matchHidden = pattern.StartsWith('.');
			return
			[
				.. Directory.EnumerateFileSystemEntries(directory)
					.Where(entry => matchHidden || !Path.GetFileName(entry).StartsWith('.'))
					.Where(entry => regex.IsMatch(Path.GetFileName(entry))),
			];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static Regex GlobPattern(string pattern)
	{
		var builder = new StringBuilder("^");
		foreach (var character in pattern)
		{
			builder.Append(character switch
			{
				'*' => "[^/]*",
				'?' => "[^/]",
				_ => Regex.Escape(character.ToString()),
			});
		}

		builder.Append('$');
		var options = RegexOptions.CultureInvariant;
		if (OperatingSystem.IsWindows())
		{
			options |= RegexOptions.IgnoreCase;
		}

		return new Regex(builder.ToString(), options, TimeSpan.FromSeconds(1));
	}

	private static bool IsBackupCandidate(string target, long currentTotal, out long size)
	{
		size = 0;
		try
		{
			var info = new FileInfo(target);
			if (info.LinkTarget is not null)
			{
				return false;
			}

			if (info.Exists)
			{
				size = info.Length;
				return size <= MaxFileBytes && currentTotal + size <= MaxTotalBytes;
			}

			var directory = new DirectoryInfo(target);
			if (!directory.Exists)
			{
				return false;
			}

			size = MeasureSize(directory, 0);
			return size >= 0 && size <= MaxTotalBytes;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// 目录超限或读不到时返回 -1；超过总预算提前剪枝。
	private static long MeasureSize(DirectoryInfo directory, int depth)
	{
		if (depth > MaxDirectoryDepth)
		{
			return -1;
		}

		long total = 0;
		try
		{
			foreach (var entry in directory.EnumerateFileSystemInfos())
			{
				if (entry.LinkTarget is not null)
				{
					continue;
				}

				if (entry is DirectoryInfo child)
				{
					var size = MeasureSize(child, depth + 1);
					if (size < 0)
					{
						return -1;
					}

					total += size;
				}
				else if (entry is FileInfo file)
				{
					total += file.Length;
				}

				if (total > MaxTotalBytes)
				{
					return -1;
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return -1;
		}

		return total;
	}

	private static bool CopyToStaging(string source, string destination)
	{
		try
		{
			var file = new FileInfo(source);
			if (file.LinkTarget is not null)
			{
				return false;
			}

			if (file.Exists)
			{
				File.Copy(source, destination, overwrite: false);
				return true;
			}

			var directory = new DirectoryInfo(source);
			if (!directory.Exists)
			{
				return false;
			}

			Directory.CreateDirectory(destination);
			foreach (var entry in directory.EnumerateFileSystemInfos())
			{
				if (entry.LinkTarget is not null)
				{
					continue;
				}

				CopyToStaging(entry.FullName, Path.Combine(destination, entry.Name));
			}

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static async Task MoveToTrashAsync(string staging, string original, CancellationToken cancellationToken)
	{
		if (OperatingSystem.IsWindows())
		{
			await RecycleWithPowerShellAsync(staging, cancellationToken).ConfigureAwait(false);
		}
		else if (OperatingSystem.IsMacOS())
		{
			await RecycleWithFinderAsync(staging, cancellationToken).ConfigureAwait(false);
		}
		else if (OperatingSystem.IsLinux())
		{
			MoveToXdgTrash(staging, original);
		}
	}

	private static void MoveToXdgTrash(string staging, string original)
	{
		var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
		if (string.IsNullOrEmpty(dataHome))
		{
			dataHome = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".local",
				"share");
		}

		var root = Path.Combine(dataHome, "Trash");
		var files = Path.Combine(root, "files");
		var info = Path.Combine(root, "info");
		Directory.CreateDirectory(files);
		Directory.CreateDirectory(info);

		var name = Path.GetFileName(original.TrimEnd('/', '\\'));
		if (name.Length == 0)
		{
			name = "deleted";
		}

		var unique = name;
		for (var index = 1; File.Exists(Path.Combine(files, unique)) || Directory.Exists(Path.Combine(files, unique)); index++)
		{
			unique = $"{name}.{index}";
		}

		var target = Path.Combine(files, unique);
		try
		{
			// 暂存目录与 XDG Trash 通常同卷：rename 不复制数据。
			if (Directory.Exists(staging))
			{
				Directory.Move(staging, target);
			}
			else
			{
				File.Move(staging, target);
			}
		}
		catch (IOException)
		{
			// 跨卷时退回复制（finally 会清掉暂存目录）。
			if (Directory.Exists(staging))
			{
				CopyDirectory(staging, target);
			}
			else
			{
				File.Copy(staging, target);
			}
		}

		var escaped = Uri.EscapeDataString(original).Replace("%2F", "/", StringComparison.Ordinal);
		File.WriteAllText(
			Path.Combine(info, unique + ".trashinfo"),
			$"[Trash Info]\nPath={escaped}\nDeletionDate={DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n",
			new UTF8Encoding(false));
	}

	private static async Task RecycleWithPowerShellAsync(string staging, CancellationToken cancellationToken)
	{
		var escapedPath = staging.Replace("'", "''", StringComparison.Ordinal);
		var script =
			"$ErrorActionPreference = 'Stop'\n" +
			"Add-Type -AssemblyName Microsoft.VisualBasic | Out-Null\n" +
			$"$p = '{escapedPath}'\n" +
			"if (Test-Path -LiteralPath $p -PathType Container) {\n" +
			"  [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($p, 'OnlyErrorDialogs', 'SendToRecycleBin')\n" +
			"} else {\n" +
			"  [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($p, 'OnlyErrorDialogs', 'SendToRecycleBin')\n" +
			"}\n";
		var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
		await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], cancellationToken)
			.ConfigureAwait(false);
	}

	private static async Task RecycleWithFinderAsync(string staging, CancellationToken cancellationToken)
	{
		var escapedPath = staging.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal);
		await RunAsync(
			"osascript",
			["-e", $"tell application \"Finder\" to delete POSIX file \"{escapedPath}\""],
			cancellationToken).ConfigureAwait(false);
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var entry in Directory.EnumerateFileSystemEntries(source))
		{
			var target = Path.Combine(destination, Path.GetFileName(entry));
			if (Directory.Exists(entry))
			{
				CopyDirectory(entry, target);
			}
			else
			{
				File.Copy(entry, target);
			}
		}
	}

	private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TrashTimeout);
		var startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo);
		if (process is null)
		{
			return;
		}

		// 不读走输出会在管道写满时卡住回收站命令。
		var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
		var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
		await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		try
		{
			await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
	}
}
