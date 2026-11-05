using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

public static class SearchTools
{
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

		// pi 的 ls 用大小写不敏感排序，而不是 Ordinal（B.txt 不会排到 a.txt 前面）。
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

		var details = new JsonObject
		{
			["path"] = directory,
			["entries"] = entries.Count,
		};
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
			Details = details,
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

		foreach (var entry in FileWalker.EnumerateEntries(root, includeGitDirectory: true))
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

		var details = new JsonObject
		{
			["path"] = root,
			["count"] = results.Count,
		};
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
			Details = details,
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

		var files = new List<string>();
		if (File.Exists(root))
		{
			if (GlobAccepts(root))
			{
				files.Add(root);
			}
		}
		else if (Directory.Exists(root))
		{
			foreach (var file in FileWalker.EnumerateFiles(root, includeGitDirectory: true))
			{
				ThrowIfAborted(invocation.CancellationToken);
				if (GlobAccepts(file))
				{
					files.Add(file);
				}
			}
		}
		else
		{
			throw new InvalidOperationException($"Path not found: {root}");
		}

		var output = new StringBuilder();
		var matches = 0;
		var truncated = false;
		var linesTruncated = false;

		foreach (var file in files)
		{
			if (truncated)
			{
				break;
			}

			ThrowIfAborted(invocation.CancellationToken);
			if (IsBinaryFile(file))
			{
				continue;
			}

			List<string>? lines;
			try
			{
				lines = [.. File.ReadLines(file)];
			}
			catch (IOException)
			{
				continue;
			}
			catch (UnauthorizedAccessException)
			{
				continue;
			}

			var display = file == root ? Path.GetFileName(file) : ToolPaths.RelativeTo(file, root is { Length: > 0 } ? root : cwd);
			var emitted = new HashSet<int>();

			for (var i = 0; i < lines.Count; i++)
			{
				ThrowIfAborted(invocation.CancellationToken);
				if (!regex.IsMatch(lines[i]))
				{
					continue;
				}

				if (matches >= limit)
				{
					truncated = true;
					break;
				}

				matches++;

				var from = Math.Max(0, i - context);
				var to = Math.Min(lines.Count - 1, i + context);
				for (var j = from; j <= to; j++)
				{
					if (!emitted.Add(j))
					{
						continue;
					}

					var separator = j == i ? ":" : "-";
					var (lineText, lineTruncated) = Truncate.Line(lines[j]);
					linesTruncated |= lineTruncated;
					output.Append(display).Append(separator).Append(j + 1).Append(separator).Append(' ').Append(lineText).Append('\n');
				}
			}
		}

		if (matches == 0)
		{
			return new AgentToolResult { Content = [new TextContent { Text = "No matches found" }] };
		}

		var rawOutput = output.ToString().TrimEnd('\n');
		var truncation = Truncate.Head(rawOutput, maxLines: int.MaxValue);
		var text = truncation.Content;
		var notices = new List<string>(3);
		if (truncated)
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

		var details = new JsonObject { ["matches"] = matches };
		if (truncated)
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
			Details = details,
		};
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

	private static bool IsBinaryFile(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			var buffer = new byte[8192];
			var read = stream.Read(buffer, 0, buffer.Length);
			return buffer.AsSpan(0, read).Contains((byte)0);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
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
