using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 旧版 settings 键的加载期迁移（pi 的 <c>SettingsManager.migrateSettings</c> 同义）。
/// 迁移只在内存里做：下一次保存自然写成新形状，不主动重写用户的配置文件。
/// </summary>
public class SettingsMigrationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-settings-" + Guid.NewGuid().ToString("N")[..8]);

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
	public void QueueModeMigratesToSteeringMode()
	{
		WriteGlobal("""{"queueMode":"all"}""");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);

		Assert.Equal("all", settings.SteeringMode);
	}

	[Fact]
	public void ExplicitSteeringModeWinsOverLegacyQueueMode()
	{
		WriteGlobal("""{"queueMode":"all","steeringMode":"one-at-a-time"}""");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);

		Assert.Equal("one-at-a-time", settings.SteeringMode);
	}

	[Fact]
	public void LegacyWebsocketsBooleanMigratesToTransport()
	{
		WriteGlobal("""{"websockets":true}""");
		Assert.Equal("websocket", CodingAgentSettings.Load(Project, AgentDir, includeProject: false).Transport);

		WriteGlobal("""{"websockets":false}""");
		Assert.Equal("sse", CodingAgentSettings.Load(Project, AgentDir, includeProject: false).Transport);
	}

	[Fact]
	public void ExplicitTransportWinsOverLegacyWebsockets()
	{
		WriteGlobal("""{"websockets":true,"transport":"sse"}""");

		Assert.Equal("sse", CodingAgentSettings.Load(Project, AgentDir, includeProject: false).Transport);
	}

	[Fact]
	public void SavingWritesOnlyValuesTheUserActuallyChanged()
	{
		WriteGlobal("{}");
		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);
		var projectDir = Path.Combine(Project, ".pi");
		Directory.CreateDirectory(projectDir);

		CodingAgentSettings.SaveProject(Project, settings);
		var written = File.ReadAllText(Path.Combine(projectDir, "settings.json"));

		Assert.DoesNotContain("transport", written, StringComparison.Ordinal);
		Assert.DoesNotContain("httpIdleTimeoutMs", written, StringComparison.Ordinal);
		Assert.DoesNotContain("extensions", written, StringComparison.Ordinal);
	}

	[Fact]
	public void SavingKeepsExplicitTransportAndIdleTimeout()
	{
		var projectDir = Path.Combine(Project, ".pi");
		Directory.CreateDirectory(projectDir);
		CodingAgentSettings.SaveProject(Project, new CodingAgentSettings
		{
			Transport = "websocket-cached",
			HttpIdleTimeoutMs = 45_000,
			Extensions = ["/home/u/.pi/agent/extensions/x.js"],
		});

		var file = CodingAgentSettings.ReadFile(Path.Combine(projectDir, "settings.json"))!;
		Assert.Equal("websocket-cached", file.Transport);
		Assert.Equal(45_000, file.HttpIdleTimeoutMs);

		// 解析后的绝对用户路径不会被写进仓库里的项目设置。
		Assert.Null(file.Extensions);
		Assert.DoesNotContain("/home/u", File.ReadAllText(Path.Combine(projectDir, "settings.json")), StringComparison.Ordinal);
	}

	[Fact]
	public void DisabledIdleTimeoutRoundTripsAsString()
	{
		var projectDir = Path.Combine(Project, ".pi");
		Directory.CreateDirectory(projectDir);
		CodingAgentSettings.SaveProject(Project, new CodingAgentSettings { HttpIdleTimeoutMs = 0 });

		var written = File.ReadAllText(Path.Combine(projectDir, "settings.json"));
		Assert.Contains("\"httpIdleTimeoutMs\": \"disabled\"", written, StringComparison.Ordinal);
		// 保存写回的是 pi 的 "disabled" 写法；读取要还原成 0（关闭），不能回落成默认 300 秒。
		Assert.Equal(0, CodingAgentSettings.Load(Project, AgentDir).HttpIdleTimeoutMs);
	}

	[Fact]
	public void InvalidTransportFallsBackToAuto()
	{
		WriteGlobal("""{"transport":"carrier-pigeon"}""");

		Assert.Equal("auto", CodingAgentSettings.Load(Project, AgentDir, includeProject: false).Transport);
	}

	[Fact]
	public void ProjectTransportOverridesGlobal()
	{
		WriteGlobal("""{"websockets":true}""");
		var projectDir = Path.Combine(Project, ".pi");
		Directory.CreateDirectory(projectDir);
		File.WriteAllText(Path.Combine(projectDir, "settings.json"), """{"transport":"websocket-cached"}""");

		Assert.Equal("websocket-cached", CodingAgentSettings.Load(Project, AgentDir).Transport);
	}

	[Fact]
	public void LegacyRetryMaxDelayMigratesToProviderMaxRetryDelay()
	{
		WriteGlobal("""{"retry":{"maxDelayMs":15000}}""");

		var file = CodingAgentSettings.ReadFile(Path.Combine(AgentDir, "settings.json"));

		Assert.NotNull(file);
		Assert.Equal(15000, file!.Retry!.Provider!.MaxRetryDelayMs);
		Assert.Null(file.Retry.MaxDelayMs);
	}

	[Fact]
	public void ProviderTimeoutIsUsedAndProjectOverridesGlobal()
	{
		WriteGlobal("""{"retry":{"provider":{"timeoutMs":120000}}}""");
		Assert.Equal(120000, CodingAgentSettings.Load(Project, AgentDir, includeProject: false).TimeoutMs);

		var projectDir = Path.Combine(Project, ".pi");
		Directory.CreateDirectory(projectDir);
		File.WriteAllText(Path.Combine(projectDir, "settings.json"), """{"retry":{"provider":{"timeoutMs":30000}}}""");
		Assert.Equal(30000, CodingAgentSettings.Load(Project, AgentDir).TimeoutMs);
	}

	[Fact]
	public void ProjectExtensionsReplaceUserExtensions()
	{
		WriteGlobal("""{"extensions":["/global/a.js","/global/b.js"]}""");
		var projectDir = Path.Combine(Project, ".pi");
		Directory.CreateDirectory(projectDir);
		File.WriteAllText(Path.Combine(projectDir, "settings.json"), """{"extensions":["/project/c.js"]}""");

		Assert.Equal(["/project/c.js"], CodingAgentSettings.Load(Project, AgentDir).Extensions);

		// 项目可以显式清空用户级列表。
		File.WriteAllText(Path.Combine(projectDir, "settings.json"), """{"extensions":[]}""");
		Assert.Empty(CodingAgentSettings.Load(Project, AgentDir).Extensions);
	}

	[Fact]
	public void RelativeExtensionsResolveAgainstTheirDeclarationScope()
	{
		WriteGlobal("""{"extensions":["ext/user.js"]}""");
		Assert.Equal([Path.Combine(AgentDir, "ext", "user.js")], CodingAgentSettings.Load(Project, AgentDir, includeProject: false).Extensions);

		var projectDir = Path.Combine(Project, ".pi");
		Directory.CreateDirectory(projectDir);
		File.WriteAllText(Path.Combine(projectDir, "settings.json"), """{"extensions":["project.js"]}""");
		Assert.Equal([Path.Combine(projectDir, "project.js")], CodingAgentSettings.Load(Project, AgentDir).Extensions);
	}

	[Fact]
	public void HttpIdleTimeoutAcceptsNumbersAndDisabled()
	{
		WriteGlobal("""{"httpIdleTimeoutMs":120000}""");
		Assert.Equal(120000, CodingAgentSettings.Load(Project, AgentDir, includeProject: false).HttpIdleTimeoutMs);

		WriteGlobal("""{"httpIdleTimeoutMs":"disabled"}""");
		Assert.Equal(0, CodingAgentSettings.Load(Project, AgentDir, includeProject: false).HttpIdleTimeoutMs);
	}

	[Fact]
	public void HttpIdleTimeoutDefaultsToFiveMinutesWhenUnsetOrUnreadable()
	{
		WriteGlobal("""{}""");
		Assert.Equal(300000, CodingAgentSettings.Load(Project, AgentDir, includeProject: false).HttpIdleTimeoutMs);

		WriteGlobal("""{"httpIdleTimeoutMs":"not-a-timeout"}""");
		Assert.Equal(300000, CodingAgentSettings.Load(Project, AgentDir, includeProject: false).HttpIdleTimeoutMs);
	}

	[Fact]
	public void LegacyRootTimeoutStillWorksForExistingPiWeftConfigs()
	{
		WriteGlobal("""{"timeoutMs":45000}""");

		Assert.Equal(45000, CodingAgentSettings.Load(Project, AgentDir, includeProject: false).TimeoutMs);
	}
}
