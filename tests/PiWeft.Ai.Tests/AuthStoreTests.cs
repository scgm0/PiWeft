using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using Xunit;

namespace PiWeft.Ai.Tests;

/// <summary>
/// auth.json 凭据解析。之前只认环境变量，把 key 存进 auth.json 的用户（pi login / 桌面端）会拿到 401，
/// 这些测试盯住「存储的凭据优先」与模板、命令型凭据的处理。
/// </summary>
[Collection("process-environment")]
public class AuthStoreTests : IDisposable
{
	private const string Provider = "deepseek";

	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "pi-auth-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");

	public AuthStoreTests()
	{
		Directory.CreateDirectory(_agentDir);
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);
		Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _previousAgentDir);
		Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

		if (Directory.Exists(_agentDir))
		{
			Directory.Delete(_agentDir, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private void WriteAuth(string json) => File.WriteAllText(AuthStore.Path(_agentDir), json);

	private static StreamOptions Options(string? apiKey = null) => new() { ApiKey = apiKey };

	[Fact]
	public void StoredCredentialWinsOverTheEnvironmentVariable()
	{
		WriteAuth("""{"deepseek":{"type":"api_key","key":"from-auth-json"}}""");
		Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "from-environment");

		Assert.Equal("from-auth-json", ProviderEnvironment.ResolveApiKey(Provider, Options()));
		Assert.Equal("from-auth-json", AuthStore.ReadApiKey(Provider, _agentDir));
	}

	[Fact]
	public void EnvironmentVariableIsUsedWhenNoCredentialIsStored()
	{
		Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "from-environment");
		Assert.Equal("from-environment", ProviderEnvironment.ResolveApiKey(Provider, Options()));
	}

	[Fact]
	public void ExplicitOptionStillWins()
	{
		WriteAuth("""{"deepseek":{"type":"api_key","key":"from-auth-json"}}""");
		Assert.Equal("explicit", ProviderEnvironment.ResolveApiKey(Provider, Options("explicit")));
	}

	[Fact]
	public void TemplateKeyResolvesFromTheCredentialEnvBlock()
	{
		WriteAuth("""{"deepseek":{"type":"api_key","key":"${MY_DEEPSEEK}","env":{"MY_DEEPSEEK":"templated"}}}""");
		Assert.Equal("templated", AuthStore.ReadApiKey(Provider, _agentDir));
	}

	[Fact]
	public void TemplateKeyFallsBackToTheProcessEnvironment()
	{
		Environment.SetEnvironmentVariable("MY_DEEPSEEK_FROM_PROCESS", "from-process");
		WriteAuth("""{"deepseek":{"type":"api_key","key":"${MY_DEEPSEEK_FROM_PROCESS}"}}""");

		Assert.Equal("from-process", AuthStore.ReadApiKey(Provider, _agentDir));
		Environment.SetEnvironmentVariable("MY_DEEPSEEK_FROM_PROCESS", null);
	}

	[Fact]
	public void TemplateKeyWithMissingVariableIsUnusable()
	{
		WriteAuth("""{"deepseek":{"type":"api_key","key":"${PI_TEST_MISSING_VAR}"}}""");
		Assert.Null(AuthStore.ReadApiKey(Provider, _agentDir));
	}

	[Fact]
	public void CommandCredentialIsExecutedAtRequestTime()
	{
		// 与 pi 一致：命令型凭据在取值时执行 stdout，不落盘、不缓存到文件。
		WriteAuth("""{"deepseek":{"type":"api_key","key":"!echo from-command"}}""");

		Assert.True(AuthStore.IsCommandCredential(Provider, _agentDir));
		Assert.Equal("from-command", AuthStore.ReadApiKey(Provider, _agentDir));
		Assert.Equal("from-command", ProviderEnvironment.ResolveApiKey(Provider, Options()));
	}

	[Fact]
	public void FailingCommandCredentialFallsBackToTheEnvironmentVariable()
	{
		WriteAuth("""{"deepseek":{"type":"api_key","key":"!exit 3"}}""");
		Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "from-environment");

		Assert.Null(AuthStore.ReadApiKey(Provider, _agentDir));
		Assert.Equal("from-environment", ProviderEnvironment.ResolveApiKey(Provider, Options()));
	}

	[Fact]
	public void CommandCredentialProvidersListsProvidersWithoutExecuting()
	{
		WriteAuth("""
			{"deepseek":{"type":"api_key","key":"!echo x"},
			 "openai":{"type":"api_key","key":"literal"}}
			""");

		Assert.Equal(["deepseek"], AuthStore.CommandCredentialProviders(_agentDir));
	}

	[Fact]
	public void OauthCredentialIsNotTreatedAsAnApiKey()
	{
		WriteAuth("""{"openai-codex":{"type":"oauth","access":"a","refresh":"r","expires":1}}""");
		Assert.Null(AuthStore.ReadApiKey("openai-codex", _agentDir));
	}

	[Theory]
	[InlineData("not json at all")]
	[InlineData("[]")]
	[InlineData("""{"deepseek":"plain string"}""")]
	public void BrokenOrUnexpectedFilesAreTolerated(string json)
	{
		WriteAuth(json);
		Assert.Null(AuthStore.ReadApiKey(Provider, _agentDir));
	}

	[Fact]
	public void BatchSnapshotUsesTheSameTemplateRulesAndSkipsMalformedEntries()
	{
		WriteAuth("""
			{"literal":{"type":"api_key","key":"test-literal"},
			 "templated":{"type":"api_key","key":"${TOKEN}","env":{"TOKEN":"test-template"}},
			 "command":{"type":"api_key","key":"!do-not-run"},
			 "missing":{"type":"api_key","key":"${PI_TEST_MISSING_VAR}"},
			 "oauth":{"type":"oauth","access":"a","refresh":"r"},
			 "malformed":{"type":17,"key":false}}
			""");
		var snapshot = AuthStore.ReadApiKeys(_agentDir);
		Assert.Equal(2, snapshot.Count);
		Assert.Equal(AuthStore.ReadApiKey("literal", _agentDir), snapshot["literal"]);
		Assert.Equal(AuthStore.ReadApiKey("templated", _agentDir), snapshot["templated"]);
		Assert.Null(AuthStore.ReadApiKey("malformed", _agentDir));
		File.Delete(AuthStore.Path(_agentDir));
		Assert.Equal(2, snapshot.Count);
		Assert.Empty(AuthStore.ReadApiKeys(_agentDir));
	}

	[Fact]
	public void AgentDirectoryExpandsTilde()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", "~/piweft-auth-dir");

		var expected = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"piweft-auth-dir",
			"auth.json");
		Assert.Equal(expected, AuthStore.Path());

		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);
	}

	[Fact]
	public void MissingFileIsTolerated() => Assert.Null(AuthStore.ReadApiKey(Provider, _agentDir));
}
