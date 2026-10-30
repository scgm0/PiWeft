using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Migrations;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 启动期迁移：pi 老版本的 oauth.json / settings.apiKeys、写在 agent 根目录的会话、
/// 以及 commands/ 目录都要搬到当前布局，否则老用户看到的是「没登录、没会话、没模板」。
/// </summary>
public class AgentMigrationsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-migrate-" + Guid.NewGuid().ToString("N")[..8]);

	public AgentMigrationsTests()
	{
		Directory.CreateDirectory(AgentDir);
		Directory.CreateDirectory(Project);
	}

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

	private string AgentFile(string name) => Path.Combine(AgentDir, name);

	[Fact]
	public void OauthAndSettingsApiKeysMigrateIntoAuthJson()
	{
		File.WriteAllText(AgentFile("oauth.json"),
			"""{"anthropic":{"access":"a","refresh":"r","expires":123},"radius":{"access":"x","refresh":"y","expires":456}}""");
		File.WriteAllText(AgentFile("settings.json"),
			"""{"defaultModel":"keep","apiKeys":{"openai":"sk-test","anthropic":"ignored"},"modelThinkingLevels":{"p/m":"high"}}""");

		var report = AgentMigrations.Run(Project, AgentDir, projectTrustOverride: false);

		Assert.Equal(["anthropic", "radius", "openai"], report.MigratedAuthProviders);

		var auth = JsonNode.Parse(File.ReadAllText(AgentFile("auth.json")))!.AsObject();
		Assert.Equal("oauth", auth["anthropic"]!["type"]!.GetValue<string>());
		Assert.Equal("a", auth["anthropic"]!["access"]!.GetValue<string>());
		Assert.Equal("oauth", auth["radius"]!["type"]!.GetValue<string>());
		Assert.Equal("api_key", auth["openai"]!["type"]!.GetValue<string>());
		Assert.Equal("sk-test", auth["openai"]!["key"]!.GetValue<string>());
		Assert.Null(auth["settings"]);

		Assert.True(File.Exists(AgentFile("oauth.json.migrated")));
		Assert.False(File.Exists(AgentFile("oauth.json")));

		var settings = JsonNode.Parse(File.ReadAllText(AgentFile("settings.json")))!.AsObject();
		Assert.Null(settings["apiKeys"]);
		Assert.Equal("keep", settings["defaultModel"]!.GetValue<string>());
		Assert.Equal("high", settings["modelThinkingLevels"]!["p/m"]!.GetValue<string>());

		// 幂等：第二次运行不再产生新的迁移。
		var again = AgentMigrations.Run(Project, AgentDir, projectTrustOverride: false);
		Assert.Empty(again.MigratedAuthProviders);
	}

	[Fact]
	public void ExistingAuthJsonIsAuthoritative()
	{
		File.WriteAllText(AgentFile("auth.json"), """{"openai":{"type":"api_key","key":"new"}}""");
		File.WriteAllText(AgentFile("oauth.json"), """{"anthropic":{"access":"a","refresh":"r","expires":1}}""");
		File.WriteAllText(AgentFile("settings.json"), """{"apiKeys":{"x":"y"}}""");

		var report = AgentMigrations.Run(Project, AgentDir, projectTrustOverride: false);

		Assert.Empty(report.MigratedAuthProviders);
		var auth = JsonNode.Parse(File.ReadAllText(AgentFile("auth.json")))!.AsObject();
		Assert.Single(auth);
		Assert.True(File.Exists(AgentFile("oauth.json")));
	}

	[Fact]
	public void SessionsInAgentRootMoveToEncodedCwdDirectory()
	{
		var session = Path.Combine(AgentDir, "2026-01-01T00-00-00-000Z_v1.jsonl");
		File.WriteAllText(session,
			"""{"type":"session","version":3,"id":"s1","timestamp":"2026-01-01T00:00:00.000Z","cwd":"/workspace/my project"}""");
		var unrelated = AgentFile("not-a-session.jsonl");
		File.WriteAllText(unrelated, """{"type":"something","cwd":"/workspace/x"}""");

		AgentMigrations.Run(Project, AgentDir, projectTrustOverride: false);

		var expected = Path.Combine(AgentDir, "sessions", "--workspace-my project--", Path.GetFileName(session));
		Assert.True(File.Exists(expected));
		Assert.False(File.Exists(session));
		Assert.True(File.Exists(unrelated));
	}

	[Fact]
	public void SessionMoveKeepsExistingTargetFile()
	{
		var directory = Path.Combine(AgentDir, "sessions", "--workspace-p--");
		Directory.CreateDirectory(directory);
		var name = "2026-01-01T00-00-00-000Z_dup.jsonl";
		var target = Path.Combine(directory, name);
		File.WriteAllText(target, "existing");
		var source = Path.Combine(AgentDir, name);
		File.WriteAllText(source, """{"type":"session","id":"s","cwd":"/workspace/p"}""");

		AgentMigrations.Run(Project, AgentDir, projectTrustOverride: false);

		Assert.Equal("existing", File.ReadAllText(target));
		Assert.True(File.Exists(source));
	}

	[Fact]
	public void CommandsDirectoryMigratesToPromptsForAgentAndTrustedProject()
	{
		Directory.CreateDirectory(Path.Combine(AgentDir, "commands"));
		Directory.CreateDirectory(Path.Combine(Project, ".pi", "commands"));
		ProjectTrust.Set(AgentDir, Project, true);

		AgentMigrations.Run(Project, AgentDir, projectTrustOverride: null);

		Assert.True(Directory.Exists(Path.Combine(AgentDir, "prompts")));
		Assert.False(Directory.Exists(Path.Combine(AgentDir, "commands")));
		Assert.True(Directory.Exists(Path.Combine(Project, ".pi", "prompts")));
		Assert.False(Directory.Exists(Path.Combine(Project, ".pi", "commands")));
	}

	[Fact]
	public void UntrustedProjectCommandsAreLeftAlone()
	{
		Directory.CreateDirectory(Path.Combine(Project, ".pi", "commands"));

		AgentMigrations.Run(Project, AgentDir, projectTrustOverride: false);

		Assert.True(Directory.Exists(Path.Combine(Project, ".pi", "commands")));
		Assert.False(Directory.Exists(Path.Combine(Project, ".pi", "prompts")));
	}

	[Fact]
	public void EncodeCwdMatchesPiLayout()
	{
		Assert.Equal("--workspace-proj--", AgentMigrations.EncodeCwd("/workspace/proj"));
		Assert.Equal("--C--Users-me--", AgentMigrations.EncodeCwd("C:\\Users\\me"));
		Assert.Equal("----", AgentMigrations.EncodeCwd("/"));
	}

	[Fact]
	public void UnparsableOauthIsLeftInPlace()
	{
		File.WriteAllText(AgentFile("oauth.json"), "{not json");

		var report = AgentMigrations.Run(Project, AgentDir, projectTrustOverride: false);

		Assert.NotEmpty(report.Warnings);
		Assert.True(File.Exists(AgentFile("oauth.json")));
		Assert.False(File.Exists(AgentFile("auth.json")));
	}
}
