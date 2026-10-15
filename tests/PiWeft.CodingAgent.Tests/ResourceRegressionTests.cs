using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Themes;
using Xunit;
using SkillCatalog = PiWeft.CodingAgent.Skills.Skills;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 资源发现的回归用例：作用域优先级、坏输入不致命、settings 声明的文件根。
/// 这些场景此前都会给出错误结果（用户级资源遮蔽项目级、坏主题让整个会话构造失败）。
/// </summary>
public class ResourceRegressionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-resreg-" + Guid.NewGuid().ToString("N")[..8]);

	private readonly string _agentDir;

	private readonly string _project;

	public ResourceRegressionTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		_project = Path.Combine(_root, "repo");
		Directory.CreateDirectory(Path.Combine(_agentDir, "skills"));
		Directory.CreateDirectory(Path.Combine(_agentDir, "themes"));
		Directory.CreateDirectory(Path.Combine(_project, ".pi", "skills"));

		// 仓库根标记：向上查找项目资源时到此为止，避免扫到测试目录之外。
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
	public void ProjectAutoDiscoveryBeatsUserDeclaredSkillWithTheSameName()
	{
		WriteSkill(Path.Combine(_project, ".pi", "skills", "shared"), "shared", "project");
		var vendor = Path.Combine(_root, "vendor", "skills");
		WriteSkill(Path.Combine(vendor, "shared"), "shared", "user-declared");

		// 用户设置在 agent 目录下声明技能路径；它必须排在项目自动发现之后。
		WriteSettings(
			CodingAgentSettings.GlobalPath(_agentDir),
			"""{ "skills": ["../vendor/skills"] }""");

		var skills = SkillCatalog.Discover(_project, _agentDir, includeProject: true);
		var shared = Assert.Single(skills, skill => skill.Name == "shared");

		Assert.Equal("project", shared.Description);
		Assert.Equal(ResourceScope.Project, shared.Origin.Scope);
	}

	[Fact]
	public void SkillDeclaredAsFileInSettingsIsDiscovered()
	{
		var skillFile = Path.Combine(_root, "single.md");
		File.WriteAllText(skillFile, "---\nname: single\ndescription: from file\n---\nbody\n");
		WriteSettings(
			CodingAgentSettings.GlobalPath(_agentDir),
			"""{ "skills": ["../single.md"] }""");

		var skills = SkillCatalog.Discover(_project, _agentDir, includeProject: false);

		var single = Assert.Single(skills, skill => skill.Name == "single");
		Assert.Equal("from file", single.Description);
	}

	[Fact]
	public void ThemeWithWrongTypesIsReportedInsteadOfThrowing()
	{
		// name 不是字符串：GetValue<string> 会抛 InvalidOperationException，必须被转成一条错误。
		File.WriteAllText(
			Path.Combine(_agentDir, "themes", "bad-name.json"),
			"""{ "name": 123, "colors": { "text": "#ffffff" } }""");

		// 颜色是小数：GetValue<int> 会抛，必须按「值不合法」处理而不是让发现流程失败。
		File.WriteAllText(
			Path.Combine(_agentDir, "themes", "fractional.json"),
			"""{ "name": "fractional", "colors": { "text": "#ffffff", "accent": 1.5 } }""");

		var themes = ThemeCatalog.Discover(_project, _agentDir, includeProject: true);

		var broken = Assert.Single(themes, entry => entry.Name == "bad-name");
		Assert.NotNull(broken.Error);
		Assert.Null(broken.Theme);

		// 小数颜色只丢弃该角色，主题本身仍然可用。
		var fractional = Assert.Single(themes, entry => entry.Name == "fractional");
		Assert.NotNull(fractional.Theme);
		Assert.True(fractional.Theme!.Colors.ContainsKey("text"));
		Assert.False(fractional.Theme.Colors.ContainsKey("accent"));
	}

	[Fact]
	public void ThemeProvidedByPackageIsVisibleToTheDiscoveryOverload()
	{
		var packageRoot = Path.Combine(_root, "pkg");
		Directory.CreateDirectory(Path.Combine(packageRoot, "themes"));
		File.WriteAllText(Path.Combine(packageRoot, "package.json"), """{ "name": "demo-pkg" }""");
		File.WriteAllText(
			Path.Combine(packageRoot, "themes", "forest.json"),
			"""{ "name": "forest", "appearance": "light", "colors": { "text": "#123456" } }""");
		WriteSettings(
			CodingAgentSettings.GlobalPath(_agentDir),
			new JsonObject { ["packages"] = new JsonArray(JsonValue.Create(packageRoot)) }.ToJsonString());

		var themes = ThemeCatalog.Discover(_project, _agentDir, includeProject: true);

		// 便捷重载（桌面主题选择器走的就是它）也必须看得到包提供的主题。
		Assert.Contains(themes, entry => entry.Name == "forest" && entry.Theme is not null);
	}

	private static void WriteSkill(string directory, string name, string description)
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(
			Path.Combine(directory, "SKILL.md"),
			$"---\nname: {name}\ndescription: {description}\n---\nbody\n");
	}

	private static void WriteSettings(string path, string json)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, json);
	}
}
