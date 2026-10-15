using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

/// <summary>内置的 <c>bash</c> 工具：在会话的工作目录里执行 shell 命令。</summary>
public static class BashTool
{
	public static AgentTool Create(string cwd) => new()
	{
		Name = "bash",
		Label = "bash",
		Description = "Execute bash commands (ls, grep, find, etc.). Output is truncated to the last 2000 lines or 50KB.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["command"] = new JsonObject { ["type"] = "string", ["description"] = "Shell command to execute" },
				["timeout"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Timeout in seconds (optional, no default timeout)",
				},
			},
			["required"] = new JsonArray("command"),
		},
		Execute = invocation => RunAsync(cwd, invocation),
	};

	private static async Task<AgentToolResult> RunAsync(string cwd, AgentToolInvocation invocation)
	{
		var command = FileTools.RequiredString(invocation, "command");
		var timeoutSeconds = JsonArgs.Number(invocation.Arguments["timeout"]);

		var result = await ShellRunner
			.RunAsync(command, cwd, timeoutSeconds, onOutput: null, invocation.CancellationToken)
			.ConfigureAwait(false);

		var truncation = Truncate.Tail(result.Output);
		var text = truncation.Content;
		string? fullOutputPath = null;

		if (truncation.Truncated)
		{
			fullOutputPath = ShellRunner.WriteFullOutput(result.Output);

			if (truncation.LastLinePartial)
			{
				text += $"\n\n[Showing last {Truncate.FormatSize(truncation.OutputBytes)} of line {truncation.TotalLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Full output: {fullOutputPath}]";
			}
			else
			{
				text += $"\n\n[Showing lines {truncation.TotalLines - truncation.OutputLines + 1}-{truncation.TotalLines} of {truncation.TotalLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Full output: {fullOutputPath}]";
			}
		}

		if (result.ExitCode is { } exitCode && exitCode != 0)
		{
			text = text.Length == 0
				? $"Command exited with code {exitCode}"
				: $"{text}\n\nCommand exited with code {exitCode}";
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = text }],
			Details = new JsonObject
			{
				["command"] = command,
				["exitCode"] = result.ExitCode,
				["truncated"] = truncation.Truncated,
				["fullOutputPath"] = fullOutputPath,
			},
		};
	}
}
