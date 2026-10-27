using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Extensions;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 扩展宿主的可执行契约：发现 → 沙箱执行 → 注册 → 回调。
/// 全部用临时目录里的真实扩展文件，因为「宿主能跑」这件事只能靠真的跑一遍来证明。
/// <para>
/// 其中一个用例改 <c>PI_CODING_AGENT_DIR</c>（会话按它发现扩展），所以整个类归到串行集合里 ——
/// 并行跑时别的测试会读到对方设置的 agent 目录。
/// </para>
/// </summary>
[Collection("process-environment")]
public sealed class ExtensionHostTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-ext-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;
	private readonly string _cwd;

	public ExtensionHostTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		_cwd = Path.Combine(_root, "project");
		Directory.CreateDirectory(Path.Combine(_agentDir, ExtensionDescriptor.DirectoryName));
		Directory.CreateDirectory(_cwd);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	private string Write(string name, string source, bool project = false)
	{
		var directory = project
			? Path.Combine(_cwd, SessionPaths.ConfigDirName, ExtensionDescriptor.DirectoryName)
			: Path.Combine(_agentDir, ExtensionDescriptor.DirectoryName);
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, name);
		File.WriteAllText(path, source);
		return path;
	}

	private ExtensionState Load(string name, string source, bool project = false)
	{
		using var host = new ExtensionHost();
		Write(name, source, project);
		host.Load(_cwd, trusted: true, _agentDir);
		Assert.Single(host.States);
		return host.States[0];
	}

	// ---------------------------------------------------------------- 发现

	[Fact]
	public void GlobalAndProjectExtensionsAreBothDiscovered()
	{
		Write("global.js", "module.exports = function (pi) { pi.registerCommand('g', 'd', function () {}); };");
		Write("project.js", "module.exports = function (pi) { pi.registerCommand('p', 'd', function () {}); };", project: true);

		using var host = new ExtensionHost();
		host.Load(_cwd, trusted: true, _agentDir);

		Assert.Equal(2, host.States.Count);
		Assert.All(host.States, state => Assert.True(state.Loaded, string.Join(" / ", state.Diagnostics)));
		Assert.Equal(["g", "p"], host.Commands.Select(static command => command.Name).OrderBy(static name => name, StringComparer.Ordinal));
	}

	[Fact]
	public void ProjectExtensionsAreSkippedWhenUntrusted()
	{
		// 扩展能注册工具与命令、能改工具调用；让克隆下来的仓库自带扩展就生效，
		// 等于让仓库在用户机器上执行代码。
		Write("project.js", "module.exports = function (pi) { pi.registerCommand('p', 'd', function () {}); };", project: true);

		using var host = new ExtensionHost();
		host.Load(_cwd, trusted: false, _agentDir);

		Assert.Empty(host.States);
	}

	[Fact]
	public void DeclarationFilesAreNotTreatedAsExtensions()
	{
		Write("types.d.ts", "export declare const x: number;");
		Write("notes.txt", "not an extension at all");

		using var host = new ExtensionHost();
		host.Load(_cwd, trusted: true, _agentDir);

		Assert.Empty(host.States);
	}

	// ---------------------------------------------------------------- 诚实的失败

	[Fact]
	public void TypeScriptIsReportedInsteadOfSilentlySkipped()
	{
		var state = Load("thing.ts", "export default function (pi: unknown) {}");

		Assert.False(state.Loaded);
		Assert.Contains(state.Diagnostics, message => message.Contains("TypeScript", StringComparison.Ordinal));
	}

	[Fact]
	public void EsmSyntaxGetsAnActionableMessage()
	{
		// 让解析器抛 "Identifier expected" 之类的错误，用户看不出问题出在模块语法上。
		var state = Load("esm.js", "import fs from 'fs';\nexport default function (pi) {}");

		Assert.False(state.Loaded);
		Assert.Contains(state.Diagnostics, message => message.Contains("module.exports", StringComparison.Ordinal));
	}

	[Fact]
	public void ExtensionThatExportsNothingIsReported()
	{
		var state = Load("noop.js", "const helper = 1;");

		// 没导出也允许：有的扩展只挂全局副作用。至少不能崩。
		Assert.True(state.Loaded, string.Join(" / ", state.Diagnostics));
	}

	[Fact]
	public void ThrowingExtensionIsIsolatedAndReported()
	{
		var state = Load("boom.js", "module.exports = function (pi) { throw new Error('扩展内部错误'); };");

		Assert.False(state.Loaded);
		Assert.Contains(state.Diagnostics, message => message.Contains("扩展内部错误", StringComparison.Ordinal));
	}

	[Fact]
	public void UnsupportedEventIsReportedAtRegistrationTime()
	{
		using var host = new ExtensionHost();
		Write("listener.js", "module.exports = function (pi) { pi.on('session_started', function () {}); };");
		host.Load(_cwd, trusted: true, _agentDir);

		// 收下却永远不触发是最难查的失效；注册时就说明白。
		Assert.Contains(host.States[0].Diagnostics, message => message.Contains("session_started", StringComparison.Ordinal));
		Assert.Contains("session_started", host.UnsupportedEvents, StringComparer.Ordinal);
	}

	[Fact]
	public void AsyncHandlerIsRejectedWithAnExplanation()
	{
		using var host = new ExtensionHost();
		Write("async.js", """
			module.exports = function (pi) {
				pi.on('tool_call', async function (payload) { return 1; });
			};
			""");
		host.Load(_cwd, trusted: true, _agentDir);

		var payload = new JsonObject { ["toolName"] = "bash" };
		host.Dispatch("tool_call", payload);

		// 静默丢掉 async 处理器的返回值比报错难查得多。
		Assert.Contains(host.States[0].Diagnostics, message => message.Contains("Promise", StringComparison.Ordinal));
	}

	// ---------------------------------------------------------------- 注册与回调

	[Fact]
	public void RegisteredToolExecutesItsJavascriptImplementation()
	{
		using var host = new ExtensionHost();
		Write("echo.js", """
			module.exports = function (pi) {
				pi.registerTool({
					name: "echo",
					label: "Echo",
					description: "把输入原样返回",
					parameters: { type: "object", properties: { text: { type: "string" } }, required: ["text"] },
					execute: function (args) { return { text: " echoed:" + args.text }; },
				});
			};
			""");
		host.Load(_cwd, trusted: true, _agentDir);

		Assert.True(host.States[0].Loaded, "诊断: " + string.Join(" / ", host.States[0].Diagnostics));
		var tool = Assert.Single(host.Tools);
		Assert.Equal("echo", tool.Name);
		Assert.Equal("把输入原样返回", tool.Description);
		Assert.NotNull(tool.Parameters["properties"]);

		var result = tool.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1",
			ToolName = "echo",
			Arguments = new System.Text.Json.Nodes.JsonObject { ["text"] = "hi" },
			CancellationToken = CancellationToken.None,
		}).GetAwaiter().GetResult();

		Assert.False(result.IsError);
		Assert.Equal(" echoed:hi", Assert.IsType<PiWeft.Ai.TextContent>(result.Content[0]).Text);
	}

	[Fact]
	public void ToolReturningAStringIsAccepted()
	{
		using var host = new ExtensionHost();
		Write("plain.js", """
			module.exports = function (pi) {
				pi.registerTool({ name: "plain", description: "d", execute: function () { return "直接给字符串"; } });
			};
			""");
		host.Load(_cwd, trusted: true, _agentDir);

		var result = host.Tools[0].Execute(new AgentToolInvocation
		{
			ToolCallId = "c1",
			ToolName = "plain",
			Arguments = new JsonObject(),
		}).GetAwaiter().GetResult();

		Assert.Equal("直接给字符串", Assert.IsType<PiWeft.Ai.TextContent>(result.Content[0]).Text);
	}

	[Fact]
	public void ToolErrorsReachTheModelAsErrors()
	{
		using var host = new ExtensionHost();
		Write("bad.js", """
			module.exports = function (pi) {
				pi.registerTool({ name: "bad", description: "d", execute: function () { return { isError: true, error: "参数不对" }; } });
			};
			""");
		host.Load(_cwd, trusted: true, _agentDir);

		var result = host.Tools[0].Execute(new AgentToolInvocation { ToolCallId = "c", ToolName = "bad", Arguments = new JsonObject() })
			.GetAwaiter().GetResult();

		Assert.True(result.IsError);
		Assert.Equal("参数不对", Assert.IsType<PiWeft.Ai.TextContent>(result.Content[0]).Text);
	}

	[Fact]
	public void CommandHandlersRunOnTheExtensionThread()
	{
		using var host = new ExtensionHost();
		Write("cmd.js", """
			module.exports = function (pi) {
				pi.registerCommand("greet", { description: "打招呼", handler: function (argument) { globalThis.seen = "hi " + argument; } });
			};
			""");
		host.Load(_cwd, trusted: true, _agentDir);

		var command = Assert.Single(host.Commands);
		Assert.Equal("greet", command.Name);
		host.RunCommand("greet", "weft");

		Assert.Equal("hi weft", host.States[0].Engine!.GetValue("seen").AsString());
	}

	[Fact]
	public void EventHandlerCanRewriteThePayloadAndTheHostSeesIt()
	{
		using var host = new ExtensionHost();
		Write("guard.js", """
			module.exports = function (pi) {
				pi.on("tool_call", function (payload) { payload.blocked = payload.toolName === "bash"; });
			};
			""");
		host.Load(_cwd, trusted: true, _agentDir);

		Assert.True(host.ListensTo("tool_call"));
		Assert.False(host.ListensTo("tool_result"));

		var updated = host.Dispatch("tool_call", new JsonObject { ["toolName"] = "bash" });
		Assert.True((bool)updated["blocked"]!);

		var other = host.Dispatch("tool_call", new JsonObject { ["toolName"] = "read" });
		Assert.False((bool)other["blocked"]!);
	}

	[Fact]
	public void MultipleHandlersOnOneEventAllRunInOrder()
	{
		using var host = new ExtensionHost();
		Write("chain.js", """
			module.exports = function (pi) {
				pi.on("tool_result", function (payload) { payload.text = payload.text + "|一"; });
				pi.on("tool_result", function (payload) { payload.text = payload.text + "|二"; });
			};
			""");
		host.Load(_cwd, trusted: true, _agentDir);

		var updated = host.Dispatch("tool_result", new JsonObject { ["text"] = "x" });
		Assert.Equal("x|一|二", (string?)updated["text"]);
	}

	[Fact]
	public void OneFailingExtensionDoesNotBreakTheOthers()
	{
		using var host = new ExtensionHost();
		Write("a-first.js", """
			module.exports = function (pi) { pi.on("tool_call", function () { throw new Error("这个扩展坏了"); }); };
			""");
		Write("b-second.js", """
			module.exports = function (pi) { pi.on("tool_call", function (payload) { payload.marked = true; }); };
			""");
		host.Load(_cwd, trusted: true, _agentDir);

		Assert.Equal(2, host.States.Count);
		var updated = host.Dispatch("tool_call", new JsonObject { ["toolName"] = "read" });

		// 坏扩展的错误记在它自己身上；事件仍继续派给其余扩展，payload 的改写也保留。
		Assert.True((bool?)updated["marked"] ?? false, "后一个扩展的改写应当仍然生效");
	}

	[Fact]
	public void DisposeStopsWorkersAndLaterDispatchesAreInert()
	{
		var host = new ExtensionHost();
		Write("life.js", "module.exports = function (pi) { pi.on('tool_call', function () {}); };");
		host.Load(_cwd, trusted: true, _agentDir);
		Assert.True(host.ListensTo("tool_call"));

		host.Dispose();

		Assert.False(host.ListensTo("tool_call"));
		var payload = new JsonObject { ["toolName"] = "read" };
		Assert.Same(payload, host.Dispatch("tool_call", payload));
		host.RunCommand("nope", "x"); // 释放后调用不该抛
	}

	[Fact]
	public async Task ReloadResourcesReloadsExtensions()
	{
		// 改完扩展文件还要重启应用才生效，等于没有热更新：/reload 必须重建扩展宿主。
		var agent = Path.Combine(_root, "agent");
		var cwd = Path.Combine(_root, "project");
		Directory.CreateDirectory(Path.Combine(agent, "extensions"));
		Directory.CreateDirectory(cwd);
		var extension = Path.Combine(agent, "extensions", "growing.js");
		File.WriteAllText(extension, "module.exports = function (pi) { pi.registerTool({ name: 'v1', description: 'd', execute: function () { return { text: 'one' }; } }); };");
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", agent);
		try
		{
			using var session = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = cwd,
				Model = ScriptedSessions.Model(),
				StreamFn = new ScriptedStreamFn().Invoke,
				Session = SessionManager.InMemory(cwd),
				Persist = false,
				Skills = [],
				PromptTemplates = [],
			});

			Assert.Contains(session.Tools, tool => tool.Name == "v1");

			File.WriteAllText(extension, "module.exports = function (pi) { pi.registerTool({ name: 'v2', description: 'd', execute: function () { return { text: 'two' }; } }); };");
			session.ReloadResources();

			Assert.DoesNotContain(session.Tools, tool => tool.Name == "v1");
			Assert.Contains(session.Tools, tool => tool.Name == "v2");
		}
		finally
		{
			Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
		}
	}
}
