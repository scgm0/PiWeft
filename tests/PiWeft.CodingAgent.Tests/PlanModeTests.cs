using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public class PlanModeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-plan-" + Guid.NewGuid().ToString("N")[..8]);

	public PlanModeTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession Session() =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
		});

	private static SystemMessage Prompt(CodingAgentSession session) =>
		Assert.IsType<SystemMessage>(session.ProjectedMessages[0]);

	[Fact]
	public void InitialPlanMode_StartsReadOnly()
	{
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Tools = PiWeft.CodingAgent.Tools.BuiltinTools.Tools(_root),
			InitialPlanMode = true,
		});

		Assert.True(session.PlanModeEnabled);
		Assert.DoesNotContain(session.Tools, static tool => tool.Name is "edit" or "write" or "powershell");
		Assert.Contains(session.Tools, static tool => tool.Name == "read");
		Assert.Contains("plan_mode", Prompt(session).Sections!.Keys);
	}

	[Fact]
	public void SetPlanMode_RemovesWriteToolsAndAddsThePromptSection()
	{
		using var session = Session();
		Assert.Contains(session.Tools, static tool => tool.Name == "write");
		Assert.False(Prompt(session).Sections!.ContainsKey("plan_mode"));

		session.SetPlanMode(true);

		Assert.True(session.PlanModeEnabled);
		Assert.DoesNotContain(session.Tools, static tool => tool.Name is "edit" or "write" or "powershell");
		Assert.Contains(session.Tools, static tool => tool.Name == "read");
		Assert.Contains("plan_mode", Prompt(session).Sections!.Keys);
		Assert.DoesNotContain(Prompt(session).ToolsAdded!, static tool => tool.Name is "edit" or "write" or "powershell");

		session.SetPlanMode(false);

		Assert.False(session.PlanModeEnabled);
		Assert.Contains(session.Tools, static tool => tool.Name == "write");
		Assert.False(Prompt(session).Sections!.ContainsKey("plan_mode"));
	}

	[Fact]
	public void Marker_EnablesPlanModeAndNextUnmarkedMessageExits()
	{
		using var session = Session();

		var message = session.PrepareInput($"{PlanMode.Marker}\n请先做只读分析");

		Assert.True(session.PlanModeEnabled);
		Assert.Equal("请先做只读分析", message.Content.Text);
		Assert.DoesNotContain(session.Tools, static tool => tool.Name is "write" or "powershell");

		// PiDeck 的 Plan 开关流：下一条无标记消息退出计划模式。
		session.PrepareInput("开始执行");
		Assert.False(session.PlanModeEnabled);
		Assert.Contains(session.Tools, static tool => tool.Name == "write");
	}

	[Fact]
	public void SetPlanMode_AlsoDisablesPowerShell()
	{
		// 默认集不含 powershell：显式给出完整目录才走得到这条路径。
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Tools = PiWeft.CodingAgent.Tools.BuiltinTools.Tools(_root),
		});
		Assert.Contains(session.Tools, static tool => tool.Name == "powershell");

		session.SetPlanMode(true);

		Assert.DoesNotContain(session.Tools, static tool => tool.Name == "powershell");
		Assert.DoesNotContain(session.Tools, static tool => tool.Name is "edit" or "write");

		session.SetPlanMode(false);

		Assert.Contains(session.Tools, static tool => tool.Name == "powershell");
	}

	[Fact]
	public void ManualPlanMode_IsNotExitedByAnOrdinaryMessage()
	{
		using var session = Session();
		session.SetPlanMode(true);

		session.PrepareInput("继续分析");

		Assert.True(session.PlanModeEnabled);
	}

	[Fact]
	public void Instructions_DemandThePlanHeadingAndNumberedSteps()
	{
		Assert.Contains("Plan:", PlanMode.Instructions);
		Assert.Contains("numbered steps", PlanMode.Instructions);
	}

	[Theory]
	[InlineData("ls -la", true)]
	[InlineData("git status", true)]
	[InlineData("rg TODO src", true)]
	[InlineData("cat README.md", true)]
	[InlineData("curl https://example.com", true)]
	[InlineData("rm -rf build", false)]
	[InlineData("echo hi > file.txt", false)]
	[InlineData("git commit -m x", false)]
	[InlineData("npm install", false)]
	[InlineData("vim file", false)]
	[InlineData("some-unknown-tool --flag", false)]
	[InlineData("find . -delete", false)]
	[InlineData("find . -name '*.tmp' -exec rm {} \\;", false)]
	[InlineData("sort -o out.txt in.txt", false)]
	[InlineData("curl -o page.html https://example.com", false)]
	[InlineData("env FOO=1 ls", false)]
	[InlineData("env", true)]
	[InlineData("less README.md", false)]
	[InlineData("git branch foo", false)]
	[InlineData("git branch -a", true)]
	[InlineData("git log --output=out.txt", false)]
	[InlineData("git remote add origin https://example.com/repo.git", false)]
	[InlineData("git remote -v", true)]
	[InlineData("git config user.name x", false)]
	[InlineData("git config --get user.name", true)]
	public void ReadOnlyShellClassification_MatchesPiDeck(string command, bool safe) =>
		Assert.Equal(safe, PlanMode.IsReadOnlyShellCommand(command));
}
