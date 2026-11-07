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
		Execute = invocation => Task.Run(() => ListDirectory(cwd, invocation)),
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
		Execute = invocation => Task.Run(() => FindFiles(cwd, invocation)),
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
		var matcher = GlobMatcher.Create(pattern, anyDepth: true);
		var matchesPath = pattern.Contains('/');
		var results = new List<string>();
		var resultLimitReached = false;

		var fullRoot = Path.GetFullPath(root);
		var rootPrefix = fullRoot[^1] is '/' or '\\' ? fullRoot.Length : fullRoot.Length + 1;
		foreach (var entry in FileWalker.EnumerateEntries(root, FileWalkerOptions(IgnoreFlavor.Fd)))
		{
			ThrowIfAborted(invocation.CancellationToken);
			var entryPath = entry.Path;
			var relative = entryPath.Length > rootPrefix ? entryPath.AsSpan(rootPrefix) : entryPath.AsSpan();
			var name = LastSegment(relative);
			// fd 的目录结果带尾斜杠：`src/` 这种 pattern 只匹配目录，而我们的相对路径不带斜杠。
			var matched = matcher.IsMatch(matchesPath ? relative : name)
				|| (entry.IsDirectory && matcher.IsMatchWithTrailingSlash(relative));
			if (!matched)
			{
				continue;
			}

			if (results.Count >= limit)
			{
				resultLimitReached = true;
				break;
			}

			results.Add(entry.IsDirectory ? string.Concat(relative, "/") : relative.ToString());
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
		Execute = invocation => Task.Run(() => GrepFiles(cwd, invocation)),
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

		// 字面量走字节快路径：命中判定不经过解码，只把要输出的行解成字符串。
		// 大小写不敏感、跨行 pattern、UTF-16 文件仍走正则路径。
		var needle = TryByteNeedle(pattern, literal, ignoreCase);
		var byteValues = needle is null ? null : ByteNeedleValues(needle);
		var matcherRegex = needle is null
			? (literal ? SafeRegex(Regex.Escape(pattern), options) : SafeRegex(pattern, options))
			: null;
		var prefilter = matcherRegex is null ? null : TryLiteralPrefilter(pattern, literal, ignoreCase);

		var budget = new ScanBudget(limit);
		var scans = new System.Collections.Concurrent.ConcurrentBag<(int Index, FileScan Scan)>();
		var index = -1;
		var parallelOptions = new ParallelOptions { CancellationToken = invocation.CancellationToken };
		try
		{
			// 边遍历边扫：目录遍历与文件扫描重叠，不必先攒齐文件列表。
			// 索引取枚举顺序（Parallel.ForEach 的枚举在调用线程上顺序发生），合并时据此保持顺序。
			Parallel.ForEach(
				singleFile ? (IEnumerable<string>)[root] : FileWalker.EnumerateFiles(root, FileWalkerOptions(IgnoreFlavor.Ripgrep)),
				parallelOptions,
				file =>
				{
					var position = Interlocked.Increment(ref index);
					if (budget.Exhausted || !GlobAccepts(file))
					{
						return;
					}

					var display = file == root
						? Path.GetFileName(file)
						: ToolPaths.RelativeTo(file, root is { Length: > 0 } ? root : cwd);
					var scan = new FileScan(display, context, budget);
					if (needle is not null)
					{
						ScanLiteral(scan, file, pattern, needle, byteValues!, invocation.CancellationToken);
					}
					else
					{
						ScanRegex(scan, file, matcherRegex!, prefilter, invocation.CancellationToken);
					}

					scans.Add((position, scan));
				});
		}
		catch (OperationCanceledException)
		{
			throw new InvalidOperationException("Operation aborted");
		}

		var ordered = scans.ToArray();
		Array.Sort(ordered, static (left, right) => left.Index.CompareTo(right.Index));

		// 并行扫描的结果按文件顺序合并，上限与尾随上下文在这里一次性裁决，
		// 输出与单线程逐个文件扫完全一致。
		var output = new MatchBuffer();
		var matches = 0;
		var matchLimitReached = false;
		var linesTruncated = false;
		foreach (var (_, scan) in ordered)
		{
			linesTruncated |= scan.LinesTruncated;
			matchLimitReached |= scan.BudgetHit;
			var allowed = limit - matches;
			if (allowed <= 0)
			{
				matchLimitReached |= scan.MatchCount > 0;
				continue;
			}

			if (scan.MatchCount <= allowed)
			{
				foreach (var line in scan.Lines)
				{
					output.Add(line.Text);
				}

				matches += scan.MatchCount;
				continue;
			}

			var seen = 0;
			var contextLeft = -1;
			foreach (var line in scan.Lines)
			{
				if (line.IsMatch)
				{
					if (seen == allowed)
					{
						matchLimitReached = true;
						break;
					}

					seen++;
					contextLeft = context;
					output.Add(line.Text);
					continue;
				}

				if (seen == allowed && contextLeft == 0)
				{
					break;
				}

				output.Add(line.Text);
				if (contextLeft > 0)
				{
					contextLeft--;
				}
			}

			matches += seen;
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
	/// 一行的输出：是否命中决定分隔符种类，合并时也靠它数匹配数。
	private readonly record struct ScanLine(string Text, bool IsMatch);

	/// 全局匹配预算。并行扫描共享它：取不到额度的文件立即收尾，
	/// 多出的部分由合并阶段丢弃。
	private sealed class ScanBudget(int limit)
	{
		private int _taken;

		public bool Exhausted => Volatile.Read(ref _taken) > limit;

		public bool TryTake() => Interlocked.Increment(ref _taken) <= limit;
	}

	/// 单个文件的行扫描状态机：前导上下文、尾随窗口、上限判断都在这里。
	private sealed class FileScan(string display, int context, ScanBudget budget)
	{
		private readonly Queue<(long Number, string Text)> _pending = new();
		private long _nextLine;
		private long _trailingUntil = -1;
		private bool _limitHit;

		public List<ScanLine> Lines { get; } = [];

		public int MatchCount { get; private set; }

		public bool LinesTruncated { get; private set; }

		public bool Stopped { get; private set; }

		/// 本文件撞到过全局匹配上限（合并阶段据此报告 limit reached）。
		public bool BudgetHit { get; private set; }

		/// 需要上下文时每一行都要文本（前导上下文要等命中到了才知道要不要输出）。
		public bool NeedsContext => context > 0;

		/// <param name="lineNumber">0 基行号；不按行推进的扫描器由自己数。</param>
		/// <param name="text">命中或需要上下文时的行文本；其余情况可为默认值。</param>
		public void Add(long lineNumber, bool isMatch, ReadOnlySpan<char> text)
		{
			var lineIndex = lineNumber;
			_nextLine = lineNumber + 1;
			if (_limitHit)
			{
				// 仅补完最后一个匹配的尾随上下文；到窗口末尾就停。
				if (lineIndex > _trailingUntil)
				{
					Stopped = true;
					return;
				}

				Emit(lineIndex, text, isMatch: false);
				if (lineIndex == _trailingUntil)
				{
					Stopped = true;
				}

				return;
			}

			if (isMatch)
			{
				if (budget.TryTake())
				{
					foreach (var previous in _pending)
					{
						Emit(previous.Number, previous.Text, isMatch: false);
					}

					_pending.Clear();
					Emit(lineIndex, text, isMatch: true);
					MatchCount++;
					_trailingUntil = lineIndex + context;
					return;
				}

				// 多出一个匹配才说明「撞到上限」；它若落在最后窗口内，按 rg 的语义当上下文显示。
				_limitHit = true;
				BudgetHit = true;
				if (lineIndex <= _trailingUntil)
				{
					Emit(lineIndex, text, isMatch: false);
					if (lineIndex == _trailingUntil)
					{
						Stopped = true;
					}
				}
				else
				{
					Stopped = true;
				}

				return;
			}

			if (lineIndex <= _trailingUntil)
			{
				Emit(lineIndex, text, isMatch: false);
				_pending.Clear();
				return;
			}

			if (context > 0)
			{
				_pending.Enqueue((lineIndex, new string(text)));
				while (_pending.Count > context)
				{
					_pending.Dequeue();
				}
			}
		}

		private void Emit(long number, ReadOnlySpan<char> text, bool isMatch)
		{
			var (lineText, lineTruncated) = Truncate.Line(new string(text));
			LinesTruncated |= lineTruncated;
			Lines.Add(new ScanLine($"{display}{(isMatch ? ':' : '-')}{number + 1}{(isMatch ? ':' : '-')} {lineText}", isMatch));
		}
	}

	/// <summary>pattern 是否是一个纯字面量：没有正则元字符。</summary>
	private static bool IsPlainLiteral(string pattern)
	{
		foreach (var c in pattern)
		{
			if (c is '\\' or '.' or '$' or '^' or '{' or '[' or '(' or '|' or ')' or '*' or '+' or '?')
			{
				return false;
			}
		}

		return true;
	}

	/// 能走字节快路径时返回 pattern 的 UTF-8 字节。
	private static byte[]? TryByteNeedle(string pattern, bool literal, bool ignoreCase) =>
		(literal || IsPlainLiteral(pattern))
			&& !ignoreCase
			&& pattern.Length > 0
			&& !pattern.Contains('\n')
			&& !pattern.Contains('\r')
			&& pattern[0] != '\n'
			? Encoding.UTF8.GetBytes(pattern)
			: null;

	/// 正则里的最长字面量片段，作为「先扫字节、再解码」的预筛。
	/// 没有它，`Truncate.Head` 这种带 `.` 的模式会退化成「整个仓库全部解码 + 逐行跑正则」。
	private static byte[]? TryLiteralPrefilter(string pattern, bool literal, bool ignoreCase)
	{
		if (literal || ignoreCase || pattern.Length == 0)
		{
			return null;
		}

		string? best = null;
		var run = new StringBuilder();
		void Flush()
		{
			if (run.Length >= 3 && (best is null || run.Length > best.Length))
			{
				best = run.ToString();
			}

			run.Clear();
		}

		for (var i = 0; i < pattern.Length; i++)
		{
			var c = pattern[i];
			if (c == '\\')
			{
				if (i + 1 >= pattern.Length)
				{
					Flush();
					continue;
				}

				var escaped = pattern[++i];
				if (char.IsLetterOrDigit(escaped))
				{
					// \d \w \s 这类字符类不是字面量
					Flush();
					continue;
				}

				run.Append(escaped);
				continue;
			}

			if (c is '.' or '$' or '^' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|')
			{
				Flush();
				continue;
			}

			run.Append(c);
		}

		Flush();
		return best is null ? null : Encoding.UTF8.GetBytes(best);
	}

	private static SearchValues<byte> ByteNeedleValues(byte[] needle)
	{
		Span<byte> values = [(byte)'\n', needle[0]];
		return SearchValues.Create(values);
	}

	/// 分块读：固定大小的池化缓冲反复用，避免为每个文件（尤其是 Go 那种 20 MB 以上
	/// 的 svg/二进制）租一整块 LOH 数组。切块点对齐到最后一个 '\n'，行语义不变。
	private const int ScanChunkSize = 256 * 1024;

	private static void ScanLiteral(
		FileScan scan,
		string file,
		string pattern,
		byte[] needle,
		SearchValues<byte> values,
		CancellationToken cancellationToken)
	{
		FileStream stream;
		try
		{
			stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0, FileOptions.SequentialScan);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}

		using (stream)
		{
			var head = new byte[3];
			var headRead = stream.Read(head, 0, head.Length);
			if (headRead >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
			{
				stream.Position = 0;
				ScanUtf16(scan, stream, line => line.IndexOf(pattern, StringComparison.Ordinal) >= 0, cancellationToken);
				return;
			}

			stream.Position = headRead >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
			ScanByteChunks(scan, stream, needle, values, cancellationToken);
		}
	}

	private static void ScanRegex(
		FileScan scan,
		string file,
		Regex regex,
		byte[]? prefilter,
		CancellationToken cancellationToken)
	{
		FileStream stream;
		try
		{
			stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0, FileOptions.SequentialScan);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}

		using (stream)
		{
			var head = new byte[3];
			var headRead = stream.Read(head, 0, head.Length);
			if (headRead >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
			{
				stream.Position = 0;
				ScanUtf16(scan, stream, regex.IsMatch, cancellationToken);
				return;
			}

			stream.Position = headRead >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
			ScanCharChunks(scan, stream, regex, prefilter, cancellationToken);
		}
	}

	/// 字节路径：命中驱动，先对整块做一次 SIMD 查找，无命中就不碰行结构。
	private static void ScanByteChunks(
		FileScan scan,
		FileStream stream,
		byte[] needle,
		SearchValues<byte> values,
		CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(ScanChunkSize);
		char[]? chars = null;
		var carry = 0;
		long lineNumber = 0;
		try
		{
			while (true)
			{
				if (scan.Stopped || cancellationToken.IsCancellationRequested)
				{
					cancellationToken.ThrowIfCancellationRequested();
					return;
				}

				var read = stream.Read(buffer, carry, buffer.Length - carry);
				var eof = read == 0;
				var total = carry + read;
				if (total == 0)
				{
					return;
				}

				var data = buffer.AsSpan(0, total);
				var cut = data.LastIndexOf((byte)'\n');
				if (cut < 0 && !eof)
				{
					// 一行比缓冲还长：扩容，最多到 8 MB 就不再等，直接按整块处理。
					if (buffer.Length >= 8 * ScanChunkSize)
					{
						cut = total - 1;
					}
					else
					{
						var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
						buffer.AsSpan(0, total).CopyTo(bigger);
						ArrayPool<byte>.Shared.Return(buffer);
						buffer = bigger;
						carry = total;
						continue;
					}
				}

				var processed = cut < 0 ? total : cut + 1;
				var chunk = buffer.AsSpan(0, processed);
				if (scan.NeedsContext)
				{
					ScanByteLines(chunk, ref lineNumber);
				}
				else
				{
					ScanByteMatches(chunk, ref lineNumber);
				}

				carry = total - processed;
				if (carry > 0)
				{
					buffer.AsSpan(processed, carry).CopyTo(buffer);
				}

				if (eof)
				{
					if (carry > 0)
					{
						// 末尾没有换行的那一行
						var tail = buffer.AsSpan(0, carry);
						if (scan.NeedsContext)
						{
							ScanByteLines(tail, ref lineNumber, last: true);
						}
						else if (tail.IndexOf(needle) >= 0)
						{
							EmitLine(tail, isMatch: true, lineNumber);
						}
					}

					return;
				}
			}
		}
		finally
		{
			if (chars is not null)
			{
				ArrayPool<char>.Shared.Return(chars);
			}

			ArrayPool<byte>.Shared.Return(buffer);
		}

		void ScanByteMatches(ReadOnlySpan<byte> data, ref long line)
		{
			var position = 0;
			while (position < data.Length)
			{
				var hit = data[position..].IndexOf(needle);
				if (hit < 0)
				{
					return;
				}

				var absolute = position + hit;
				var lineStart = absolute == 0 ? 0 : data[..absolute].LastIndexOf((byte)'\n') + 1;
				line += CountNewlines(data[position..lineStart]);
				var end = data[lineStart..].IndexOf((byte)'\n');
				var lineEnd = end < 0 ? data.Length : lineStart + end;
				EmitLine(data[lineStart..lineEnd], isMatch: true, line);
				if (scan.Stopped || cancellationToken.IsCancellationRequested)
				{
					cancellationToken.ThrowIfCancellationRequested();
					return;
				}

				if (end < 0)
				{
					return;
				}

				line++;
				position = lineEnd + 1;
			}
		}

		void ScanByteLines(ReadOnlySpan<byte> data, ref long line, bool last = false)
		{
			var position = 0;
			var lineStart = 0;
			var matched = false;
			while (position < data.Length)
			{
				var next = data[position..].IndexOfAny(values);
				if (next < 0)
				{
					matched |= data[position..].IndexOf(needle) >= 0;
					break;
				}

				var absolute = position + next;
				if (data[absolute] == (byte)'\n')
				{
					EmitLine(data[lineStart..absolute], matched, line);
					line++;
					if (scan.Stopped || cancellationToken.IsCancellationRequested)
					{
						cancellationToken.ThrowIfCancellationRequested();
						return;
					}

					matched = false;
					lineStart = absolute + 1;
					position = lineStart;
					continue;
				}

				if (data[absolute..].StartsWith(needle))
				{
					matched = true;
					position = absolute + needle.Length;
					continue;
				}

				position = absolute + 1;
			}

			if (last && lineStart < data.Length)
			{
				EmitLine(data[lineStart..], matched, line);
			}
		}

		static long CountNewlines(ReadOnlySpan<byte> data) => data.Count((byte)'\n');

		void EmitLine(ReadOnlySpan<byte> line, bool isMatch, long lineNumber)
		{
			if (line.Length > 0 && line[^1] == (byte)'\r')
			{
				line = line[..^1];
			}

			if (!isMatch && !scan.NeedsContext)
			{
				scan.Add(lineNumber, false, default);
				return;
			}

			var needed = Encoding.UTF8.GetMaxCharCount(line.Length);
			if (chars is null || chars.Length < needed)
			{
				if (chars is not null)
				{
					ArrayPool<char>.Shared.Return(chars);
				}

				chars = ArrayPool<char>.Shared.Rent(needed);
			}

			var count = Encoding.UTF8.GetChars(line, chars);
			scan.Add(lineNumber, isMatch, chars.AsSpan(0, count));
		}
	}

	/// 正则路径：字节块解码成字符块再按行匹配，解码器跨块保持状态，多字节序列不会断在块边界上。
	private static void ScanCharChunks(
		FileScan scan,
		FileStream stream,
		Regex regex,
		byte[]? prefilter,
		CancellationToken cancellationToken)
	{
		var bytes = ArrayPool<byte>.Shared.Rent(ScanChunkSize);
		var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(ScanChunkSize));
		var decoder = Encoding.UTF8.GetDecoder();
		var carry = 0;
		long lineNumber = 0;
		try
		{
			while (true)
			{
				if (scan.Stopped || cancellationToken.IsCancellationRequested)
				{
					cancellationToken.ThrowIfCancellationRequested();
					return;
				}

				var read = stream.Read(bytes, carry, bytes.Length - carry);
				var eof = read == 0;
				var total = carry + read;
				if (total == 0)
				{
					return;
				}

				var data = bytes.AsSpan(0, total);
				var cut = data.LastIndexOf((byte)'\n');
				var processed = cut < 0 ? (eof ? total : -1) : cut + 1;
				if (processed < 0)
				{
					if (bytes.Length < 8 * ScanChunkSize)
					{
						var bigger = ArrayPool<byte>.Shared.Rent(bytes.Length * 2);
						data.CopyTo(bigger);
						ArrayPool<byte>.Shared.Return(bytes);
						bytes = bigger;
						carry = total;
						continue;
					}

					processed = total;
				}

				// 预筛：这一块里连必需的片段都没有，就没必要解码，也不可能命中。
				if (prefilter is not null && cut >= 0 && bytes.AsSpan(0, processed).IndexOf(prefilter) < 0)
				{
					carry = total - processed;
					if (carry > 0)
					{
						bytes.AsSpan(processed, carry).CopyTo(bytes);
					}

					if (eof)
					{
						return;
					}

					continue;
				}

				// 长行会把字节缓冲撑大，字符缓冲要跟着长（GetMaxCharCount 已含替换字符的余量）。
				var needed = Encoding.UTF8.GetMaxCharCount(processed);
				if (chars.Length < needed)
				{
					ArrayPool<char>.Shared.Return(chars);
					chars = ArrayPool<char>.Shared.Rent(needed);
				}

				var charCount = decoder.GetChars(bytes.AsSpan(0, processed), chars, flush: eof && processed == total);
				var span = chars.AsSpan(0, charCount);
				var lineStart = 0;
				while (true)
				{
					if (scan.Stopped || cancellationToken.IsCancellationRequested)
					{
						cancellationToken.ThrowIfCancellationRequested();
						return;
					}

					var newline = span[lineStart..].IndexOf('\n');
					var end = newline < 0 ? span.Length : lineStart + newline;
					var line = span[lineStart..end];
					if (line.Length > 0 && line[^1] == '\r')
					{
						line = line[..^1];
					}

					scan.Add(lineNumber++, regex.IsMatch(line), line);
					if (newline < 0)
					{
						break;
					}

					lineStart = end + 1;
				}

				carry = total - processed;
				if (carry > 0)
				{
					bytes.AsSpan(processed, carry).CopyTo(bytes);
				}

				if (eof)
				{
					return;
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(bytes);
			ArrayPool<char>.Shared.Return(chars);
		}
	}

	private delegate bool LinePredicate(ReadOnlySpan<char> line);

	/// UTF-16 文件走按行解码：换行占两个字节，不能按字节扫。
	private static string TrimCarriageReturn(StringBuilder line)
	{
		if (line.Length > 0 && line[^1] == '\r')
		{
			line.Length--;
		}

		return line.ToString();
	}

	private static void ScanUtf16(FileScan scan, Stream stream, LinePredicate predicate, CancellationToken cancellationToken)
	{
		try
		{
			using var reader = OpenUtf16Reader(stream);
			var buffer = ArrayPool<char>.Shared.Rent(64 * 1024);
			var pending = new StringBuilder();
			var lineNumber = 0L;
			try
			{
				while (true)
				{
					if (scan.Stopped || cancellationToken.IsCancellationRequested)
					{
						cancellationToken.ThrowIfCancellationRequested();
						return;
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
							pending.Append(buffer, position, read - position);
							break;
						}

						pending.Append(buffer, position, newline - position);
						Add(TrimCarriageReturn(pending));
						pending.Clear();
						position = newline + 1;
					}
				}

				if (pending.Length > 0)
				{
					Add(TrimCarriageReturn(pending));
				}
			}
			finally
			{
				ArrayPool<char>.Shared.Return(buffer);
			}

			void Add(string text)
			{
				var isMatch = predicate(text);
				scan.Add(lineNumber++, isMatch, isMatch || scan.NeedsContext ? text : default);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 读不了就跳过，与 rg 一致。
		}
	}

	private static StreamReader OpenUtf16Reader(Stream stream)
	{
		var bom = new byte[2];
		var read = stream.Read(bom, 0, 2);
		var encoding = read == 2 && bom[0] == 0xFE && bom[1] == 0xFF ? Encoding.BigEndianUnicode : Encoding.Unicode;
		stream.Position = read == 2 ? 2 : 0;
		return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
	}

	/// 相对路径的最后一段（不分配：调用方拿去匹配时用 span）。
	private static ReadOnlySpan<char> LastSegment(ReadOnlySpan<char> path)
	{
		var slash = path.LastIndexOfAny('/', '\\');
		return slash < 0 ? path : path[(slash + 1)..];
	}

	/// find 的 glob 匹配：常见的 `*.ext` 与纯名字走比较，
	/// 其余（`**`、字符类、花括号分支）仍交给生成的正则。
	private sealed class GlobMatcher
	{
		private readonly Regex? _regex;
		private readonly string? _literal;
		private readonly string? _suffix;
		private readonly StringComparison _comparison;

		private GlobMatcher(string? literal, string? suffix, Regex? regex, StringComparison comparison)
		{
			_literal = literal;
			_suffix = suffix;
			_regex = regex;
			_comparison = comparison;
		}

		public static GlobMatcher Create(string pattern, bool anyDepth)
		{
			var ignoreCase = GlobSyntax.IsSmartCaseInsensitive(pattern);
			var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if (!pattern.Contains('/'))
			{
				if (pattern.StartsWith("*.", StringComparison.Ordinal)
					&& pattern.AsSpan(1).IndexOfAny(GlobMetaCharacters) < 0)
				{
					return new GlobMatcher(null, pattern[1..], null, comparison);
				}

				if (pattern.AsSpan().IndexOfAny(GlobMetaCharacters) < 0)
				{
					return new GlobMatcher(pattern, null, null, comparison);
				}
			}

			return new GlobMatcher(null, null, GlobToRegex(pattern, ignoreCase, anyDepth), comparison);
		}

		public bool IsMatch(ReadOnlySpan<char> candidate)
		{
			if (_literal is not null)
			{
				return candidate.Equals(_literal, _comparison);
			}

			if (_suffix is not null)
			{
				return candidate.EndsWith(_suffix, _comparison);
			}

			return _regex!.IsMatch(candidate);
		}

		/// 目录要额外试一次带尾斜杠的形式（`src/` 这种 pattern）。只在快路径上多一次比较。
		public bool IsMatchWithTrailingSlash(ReadOnlySpan<char> candidate)
		{
			if (_literal is null && _suffix is null)
			{
				return _regex!.IsMatch(string.Concat(candidate, "/"));
			}

			return false;
		}

		private static readonly SearchValues<char> GlobMetaCharacters =
			SearchValues.Create("*?[{\\");
	}

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
