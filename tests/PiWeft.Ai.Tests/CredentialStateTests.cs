using PiWeft.Ai.Auth;
using Xunit;

namespace PiWeft.Ai.Tests;

/// <summary>
/// 凭据读取的回归用例：<see cref="AuthStore.ReadCredentialState"/> 一次解析同时给出可用 key
/// 与「是否命令型」，`ProviderEnvironment` 不再为同一个 provider 读两遍 auth.json。
/// </summary>
public class CredentialStateTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-cred-" + Guid.NewGuid().ToString("N")[..8]);

	private readonly string _agentDir;

	public CredentialStateTests()
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

	private void WriteAuth(string json) => File.WriteAllText(AuthStore.Path(_agentDir), json);

	[Fact]
	public void ReturnsPlainKey()
	{
		WriteAuth("""{ "openai": { "type": "api_key", "key": "sk-plain" } }""");

		var (key, isCommand) = AuthStore.ReadCredentialState("openai", _agentDir);

		Assert.Equal("sk-plain", key);
		Assert.False(isCommand);
	}

	[Fact]
	public void FlagsCommandCredentialsWithoutReturningTheKey()
	{
		WriteAuth("""{ "openai": { "type": "api_key", "key": "!op read secret" } }""");

		var (key, isCommand) = AuthStore.ReadCredentialState("openai", _agentDir);

		Assert.Null(key);
		Assert.True(isCommand);
	}

	[Fact]
	public void ResolvesTemplateFromTheCredentialEnv()
	{
		WriteAuth("""{ "openai": { "type": "api_key", "key": "${TOKEN}", "env": { "TOKEN": "sk-from-env" } } }""");

		var (key, isCommand) = AuthStore.ReadCredentialState("openai", _agentDir);

		Assert.Equal("sk-from-env", key);
		Assert.False(isCommand);
	}

	[Fact]
	public void IsEmptyForAnUnknownProvider()
	{
		WriteAuth("""{ "openai": { "type": "api_key", "key": "sk" } }""");

		var (key, isCommand) = AuthStore.ReadCredentialState("anthropic", _agentDir);

		Assert.Null(key);
		Assert.False(isCommand);
	}

	[Fact]
	public void IsEmptyWhenThereIsNoAuthFile()
	{
		var (key, isCommand) = AuthStore.ReadCredentialState("openai", _agentDir);

		Assert.Null(key);
		Assert.False(isCommand);
	}
}
