using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

/// <summary>内置文件工具：<c>read</c>、<c>write</c> 与 <c>edit</c>。</summary>
public static class FileTools
{
	private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",
	};

	public static AgentTool Read(string cwd) => new()
	{
		Name = "read",
		Label = "read",
		Description =
			$"Read the contents of a file. Supports text files and images (jpg, png, gif, webp, bmp). Images are sent as attachments. For text files, output is truncated to {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to the file to read (relative or absolute)" },
				["offset"] = new JsonObject { ["type"] = "number", ["description"] = "Line number to start reading from (1-indexed)" },
				["limit"] = new JsonObject { ["type"] = "number", ["description"] = "Maximum number of lines to read" },
			},
			["required"] = new JsonArray("path"),
		},
		Execute = invocation => Task.FromResult(ReadFile(cwd, invocation)),
	};

	private static AgentToolResult ReadFile(string cwd, AgentToolInvocation invocation)
	{
		var path = RequiredString(invocation, "path");
		var offset = OptionalInt(invocation, "offset");
		var limit = OptionalInt(invocation, "limit");
		var absolutePath = ToolPaths.ResolveReadPath(path, cwd);

		if (!File.Exists(absolutePath))
		{
			throw new FileNotFoundException($"File not found: {path}", absolutePath);
		}

		if (ImageExtensions.Contains(Path.GetExtension(absolutePath)))
		{
			var bytes = File.ReadAllBytes(absolutePath);
			var mimeType = MimeTypeFor(Path.GetExtension(absolutePath));
			return new AgentToolResult
			{
				Content =
				[
					new TextContent { Text = $"Read image file [{mimeType}]" },
					new ImageContent { Data = Convert.ToBase64String(bytes), MimeType = mimeType },
				],
				Details = new JsonObject { ["path"] = absolutePath, ["type"] = "image", ["mimeType"] = mimeType },
			};
		}

		var text = File.ReadAllText(absolutePath, Encoding.UTF8);
		var allLines = text.Split('\n');
		var totalFileLines = allLines.Length;
		var startLine = offset is { } o ? Math.Max(0, o - 1) : 0;
		var startLineDisplay = startLine + 1;

		if (startLine >= allLines.Length)
		{
			throw new InvalidOperationException(
				$"Offset {offset} is beyond end of file ({allLines.Length} lines total)");
		}

		string selected;
		int? userLimitedLines = null;
		if (limit is { } userLimit)
		{
			var endLine = Math.Min(startLine + userLimit, allLines.Length);
			selected = string.Join("\n", allLines[startLine..endLine]);
			userLimitedLines = endLine - startLine;
		}
		else
		{
			selected = string.Join("\n", allLines[startLine..]);
		}

		var truncation = Truncate.Head(selected);
		string output;
		JsonNode? details = null;
		var limitNote = $"[Truncated. Full file: {absolutePath}]";

		if (truncation.FirstLineExceedsLimit)
		{
			var firstLineSize = Truncate.FormatSize(Truncate.ByteCount(allLines[startLine]));
			output =
				$"[Line {startLineDisplay} is {firstLineSize}, exceeds {Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit. Use bash: sed -n '{startLineDisplay}p' {path} | head -c {Truncate.DefaultMaxBytes}]";
			details = TruncationDetails(truncation, absolutePath);
			limitNote = string.Empty;
		}
		else if (truncation.Truncated)
		{
			var endLineDisplay = startLineDisplay + truncation.OutputLines - 1;
			var nextOffset = endLineDisplay + 1;
			output = truncation.Content;
			output += truncation.TruncatedBy == "lines"
				? $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines}. Use offset={nextOffset} to continue.]"
				: $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Use offset={nextOffset} to continue.]";
			details = TruncationDetails(truncation, absolutePath);
			limitNote = string.Empty;
		}
		else if (userLimitedLines is { } limited && startLine + limited < allLines.Length)
		{
			var remaining = allLines.Length - (startLine + limited);
			var nextOffset = startLine + limited + 1;
			output = $"{truncation.Content}\n\n[{remaining} more lines in file. Use offset={nextOffset} to continue.]";
			limitNote = string.Empty;
		}
		else
		{
			output = truncation.Content;
			limitNote = string.Empty;
		}

		if (limitNote.Length > 0)
		{
			output += $"\n\n{limitNote}";
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = output }],
			Details = details,
		};
	}

	public static AgentTool Write(string cwd) => new()
	{
		Name = "write",
		Label = "write",
		Description = "Create or overwrite a file with the given content.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to the file to write (relative or absolute)" },
				["content"] = new JsonObject { ["type"] = "string", ["description"] = "Content to write to the file" },
			},
			["required"] = new JsonArray("path", "content"),
		},
		Execute = invocation => Task.FromResult(WriteFile(cwd, invocation)),
	};

	private static AgentToolResult WriteFile(string cwd, AgentToolInvocation invocation)
	{
		var path = RequiredString(invocation, "path");
		var content = RequiredString(invocation, "content");
		var absolutePath = ToolPaths.ResolveToCwd(path, cwd);

		ToolPaths.EnsureParentDirectory(absolutePath);
		File.WriteAllText(absolutePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		return new AgentToolResult
		{
			Content = [new TextContent { Text = $"Successfully wrote to {path}" }],
			Details = new JsonObject
			{
				["path"] = absolutePath,
				["bytes"] = Truncate.ByteCount(content),
			},
		};
	}

	public static AgentTool Edit(string cwd) => new()
	{
		Name = "edit",
		Label = "edit",
		Description = "Make precise file edits with exact text replacement, including multiple disjoint edits in one call.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to the file to edit (relative or absolute)" },
				["edits"] = new JsonObject
				{
					["type"] = "array",
					["description"] =
						"One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead.",
					["items"] = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject
						{
							["oldText"] = new JsonObject
							{
								["type"] = "string",
								["description"] =
									"Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call.",
							},
							["newText"] = new JsonObject
							{
								["type"] = "string",
								["description"] = "Replacement text for this targeted edit.",
							},
						},
						["required"] = new JsonArray("oldText", "newText"),
					},
				},
			},
			["required"] = new JsonArray("path", "edits"),
		},
		Execute = invocation => Task.FromResult(EditFile(cwd, invocation)),
	};

	private static AgentToolResult EditFile(string cwd, AgentToolInvocation invocation)
	{
		var path = RequiredString(invocation, "path");
		var absolutePath = ToolPaths.ResolveToCwd(path, cwd);

		if (!File.Exists(absolutePath))
		{
			throw new FileNotFoundException($"File not found: {path}", absolutePath);
		}

		var editsNode = invocation.Arguments["edits"] as JsonArray
			?? throw new InvalidOperationException("edits must be an array");

		var edits = new List<EditReplacement>(editsNode.Count);
		foreach (var node in editsNode)
		{
			if (node is not JsonObject edit)
			{
				throw new InvalidOperationException("each edit must be an object with oldText and newText");
			}

			var oldText = JsonArgs.String(edit["oldText"])
				?? throw new InvalidOperationException("edits[].oldText is required");
			var newText = JsonArgs.String(edit["newText"])
				?? throw new InvalidOperationException("edits[].newText is required");
			edits.Add(new EditReplacement(oldText, newText));
		}

		// 直接读字节：基于 StreamReader 的写法会吃掉开头的 BOM，而我们必须保留它。
		var raw = Encoding.UTF8.GetString(File.ReadAllBytes(absolutePath));
		var (hadBom, withoutBom) = EditDiff.SplitBom(raw);
		var lineEnding = EditDiff.DetectLineEnding(withoutBom);
		var normalized = EditDiff.NormalizeToLf(withoutBom);

		var applied = EditDiff.ApplyEdits(normalized, edits, path);
		var restored = EditDiff.RestoreLineEndings(applied.NewContent, lineEnding);
		File.WriteAllText(
			absolutePath,
			hadBom ? "\uFEFF" + restored : restored,
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		return new AgentToolResult
		{
			Content =
			[
				new TextContent
				{
					Text = $"Successfully edited {path} ({edits.Count} replacement{(edits.Count == 1 ? string.Empty : "s")})",
				},
			],
			Details = new JsonObject
			{
				["path"] = absolutePath,
				["patch"] = EditDiff.UnifiedPatch(applied.BaseContent, applied.NewContent, path),
				["firstChangedLine"] = EditDiff.FirstChangedLine(applied.BaseContent, applied.NewContent),
			},
		};
	}

	private static JsonObject TruncationDetails(TruncationResult truncation, string path) => new()
	{
		["path"] = path,
		["truncation"] = truncation.ToJson(),
	};

	private static string MimeTypeFor(string extension) => extension.ToLowerInvariant() switch
	{
		".jpg" or ".jpeg" => "image/jpeg",
		".png" => "image/png",
		".gif" => "image/gif",
		".webp" => "image/webp",
		".bmp" => "image/bmp",
		_ => "application/octet-stream",
	};

	internal static string RequiredString(AgentToolInvocation invocation, string name) =>
		JsonArgs.String(invocation.Arguments[name])
		?? throw new InvalidOperationException($"{name} is required");

	internal static int? OptionalInt(AgentToolInvocation invocation, string name) =>
		JsonArgs.Int(invocation.Arguments[name]);
}

internal static class TruncationJson
{
	public static JsonObject ToJson(this TruncationResult truncation) => new()
	{
		["truncated"] = truncation.Truncated,
		["truncatedBy"] = truncation.TruncatedBy,
		["totalLines"] = truncation.TotalLines,
		["totalBytes"] = truncation.TotalBytes,
		["outputLines"] = truncation.OutputLines,
		["outputBytes"] = truncation.OutputBytes,
		["lastLinePartial"] = truncation.LastLinePartial,
		["firstLineExceedsLimit"] = truncation.FirstLineExceedsLimit,
		["maxLines"] = truncation.MaxLines,
		["maxBytes"] = truncation.MaxBytes,
	};
}
