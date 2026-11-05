using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

/// bash / powershell 共用的结果格式化：截断、完整输出路径、状态行与 structuredContent。
internal sealed record ShellToolOutput(
	string Text,
	JsonObject Details,
	JsonObject StructuredContent,
	bool IsError,
	string? FullOutputPath,
	TruncationResult Truncation);

internal static class ShellToolFormatter
{
	private const long StructuredOutputMaxBytes = 1024 * 1024;

	public static ShellToolOutput Format(
		ShellResult result,
		string command,
		string tempFilePrefix,
		double wallTimeSeconds,
		string emptyText = "(no output)")
	{
		var truncation = Truncate.Tail(result.Output);
		if (result.OutputTruncated)
		{
			// 内存里只有尾部窗口：行号/字节数按命令的真实总量报告。
			truncation = truncation with { TotalLines = result.TotalLines, TotalBytes = result.TotalBytes };
		}

		var text = truncation.Content.Length > 0 ? truncation.Content : emptyText;
		string? fullOutputPath = null;
		JsonObject? truncationDetails = null;
		if (truncation.Truncated)
		{
			fullOutputPath = result.FullOutputPath ?? ShellRunner.WriteFullOutput(result.Output, tempFilePrefix);
			truncationDetails = truncation.ToJson();
			var startLine = truncation.TotalLines - truncation.OutputLines + 1;
			var endLine = truncation.TotalLines;
			if (truncation.LastLinePartial)
			{
				text += $"\n\n[Showing last {Truncate.FormatSize(truncation.OutputBytes)} of line {endLine} (line is {Truncate.FormatSize(LastLineBytes(result.Output))}). Full output: {fullOutputPath}]";
			}
			else if (truncation.TruncatedBy == "lines")
			{
				text += $"\n\n[Showing lines {startLine}-{endLine} of {truncation.TotalLines}. Full output: {fullOutputPath}]";
			}
			else
			{
				text += $"\n\n[Showing lines {startLine}-{endLine} of {truncation.TotalLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Full output: {fullOutputPath}]";
			}
		}

		var isError = result.ExitCode is { } exitCode && exitCode != 0;
		if (isError)
		{
			text = text.Length > 0
				? $"{text}\n\nCommand exited with code {result.ExitCode}"
				: $"Command exited with code {result.ExitCode}";
		}

		var details = new JsonObject
		{
			["command"] = command,
			["exitCode"] = result.ExitCode,
			["truncated"] = truncation.Truncated,
			["fullOutputPath"] = fullOutputPath,
		};
		if (truncationDetails is not null)
		{
			details["truncation"] = truncationDetails;
		}

		var structuredOutput = result.Output;
		var structuredTruncated = false;
		if (result.OutputTruncated)
		{
			structuredTruncated = true;
			if (result.FullOutputPath is { Length: > 0 } path)
			{
				try
				{
					var full = ShellRunner.ReadFullOutput(path, StructuredOutputMaxBytes);
					structuredOutput = full.Content;
					structuredTruncated = full.Truncated;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// 完整文件不可读时保留尾部窗口；truncated 仍为 true。
				}
			}
		}

		var structured = new JsonObject
		{
			["output"] = structuredOutput,
			["truncated"] = structuredTruncated,
			["exit_code"] = result.ExitCode ?? 0,
			["wall_time_seconds"] = Math.Round(wallTimeSeconds * 10) / 10,
		};
		if (structuredTruncated && fullOutputPath is { Length: > 0 } outputPath)
		{
			structured["full_output_path"] = outputPath;
		}

		return new ShellToolOutput(text, details, structured, isError, fullOutputPath, truncation);
	}

	public static AgentToolResult UpdateResult(ShellSnapshot snapshot)
	{
		var truncation = Truncate.Tail(snapshot.Content);
		if (snapshot.OutputTruncated)
		{
			truncation = truncation with { TotalLines = snapshot.TotalLines, TotalBytes = snapshot.TotalBytes };
		}

		var details = new JsonObject();
		if (truncation.Truncated)
		{
			details["truncation"] = truncation.ToJson();
			details["fullOutputPath"] = snapshot.FullOutputPath;
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = truncation.Content }],
			Details = details.Count > 0 ? details : null,
		};
	}

	public static JsonObject ShellOutputSchema() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["output"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Combined stdout and stderr, possibly truncated",
			},
			["truncated"] = new JsonObject { ["type"] = "boolean" },
			["full_output_path"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Full output, when truncated",
			},
			["exit_code"] = new JsonObject { ["type"] = "number" },
			["wall_time_seconds"] = new JsonObject { ["type"] = "number" },
		},
		["required"] = new JsonArray("output", "truncated", "exit_code", "wall_time_seconds"),
	};

	private static long LastLineBytes(string text)
	{
		var newline = text.LastIndexOf('\n');
		return Truncate.ByteCount(newline < 0 ? text : text[(newline + 1)..]);
	}
}
