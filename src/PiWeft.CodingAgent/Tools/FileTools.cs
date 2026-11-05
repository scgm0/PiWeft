using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

public static class FileTools
{
	public static AgentTool Read(string cwd, Func<Model?>? model = null) => new()
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
		OutputSchema = ReadOutputSchema(),
		Execute = invocation => Task.FromResult(ReadFile(cwd, invocation, model)),
	};

	private static AgentToolResult ReadFile(string cwd, AgentToolInvocation invocation, Func<Model?>? model)
	{
		ThrowIfAborted(invocation.CancellationToken);
		var path = RequiredString(invocation, "path");
		var offset = OptionalInt(invocation, "offset");
		var limit = OptionalInt(invocation, "limit");
		var absolutePath = ToolPaths.ResolveReadPath(path, cwd);

		if (!File.Exists(absolutePath))
		{
			throw new FileNotFoundException($"File not found: {path}", absolutePath);
		}

		var mimeType = DetectImageMimeType(absolutePath);
		if (mimeType is not null)
		{
			var bytes = File.ReadAllBytes(absolutePath);
			var encoded = Convert.ToBase64String(bytes);
			var note = $"Read image file [{mimeType}]";
			if (model?.Invoke() is { SupportsImages: false })
			{
				note += "\n[Current model does not support images. The image will be omitted from this request.]";
			}

			return new AgentToolResult
			{
				Content =
				[
					new TextContent { Text = note },
					new ImageContent { Data = encoded, MimeType = mimeType },
				],
				Details = new JsonObject { ["path"] = absolutePath, ["type"] = "image", ["mimeType"] = mimeType },
				StructuredContent = new JsonObject
				{
					["type"] = "image",
					["data"] = encoded,
					["mimeType"] = mimeType,
					["note"] = note,
				},
			};
		}

		// pi 用 Buffer.toString("utf-8") 读文本：BOM 会作为 U+FEFF 留在正文里，
		// 而 File.ReadAllText 会静默吃掉它。
		ThrowIfAborted(invocation.CancellationToken);
		var text = Encoding.UTF8.GetString(File.ReadAllBytes(absolutePath));
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
			// 负的 limit 没有意义；夹到 0，避免 C# range 的 end < start 直接抛异常。
			userLimit = Math.Max(0, userLimit);
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
			StructuredContent = JsonValue.Create(output),
		};
	}

	private static JsonObject ReadOutputSchema() => new()
	{
		["anyOf"] = new JsonArray(
			new JsonObject { ["type"] = "string" },
			new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["type"] = new JsonObject { ["type"] = "string" },
					["data"] = new JsonObject { ["type"] = "string" },
					["mimeType"] = new JsonObject { ["type"] = "string" },
					["note"] = new JsonObject { ["type"] = "string" },
				},
				["required"] = new JsonArray("type", "data", "mimeType", "note"),
			}),
	};

	public static AgentTool Write(string cwd) => new()
	{
		Name = "write",
		Label = "write",
		Description = "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.",
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

	/// 同目录临时文件 + 原子替换：写到一半崩溃/断电不会把原文件截成半截（那可能是用户几天的改动）。
	/// 覆盖时保留原有的 Unix 权限位，避免 <c>chmod +x</c> 过的脚本丢掉执行位。
	private static void WriteAtomically(string path, string content)
	{
		// 写入要跟随符号链接：Node 的 fs.writeFile 写到目标，而 rename 覆盖的是链接本身。
		// 不解析的话，一次 edit 就会把 ~/.config 里的链变成普通文件，别处的链接全部断掉。
		var target = ResolveSymlinkTarget(path);

		UnixFileMode? preserved = null;
		if (!OperatingSystem.IsWindows() && File.Exists(target))
		{
			try
			{
				preserved = File.GetUnixFileMode(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
			{
				// 读不到权限位就按默认落盘。
			}
		}

		var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.Move(temporary, target, overwrite: true);

			if (preserved is { } mode && !OperatingSystem.IsWindows())
			{
				try
				{
					File.SetUnixFileMode(target, mode);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
				{
					// 恢复权限失败不影响写入结果本身。
				}
			}
		}
		finally
		{
			if (File.Exists(temporary))
			{
				try
				{
					File.Delete(temporary);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// 清理失败不影响主流程。
				}
			}
		}
	}

	private static string ResolveSymlinkTarget(string path)
	{
		try
		{
			// FileInfo.LinkTarget 对普通文件/不存在的路径返回 null 且不抛异常；
			// File.ResolveLinkTarget 在路径不存在时抛 FileNotFoundException —— 新建文件的常见路径
			// 不该为「它不是链接」付一次异常的成本。
			if (new FileInfo(path).LinkTarget is null)
			{
				return path;
			}

			return File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target
				? Path.GetFullPath(target.FullName)
				: path;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException)
		{
			return path;
		}
	}

	private static AgentToolResult WriteFile(string cwd, AgentToolInvocation invocation)
	{
		var path = RequiredString(invocation, "path");
		var content = RequiredString(invocation, "content");
		var absolutePath = ToolPaths.ResolveToCwd(path, cwd);

		FileMutationQueue.Mutate(absolutePath, () =>
		{
			ThrowIfAborted(invocation.CancellationToken);
			EnsureWritableIfExisting(absolutePath, path);
			ToolPaths.EnsureParentDirectory(absolutePath);
			WriteAtomically(absolutePath, content);
		});

		return new AgentToolResult
		{
			Content = [new TextContent { Text = $"Successfully wrote to {path}" }],
		};
	}

	public static AgentTool Edit(string cwd) => new()
	{
		Name = "edit",
		Label = "edit",
		Description = "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.",
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
		PrepareArguments = PrepareEditArguments,
		Execute = invocation => Task.FromResult(EditFile(cwd, invocation)),
	};

	private static AgentToolResult EditFile(string cwd, AgentToolInvocation invocation)
	{
		var path = RequiredString(invocation, "path");
		var absolutePath = ToolPaths.ResolveToCwd(path, cwd);
		// 顶层 agent loop 会执行 PrepareArguments；直接调用工具（测试、codemode 之外的宿主）
		// 时也要接受 pi 的三种旧式参数，否则一个 JSON 字符串 edits 就会直接失败。
		var arguments = PrepareEditArguments(invocation.Arguments);

		return FileMutationQueue.Mutate(
			absolutePath,
			() => ApplyEdits(path, absolutePath, arguments, invocation.CancellationToken));
	}

	private static AgentToolResult ApplyEdits(
		string path,
		string absolutePath,
		JsonObject arguments,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(absolutePath))
		{
			throw new FileNotFoundException($"File not found: {path}", absolutePath);
		}

		ThrowIfAborted(cancellationToken);
		EnsureWritableIfExisting(absolutePath, path);

		var editsNode = arguments["edits"] as JsonArray
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

		ThrowIfAborted(cancellationToken);
		var applied = EditDiff.ApplyEdits(normalized, edits, path);
		var restored = EditDiff.RestoreLineEndings(applied.NewContent, lineEnding);
		WriteAtomically(absolutePath, hadBom ? "\uFEFF" + restored : restored);

		var diff = EditDiff.DisplayDiff(applied.BaseContent, applied.NewContent);
		return new AgentToolResult
		{
			Content =
			[
				new TextContent
				{
					Text = $"Successfully replaced {edits.Count} block(s) in {path}.",
				},
			],
			Details = new JsonObject
			{
				["path"] = absolutePath,
				["diff"] = diff.Diff,
				["patch"] = EditDiff.UnifiedPatch(applied.BaseContent, applied.NewContent, path),
				["firstChangedLine"] = diff.FirstChangedLine,
			},
		};
	}

	/// pi 的 edit 兼容层：edits 可能是 JSON 字符串、单个对象，或旧式顶层 oldText/newText。
	private static JsonObject PrepareEditArguments(JsonObject arguments)
	{
		var result = (JsonObject)arguments.DeepClone();
		var editsNode = result["edits"];

		if (editsNode is JsonValue value && value.GetValueKind() == JsonValueKind.String)
		{
			try
			{
				editsNode = JsonNode.Parse(value.GetValue<string>());
				if (editsNode is JsonArray || IsSingleEdit(editsNode))
				{
					result["edits"] = editsNode;
				}
				else
				{
					editsNode = result["edits"];
				}
			}
			catch (JsonException)
			{
				// 保留原文；后续 schema 校验会给出明确错误。
				editsNode = result["edits"];
			}
		}

		if (IsSingleEdit(editsNode))
		{
			result["edits"] = new JsonArray(((JsonObject)editsNode!).DeepClone());
			editsNode = result["edits"];
		}

		if (JsonArgs.String(result["oldText"]) is { } oldText && JsonArgs.String(result["newText"]) is { } newText)
		{
			var array = editsNode as JsonArray ?? new JsonArray();
			array.Add((JsonNode)new JsonObject { ["oldText"] = oldText, ["newText"] = newText });
			result["edits"] = array;
			result.Remove("oldText");
			result.Remove("newText");
		}

		return result;
	}

	private static bool IsSingleEdit(JsonNode? node) =>
		node is JsonObject edit && JsonArgs.String(edit["oldText"]) is not null && JsonArgs.String(edit["newText"]) is not null;

	private static void EnsureWritableIfExisting(string absolutePath, string displayPath)
	{
		if (!File.Exists(absolutePath))
		{
			return;
		}

		if (OperatingSystem.IsWindows() &&
			(File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) != 0)
		{
			throw new InvalidOperationException($"Could not edit file: {displayPath}. Error code: EACCES.");
		}

		try
		{
			using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
		}
		catch (UnauthorizedAccessException exception)
		{
			throw new InvalidOperationException($"Could not edit file: {displayPath}. Error code: EACCES.", exception);
		}
		catch (IOException exception) when (!OperatingSystem.IsWindows() &&
			exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"Could not edit file: {displayPath}. Error code: EACCES.", exception);
		}
	}

	private static void ThrowIfAborted(CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			throw new InvalidOperationException("Operation aborted");
		}
	}

	private static JsonObject TruncationDetails(TruncationResult truncation, string path) => new()
	{
		["path"] = path,
		["truncation"] = truncation.ToJson(),
	};

	/// 按文件头识别图片，而不是按扩展名。pi 的 mime.ts 同样只接受可安全内联的静态图片。
	private static string? DetectImageMimeType(string path)
	{
		const int sniffBytes = 4100;
		var buffer = new byte[sniffBytes];
		int read;
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			read = stream.Read(buffer, 0, buffer.Length);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		var data = buffer.AsSpan(0, read);
		if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF && data[3] != 0xF7)
		{
			return "image/jpeg";
		}

		if (StartsWith(data, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) && IsStaticPng(data))
		{
			return "image/png";
		}

		if (StartsWithAscii(data, 0, "GIF87a") || StartsWithAscii(data, 0, "GIF89a"))
		{
			return "image/gif";
		}

		if (StartsWithAscii(data, 0, "RIFF") && StartsWithAscii(data, 8, "WEBP"))
		{
			return "image/webp";
		}

		if (StartsWithAscii(data, 0, "BM") && IsBmp(data))
		{
			return "image/bmp";
		}

		return null;
	}

	private static bool IsStaticPng(ReadOnlySpan<byte> data)
	{
		if (data.Length < 16 || ReadUInt32BigEndian(data, 8) != 13 || !StartsWithAscii(data, 12, "IHDR"))
		{
			return false;
		}

		var offset = 8;
		while (offset + 8 <= data.Length)
		{
			var length = ReadUInt32BigEndian(data, offset);
			var typeOffset = offset + 4;
			if (StartsWithAscii(data, typeOffset, "acTL"))
			{
				return false; // animated PNG 不内联
			}

			if (StartsWithAscii(data, typeOffset, "IDAT"))
			{
				return true;
			}

			var next = (long)offset + 8 + length + 4;
			if (next <= offset || next > data.Length)
			{
				return true;
			}

			offset = (int)next;
		}

		return true;
	}

	private static bool IsBmp(ReadOnlySpan<byte> data)
	{
		if (data.Length < 26)
		{
			return false;
		}

		var declaredSize = ReadUInt32LittleEndian(data, 2);
		var pixelOffset = ReadUInt32LittleEndian(data, 10);
		var dibSize = ReadUInt32LittleEndian(data, 14);
		if (declaredSize != 0 && declaredSize < 26)
		{
			return false;
		}

		if (pixelOffset < 14 + dibSize)
		{
			return false;
		}

		if (declaredSize != 0 && pixelOffset >= declaredSize)
		{
			return false;
		}

		int planes;
		int bits;
		if (dibSize == 12)
		{
			planes = ReadUInt16LittleEndian(data, 22);
			bits = ReadUInt16LittleEndian(data, 24);
		}
		else if (dibSize is >= 40 and <= 124)
		{
			if (data.Length < 30)
			{
				return false;
			}

			planes = ReadUInt16LittleEndian(data, 26);
			bits = ReadUInt16LittleEndian(data, 28);
		}
		else
		{
			return false;
		}

		return planes == 1 && bits is 1 or 4 or 8 or 16 or 24 or 32;
	}

	private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
		data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

	private static bool StartsWithAscii(ReadOnlySpan<byte> data, int offset, string text)
	{
		if (data.Length < offset + text.Length)
		{
			return false;
		}

		for (var index = 0; index < text.Length; index++)
		{
			if (data[offset + index] != text[index])
			{
				return false;
			}
		}

		return true;
	}

	private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset) =>
		((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

	private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> data, int offset) =>
		data[offset] | ((uint)data[offset + 1] << 8) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);

	private static int ReadUInt16LittleEndian(ReadOnlySpan<byte> data, int offset) =>
		data[offset] | (data[offset + 1] << 8);

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
