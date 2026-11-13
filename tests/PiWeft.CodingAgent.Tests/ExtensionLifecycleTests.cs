using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Extensions;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 扩展事件必须真的接到会话生命周期上，不能只在 ExtensionHost 里单测 Dispatch。
public sealed class ExtensionLifecycleTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-extlife-" + Guid.NewGuid().ToString("N")[..8]);

	public ExtensionLifecycleTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public async Task BeforeAgentStartToolResultAndAgentEndReachTheExtension()
	{
		var extensionPath = Path.Combine(_root, "lifecycle.js");
		File.WriteAllText(extensionPath, """
			module.exports = function (pi) {
				pi.registerTool({
					name: "echo",
					label: "Echo",
					description: "echo a value",
					parameters: { type: "object", properties: {} },
					execute: function () { return { content: [{ type: "text", text: "raw" }] }; }
				});
				pi.on("before_agent_start", function (event) {
					return { systemPrompt: event.systemPrompt + "\nEXT-MARK" };
				});
				pi.on("tool_result", function (event) {
					return { content: [{ type: "text", text: "rewritten-" + event.toolName }] };
				});
				var events = ["session_start", "agent_start", "agent_end", "turn_start", "turn_end",
					"message_start", "message_end", "tool_execution_start", "tool_execution_end",
					"session_info_changed", "thinking_level_select", "model_select", "session_shutdown"];
				for (var i = 0; i < events.length; i++) {
					(function (name) {
						pi.on(name, function () { pi.ui.notify(name, "info"); });
					})(events[i]);
				}
			};
			""");

		var ui = new RecordingUi();
		using var extensions = new ExtensionHost(ui);
		extensions.Load(_root, trusted: true, Path.Combine(_root, "agent"), [extensionPath], includeDiscovered: false);
		Assert.True(extensions.States.Single().Loaded, string.Join(" / ", extensions.States.Single().Diagnostics));

		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("call-1", "echo", new JsonObject());
		script.EnqueueText("done");

		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
			Extensions = extensions,
			UiHost = ui,
		});

		await session.PromptAsync("hi");
		session.SetSessionName("renamed");
		session.SetThinkingLevel(ThinkingLevels.High);
		session.SetModel(ScriptedSessions.Model("other-model"));
		session.Dispose();

		Assert.Contains("EXT-MARK", session.CurrentSystemPrompt, StringComparison.Ordinal);
		// 扩展看到的是基础提示词，不会把上一轮的改写再叠一遍。
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(session.CurrentSystemPrompt, "EXT-MARK"));
		var result = Assert.Single(session.Messages.OfType<ToolResultMessage>());
		Assert.Equal("rewritten-echo", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
		foreach (var expected in new[]
		{
			"session_start", "agent_start", "agent_end", "turn_start", "turn_end",
			"message_start", "message_end", "tool_execution_start", "tool_execution_end",
			"session_info_changed", "thinking_level_select", "model_select", "session_shutdown",
		})
		{
			Assert.Contains(expected, ui.Notifications);
		}
	}

	private sealed class RecordingUi : IUiHost
	{
		public bool HasUi => true;

		public List<string> Notifications { get; } = [];

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);

		public void Notify(UiNotifyRequest request) => Notifications.Add(request.Message);
	}

	[Fact]
	public async Task BeforeAgentStartMutatesSystemPromptSections()
	{
		var extensionPath = Path.Combine(_root, "sections.js");
		File.WriteAllText(extensionPath, """
			module.exports = function (pi) {
				pi.on("before_agent_start", function (event) {
					event.systemPromptOptions.sections["extension_section"] = "SECTION-TEXT";
					return undefined;
				});
			};
			""");

		using var extensions = new ExtensionHost();
		extensions.Load(_root, trusted: true, Path.Combine(_root, "agent"), [extensionPath], includeDiscovered: false);
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
			Extensions = extensions,
		});

		await session.PromptAsync("hi");

		// 基础提示词里没有这一段；扩展改的是 systemPromptOptions.sections，本轮请求要带上。
		Assert.Contains("SECTION-TEXT", session.CurrentSystemPrompt, StringComparison.Ordinal);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(session.CurrentSystemPrompt, "SECTION-TEXT"));
	}

	[Fact]
	public async Task BeforeAgentStartMessageIsInjectedAfterTheUserPrompt()
	{
		var extensionPath = Path.Combine(_root, "context.js");
		File.WriteAllText(extensionPath, """
			module.exports = function (pi) {
				pi.on("before_agent_start", function () {
					return { message: { customType: "ext-context", content: "EXT-CONTEXT", display: false } };
				});
			};
			""");

		using var extensions = new ExtensionHost();
		extensions.Load(_root, trusted: true, Path.Combine(_root, "agent"), [extensionPath], includeDiscovered: false);
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
			Extensions = extensions,
		});

		await session.PromptAsync("hi");

		var request = Assert.Single(script.Requests);
		var injected = Assert.Single(request.OfType<UserMessage>(), static message => message.CustomType == "ext-context");
		Assert.Equal("EXT-CONTEXT", injected.Content.Text);
		Assert.False(injected.Display);
		Assert.Contains(session.Messages.OfType<UserMessage>(), static message => message.CustomType == "ext-context");
	}

	[Fact]
	public void SystemCommandExecutesRegisteredExtensionCommands()
	{
		var extensionPath = Path.Combine(_root, "command.js");
		File.WriteAllText(extensionPath, """
			module.exports = function (pi) {
				pi.registerCommand("hello", { description: "say hello", handler: function (argument) { pi.ui.notify("hello " + argument, "info"); } });
			};
			""");

		var ui = new RecordingUi();
		using var extensions = new ExtensionHost(ui);
		extensions.Load(_root, trusted: true, Path.Combine(_root, "agent"), [extensionPath], includeDiscovered: false);
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
			Extensions = extensions,
			UiHost = ui,
		});

		Assert.True(session.TryExecuteSystemCommand("/hello weft", CancellationToken.None, out _));
		Assert.Contains("hello weft", ui.Notifications);
		Assert.False(session.TryExecuteSystemCommand("/not-a-command", CancellationToken.None, out _));
	}
}
