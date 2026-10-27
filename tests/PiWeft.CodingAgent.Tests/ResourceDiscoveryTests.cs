using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>pi 包清单（<c>package.json</c> 的 <c>pi</c> 字段）解析与资源路径展开。</summary>
public class PiPackageTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-pkg-" + Guid.NewGuid().ToString("N")[..8]);

	public PiPackageTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Parse_ReadsDeclaredResourceArrays()
	{
		var manifest = PiPackageManifest.Parse("""
			{ "name": "demo", "pi": { "skills": ["./resources/skills"], "prompts": ["./resources/prompts/*.md"], "themes": ["./themes/*.json"] } }
			""");

		Assert.Equal(["./resources/skills"], manifest.Skills);
		Assert.Equal(["./resources/prompts/*.md"], manifest.Prompts);
		Assert.Equal(["./themes/*.json"], manifest.Themes);
		Assert.Empty(manifest.Extensions);
		Assert.False(manifest.IsEmpty);
	}

	[Fact]
	public void Parse_NonStringArrayEntry_DisablesThatField()
	{
		// pi：数组里出现非字符串时整个字段不生效。
		var manifest = PiPackageManifest.Parse("""{ "pi": { "skills": ["./a", 42] } }""");
		Assert.Empty(manifest.Skills);
	}

	[Fact]
	public void Parse_WithoutPiField_IsEmpty()
	{
		Assert.True(PiPackageManifest.Parse("""{ "name": "plain" }""").IsEmpty);
		Assert.True(PiPackageManifest.Parse("not json").IsEmpty);
	}

	[Fact]
	public void ResolveResourcePaths_UsesDeclaredGlobs()
	{
		var prompts = Path.Combine(_root, "resources", "prompts");
		Directory.CreateDirectory(prompts);
		File.WriteAllText(Path.Combine(prompts, "review.md"), "review");
		File.WriteAllText(Path.Combine(prompts, "ship.md"), "ship");
		File.WriteAllText(Path.Combine(prompts, "notes.txt"), "ignored");

		var package = new PiPackage
		{
			Name = "demo",
			Source = "./demo",
			Root = _root,
			Scope = ResourceScope.Project,
			Manifest = PiPackageManifest.Parse("""{ "pi": { "prompts": ["./resources/prompts/*.md"] } }"""),
		};

		var resolved = package.ResolveResourcePaths(ResourcePaths.PromptsDirectoryName);

		Assert.Equal(2, resolved.Count);
		Assert.All(resolved, path => Assert.EndsWith(".md", path, StringComparison.Ordinal));
	}

	[Fact]
	public void ResolveResourcePaths_WithoutManifest_UsesConventionalDirectory()
	{
		var skills = Path.Combine(_root, "skills");
		Directory.CreateDirectory(skills);

		var package = new PiPackage
		{
			Name = "demo",
			Source = "./demo",
			Root = _root,
			Scope = ResourceScope.Project,
		};

		Assert.Equal([skills], package.ResolveResourcePaths(ResourcePaths.SkillsDirectoryName));
		Assert.Empty(package.ResolveResourcePaths(ResourcePaths.ThemesDirectoryName));
	}

	[Theory]
	[InlineData("npm:@scope/pkg@1.0.0", "node_modules/@scope/pkg")]
	[InlineData("npm:plain", "node_modules/plain")]
	public void ResolveRoot_MapsNpmSources(string source, string expectedSuffix)
	{
		var root = PiPackageStore.ResolveRoot(new PackageSource
		{
			Value = source,
			Scope = ResourceScope.User,
			ScopeRoot = _root,
		});

		Assert.NotNull(root);
		Assert.EndsWith(expectedSuffix.Replace('/', Path.DirectorySeparatorChar), root, StringComparison.Ordinal);
	}

	[Fact]
	public void ResolveRoot_MapsLocalSourcesRelativeToScopeRoot()
	{
		var root = PiPackageStore.ResolveRoot(new PackageSource
		{
			Value = "./vendor/demo",
			Scope = ResourceScope.Project,
			ScopeRoot = _root,
		});

		Assert.Equal(Path.GetFullPath(Path.Combine(_root, "vendor", "demo")), root);
	}
}

