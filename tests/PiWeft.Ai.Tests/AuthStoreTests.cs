using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using PiWeft.Ai.Providers.Bedrock;
using Xunit;

namespace PiWeft.Ai.Tests;

/// auth.json 凭据解析。之前只认环境变量，把 key 存进 auth.json 的用户（pi login / 桌面端）会拿到 401，
/// 这些测试盯住「存储的凭据优先」与模板、命令型凭据的处理。
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
	public void ConfiguredProvidersCountsLiteralTemplatesAndCommandsWithoutExecuting()
	{
		WriteAuth("""
			{"literal":{"type":"api_key","key":"k"},
			 "template":{"type":"api_key","key":"${TOKEN}","env":{"TOKEN":"resolved"}},
			 "missing":{"type":"api_key","key":"${MISSING}"},
			 "command":{"type":"api_key","key":"!never execute this"},
			 "envonly":{"type":"api_key","env":{"AWS_ACCESS_KEY_ID":"x"}},
			 "marker":{"type":"api_key"},
			 "oauth":{"type":"oauth","access":"a","refresh":"r","expires":1}}
			""");
		var providers = AuthStore.ConfiguredProviders(_agentDir);
		Assert.Contains("literal", providers);
		Assert.Contains("template", providers);
		Assert.Contains("command", providers);
		Assert.Contains("envonly", providers);
		Assert.DoesNotContain("missing", providers);
		Assert.DoesNotContain("marker", providers);
		Assert.DoesNotContain("oauth", providers);
	}

	[Fact]
	public void StoredCredentialSnapshotsKeepEnvironmentAndDoNotExecuteCommands()
	{
		WriteAuth("""
			{"command":{"type":"api_key","key":"!never execute"},
			 "template":{"type":"api_key","key":"${MISSING}"},
			 "cloudflare":{"type":"api_key","key":"cf","env":{"CLOUDFLARE_ACCOUNT_ID":"acc"}},
			 "ambient":{"type":"api_key"}}
			""");
		var all = AuthStore.ReadAllStoredApiKeys(_agentDir);
		Assert.True(all["command"].IsCommand);
		Assert.True(all["command"].HasUsableKey);
		Assert.True(all["template"].IsTemplate);
		Assert.False(all["template"].HasUsableKey);
		Assert.True(all["cloudflare"].HasUsableKey);
		Assert.Equal("acc", all["cloudflare"].Environment["CLOUDFLARE_ACCOUNT_ID"]);
		Assert.True(all["ambient"].IsAmbientMarker);
		Assert.True(all["ambient"].IsConfigured);
		Assert.Equal("acc", AuthStore.ReadStoredEnvironmentValue("cloudflare", "CLOUDFLARE_ACCOUNT_ID", _agentDir));
	}

	[Fact]
	public void SaveApiKeyCredentialWritesEnvironmentAndRemovesTheWholeEntry()
	{
		AuthStore.SaveApiKeyCredential(Provider, "key", new Dictionary<string, string>
		{
			["CLOUDFLARE_ACCOUNT_ID"] = "acc",
			["EMPTY"] = "",
		}, _agentDir);

		var stored = AuthStore.ReadStoredApiKey(Provider, _agentDir)!;
		Assert.Equal("key", stored.Key);
		Assert.Equal("acc", stored.Environment["CLOUDFLARE_ACCOUNT_ID"]);
		Assert.False(stored.Environment.ContainsKey("EMPTY"));
		Assert.True(AuthStore.RemoveCredential(Provider, _agentDir));
		Assert.Null(AuthStore.ReadStoredApiKey(Provider, _agentDir));
		Assert.False(AuthStore.RemoveCredential(Provider, _agentDir));

		// 没有 key 也没有 env 时保存 ambient 标记；不能把它写成空 key 字段。
		AuthStore.SaveApiKeyCredential(Provider, null, null, _agentDir);
		Assert.True(AuthStore.ReadStoredApiKey(Provider, _agentDir)!.IsAmbientMarker);
	}

	[Fact]
	public void ApiKeySaveRefusesOauthUnlessExplicitlyConfirmed()
	{
		OAuthStore.Save(Provider, new OAuthCredential
		{
			Access = "access",
			Refresh = "refresh",
			Expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
		}, _agentDir);

		Assert.Throws<InvalidOperationException>(() => AuthStore.SaveApiKey(Provider, "new", _agentDir));
		Assert.NotNull(OAuthStore.Read(Provider, _agentDir));

		AuthStore.SaveApiKeyCredential(Provider, "new", env: null, _agentDir, overwriteOAuth: true);
		Assert.Null(OAuthStore.Read(Provider, _agentDir));
		Assert.Equal("new", AuthStore.ReadApiKey(Provider, _agentDir));
	}

	[Fact]
	public void ProviderEnvironmentReadsCredentialScopedAwsAndExpandsBaseUrlPlaceholders()
	{
		AuthStore.SaveApiKeyCredential("amazon-bedrock", null, new Dictionary<string, string>
		{
			["AWS_ACCESS_KEY_ID"] = "stored-access",
			["AWS_SECRET_ACCESS_KEY"] = "stored-secret",
			["AWS_SESSION_TOKEN"] = "stored-token",
		}, _agentDir);
		var credentials = AwsCredentials.FromEnvironment("amazon-bedrock");
		Assert.True(credentials.IsValid);
		Assert.Equal("stored-access", credentials.AccessKeyId);
		Assert.Equal("stored-token", credentials.SessionToken);

		AuthStore.SaveApiKeyCredential("cloudflare-workers-ai", "cf", new Dictionary<string, string>
		{
			["CLOUDFLARE_ACCOUNT_ID"] = "account",
		}, _agentDir);
		Assert.Equal(
			"https://api.cloudflare.com/client/v4/accounts/account/ai/v1",
			ProviderEnvironment.ExpandBaseUrl("cloudflare-workers-ai",
				"https://api.cloudflare.com/client/v4/accounts/{CLOUDFLARE_ACCOUNT_ID}/ai/v1"));
	}

	[Fact]
	public void SaveApiKeyRefusesToOverwriteMalformedAuthFile()
	{
		WriteAuth("{not json");
		Assert.Throws<CodexAuthException>(() => AuthStore.SaveApiKey(Provider, "new", _agentDir));
		Assert.Equal("{not json", File.ReadAllText(AuthStore.Path(_agentDir)));
	}

	[Fact]
	public async Task StoredCredentialWinsOverTheEnvironmentVariable()
	{
		WriteAuth("""{"deepseek":{"type":"api_key","key":"from-auth-json"}}""");
		Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "from-environment");

		Assert.Equal("from-auth-json", await ProviderEnvironment.ResolveApiKeyAsync(Provider, Options()));
		Assert.Equal("from-auth-json", AuthStore.ReadApiKey(Provider, _agentDir));
	}

	[Fact]
	public async Task EnvironmentVariableIsUsedWhenNoCredentialIsStored()
	{
		Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "from-environment");
		Assert.Equal("from-environment", await ProviderEnvironment.ResolveApiKeyAsync(Provider, Options()));
	}

	[Fact]
	public async Task ExplicitOptionStillWins()
	{
		WriteAuth("""{"deepseek":{"type":"api_key","key":"from-auth-json"}}""");
		Assert.Equal("explicit", await ProviderEnvironment.ResolveApiKeyAsync(Provider, Options("explicit")));
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
	public async Task CommandCredentialIsExecutedAtRequestTime()
	{
		// 与 pi 一致：命令型凭据在取值时执行 stdout，不落盘、不缓存到文件。
		WriteAuth("""{"deepseek":{"type":"api_key","key":"!echo from-command"}}""");

		Assert.True(AuthStore.IsCommandCredential(Provider, _agentDir));
		Assert.Equal("from-command", AuthStore.ReadApiKey(Provider, _agentDir));
		Assert.Equal("from-command", await AuthStore.ReadApiKeyAsync(Provider, _agentDir));
		Assert.Equal("from-command", await ProviderEnvironment.ResolveApiKeyAsync(Provider, Options()));
	}

	[Fact]
	public async Task FailingCommandCredentialFallsBackToTheEnvironmentVariable()
	{
		WriteAuth("""{"deepseek":{"type":"api_key","key":"!exit 3"}}""");
		Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "from-environment");

		Assert.Null(AuthStore.ReadApiKey(Provider, _agentDir));
		Assert.Null(await AuthStore.ReadApiKeyAsync(Provider, _agentDir));
		Assert.Equal("from-environment", await ProviderEnvironment.ResolveApiKeyAsync(Provider, Options()));
	}

	[Fact]
	public async Task AsyncResolutionUsesStoredOAuthAccessToken()
	{
		WriteAuth("""{"deepseek":{"type":"oauth","access":"oauth-access","refresh":"r","expires":9999999999}}""");

		Assert.Equal("oauth-access", await ProviderEnvironment.ResolveApiKeyAsync(Provider, Options()));
	}

	[Fact]
	public async Task OversizedCommandOutputIsRejectedWithoutBufferingItAll()
	{
		if (OperatingSystem.IsWindows()) return;

		// 10 万字符远超 64 KiB：必须在读入上限处终止，而不是先 ReadToEnd 再判断。
		WriteAuth("""{"deepseek":{"type":"api_key","key":"!yes x | head -c 100000"}}""");

		Assert.Null(await AuthStore.ReadApiKeyAsync(Provider, _agentDir));
	}

	[Fact]
	public async Task CommandCredentialCancellationStopsTheCommand()
	{
		var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";
		WriteAuth($$$"""{"deepseek":{"type":"api_key","key":"!{{{command}}}"}}""");

		using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			AuthStore.ReadApiKeyAsync(Provider, _agentDir, cancellation.Token));
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
