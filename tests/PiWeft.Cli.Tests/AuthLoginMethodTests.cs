using PiWeft.Ai.Auth;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <c>pi login &lt;provider&gt; api-key</c> 与桌面端共用认证目录：标准供应商只问 Key，
/// Cloudflare / Vertex / Bedrock 会按目录里的字段询问额外 env 或认证方式。
[Collection("process-environment")]
public sealed class AuthLoginMethodTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "pi-login-key-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousAgentDir = Environment.GetEnvironmentVariable(AgentPaths.EnvAgentDir);

	public AuthLoginMethodTests()
	{
		Directory.CreateDirectory(_agentDir);
		Environment.SetEnvironmentVariable(AgentPaths.EnvAgentDir, _agentDir);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(AgentPaths.EnvAgentDir, _previousAgentDir);
		if (Directory.Exists(_agentDir)) Directory.Delete(_agentDir, recursive: true);
		GC.SuppressFinalize(this);
	}

	private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string input, params string[] args)
	{
		using var reader = new StringReader(input);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var exitCode = await AuthCommands.RunAsync("login", args, output, error, reader, CancellationToken.None);
		return (exitCode ?? -1, output.ToString(), error.ToString());
	}

	private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAuthCheckAsync(params string[] args)
	{
		using var reader = new StringReader(string.Empty);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var exitCode = await AuthCommands.RunAsync("auth", args, output, error, reader, CancellationToken.None);
		return (exitCode ?? -1, output.ToString(), error.ToString());
	}

	[Fact]
	public async Task StandardProviderAsksForOneKeyAndWritesAuthJson()
	{
		var result = await RunAsync("sk-deepseek\n", "deepseek", "api-key");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("凭据已写入", result.StdOut);
		var stored = AuthStore.ReadStoredApiKey("deepseek", _agentDir)!;
		Assert.Equal("sk-deepseek", stored.Key);
		Assert.True(stored.HasUsableKey);
	}

	[Fact]
	public async Task CloudflareAsksForAccountAndGatewayFields()
	{
		var result = await RunAsync("cf-key\naccount\ngateway\n", "cloudflare-ai-gateway", "api-key");

		Assert.Equal(0, result.ExitCode);
		var stored = AuthStore.ReadStoredApiKey("cloudflare-ai-gateway", _agentDir)!;
		Assert.Equal("cf-key", stored.Key);
		Assert.Equal("account", stored.Environment["CLOUDFLARE_ACCOUNT_ID"]);
		Assert.Equal("gateway", stored.Environment["CLOUDFLARE_GATEWAY_ID"]);
	}

	[Fact]
	public async Task VertexAdcWritesEnvironmentWithoutAKey()
	{
		// 1 = API Key，2 = ADC；ADC 依次询问项目、区域、可选凭据文件。
		var result = await RunAsync("2\nproject\nus-central1\n\n", "google-vertex", "api-key");

		Assert.Equal(0, result.ExitCode);
		var stored = AuthStore.ReadStoredApiKey("google-vertex", _agentDir)!;
		Assert.False(stored.HasKey);
		Assert.Equal("project", stored.Environment["GOOGLE_CLOUD_PROJECT"]);
		Assert.Equal("us-central1", stored.Environment["GOOGLE_CLOUD_LOCATION"]);
	}

	[Fact]
	public async Task BedrockAmbientChoiceWritesCredentialMarker()
	{
		// 1 = AWS 访问密钥，2 = 现有环境变量；后者没有字段，只写 marker。
		var result = await RunAsync("2\n", "amazon-bedrock", "api-key");

		Assert.Equal(0, result.ExitCode);
		var stored = AuthStore.ReadStoredApiKey("amazon-bedrock", _agentDir)!;
		Assert.True(stored.IsAmbientMarker);
	}

	[Fact]
	public async Task VertexServiceAccountIsReadyForAuthCheck()
	{
		var credentials = Path.Combine(_agentDir, "service-account.json");
		File.WriteAllText(credentials, "{}");
		var login = await RunAsync($"3\n{credentials}\nproject\nus-central1\n", "google-vertex", "api-key");
		Assert.Equal(0, login.ExitCode);

		var check = await RunAuthCheckAsync("check", "--provider", "google-vertex");
		Assert.Equal(0, check.ExitCode);
		Assert.Equal("ready", check.StdOut.Trim());

		var credentialsOutput = await RunAuthCheckAsync("check", "--provider", "google-vertex", "--credentials");
		Assert.Equal("ambient", credentialsOutput.StdOut.Trim());
	}

	[Fact]
	public async Task BedrockStoredAwsKeysAreReadyForAuthCheck()
	{
		var login = await RunAsync("1\naccess\nsecret\n\n", "amazon-bedrock", "api-key");
		Assert.Equal(0, login.ExitCode);

		var check = await RunAuthCheckAsync("check", "--provider", "amazon-bedrock");
		Assert.Equal(0, check.ExitCode);
		Assert.Equal("ready", check.StdOut.Trim());
	}

	[Fact]
	public async Task CloudflareAuthCheckRequiresTheAccountField()
	{
		AuthStore.SaveApiKey("cloudflare-workers-ai", "cf-key", _agentDir);
		var missing = await RunAuthCheckAsync("check", "--provider", "cloudflare-workers-ai");
		Assert.Equal(1, missing.ExitCode);
		Assert.Equal("not_ready", missing.StdOut.Trim());

		AuthStore.SaveApiKeyCredential("cloudflare-workers-ai", "cf-key", new Dictionary<string, string>
		{
			["CLOUDFLARE_ACCOUNT_ID"] = "account",
		}, _agentDir);
		var ready = await RunAuthCheckAsync("check", "--provider", "cloudflare-workers-ai");
		Assert.Equal(0, ready.ExitCode);
		Assert.Equal("ready", ready.StdOut.Trim());
	}

	[Fact]
	public async Task OauthOnlyProviderRejectsApiKeyMethod()
	{
		var result = await RunAsync(string.Empty, "openai-codex", "api-key");

		Assert.Equal(2, result.ExitCode);
		Assert.Contains("no interactive API key login", result.StdErr);
	}
}