/// <summary>统一资源解析：settings 显式路径、包资源与优先级。</summary>
public class ResourceResolverTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-res-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;
	private readonly string _project;

	public ResourceResolverTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		_project = Path.Combine(_root, "repo");
		Directory.CreateDirectory(_agentDir);
		Directory.CreateDirectory(Path.Combine(_project, ".git"));
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
	public void SkillRoots_IncludeSettingsDeclaredPathsWithCorrectBases()
	{
		// 用户设置里的相对路径从 agent 目录解析。
		WriteSettings(CodingAgentSettings.GlobalPath(_agentDir), """{ "skills": ["shared-skills"] }""");
		// 项目设置里的相对路径从项目 .pi 解析。
		WriteSettings(CodingAgentSettings.ProjectPath(_project), """{ "skills": ["project-skills"] }""");

		var roots = ResourceResolver.SkillRoots(_project, _agentDir, includeProject: true);
		var paths = roots.Select(root => root.Path).ToList();

		Assert.Contains(Path.Combine(_agentDir, "shared-skills"), paths);
		Assert.Contains(Path.Combine(_project, ".pi", "project-skills"), paths);

		// 项目声明的根排在用户声明之前（rank 0 vs 2）。
		var projectIndex = paths.IndexOf(Path.Combine(_project, ".pi", "project-skills"));
		var userIndex = paths.IndexOf(Path.Combine(_agentDir, "shared-skills"));
		Assert.True(projectIndex < userIndex);
	}

	[Fact]
	public void Resolve_ExcludesProjectResourcesWhenUntrusted()
	{
		var projectSkills = Path.Combine(_project, ".pi", "skills", "local");
		Directory.CreateDirectory(projectSkills);
		File.WriteAllText(Path.Combine(projectSkills, "SKILL.md"), """
			---
			name: local
			description: project-only skill
			---
			body
			""");

		var userSkills = Path.Combine(_agentDir, "skills", "shared");
		Directory.CreateDirectory(userSkills);
		File.WriteAllText(Path.Combine(userSkills, "SKILL.md"), """
			---
			name: shared
			description: user skill
			---
			body
			""");

		var trusted = ResourceResolver.Resolve(_project, _agentDir, includeProject: true);
		Assert.Contains(trusted.Skills, skill => skill.Name == "local");

		var untrusted = ResourceResolver.Resolve(_project, _agentDir, includeProject: false);
		Assert.DoesNotContain(untrusted.Skills, skill => skill.Name == "local");
		Assert.Contains(untrusted.Skills, skill => skill.Name == "shared");
	}

	[Fact]
	public void Resolve_IncludesPackageResources()
	{
		var packageRoot = Path.Combine(_root, "vendor", "demo");
		var skills = Path.Combine(packageRoot, "skills", "packaged");
		Directory.CreateDirectory(skills);
		File.WriteAllText(Path.Combine(packageRoot, "package.json"), """{ "name": "demo" }""");
		File.WriteAllText(Path.Combine(skills, "SKILL.md"), """
			---
			name: packaged
			description: from a pi package
			---
			body
			""");

		// 项目设置里的相对路径从项目 .pi 解析，因此要向上两级才到 _root。
		WriteSettings(CodingAgentSettings.ProjectPath(_project), """{ "packages": ["../../vendor/demo"] }""");

		var snapshot = ResourceResolver.Resolve(_project, _agentDir, includeProject: true);

		var skill = Assert.Single(snapshot.Skills, candidate => candidate.Name == "packaged");
		Assert.Equal(ResourceScope.Package, skill.Origin.Scope);
		Assert.Equal("demo", skill.Origin.Package);
		Assert.Single(snapshot.Packages);
	}

	[Fact]
	public void Resolve_ProjectSkillOverridesUserSkillWithSameName()
	{
		WriteSkill(Path.Combine(_project, ".pi", "skills", "dup"), "dup", "project wins");
		WriteSkill(Path.Combine(_agentDir, "skills", "dup"), "dup", "user loses");

		var snapshot = ResourceResolver.Resolve(_project, _agentDir, includeProject: true);
		var skill = Assert.Single(snapshot.Skills, candidate => candidate.Name == "dup");

		Assert.Equal("project wins", skill.Description);
		Assert.Equal(ResourceScope.Project, skill.Origin.Scope);
	}

	private static void WriteSkill(string directory, string name, string description)
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"""
			---
			name: {name}
			description: {description}
			---
			body
			""");
	}

	private static void WriteSettings(string path, string json)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, JsonNode.Parse(json)!.ToJsonString());
	}
}

/// <summary>
/// 资源快照里的扩展条目：只发现、不执行，且必须带上「为什么没生效」。
/// </summary>
public sealed class ExtensionDiscoveryTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-ext-res-" + Guid.NewGuid().ToString("N")[..8]);

	private string AgentDir => Path.Combine(_root, "agent");

	private string Project => Path.Combine(_root, "project");

	public ExtensionDiscoveryTests()
	{
		Directory.CreateDirectory(Path.Combine(AgentDir, "extensions"));
		Directory.CreateDirectory(Path.Combine(Project, ".pi", "extensions"));
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

	[Fact]
	public void ExtensionsAreListedWithTheirReasonAndRespectTrust()
	{
		File.WriteAllText(Path.Combine(AgentDir, "extensions", "good.js"), "module.exports = function (pi) {};");
		File.WriteAllText(Path.Combine(AgentDir, "extensions", "typed.ts"), "export default function (pi: unknown) {}");
		File.WriteAllText(Path.Combine(Project, ".pi", "extensions", "repo.js"), "module.exports = function (pi) {};");

		var trusted = ResourceResolver.Resolve(Project, AgentDir, includeProject: true);
		Assert.Equal(3, trusted.Extensions.Count);

		var js = trusted.Extensions.Single(entry => entry.Name == "good");
		Assert.Equal("全局", js.Origin);
		Assert.Equal(string.Empty, js.Note);

		// TypeScript 要写明需要转译：只列名字不写原因，用户会以为它在跑。
		var ts = trusted.Extensions.Single(entry => entry.Name == "typed");
		Assert.Contains("TypeScript", ts.Note, StringComparison.Ordinal);

		// 不受信时项目扩展整个消失：仓库不该自带在用户机器上执行的代码。
		var untrusted = ResourceResolver.Resolve(Project, AgentDir, includeProject: false);
		Assert.DoesNotContain(untrusted.Extensions, entry => entry.Name == "repo");
		Assert.Contains(untrusted.Extensions, entry => entry.Name == "good");
	}
}
