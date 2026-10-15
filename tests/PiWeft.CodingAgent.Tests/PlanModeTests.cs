using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>D-01：计划模式的工具限制、提示词章节与 PiDeck 标记。</summary>
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
	public void SetPlanMode_RemovesWriteToolsAndAddsThePromptSection()
	{
		using var session = Session();
		Assert.Contains(session.Tools, static tool => tool.Name == "write");
		Assert.False(Prompt(session).Sections!.ContainsKey("plan_mode"));

		session.SetPlanMode(true);

		Assert.True(session.PlanModeEnabled);
		Assert.DoesNotContain(session.Tools, static tool => tool.Name is "edit" or "write");
		Assert.Contains(session.Tools, static tool => tool.Name == "read");
		Assert.Contains("plan_mode", Prompt(session).Sections!.Keys);
		Assert.DoesNotContain(Prompt(session).ToolsAdded!, static tool => tool.Name is "edit" or "write");

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
		Assert.DoesNotContain(session.Tools, static tool => tool.Name == "write");

		// PiDeck 的 Plan 开关流：下一条无标记消息退出计划模式。
		session.PrepareInput("开始执行");
		Assert.False(session.PlanModeEnabled);
		Assert.Contains(session.Tools, static tool => tool.Name == "write");
	}

	[Fact]
	public void ManualPlanMode_IsNotExitedByAnOrdinaryMessage()
	{
		using var session = Session();
		session.SetPlanMode(true);

		session.PrepareInput("继续分析");

		Assert.True(session.PlanModeEnabled);
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
	public void ReadOnlyShellClassification_MatchesPiDeck(string command, bool safe) =>
		Assert.Equal(safe, PlanMode.IsReadOnlyShellCommand(command));
}
