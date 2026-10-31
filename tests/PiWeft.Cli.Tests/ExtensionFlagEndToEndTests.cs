using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// CLI 未知 flag 的完整链路：参数解析 → 会话 → 扩展宿主 → <c>pi.getFlag</c>。
/// 单测分别覆盖了各段，这里验证它们接在一起时不会把值丢掉。
/// </summary>
[Collection("process-environment")]
public sealed class ExtensionFlagEndToEndTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-flag-e2e-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _agentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");

	public ExtensionFlagEndToEndTests()
	{
		Directory.CreateDirectory(_root);
		var extensionDirectory = Path.Combine(_root, "agent", "extensions");
		Directory.CreateDirectory(extensionDirectory);
		File.WriteAllText(Path.Combine(extensionDirectory, "flag.js"), """
			module.exports = function (pi) {
				pi.registerFlag("plan", { type: "string", default: "off" });
				pi.registerTool({
					name: "flag-value",
					description: "d",
					execute: function () { return { text: pi.getFlag("plan") }; }
				});
			};
			""");
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", Path.Combine(_root, "agent"));
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CliArguments Arguments(params string[] extra)
	{
		var parsed = CliArguments.Parse(
		[
			"--print",
			"--model", "anthropic/claude-sonnet-4-5",
			"--api-key", "test-key",
			"--session-dir", Path.Combine(_root, "sessions"),
			"--no-skills", "--no-prompt-templates", "--no-context-files",
			"--approve",
			.. extra,
		]);
		Assert.Null(parsed.Error);
		return parsed.Arguments!;
	}

	private static async Task<string> ReadFlagToolAsync(CodingAgentSession session)
	{
		var tool = Assert.Single(session.Tools, candidate => candidate.Name == "flag-value");
		var result = await tool.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1",
			ToolName = "flag-value",
			Arguments = new JsonObject(),
		});
		return Assert.IsType<PiWeft.Ai.TextContent>(result.Content[0]).Text;
	}

	[Fact]
	public async Task StringFlagFromTheCommandLineReachesPiGetFlag()
	{
		var arguments = Arguments("--plan", "strict");
		var (environment, error) = CliEnvironment.Create(arguments, _root, new FakeAnthropicHandler());
		Assert.Null(error);
		Assert.NotNull(environment);

		using var session = environment!.CreateSession(arguments, _root);

		Assert.Empty(session.UnknownExtensionFlags);
		Assert.Empty(session.ExtensionFlagErrors);
		Assert.Equal("strict", await ReadFlagToolAsync(session));
	}

	[Fact]
	public async Task UnregisteredFlagIsReportedForTheCliToReject()
	{
		var arguments = Arguments("--nope");
		var (environment, error) = CliEnvironment.Create(arguments, _root, new FakeAnthropicHandler());
		Assert.Null(error);

		using var session = environment!.CreateSession(arguments, _root);

		Assert.Contains("nope", session.UnknownExtensionFlags);
	}

	[Fact]
	public async Task CliRejectsUnknownFlagsBeforeCallingTheModel()
	{
		var handler = new FakeAnthropicHandler();
		var arguments = Arguments("--nope");
		var (environment, error) = CliEnvironment.Create(arguments, _root, handler);
		Assert.Null(error);

		var errorOutput = new StringWriter();
		var runner = new CliRunner(environment!, new StringWriter(), errorOutput);

		var exitCode = await runner.RunAsync(arguments, _root, new StringReader(string.Empty), CancellationToken.None);

		Assert.Equal(2, exitCode);
		Assert.Equal(0, handler.Requests);
		Assert.Contains("Unknown option --nope", errorOutput.ToString());
	}

	[Fact]
	public async Task NoExtensionsStillLoadsExplicitPaths()
	{
		var explicitPath = Path.Combine(_root, "explicit.js");
		File.WriteAllText(explicitPath, """
			module.exports = function (pi) {
				pi.registerTool({ name: "explicit", description: "d", execute: function () { return { text: "x" }; } });
			};
			""");

		var arguments = Arguments("-ne", "-e", explicitPath);
		var (environment, error) = CliEnvironment.Create(arguments, _root, new FakeAnthropicHandler());
		Assert.Null(error);

		using var session = environment!.CreateSession(arguments, _root);

		Assert.Contains(session.Tools, tool => tool.Name == "explicit");
		Assert.DoesNotContain(session.Tools, tool => tool.Name == "flag-value");
	}
}
