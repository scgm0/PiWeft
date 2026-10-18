using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

/// <summary>
/// 内置 <c>bash</c> 工具执行时的会话上下文。非空字段会以 <c>PI_*</c> 环境变量注入子进程，
/// 扩展与脚本因此能感知当前会话（与 pi 的 <c>exposeSessionEnvironment</c> 一致）。
/// </summary>
public sealed record BashSessionEnvironment
{
	public string? SessionId { get; init; }

	public string? SessionFile { get; init; }

	public string? Provider { get; init; }

	public string? ModelId { get; init; }

	public string? ThinkingLevel { get; init; }

	/// <summary><c>settings.shellPath</c>。</summary>
	public string? ShellPath { get; init; }

	/// <summary><c>settings.shellCommandPrefix</c>。</summary>
	public string? CommandPrefix { get; init; }
}

/// <summary>内置的 <c>bash</c> 工具：在会话的工作目录里执行 shell 命令。</summary>
public static class BashTool
{
	public static AgentTool Create(string cwd, Func<BashSessionEnvironment?>? context = null) => new()
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
		Execute = invocation => RunAsync(cwd, invocation, context),
	};

	private static async Task<AgentToolResult> RunAsync(
		string cwd,
		AgentToolInvocation invocation,
		Func<BashSessionEnvironment?>? context)
	{
		var command = FileTools.RequiredString(invocation, "command");
		var timeoutSeconds = JsonArgs.Number(invocation.Arguments["timeout"]);
		var session = context?.Invoke();

		var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
		if (session is not null)
		{
			if (session.SessionId is { Length: > 0 } sessionId)
			{
				environment["PI_SESSION_ID"] = sessionId;
			}

			if (session.SessionFile is { Length: > 0 } sessionFile)
			{
				environment["PI_SESSION_FILE"] = sessionFile;
			}

			if (session.Provider is { Length: > 0 } provider)
			{
				environment["PI_PROVIDER"] = provider;
			}

			if (session.ModelId is { Length: > 0 } modelId)
			{
				environment["PI_MODEL"] = modelId;
			}

			if (session.ThinkingLevel is { Length: > 0 } thinkingLevel)
			{
				environment["PI_REASONING_LEVEL"] = thinkingLevel;
			}
		}

		var result = await ShellRunner
			.RunAsync(
				command,
				cwd,
				timeoutSeconds,
				onOutput: null,
				invocation.CancellationToken,
				new ShellInvocation
				{
					ShellPath = session?.ShellPath,
					CommandPrefix = session?.CommandPrefix,
					Environment = environment.Count > 0 ? environment : null,
				})
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
