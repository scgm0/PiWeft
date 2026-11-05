using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

/// 内置 <c>bash</c> 工具执行时的会话上下文。非空字段会以 <c>PI_*</c> 环境变量注入子进程，
/// 扩展与脚本因此能感知当前会话（与 pi 的 <c>exposeSessionEnvironment</c> 一致）。
public sealed record BashSessionEnvironment
{
	public string? SessionId { get; init; }

	public string? SessionFile { get; init; }

	public string? Provider { get; init; }

	public string? ModelId { get; init; }

	public string? ThinkingLevel { get; init; }

	public string? ShellPath { get; init; }

	public string? CommandPrefix { get; init; }
}

public static class BashTool
{
	public static AgentTool Create(string cwd, Func<BashSessionEnvironment?>? context = null) => new()
	{
		Name = "bash",
		Label = "bash",
		Description =
			$"Execute a bash command in the current working directory. Returns stdout and stderr. " +
			$"Output is truncated to last {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). " +
			"If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.",
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
		OutputSchema = ShellToolFormatter.ShellOutputSchema(),
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

		invocation.OnUpdate?.Invoke(new AgentToolResult { Content = [], Details = null });
		var startedAt = Stopwatch.GetTimestamp();
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
					TempFilePrefix = "pi-bash",
				},
				onSnapshot: snapshot => invocation.OnUpdate?.Invoke(ShellToolFormatter.UpdateResult(snapshot)))
			.ConfigureAwait(false);

		var wallTimeSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
		var formatted = ShellToolFormatter.Format(
			result,
			command,
			"pi-bash",
			wallTimeSeconds,
			emptyText: result.Cancelled || result.TimedOut ? string.Empty : "(no output)");

		if (result.Cancelled)
		{
			throw new InvalidOperationException(AppendStatus(formatted.Text, "Command aborted"));
		}

		if (result.TimedOut)
		{
			var seconds = timeoutSeconds is { } timeout
				? timeout.ToString("0.###", CultureInfo.InvariantCulture)
				: "timeout";
			throw new InvalidOperationException(AppendStatus(formatted.Text, $"Command timed out after {seconds} seconds"));
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = formatted.Text }],
			Details = formatted.Details,
			StructuredContent = formatted.StructuredContent,
			IsError = formatted.IsError,
		};
	}

	internal static string AppendStatus(string text, string status) =>
		text.Length > 0 ? $"{text}\n\n{status}" : status;
}
