using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public sealed class ParityRegressionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-parity-" + Guid.NewGuid().ToString("N")[..8]);

	public ParityRegressionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string AgentDir => Path.Combine(_root, "agent");
	private string Project => Path.Combine(_root, "project");

	private void WriteGlobal(string json)
	{
		Directory.CreateDirectory(AgentDir);
		File.WriteAllText(Path.Combine(AgentDir, "settings.json"), json);
	}

	[Fact]
	public void ObjectPackageEntriesDoNotPoisonTheRestOfSettings()
	{
		WriteGlobal(
			"""
			{
			  "defaultModel": "keep-me",
			  "packages": [
			    { "source": "npm:@scope/tools", "autoload": false, "skills": ["skills/a"] }
			  ]
			}
			""");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);

		Assert.Equal("keep-me", settings.DefaultModel);
		var package = Assert.Single(settings.Packages);
		Assert.Equal("npm:@scope/tools", package.Source);
		Assert.False(package.Autoload);
		Assert.Equal(["skills/a"], package.Skills);
	}

	[Fact]
	public void LegacySkillsObjectIsReadAsCustomDirectories()
	{
		WriteGlobal("""{"defaultModel":"m","skills":{"enableSkillCommands":false,"customDirectories":["/tmp/skills"]}}""");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);

		Assert.Equal("m", settings.DefaultModel);
		Assert.Equal(["/tmp/skills"], settings.Skills);
	}

	[Fact]
	public void CacheWarmingAndQueueModesAreReadFromSettings()
	{
		WriteGlobal("""{"cacheWarming":"idle","steeringMode":"all","followUpMode":"all"}""");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);

		Assert.Equal("idle", settings.CacheWarming);
		Assert.Equal("all", settings.SteeringMode);
		Assert.Equal("all", settings.FollowUpMode);
	}

	[Fact]
	public void ProjectDefaultToolsAdjustmentsStackOnUserSelection()
	{
		var global = new SettingsFile { DefaultTools = ["read", "bash"] };
		var project = new SettingsFile { DefaultTools = ["+grep", "-bash"] };

		var settings = CodingAgentSettings.Merge(global, project);

		Assert.True(settings.DefaultToolsConfigured);
		Assert.Equal(["read", "bash", "+grep", "-bash"], settings.DefaultTools);
	}

	[Fact]
	public void EmptyDefaultToolsIsAnExplicitDisable()
	{
		var global = new SettingsFile { DefaultTools = ["read"] };
		var project = new SettingsFile { DefaultTools = [] };

		var settings = CodingAgentSettings.Merge(global, project);

		Assert.True(settings.DefaultToolsConfigured);
		Assert.Empty(settings.DefaultTools);
	}

	[Fact]
	public void SystemAndAppendFilesLoadFromAgentAndTrustedProject()
	{
		Directory.CreateDirectory(AgentDir);
		File.WriteAllText(Path.Combine(AgentDir, "SYSTEM.md"), "agent system");
		File.WriteAllText(Path.Combine(AgentDir, "APPEND_SYSTEM.md"), "agent append");
		Directory.CreateDirectory(Path.Combine(Project, ".pi"));
		File.WriteAllText(Path.Combine(Project, ".pi", "SYSTEM.md"), "project system");
		File.WriteAllText(Path.Combine(Project, ".pi", "APPEND_SYSTEM.md"), "project append");

		Assert.Equal("project system", SystemPromptFiles.LoadSystem(Project, projectTrusted: true, AgentDir));
		Assert.Equal("agent system", SystemPromptFiles.LoadSystem(Project, projectTrusted: false, AgentDir));
		Assert.Equal("project append", SystemPromptFiles.LoadAppend(Project, projectTrusted: true, AgentDir));
	}

	[Fact]
	public void AgentsOverrideTakesPrecedenceOverAgentsMd()
	{
		var project = Path.Combine(_root, "ctx");
		Directory.CreateDirectory(project);
		File.WriteAllText(Path.Combine(project, "AGENTS.md"), "plain");
		File.WriteAllText(Path.Combine(project, "AGENTS.override.md"), "override");
		Directory.CreateDirectory(AgentDir);

		var rendered = ContextFiles.Load(project, AgentDir);

		Assert.Contains("override", rendered);
		Assert.DoesNotContain("plain", rendered);
	}
}
