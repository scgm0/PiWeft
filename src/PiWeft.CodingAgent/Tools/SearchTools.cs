using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

public static class SearchTools
{
	private static FileWalkOptions FileWalkerOptions(IgnoreFlavor flavor) => new()
	{
		Flavor = flavor,
		IncludeGitDirectory = true,
	};

	public static AgentTool Ls(string cwd) => new()
	{
		Name = "ls",
		Label = "ls",
		Description =
			$"List directory contents. Returns entries sorted alphabetically, with '/' suffix for directories. Includes dotfiles. Output is truncated to 500 entries or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first).",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Directory to list (default: current directory)" },
				["limit"] = new JsonObject { ["type"] = "number", ["description"] = "Maximum number of entries to return (default: 500)" },
			},
		},
		Execute = invocation => Task.FromResult(ListDirectory(cwd, invocation)),
	};

	private static AgentToolResult ListDirectory(string cwd, AgentToolInvocation invocation)
	{
		ThrowIfAborted(invocation.CancellationToken);
		var path = JsonArgs.String(invocation.Arguments["path"]);
		var limit = Math.Max(1, FileTools.OptionalInt(invocation, "limit") ?? 500);
		var directory = path is null ? cwd : ToolPaths.ResolveToCwd(path, cwd);

		if (!Directory.Exists(directory))
		{
			if (File.Exists(directory))
			{
				throw new InvalidOperationException($"Not a directory: {directory}");
			}

			throw new InvalidOperationException($"Path not found: {directory}");
		}

		var entries = new List<string>();
		try
		{
			foreach (var child in Directory.EnumerateDirectories(directory))
			{
				var name = Path.GetFileName(child);
				if (name.Length > 0)
				{
					entries.Add(name + "/");
				}
			}

			foreach (var child in Directory.EnumerateFiles(directory))
			{
				var name = Path.GetFileName(child);
				if (name.Length > 0)
				{
					entries.Add(name);
				}
			}
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
		{
			throw new InvalidOperationException($"Cannot read directory: {exception.Message}", exception);
		}

		// pi 的 ls 用大小写不敏感排序，不用 Ordinal（B.txt 不会排到 a.txt 前面）。
		entries.Sort(StringComparer.CurrentCultureIgnoreCase);

		if (entries.Count == 0)
		{
			return new AgentToolResult { Content = [new TextContent { Text = "(empty directory)" }] };
		}

		var entryLimitReached = entries.Count > limit;
		var shown = entryLimitReached ? entries.GetRange(0, limit) : entries;
		var rawOutput = string.Join("\n", shown);
		var truncation = Truncate.Head(rawOutput, maxLines: int.MaxValue);
		var output = truncation.Content;
		var notices = new List<string>(2);
		if (entryLimitReached)
		{
			notices.Add($"{limit} entries limit reached. Use limit={limit * 2} for more");
		}

		if (truncation.Truncated)
		{
			notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
		}

		if (notices.Count > 0)
		{
			output += $"\n\n[{string.Join(". ", notices)}]";
		}

		var details = new JsonObject();
		if (entryLimitReached)
		{
			details["entryLimitReached"] = limit;
		}

		if (truncation.Truncated)
		{
			details["truncation"] = truncation.ToJson();
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = output }],
			Details = details.Count > 0 ? details : null,
		};
	}

	public static AgentTool Find(string cwd) => new()
	{
		Name = "find",
		Label = "find",
		Description =
			$"Search for files by glob pattern. Returns matching file paths relative to the search directory. Respects .gitignore. Output is truncated to 1000 results or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first).",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["pattern"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Glob pattern to match files, e.g. '*.ts', '**/*.json', or 'src/**/*.spec.ts'",
				},
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Directory to search in (default: current directory)" },
				["limit"] = new JsonObject { ["type"] = "number", ["description"] = "Maximum number of results (default: 1000)" },
			},
			["required"] = new JsonArray("pattern"),
		},
		Execute = invocation => Task.FromResult(FindFiles(cwd, invocation)),
	};

	private static AgentToolResult FindFiles(string cwd, AgentToolInvocation invocation)
	{
		var pattern = FileTools.RequiredString(invocation, "pattern");
		var path = JsonArgs.String(invocation.Arguments["path"]);
		var limit = Math.Max(1, FileTools.OptionalInt(invocation, "limit") ?? 1000);
		var root = path is null ? cwd : ToolPaths.ResolveToCwd(path, cwd);

		if (!Directory.Exists(root))
		{
			throw new InvalidOperationException($"Path not found: {root}");
		}

		// fd 的 --glob 是 smart-case：pattern 里没有大写字母就大小写不敏感。
		// 路径型 pattern 在 fd 里是对绝对路径匹配（pi 用 --full-path + **/ 前缀），
		// 因此在任意深度都能命中。
		var regex = GlobToRegex(pattern, GlobSyntax.IsSmartCaseInsensitive(pattern), anyDepth: true);
		var results = new List<string>();
		var resultLimitReached = false;

		foreach (var entry in FileWalker.EnumerateEntries(root, FileWalkerOptions(IgnoreFlavor.Fd)))
		{
			ThrowIfAborted(invocation.CancellationToken);
			var isDirectory = Directory.Exists(entry);
			var relative = GlobSyntax.NormalizeSlashes(ToolPaths.RelativeTo(entry, root));
			var candidate = pattern.Contains('/') ? relative : Path.GetFileName(entry);
			// fd 的目录结果带尾斜杠：`src/` 这种 pattern 只匹配目录，而我们的相对路径不带斜杠。
			if (!regex.IsMatch(candidate) && !(isDirectory && regex.IsMatch(candidate + "/")))
			{
				continue;
			}

			if (results.Count >= limit)
			{
				resultLimitReached = true;
				break;
			}

			results.Add(isDirectory ? relative + "/" : relative);
		}

		if (results.Count == 0)
		{
			return new AgentToolResult { Content = [new TextContent { Text = "No files found matching pattern" }] };
		}

		var rawOutput = string.Join("\n", results);
		var truncation = Truncate.Head(rawOutput, maxLines: int.MaxValue);
		var output = truncation.Content;
		var notices = new List<string>(2);
		if (resultLimitReached)
		{
			notices.Add($"{limit} results limit reached. Use limit={limit * 2} for more, or refine pattern");
		}

		if (truncation.Truncated)
		{
			notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
		}

		if (notices.Count > 0)
		{
			output += $"\n\n[{string.Join(". ", notices)}]";
		}

		var details = new JsonObject();
		if (resultLimitReached)
		{
			details["resultLimitReached"] = limit;
		}

		if (truncation.Truncated)
		{
			details["truncation"] = truncation.ToJson();
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = output }],
			Details = details.Count > 0 ? details : null,
		};
	}

	public static AgentTool Grep(string cwd) => new()
	{
		Name = "grep",
		Label = "grep",
		Description =
			$"Search file contents for a pattern. Returns matching lines with file paths and line numbers. Respects .gitignore. Output is truncated to 100 matches or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Long lines are truncated to {Truncate.GrepMaxLineLength} chars.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Search pattern (regex or literal string)" },
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Directory or file to search (default: current directory)" },
				["glob"] = new JsonObject { ["type"] = "string", ["description"] = "Filter files by glob pattern, e.g. '*.ts' or '**/*.spec.ts'" },
				["ignoreCase"] = new JsonObject { ["type"] = "boolean", ["description"] = "Case-insensitive search (default: false)" },
				["literal"] = new JsonObject { ["type"] = "boolean", ["description"] = "Treat pattern as literal string instead of regex (default: false)" },
				["context"] = new JsonObject { ["type"] = "number", ["description"] = "Number of lines to show before and after each match (default: 0)" },
				["limit"] = new JsonObject { ["type"] = "number", ["description"] = "Maximum number of matches to return (default: 100)" },
			},
			["required"] = new JsonArray("pattern"),
		},
		Execute = invocation => Task.FromResult(GrepFiles(cwd, invocation)),
	};

	private static AgentToolResult GrepFiles(string cwd, AgentToolInvocation invocation)
	{
		var pattern = FileTools.RequiredString(invocation, "pattern");
		var path = JsonArgs.String(invocation.Arguments["path"]);
		var glob = JsonArgs.String(invocation.Arguments["glob"]);
		var ignoreCase = JsonArgs.Bool(invocation.Arguments["ignoreCase"]) ?? false;
		var literal = JsonArgs.Bool(invocation.Arguments["literal"]) ?? false;
		var context = Math.Max(0, FileTools.OptionalInt(invocation, "context") ?? 0);
		var limit = Math.Max(1, FileTools.OptionalInt(invocation, "limit") ?? 100);

		// pi 的 rg 使用线性时间、无 lookaround/backreference 的 Rust regex。
		// NonBacktracking 让 .NET 侧也拒绝这类模式，并避免灾难性回溯。
		var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.NonBacktracking;
		if (ignoreCase)
		{
			options |= RegexOptions.IgnoreCase;
		}

		var regex = literal
			? new Regex(Regex.Escape(pattern), options, TimeSpan.FromSeconds(2))
			: SafeRegex(pattern, options);

		// rg 的 --glob 支持 `!pattern` 反向排除；工具只收一个 glob，因此「排除」就是
		// 「除了命中的都搜」，与 rg 单个 --glob 的行为一致。
		var excludeGlob = glob is { Length: > 1 } && glob[0] == '!';
		var globPattern = excludeGlob ? glob![1..] : glob;
		var globRegex = globPattern is null ? null : GlobToRegex(globPattern);
		var root = path is null ? cwd : ToolPaths.ResolveToCwd(path, cwd);

		bool GlobAccepts(string file) =>
			globRegex is null || GlobMatches(globRegex, globPattern!, file, root) != excludeGlob;

		var singleFile = File.Exists(root);
		if (!singleFile && !Directory.Exists(root))
		{
			throw new InvalidOperationException($"Path not found: {root}");
		}

		var output = new MatchBuffer();
		var matches = 0;
		var matchLimitReached = false;
		var linesTruncated = false;
		var stop = false;

		// 逐行扫描：只按 '\n' 切行（rg 同样只认 '\n'），孤立 CR 留在行内。
		// 每个文件独立维护待输出的前导上下文与尾随窗口。
		void SearchFile(string file)
		{
			var display = file == root
				? Path.GetFileName(file)
				: ToolPaths.RelativeTo(file, root is { Length: > 0 } ? root : cwd);
			var pending = new Queue<(long Number, string Text)>();
			long lineIndex = 0;
			long trailingUntil = -1;
			var limitHit = false;

			void Emit(long number, string line, bool isMatch)
			{
				var (lineText, lineTruncated) = Truncate.Line(line);
				linesTruncated |= lineTruncated;
				output.Add($"{display}{(isMatch ? ':' : '-')}{number + 1}{(isMatch ? ':' : '-')} {lineText}");
			}

			try
			{
				foreach (var line in ReadLines(file, invocation.CancellationToken))
				{
					var isMatch = regex.IsMatch(line);
					if (limitHit)
					{
						// 仅补完最后一个匹配的尾随上下文；到窗口末尾就停。
						if (lineIndex > trailingUntil)
						{
							stop = true;
								return;
						}
						Emit(lineIndex, line, isMatch: false);
						if (lineIndex == trailingUntil)
						{
							stop = true;
							return;
						}
						lineIndex++;
						continue;
					}

					if (isMatch && matches < limit)
					{
						foreach (var previous in pending)
						{
							Emit(previous.Number, previous.Text, isMatch: false);
						}
						pending.Clear();
						Emit(lineIndex, line, isMatch: true);
						matches++;
						trailingUntil = lineIndex + context;
					}
					else if (isMatch)
					{
						// 多出一个匹配才说明「撞到上限」；它若落在最后窗口内，按 rg 的语义当上下文显示。
						matchLimitReached = true;
						limitHit = true;
						if (lineIndex <= trailingUntil)
						{
							Emit(lineIndex, line, isMatch: false);
							if (lineIndex == trailingUntil)
							{
								stop = true;
							}
						}
						else
						{
							stop = true;
						}

						lineIndex++;
						return;
					}
					else if (lineIndex <= trailingUntil)
					{
						Emit(lineIndex, line, isMatch: false);
						pending.Clear();
					}
					else
					{
						if (context > 0)
						{
							pending.Enqueue((lineIndex, line));
							while (pending.Count > context)
							{
								pending.Dequeue();
							}
						}
					}

					lineIndex++;
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// rg 也跳过读不了的文件；已经有结果时不要把整次搜索变成错误。
			}
		}

		if (singleFile)
		{
			if (GlobAccepts(root))
			{
				SearchFile(root);
			}
		}
		else
		{
			foreach (var file in FileWalker.EnumerateFiles(root, FileWalkerOptions(IgnoreFlavor.Ripgrep)))
			{
				ThrowIfAborted(invocation.CancellationToken);
				if (stop)
				{
					break;
				}

				if (!GlobAccepts(file))
				{
					continue;
				}

				SearchFile(file);
			}
		}

		if (matches == 0)
		{
			return new AgentToolResult { Content = [new TextContent { Text = "No matches found" }] };
		}

		var truncation = Truncate.Head(output.Render(), maxLines: int.MaxValue) with
		{
			TotalLines = (int)Math.Min(output.TotalLines, int.MaxValue),
			TotalBytes = output.TotalBytes - 1,
		};
		var text = truncation.Content;
		var notices = new List<string>(3);
		if (matchLimitReached)
		{
			notices.Add($"{limit} matches limit reached. Use limit={limit * 2} for more, or refine pattern");
		}

		if (truncation.Truncated)
		{
			notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
		}

		if (linesTruncated)
		{
			notices.Add($"Some lines truncated to {Truncate.GrepMaxLineLength} chars. Use read tool to see full lines");
		}

		if (notices.Count > 0)
		{
			text += $"\n\n[{string.Join(". ", notices)}]";
		}

		var details = new JsonObject();
		if (matchLimitReached)
		{
			details["matchLimitReached"] = limit;
		}

		if (truncation.Truncated)
		{
			details["truncation"] = truncation.ToJson();
		}

		if (linesTruncated)
		{
			details["linesTruncated"] = true;
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = text }],
			Details = details.Count > 0 ? details : null,
		};
	}

	/// 输出缓冲区：只保存能进字节预算的行，其余只计数（与 pi 的 rg 读取端一致）。
	private sealed class MatchBuffer
	{
		private readonly List<string> _lines = [];

		public long TotalLines { get; private set; }

		public long TotalBytes { get; private set; }

		public void Add(string line)
		{
			// 每行按「行 + 换行」计数；最后一行没有换行，报告时减 1。
			if (TotalBytes - 1 <= Truncate.DefaultMaxBytes)
			{
				_lines.Add(line);
			}

			TotalBytes += Encoding.UTF8.GetByteCount(line) + 1;
			TotalLines++;
		}

		public string Render() => string.Join('\n', _lines);
	}

	/// 按 '\n' 切行并识别 UTF-16 BOM（rg 也会读带 BOM 的 UTF-16 文件）。
	/// 孤立 CR 不当换行；行尾的一个 CR 与 rg 一样剥掉。
	/// UTF-8 走字节快路径：一次 64 KiB 池化读入，用 `IndexOf(byte)` 找换行，
	/// 只把行切片解码成字符串；UTF-16 需要按码元对齐，退回 StreamReader。
	private static IEnumerable<string> ReadLines(string path, CancellationToken cancellationToken)
	{
		using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite,
			64 * 1024,
			FileOptions.SequentialScan);

		var bom = new byte[3];
		var bomRead = 0;
		while (bomRead < bom.Length)
		{
			var read = stream.Read(bom, bomRead, bom.Length - bomRead);
			if (read == 0)
			{
				break;
			}

			bomRead += read;
		}

		var offset = 0;
		if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
		{
			offset = 3;
		}
		else if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
		{
			foreach (var line in ReadLinesWithReader(stream, 2, Encoding.Unicode, cancellationToken))
			{
				yield return line;
			}

			yield break;
		}
		else if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
		{
			foreach (var line in ReadLinesWithReader(stream, 2, Encoding.BigEndianUnicode, cancellationToken))
			{
				yield return line;
			}

			yield break;
		}

		stream.Position = offset;
		var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
		ArrayBufferWriter<byte>? partial = null;
		try
		{
			while (true)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					throw new InvalidOperationException("Operation aborted");
				}

				var read = stream.Read(buffer, 0, buffer.Length);
				if (read == 0)
				{
					break;
				}

				var position = 0;
				while (position < read)
				{
					var newline = buffer.AsSpan(position, read - position).IndexOf((byte)'\n');
					if (newline < 0)
					{
						partial ??= new ArrayBufferWriter<byte>(1024);
						partial.Write(buffer.AsSpan(position, read - position));
						break;
					}

					var absolute = position + newline;
					if (partial is { WrittenCount: > 0 })
					{
						partial.Write(buffer.AsSpan(position, absolute - position));
						yield return DecodeLine(partial.WrittenSpan);
						partial.ResetWrittenCount();
					}
					else
					{
						yield return DecodeLine(buffer.AsSpan(position, absolute - position));
					}

					position = absolute + 1;
				}
			}

			if (partial is { WrittenCount: > 0 })
			{
				yield return DecodeLine(partial.WrittenSpan);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	/// 行尾的一个 CR 与 rg 一样剥掉；无效 UTF-8 按替换字符解码（与 StreamReader 同义）。
	private static string DecodeLine(ReadOnlySpan<byte> line)
	{
		if (!line.IsEmpty && line[^1] == (byte)'\r')
		{
			line = line[..^1];
		}

		return Encoding.UTF8.GetString(line);
	}

	/// UTF-16 路径：换行符占两个字节，不能按字节扫描。
	private static IEnumerable<string> ReadLinesWithReader(
		Stream stream,
		int offset,
		Encoding encoding,
		CancellationToken cancellationToken)
	{
		stream.Position = offset;
		using var reader = new StreamReader(
			stream,
			encoding,
			detectEncodingFromByteOrderMarks: false,
			bufferSize: 64 * 1024,
			leaveOpen: true);

		var buffer = ArrayPool<char>.Shared.Rent(64 * 1024);
		var line = new StringBuilder();
		try
		{
			while (true)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					throw new InvalidOperationException("Operation aborted");
				}

				var read = reader.Read(buffer, 0, buffer.Length);
				if (read == 0)
				{
					break;
				}

				var position = 0;
				while (position < read)
				{
					var newline = Array.IndexOf(buffer, '\n', position, read - position);
					if (newline < 0)
					{
						line.Append(buffer, position, read - position);
						break;
					}

					line.Append(buffer, position, newline - position);
					yield return TrimCarriageReturn(line);
					line.Clear();
					position = newline + 1;
				}
			}

			if (line.Length > 0)
			{
				yield return TrimCarriageReturn(line);
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer);
		}
	}

	private static string TrimCarriageReturn(StringBuilder line)
	{
		if (line.Length > 0 && line[^1] == '\r')
		{
			line.Length--;
		}

		return line.ToString();
	}

	private static bool GlobMatches(Regex regex, string glob, string file, string root)
	{
		// 与 rg 的 --glob 一致：不带 '/' 的 glob 只比文件名（任意深度），
		// 带 '/' 的比相对搜索根（以 '/' 开头的绝对 glob 比绝对路径）。
		if (glob.StartsWith('/'))
		{
			return regex.IsMatch(GlobSyntax.NormalizeSlashes(Path.GetFullPath(file)));
		}

		var relative = GlobSyntax.NormalizeSlashes(ToolPaths.RelativeTo(file, root));
		return regex.IsMatch(glob.Contains('/') ? relative : Path.GetFileName(file));
	}

	private static void ThrowIfAborted(CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			throw new InvalidOperationException("Operation aborted");
		}
	}

	private static Regex SafeRegex(string pattern, RegexOptions options)
	{
		try
		{
			return new Regex(pattern, options, TimeSpan.FromSeconds(2));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
		{
			throw new InvalidOperationException($"Invalid regular expression: {ex.Message}");
		}
	}

	/// glob → 正则（与 pi 交给 fd/ripgrep 的 globset 语法对齐）：
	/// <c>*</c> 不跨目录、<c>**</c> 跨目录、<c>?</c> 单字符、<c>[abc]</c>/<c>[!abc]</c> 字符类、
	/// <c>{a,b}</c> 分支与反斜杠转义。
	/// <para>
	/// <paramref name="anyDepth"/> 对应 pi 在 find 里给路径型 pattern 补的 <c>**/</c> 前缀：
	/// fd 用 <c>--full-path</c> 匹配绝对路径，于是 <c>src/*.ts</c> 在任意深度都能命中。
	/// </para>
	public static Regex GlobToRegex(string pattern, bool ignoreCase = false, bool anyDepth = false)
	{
		var builder = new StringBuilder(pattern.Length * 2 + 16);
		builder.Append('^');
		if (anyDepth && pattern.Contains('/') && !pattern.StartsWith('/') &&
			!pattern.StartsWith("**/", StringComparison.Ordinal) && pattern != "**")
		{
			builder.Append("(?:.*/)?");
		}

		AppendGlob(builder, pattern, 0, pattern.Length);
		builder.Append('$');
		return GlobSyntax.Compile(builder, ignoreCase);
	}

	private static void AppendGlob(StringBuilder builder, string pattern, int start, int end)
	{
		for (var index = start; index < end; index++)
		{
			var c = pattern[index];
			switch (c)
			{
				case '\\':
					if (index + 1 < end)
					{
						builder.Append(Regex.Escape(pattern[++index].ToString()));
					}
					else
					{
						builder.Append("\\\\");
					}

					break;
				case '*':
					if (index + 1 < end && pattern[index + 1] == '*')
					{
						index++;
						if (index + 1 < end && pattern[index + 1] == '/')
						{
							index++;
							builder.Append("(?:.*/)?");
						}
						else
						{
							builder.Append(".*");
						}
					}
					else
					{
						builder.Append("[^/]*");
					}

					break;
				case '?':
					builder.Append("[^/]");
					break;
				case '[':
				{
					var close = GlobSyntax.FindClassEnd(pattern, index + 1, end);
					if (close < 0)
					{
						builder.Append("\\[");
					}
					else
					{
						GlobSyntax.AppendClass(builder, pattern, index + 1, close);
						index = close;
					}

					break;
				}
				case '{':
				{
					var close = GlobSyntax.FindBraceEnd(pattern, index + 1, end);
					if (close < 0)
					{
						builder.Append("\\{");
					}
					else
					{
						GlobSyntax.AppendBraceAlternatives(builder, pattern, index + 1, close, AppendGlob);
						index = close;
					}

					break;
				}
				default:
					builder.Append(Regex.Escape(c.ToString()));
					break;
			}
		}
	}
}
