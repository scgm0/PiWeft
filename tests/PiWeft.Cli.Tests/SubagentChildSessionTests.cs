using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Subagents;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.Cli.Tests;

/// 子代理会话的装配：角色工具白名单、模型与委派边界。
public sealed class SubagentChildSessionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-child-" + Guid.NewGuid().ToString("N")[..8]);

	public SubagentChildSessionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession CreateParent(IReadOnlyList<AgentTool>? tools = null)
	{
		var model = TestModels.Model();
		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings(),
			Tools = tools,
		});
	}

	private (CliEnvironment Environment, CliArguments Arguments) Environment(params string[] extra)
	{
		var model = TestModels.Model();
		var registry = new ModelRegistry();
		registry.Add(model);
		var environment = CliEnvironment.ForTests(
			registry,
			new CodingAgentSettings(),
			model,
			BuiltinTools.Tools(_root),
			new ScriptedStreamFn().Invoke);
		var arguments = CliArguments.Parse(["--no-session", .. extra]).Arguments!;
		return (environment, arguments);
	}

	[Fact]
	public void ChildSession_UsesTheRoleToolsAndBoundary()
	{
		using var parent = CreateParent();
		var (environment, arguments) = Environment();
		var definition = new SubagentDefinition
		{
			Name = "scout",
			Description = "recon",
			SystemPrompt = "You investigate a codebase. You do not edit files.",
			Tools = ["read", "grep", "find", "ls"],
			ExcludeTools = ["find"],
			Model = "test/test-model",
		};

		var context = new SubagentStartContext(parent, "父会话", definition, "scout-1", "看下登录流程。", 1);
		using var child = environment.CreateChildSession(
			arguments,
			_root,
			SessionManager.InMemory(_root),
			context,
			SubagentCatalog.Empty,
			subagentHost: null,
			extraTools: null,
			uiHost: null);

		Assert.Equal(["grep", "ls", "read"], child.Tools.Select(static tool => tool.Name).Order(StringComparer.Ordinal));
		Assert.Null(child.Subagents);
		Assert.Equal("test-model", child.Model.Id);
		Assert.Equal("test", child.Model.Provider);
		Assert.Contains("You investigate a codebase", child.CurrentSystemPrompt);
		Assert.Contains("你看不到父会话的历史", child.CurrentSystemPrompt);
	}

	[Fact]
	public void ChildSession_FollowsPlanModeAndTheParentToolSet()
	{
		using var parent = CreateParent();
		parent.SetPlanMode(true);
		var (environment, arguments) = Environment();

		// 角色没写白名单：继承父会话当前可用的工具，计划模式已经把 edit/write 拿掉。
		var inherited = new SubagentDefinition
		{
			Name = "writer",
			Description = "writer",
			Tools = null,
		};
		var context = new SubagentStartContext(parent, "父会话", inherited, "writer-1", "改一下。", 1);
		using var child = environment.CreateChildSession(
			arguments,
			_root,
			SessionManager.InMemory(_root),
			context,
			SubagentCatalog.Empty,
			subagentHost: null,
			extraTools: null,
			uiHost: null);

		var names = child.Tools.Select(static tool => tool.Name).ToArray();
		Assert.DoesNotContain("edit", names);
		Assert.DoesNotContain("write", names);
		Assert.Contains("read", names);
		Assert.Contains("bash", names);
		Assert.True(child.PlanModeEnabled);
		Assert.Contains("<plan_mode>", child.CurrentSystemPrompt);

		// 显式白名单同样过计划模式。
		var explicitWrite = new SubagentDefinition
		{
			Name = "writer",
			Description = "writer",
			Tools = ["read", "edit", "write", "bash"],
		};
		var explicitContext = new SubagentStartContext(parent, "父会话", explicitWrite, "writer-2", "再改一下。", 1);
		using var explicitChild = environment.CreateChildSession(
			arguments,
			_root,
			SessionManager.InMemory(_root),
			explicitContext,
			SubagentCatalog.Empty,
			subagentHost: null,
			extraTools: null,
			uiHost: null);

		Assert.Equal(["bash", "read"], explicitChild.Tools.Select(static tool => tool.Name).Order(StringComparer.Ordinal));
		// bash 留在工具面里，但会话本身也在计划模式：GuardedBeforeToolCall 会拦住写操作。
		Assert.True(explicitChild.PlanModeEnabled);
		Assert.Contains("<plan_mode>", explicitChild.CurrentSystemPrompt);
	}

	[Fact]
	public void ChildSession_DoesNotWidenAnExplicitToolAllowlist()
	{
		using var parent = CreateParent([FileTools.Read(_root)]);
		var (environment, arguments) = Environment("--tools", "read");
		var definition = new SubagentDefinition
		{
			Name = "worker",
			Description = "writer",
			Tools = ["read", "grep", "write", "bash"],
		};

		var context = new SubagentStartContext(parent, "父会话", definition, "worker-1", "改一下。", 1);
		using var child = environment.CreateChildSession(
			arguments,
			_root,
			SessionManager.InMemory(_root),
			context,
			SubagentCatalog.Empty,
			subagentHost: null,
			extraTools: null,
			uiHost: null);

		// 父会话用 --tools read 收窄过：角色白名单不能再加回 grep、write、bash。
		Assert.Equal(["read"], child.Tools.Select(static tool => tool.Name));
		Assert.Contains("工具限制", child.CurrentSystemPrompt);
		Assert.Contains("grep", child.CurrentSystemPrompt);
		Assert.Contains("write", child.CurrentSystemPrompt);
	}

	[Fact]
	public void ChildSession_PutsTheOutputSchemaInTheBoundary()
	{
		using var parent = CreateParent();
		var (environment, arguments) = Environment();
		var definition = new SubagentDefinition
		{
			Name = "reviewer",
			Description = "review",
			Tools = ["read"],
		};
		var context = new SubagentStartContext(parent, "父会话", definition, "reviewer-1", "检查。", 1)
		{
			OutputSchema = new System.Text.Json.Nodes.JsonObject
			{
				["type"] = "object",
				["properties"] = new System.Text.Json.Nodes.JsonObject
				{
					["status"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
				},
			},
		};

		using var child = environment.CreateChildSession(
			arguments,
			_root,
			SessionManager.InMemory(_root),
			context,
			SubagentCatalog.Empty,
			subagentHost: null,
			extraTools: null,
			uiHost: null);

		Assert.Contains("最终回复必须是一个 JSON 值", child.CurrentSystemPrompt);
		Assert.Contains("\"status\"", child.CurrentSystemPrompt);
	}

	[Fact]
	public void HtmlExportMarksReportsAndDoesNotUseThemAsTheTitle()
	{
		using var parent = CreateParent();
		parent.Session.AppendCustomMessage(
			SubagentHub.ResultMessageType,
			MessageContent.FromText("子代理「scout-1」完成了一轮。\n## 结论\n\n没有阻塞项。"));

		var html = SessionHtml.Render(parent);

		Assert.Contains("<article class=\"report\">", html);
		Assert.Contains("<h2>subagent result</h2>", html);
		// 回报开场的会话不拿机器文本当标题。
		Assert.DoesNotContain("<h1>子代理", html);
		Assert.Contains($"<h1>Session {parent.Session.SessionId}</h1>", html);
	}

	[Fact]
	public void ChildSession_DropsDelegationAndLinkedTools()
	{
		using var parent = CreateParent();
		var (environment, arguments) = Environment();
		var definition = new SubagentDefinition
		{
			Name = "writer",
			Description = "writer",
			Tools = ["read", "delegate", "wait_subagents", "start_session", "send_to_session", "codemode"],
		};

		var context = new SubagentStartContext(parent, "父会话", definition, "writer-1", "改一下。", 1);
		using var child = environment.CreateChildSession(
			arguments,
			_root,
			SessionManager.InMemory(_root),
			context,
			SubagentCatalog.Empty,
			subagentHost: null,
			extraTools: null,
			uiHost: null);

		Assert.Equal(["read"], child.Tools.Select(static tool => tool.Name));
	}
}
