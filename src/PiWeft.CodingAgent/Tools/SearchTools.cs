using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

/// <summary>内置搜索工具：<c>ls</c>、<c>find</c> 与 <c>grep</c>。</summary>
public static class SearchTools
{
	public static AgentTool Ls(string cwd) => new()
	{
		Name = "ls",
		Label = "ls",
		Description = "List directory contents. Directories are shown with a trailing slash.",
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
		var path = JsonArgs.String(invocation.Arguments["path"]);
		var limit = FileTools.OptionalInt(invocation, "limit") ?? 500;
		var directory = path is null ? cwd : ToolPaths.ResolveToCwd(path, cwd);

		if (!Directory.Exists(directory))
		{
			throw new DirectoryNotFoundException($"Directory not found: {path ?? directory}");
		}

		var entries = new List<string>();
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

		entries.Sort(StringComparer.Ordinal);

		if (entries.Count == 0)
		{
			return new AgentToolResult { Content = [new TextContent { Text = "(empty directory)" }] };
		}

		var truncated = entries.Count > limit;
		var shown = truncated ? entries.GetRange(0, limit) : entries;
		var output = string.Join("\n", shown);
		if (truncated)
		{
			output += $"\n\n[Showing {limit} of {entries.Count} entries. Use a smaller path or a larger limit.]";
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = output }],
			Details = new JsonObject { ["path"] = directory, ["entries"] = entries.Count },
		};
	}

	public static AgentTool Find(string cwd) => new()
	{
		Name = "find",
		Label = "find",
		Description = "Find files by glob pattern. Respects .gitignore.",
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
		var limit = FileTools.OptionalInt(invocation, "limit") ?? 1000;
		var root = path is null ? cwd : ToolPaths.ResolveToCwd(path, cwd);

		if (!Directory.Exists(root))
		{
			throw new DirectoryNotFoundException($"Directory not found: {path ?? root}");
		}

		var regex = GlobToRegex(pattern);
		var results = new List<string>();
		var truncated = false;

		foreach (var file in FileWalker.EnumerateFiles(root))
		{
			var relative = ToolPaths.RelativeTo(file, root);
			if (!regex.IsMatch(relative))
			{
				continue;
			}

			if (results.Count >= limit)
			{
				truncated = true;
				break;
			}

			results.Add(relative);
		}

		if (results.Count == 0)
		{
			return new AgentToolResult { Content = [new TextContent { Text = "No files found matching pattern" }] };
		}

		var output = new StringBuilder(string.Join("\n", results));
		if (truncated)
		{
			output.Append($"\n\n[Truncated at {limit} results. Use a narrower pattern or path.]");
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = output.ToString() }],
			Details = new JsonObject { ["path"] = root, ["count"] = results.Count },
		};
	}

	public static AgentTool Grep(string cwd) => new()
	{
		Name = "grep",
		Label = "grep",
		Description = "Search file contents for patterns. Respects .gitignore.",
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
		var context = FileTools.OptionalInt(invocation, "context") ?? 0;
		var limit = FileTools.OptionalInt(invocation, "limit") ?? 100;

		var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
		if (ignoreCase)
		{
			options |= RegexOptions.IgnoreCase;
		}

		if (literal)
		{
			options |= RegexOptions.IgnorePatternWhitespace;
		}

		var regex = literal
			? new Regex(Regex.Escape(pattern), options, TimeSpan.FromSeconds(2))
			: SafeRegex(pattern, options);

		var globRegex = glob is null ? null : GlobToRegex(glob);
		var root = path is null ? cwd : ToolPaths.ResolveToCwd(path, cwd);

		var files = new List<string>();
		if (File.Exists(root))
		{
			files.Add(root);
		}
		else if (Directory.Exists(root))
		{
			foreach (var file in FileWalker.EnumerateFiles(root))
			{
				if (globRegex is null || globRegex.IsMatch(ToolPaths.RelativeTo(file, root)))
				{
					files.Add(file);
				}
			}
		}
		else
		{
			throw new DirectoryNotFoundException($"Path not found: {path ?? root}");
		}

		var output = new StringBuilder();
		var matches = 0;
		var truncated = false;

		foreach (var file in files)
		{
			if (truncated)
			{
				break;
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
					var (text, _) = Truncate.Line(lines[j]);
					output.Append(display).Append(separator).Append(j + 1).Append(separator).Append(' ').Append(text).Append('\n');
				}
			}
		}

		if (matches == 0)
		{
			return new AgentToolResult { Content = [new TextContent { Text = "No matches found" }] };
		}

		var text2 = output.ToString().TrimEnd('\n');
		if (truncated)
		{
			text2 += $"\n\n[Truncated at {limit} matches. Use a narrower pattern or path.]";
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = text2 }],
			Details = new JsonObject { ["matches"] = matches },
		};
	}

	private static Regex SafeRegex(string pattern, RegexOptions options)
	{
		try
		{
			return new Regex(pattern, options, TimeSpan.FromSeconds(2));
		}
		catch (ArgumentException ex)
		{
			throw new InvalidOperationException($"Invalid regular expression: {ex.Message}");
		}
	}

	/// <summary>把 glob（<c>*</c>、<c>?</c>、<c>**</c>）转成针对 posix 分隔相对路径的正则。</summary>
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
