using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

/// <summary>内置 <c>powershell</c> 工具：在 Windows（以及装有 pwsh 的平台）执行 PowerShell。</summary>
public static class PowerShellTool
{
	public static AgentTool Create(string cwd, Func<BashSessionEnvironment?>? context = null) => new()
	{
		Name = "powershell",
		Label = "powershell",
		Description = "Execute a PowerShell command in the current working directory. Returns stdout and stderr. Output is truncated to the last 2000 lines or 50KB.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["command"] = new JsonObject { ["type"] = "string", ["description"] = "PowerShell command to execute" },
				["timeout"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Timeout in seconds (optional, no default timeout)",
				},
			},
			["required"] = new JsonArray("command"),
		},
		Execute = async invocation =>
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

			var result = await ShellRunner.RunAsync(
				command,
				cwd,
				timeoutSeconds,
				onOutput: null,
				invocation.CancellationToken,
				new ShellInvocation
				{
					PowerShell = true,
					Environment = environment.Count > 0 ? environment : null,
				}).ConfigureAwait(false);

			var truncation = Truncate.Tail(result.Output);
			var text = truncation.Content;
			string? fullOutputPath = null;
			if (truncation.Truncated)
			{
				fullOutputPath = ShellRunner.WriteFullOutput(result.Output);
				text += $"\n\n[Showing last {Truncate.FormatSize(truncation.OutputBytes)} of line {truncation.TotalLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Full output: {fullOutputPath}]";
			}

			if (result.ExitCode is { } exitCode && exitCode != 0)
			{
				text = text.Length == 0 ? $"Command exited with code {exitCode}" : $"{text}\n\nCommand exited with code {exitCode}";
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
		},
	};
}
