using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Imaging;

namespace PiWeft.CodingAgent.Tools;

public static class FileTools
{
	public static AgentTool Read(string cwd, Func<Model?>? model = null, Func<bool>? autoResize = null) => new()
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
		ConstrainedSampling = PiWeft.Ai.ConstrainedSampling.JsonSchema(),
		// 读文件本身是同步 IO，图片还要解码/缩放：放到线程池，别在内核把工具调用
		// 同步跑在桌面 UI 线程上时卡住整窗（一张 12MP 照片要几百毫秒）。
		Execute = invocation => Task.Run(() => ReadFile(cwd, invocation, model, autoResize)),
	};

	private static AgentToolResult ReadFile(
		string cwd,
		AgentToolInvocation invocation,
		Func<Model?>? model,
		Func<bool>? autoResize)
	{
		ThrowIfAborted(invocation.CancellationToken);
		var path = RequiredString(invocation, "path");
		var offset = OptionalInt(invocation, "offset");
		var limit = OptionalInt(invocation, "limit");
		var absolutePath = ToolPaths.ResolveReadPath(path, cwd);

		if (!File.Exists(absolutePath))
		{
			// 模型给错路径是常见情况：返回错误结果而不是抛异常，否则调试器会在
			// 第一次异常处中断，模型也拿不到可继续的错误信息。
			return AgentToolResult.Error($"File not found: {path}");
		}

		var mimeType = DetectImageMimeType(absolutePath);
		if (mimeType is not null)
		{
			var currentModel = model?.Invoke();
			var bytes = File.ReadAllBytes(absolutePath);
			// pi 的 read 会按模型限制缩放/转码（autoResizeImages 默认开）：
			// 超限的图片先应用 EXIF 方向、Lanczos3 缩小，再在 PNG/JPEG 里挑能进预算的那个。
			var processed = ImagePipeline.Process(
				bytes,
				mimeType,
				ImageResizeOptions.FromInputLimits(currentModel?.InputLimits),
				autoResize?.Invoke() ?? true);
			var note = $"Read image file [{mimeType}]";
			if (processed.Ok)
			{
				note = $"Read image file [{processed.MimeType}]";
				foreach (var hint in processed.Hints)
				{
					note += "\n" + hint;
				}
			}
			else if (processed.Error is { Length: > 0 } error)
			{
				note += "\n" + error;
			}

			if (currentModel is { SupportsImages: false })
			{
				note += "\n[Current model does not support images. The image will be omitted from this request.]";
			}

			var imageDetails = new JsonObject
			{
				["path"] = absolutePath,
				["type"] = "image",
				["mimeType"] = processed.Ok ? processed.MimeType : mimeType,
				["resized"] = processed.WasResized,
			};

			if (!processed.Ok)
			{
				// 与 pi 一致：转不了/缩不下来的图片只留说明，不把坏数据塞进上下文。
				return new AgentToolResult
				{
					Content = [new TextContent { Text = note }],
					Details = imageDetails,
				};
			}

			return new AgentToolResult
			{
				Content =
				[
					new TextContent { Text = note },
					new ImageContent { Data = processed.Data, MimeType = processed.MimeType },
				],
				Details = imageDetails,
				StructuredContent = new JsonObject
				{
					["type"] = "image",
					["data"] = processed.Data,
					["mimeType"] = processed.MimeType,
					["note"] = note,
				},
			};
		}

		// pi 用 Buffer.toString("utf-8") 读文本：BOM 会作为 U+FEFF 留在正文里，
		// 而 File.ReadAllText 会静默吃掉它。大文件不能整份读进内存：��保留会显示的行，
		// 其余只计数；取消在解码块之间检查。
		var startLine = offset is { } o ? Math.Max(0, o - 1) : 0;
		var startLineDisplay = startLine + 1;
		var selectedLines = SelectLines(
			absolutePath,
			startLine,
			limit is { } requestedLimit ? Math.Max(0, requestedLimit) : null,
			invocation.CancellationToken);
		var totalFileLines = selectedLines.TotalFileLines;

		if (startLine >= totalFileLines)
		{
			return AgentToolResult.Error(
				$"Offset {offset} is beyond end of file ({totalFileLines} lines total)");
		}

		var truncation = selectedLines.Truncation;
		int? userLimitedLines = limit is { } userLimit
			? Math.Min(Math.Max(0, userLimit), Math.Max(0, totalFileLines - startLine))
			: null;
		string output;
		JsonNode? details = null;

		if (truncation.FirstLineExceedsLimit)
		{
			var firstLineSize = Truncate.FormatSize(selectedLines.FirstLineBytes);
			output =
				$"[Line {startLineDisplay} is {firstLineSize}, exceeds {Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit. Use bash: sed -n '{startLineDisplay}p' {path} | head -c {Truncate.DefaultMaxBytes}]";
			details = TruncationDetails(truncation, absolutePath);
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
		}
		else if (userLimitedLines is { } limited && startLine + limited < totalFileLines)
		{
			var remaining = totalFileLines - (startLine + limited);
			var nextOffset = startLine + limited + 1;
			output = $"{truncation.Content}\n\n[{remaining} more lines in file. Use offset={nextOffset} to continue.]";
		}
		else
		{
			output = truncation.Content;
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = output }],
			Details = details,
			StructuredContent = JsonValue.Create(output),
		};
	}

	private readonly record struct SelectedLines(
		int TotalFileLines,
		TruncationResult Truncation,
		long FirstLineBytes);

	/// 按 <c>\n</c> 切行（不把孤立 <c>\r</c> 当换行，与 pi 的 <c>text.split("\n")</c> 一致），
	/// 只保留选中区间且能进截断预算的行；大文件内存上界是「预算 + 一行」。
	private static SelectedLines SelectLines(
		string path,
		int startLine,
		int? limit,
		CancellationToken cancellationToken)
	{
		var endLine = limit is { } userLimit ? (long)startLine + userLimit : long.MaxValue;
		var storedLines = new List<string>();
		var line = new StringBuilder();
		long storedBytes = 0;
		long selectedBytes = 0;
		long lineBytes = 0;
		long firstLineBytes = 0;
		long lineIndex = 0;
		var selectedLineCount = 0;
		var lastLineEmpty = true;
		var hasText = false;
		var storedPastCaps = false;

		void FinishLine()
		{
			var selected = lineIndex >= startLine && lineIndex < endLine;
			if (selected)
			{
				if (selectedLineCount == 0)
				{
					firstLineBytes = lineBytes;
				}

				selectedLineCount++;
				selectedBytes += lineBytes;
				lastLineEmpty = !hasText;

				if (!storedPastCaps)
				{
					var text = line.ToString();
					storedBytes += Encoding.UTF8.GetByteCount(text) + (storedLines.Count > 0 ? 1 : 0);
					storedLines.Add(text);
					if (storedLines.Count > Truncate.DefaultMaxLines || storedBytes > Truncate.DefaultMaxBytes)
					{
						// 与 pi 一样多存一行，让 truncateHead 按完整输入判断截断；之后只计数。
						storedPastCaps = true;
					}
				}
			}

			lineIndex++;
			line.Clear();
			lineBytes = 0;
			hasText = false;
		}

		var buffer = ArrayPool<char>.Shared.Rent(64 * 1024);
		try
		{
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite,
				64 * 1024,
				FileOptions.SequentialScan);
			// BOM 必须保留：pi 的 Buffer.toString("utf-8") 不会吞掉它。
			using var reader = new StreamReader(
				stream,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
				detectEncodingFromByteOrderMarks: false,
				bufferSize: 64 * 1024);

			while (true)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					throw new InvalidOperationException("Operation aborted");
				}
				var read = reader.Read(buffer, 0, buffer.Length);
				if (read == 0)
				{
					FinishLine();
					break;
				}

				var span = buffer.AsSpan(0, read);
				while (!span.IsEmpty)
				{
					var newline = span.IndexOf('\n');
					var segment = newline < 0 ? span : span[..newline];
					var selected = lineIndex >= startLine && lineIndex < endLine;
					if (selected && !segment.IsEmpty)
					{
						hasText = true;
						if (!storedPastCaps && lineBytes <= Truncate.DefaultMaxBytes)
						{
							line.Append(segment);
						}

						lineBytes += Encoding.UTF8.GetByteCount(segment);
					}

					if (newline < 0)
					{
						break;
					}

					FinishLine();
					span = span[(newline + 1)..];
				}
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer);
		}

		var stored = storedLines.Count == 0 ? string.Empty : string.Join('\n', storedLines);
		var truncation = Truncate.Head(stored) with
		{
			TotalLines = selectedBytes == 0 ? 0 : selectedLineCount - (lastLineEmpty ? 1 : 0),
			TotalBytes = selectedBytes,
		};

		// 流式路径不保存超预算的首行，但模型仍需要 pi 的可操作提示（与整份读取的语义一致）。
		if (selectedLineCount > 0 && firstLineBytes > Truncate.DefaultMaxBytes)
		{
			truncation = truncation with
			{
				Content = string.Empty,
				Truncated = true,
				TruncatedBy = "bytes",
				OutputLines = 0,
				OutputBytes = 0,
				FirstLineExceedsLimit = true,
			};
		}

		return new SelectedLines((int)lineIndex, truncation, firstLineBytes);
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
		ConstrainedSampling = PiWeft.Ai.ConstrainedSampling.JsonSchema(),
		Execute = invocation => Task.FromResult(WriteFile(cwd, invocation)),
	};

	/// 就地写入（与 pi 的 <c>fs.writeFile</c> 同语义）：打开现有的 inode、截断、写满。
	/// 不换文件，是因为「换文件」不是写入而是「注销旧文件再建一个新的」：硬链接会断开、
	/// 别的进程已经打开的句柄还指着旧内容、ACL/扩展属性/inode 全变。pi 接受写到一半
	/// 崩溃留下半截文件，这里同理；需要原子性的会话、设置、导入各自走临时文件 + rename。
	private static void WriteInPlace(string path, string content)
	{
		// 写入跟随符号链接：FileMode.Create 在链接路径上打开的就是目标文件，
		// 不解析也能保持链接本身不变（rename 式写入会把链接换成普通文件）。
		// 分块编码写入：不整份 GetBytes，十几 MB 的写入不再多一次等大分配。
		using var stream = new FileStream(
			path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0, FileOptions.SequentialScan);
		if (content.Length == 0)
		{
			return;
		}

		var encoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetEncoder();
		var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(WriteChunkChars));
		try
		{
			var remaining = content.AsSpan();
			while (!remaining.IsEmpty)
			{
				var chunk = remaining.Length > WriteChunkChars ? remaining[..WriteChunkChars] : remaining;
				var last = chunk.Length == remaining.Length;
				encoder.Convert(chunk, buffer, flush: last, out var charsUsed, out var bytesUsed, out _);
				if (bytesUsed > 0)
				{
					stream.Write(buffer, 0, bytesUsed);
				}

				remaining = remaining[charsUsed..];
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private const int WriteChunkChars = 16 * 1024;

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
			WriteInPlace(absolutePath, content);
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
		ConstrainedSampling = PiWeft.Ai.ConstrainedSampling.JsonSchema(),
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
		var normalized = EditDiff.NormalizeToLf(withoutBom);

		ThrowIfAborted(cancellationToken);
		// FinalContent 只替换命中的片段，未触碰的行保留原文的 CRLF/LF/CR。
		var applied = EditDiff.ApplyEdits(withoutBom, normalized, edits, path);
		var diff = EditDiff.DisplayDiff(applied.BaseContent, applied.NewContent);
		WriteInPlace(absolutePath, hadBom ? "\uFEFF" + applied.FinalContent : applied.FinalContent);
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

	/// 按文件头识别图片，不看扩展名。pi 的 mime.ts 同样只接受可安全内联的静态图片。
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
