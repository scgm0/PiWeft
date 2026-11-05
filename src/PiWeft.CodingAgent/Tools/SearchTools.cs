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
		entries.Sort(StringComparer.OrdinalIgnoreCase);

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

		var regex = GlobToRegex(pattern);
		var results = new List<string>();
		var resultLimitReached = false;

		foreach (var entry in FileWalker.EnumerateEntries(root, includeGitDirectory: true))
		{
			ThrowIfAborted(invocation.CancellationToken);
			var isDirectory = Directory.Exists(entry);
			var relative = ToolPaths.RelativeTo(entry, root);
			var candidate = pattern.Contains('/') ? relative : Path.GetFileName(entry);
			if (!regex.IsMatch(candidate))
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

		var globRegex = glob is null ? null : GlobToRegex(glob);
		var root = path is null ? cwd : ToolPaths.ResolveToCwd(path, cwd);

		var files = new List<string>();
		if (File.Exists(root))
		{
			if (globRegex is null || GlobMatches(globRegex, glob!, root, root))
			{
				files.Add(root);
			}
		}
		else if (Directory.Exists(root))
		{
			foreach (var file in FileWalker.EnumerateFiles(root, includeGitDirectory: true))
			{
				ThrowIfAborted(invocation.CancellationToken);
				if (globRegex is null || GlobMatches(globRegex, glob!, file, root))
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
		var relative = ToolPaths.RelativeTo(file, root);
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

	public static Regex GlobToRegex(string pattern)
	{
		var builder = new StringBuilder(pattern.Length * 2 + 8);
		builder.Append('^');

		for (var i = 0; i < pattern.Length; i++)
		{
			var c = pattern[i];
			switch (c)
			{
				case '*':
					if (i + 1 < pattern.Length && pattern[i + 1] == '*')
					{
						i++;
						if (i + 1 < pattern.Length && pattern[i + 1] == '/')
						{
							i++;
							builder.Append("(.*/)?");
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
					var close = pattern.IndexOf(']', i + 1);
					if (close > i)
					{
						builder.Append(pattern, i, close - i + 1);
						i = close;
					}
					else
					{
						builder.Append("\\[");
					}

					break;
				}
				default:
					builder.Append(Regex.Escape(c.ToString()));
					break;
			}
		}

		builder.Append('$');
		return new Regex(builder.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
	}
}
