using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

public static class PowerShellTool
{
	public static AgentTool Create(string cwd, Func<BashSessionEnvironment?>? context = null) => new()
	{
		Name = "powershell",
		Label = "powershell",
		Description =
			$"Execute a PowerShell command in the current working directory. Returns stdout and stderr. " +
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
			if (session.SessionId is { Length: > 0 } sessionId) environment["PI_SESSION_ID"] = sessionId;
			if (session.SessionFile is { Length: > 0 } sessionFile) environment["PI_SESSION_FILE"] = sessionFile;
			if (session.Provider is { Length: > 0 } provider) environment["PI_PROVIDER"] = provider;
			if (session.ModelId is { Length: > 0 } modelId) environment["PI_MODEL"] = modelId;
			if (session.ThinkingLevel is { Length: > 0 } thinkingLevel) environment["PI_REASONING_LEVEL"] = thinkingLevel;
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
					PowerShell = true,
					Environment = environment.Count > 0 ? environment : null,
					TempFilePrefix = "pi-powershell",
				},
				onSnapshot: snapshot => invocation.OnUpdate?.Invoke(ShellToolFormatter.UpdateResult(snapshot)))
			.ConfigureAwait(false);

		var wallTimeSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
		var formatted = ShellToolFormatter.Format(
			result,
			command,
			"pi-powershell",
			wallTimeSeconds,
			emptyText: result.Cancelled || result.TimedOut ? string.Empty : "(no output)");

		if (result.Cancelled)
		{
			throw new InvalidOperationException(BashTool.AppendStatus(formatted.Text, "Command aborted"));
		}

		if (result.TimedOut)
		{
			throw new InvalidOperationException(BashTool.TimeoutStatus(result, timeoutSeconds, formatted.Text));
		}

		return new AgentToolResult
		{
			Content = [new TextContent { Text = formatted.Text }],
			Details = formatted.Details,
			StructuredContent = formatted.StructuredContent,
			IsError = formatted.IsError,
		};
	}
}
