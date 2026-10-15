using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>PI-11：trust.json 存储、受保护资源探测与决策解析。</summary>
public class ProjectTrustTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-trust-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public ProjectTrustTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_agentDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void SetAndGet_RoundTripsAndLooksUpParents()
	{
		var project = Path.Combine(_root, "repo");
		var nested = Path.Combine(project, "src", "deep");
		Directory.CreateDirectory(nested);

		ProjectTrust.Set(_agentDir, project, false);

		Assert.False(ProjectTrust.Get(_agentDir, project));
		// 向上查找命中父目录决策。
		Assert.False(ProjectTrust.Get(_agentDir, nested));

		ProjectTrust.Set(_agentDir, project, true);
		Assert.True(ProjectTrust.Get(_agentDir, nested));

		ProjectTrust.Set(_agentDir, project, null);
		Assert.Null(ProjectTrust.Get(_agentDir, nested));
	}

	[Fact]
	public void Get_IgnoresMalformedEntries()
	{
		var valid = Path.GetFullPath(Path.Combine(_root, "valid"));
		var bad = Path.GetFullPath(Path.Combine(_root, "bad"));
		File.WriteAllText(
			ProjectTrust.StorePath(_agentDir),
			System.Text.Json.Nodes.JsonNode.Parse($$"""{"{{valid}}":true,"{{bad}}":"yes"}""")!.ToJsonString());

		// 坏值被跳过，合法值仍可用。
		Assert.True(ProjectTrust.Get(_agentDir, valid));
		Assert.Null(ProjectTrust.Get(_agentDir, bad));
	}

	[Fact]
	public void HasTrustRequiringProjectResources_DetectsPiAndAgentsResources()
	{
		var plain = Path.Combine(_root, "plain");
		Directory.CreateDirectory(plain);
		Assert.False(ProjectTrust.HasTrustRequiringProjectResources(plain));

		// AGENTS.md 不是受信任保护的资源（与 pi 一致）。
		File.WriteAllText(Path.Combine(plain, "AGENTS.md"), "x");
		Assert.False(ProjectTrust.HasTrustRequiringProjectResources(plain));

		Directory.CreateDirectory(Path.Combine(plain, ".pi", "skills"));
		Assert.True(ProjectTrust.HasTrustRequiringProjectResources(plain));

		var agents = Path.Combine(_root, "agents-repo");
		Directory.CreateDirectory(Path.Combine(agents, ".agents", "skills"));
		Assert.True(ProjectTrust.HasTrustRequiringProjectResources(agents));
	}

	[Fact]
	public void Resolve_PrefersOverrideThenStoreThenDefault()
	{
		var project = Path.Combine(_root, "repo");
		Directory.CreateDirectory(Path.Combine(project, ".pi", "skills"));

		// 显式开关最优先。
		Assert.True(ProjectTrust.Resolve(project, true, "never", _agentDir));
		Assert.False(ProjectTrust.Resolve(project, false, "always", _agentDir));

		// 已存决策次之。
		ProjectTrust.Set(_agentDir, project, false);
		Assert.False(ProjectTrust.Resolve(project, null, "always", _agentDir));

		// 默认值兜底。
		ProjectTrust.Set(_agentDir, project, null);
		Assert.False(ProjectTrust.Resolve(project, null, "never", _agentDir));
		Assert.False(ProjectTrust.Resolve(project, null, "ask", _agentDir));
		Assert.True(ProjectTrust.Resolve(project, null, "always", _agentDir));

		// 没有受保护资源时无需信任。
		var plain = Path.Combine(_root, "plain2");
		Directory.CreateDirectory(plain);
		Assert.True(ProjectTrust.Resolve(plain, null, "never", _agentDir));
	}

	[Fact]
	public void Discovery_WithoutProjectTrust_SkipsProjectResources()
	{
		var project = Path.Combine(_root, "repo");
		Directory.CreateDirectory(Path.Combine(project, ".pi", "skills", "project-skill"));
		File.WriteAllText(
			Path.Combine(project, ".pi", "skills", "project-skill", "SKILL.md"),
			"---\nname: project-skill\ndescription: project scoped\n---\n\nbody\n");
		Directory.CreateDirectory(Path.Combine(project, ".pi", "prompts"));
		File.WriteAllText(Path.Combine(project, ".pi", "prompts", "review.md"), "project review");

		Directory.CreateDirectory(Path.Combine(_agentDir, "skills", "user-skill"));
		File.WriteAllText(
			Path.Combine(_agentDir, "skills", "user-skill", "SKILL.md"),
			"---\nname: user-skill\ndescription: user scoped\n---\n\nbody\n");
		Directory.CreateDirectory(Path.Combine(_agentDir, "prompts"));
		File.WriteAllText(Path.Combine(_agentDir, "prompts", "user.md"), "user prompt");

		var trustedSkills = CodingAgent.Skills.Skills.Discover(project, _agentDir);
		Assert.Contains(trustedSkills, static skill => skill.Name == "project-skill");

		var untrustedSkills = CodingAgent.Skills.Skills.Discover(project, _agentDir, includeProject: false);
		Assert.DoesNotContain(untrustedSkills, static skill => skill.Name == "project-skill");
		Assert.Contains(untrustedSkills, static skill => skill.Name == "user-skill");

		var trustedPrompts = CodingAgent.Prompts.PromptTemplates.Discover(project, _agentDir);
		Assert.Contains(trustedPrompts, static template => template.Name == "review");

		var untrustedPrompts = CodingAgent.Prompts.PromptTemplates.Discover(project, _agentDir, includeProject: false);
		Assert.DoesNotContain(untrustedPrompts, static template => template.Name == "review");
		Assert.Contains(untrustedPrompts, static template => template.Name == "user");
	}

	[Fact]
	public void GlobalDefaultProjectTrust_IgnoresProjectSettings()
	{
		File.WriteAllText(
			CodingAgentSettings.GlobalPath(_agentDir),
			"""{"defaultProjectTrust":"never"}""");
		Directory.CreateDirectory(Path.GetDirectoryName(CodingAgentSettings.ProjectPath(_root))!);
		File.WriteAllText(
			CodingAgentSettings.ProjectPath(_root),
			"""{"defaultProjectTrust":"always"}""");

		// 项目设置不能自我授权。
		Assert.Equal("never", CodingAgentSettings.GlobalDefaultProjectTrust(_agentDir));

		var merged = CodingAgentSettings.Load(_root, _agentDir);
		Assert.Equal("always", merged.DefaultProjectTrust);

		// 不受信时不合并项目设置。
		var globalOnly = CodingAgentSettings.Load(_root, _agentDir, includeProject: false);
		Assert.Equal("never", globalOnly.DefaultProjectTrust);
	}

	private sealed class FakeUiHost(string? response) : IUiHost
	{
		public bool HasUi { get; set; } = true;

		public UiInputRequest? LastRequest { get; private set; }

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(response);
		}
	}

	private string Project()
	{
		var project = Path.Combine(_root, "repo");
		Directory.CreateDirectory(Path.Combine(project, ".pi", "skills"));
		File.WriteAllText(Path.Combine(project, ".pi", "skills", "demo.md"), "---\nname: demo\n---\n");
		File.WriteAllText(CodingAgentSettings.GlobalPath(_agentDir), """{"defaultProjectTrust":"ask"}""");
		return project;
	}

	private static string Answer(string value) =>
		AskQuestions.EncodeResponse([new AskAnswer("trust", "select", JsonValue.Create(value), value, false)], cancelled: false);

	[Fact]
	public void HasPendingDecision_OnlyForProtectedProjectsWithAskDefault()
	{
		var project = Project();

		// ask + 有受保护项目资源 + 无已存决策 → 待决。
		Assert.True(ProjectTrust.HasPendingDecision(project, _agentDir));
		// 显式开关不进入询问路径。
		Assert.False(ProjectTrust.HasPendingDecision(project, _agentDir, trustOverride: true));
		Assert.False(ProjectTrust.HasPendingDecision(project, _agentDir, trustOverride: false));

		// 已记住后不再待决。
		ProjectTrust.Set(_agentDir, project, true);
		Assert.False(ProjectTrust.HasPendingDecision(project, _agentDir));
	}

	[Fact]
	public void HasPendingDecision_FalseWithoutProjectResources()
	{
		var plain = Path.Combine(_root, "plain");
		Directory.CreateDirectory(plain);
		File.WriteAllText(CodingAgentSettings.GlobalPath(_agentDir), """{"defaultProjectTrust":"ask"}""");

		Assert.False(ProjectTrust.HasPendingDecision(plain, _agentDir));
	}

	[Fact]
	public async Task PromptAsync_PersistsExplicitChoices()
	{
		var project = Project();

		// 「信任（记住）」写入 true。
		Assert.True(await ProjectTrust.PromptAsync(project, new FakeUiHost(Answer("trust")), _agentDir));
		Assert.True(ProjectTrust.Get(_agentDir, project));

		// 「不信任（记住）」写入 false。
		Assert.False(await ProjectTrust.PromptAsync(project, new FakeUiHost(Answer("deny")), _agentDir));
		Assert.False(ProjectTrust.Get(_agentDir, project));

		// 「仅本次会话」返回 true 但不落盘。
		ProjectTrust.Set(_agentDir, project, null);
		Assert.True(await ProjectTrust.PromptAsync(project, new FakeUiHost(Answer("session")), _agentDir));
		Assert.Null(ProjectTrust.Get(_agentDir, project));
	}

	[Fact]
	public async Task PromptAsync_CancelAndNoUiFailClosed()
	{
		var project = Project();

		// 取消（空响应）按不信任处理，且不写决策。
		Assert.False(await ProjectTrust.PromptAsync(project, new FakeUiHost(null), _agentDir));
		Assert.Null(ProjectTrust.Get(_agentDir, project));

		// 无 UI 同样失败关闭。
		Assert.False(await ProjectTrust.PromptAsync(project, new FakeUiHost(Answer("trust")) { HasUi = false }, _agentDir));
		Assert.Null(ProjectTrust.Get(_agentDir, project));
	}
}
