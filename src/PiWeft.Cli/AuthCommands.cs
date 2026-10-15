using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;

using PiWeft.Ai.Json;
namespace PiWeft.Cli;

/// <summary>凭据相关命令：<c>pi login &lt;provider&gt;</c> 与 <c>pi auth &lt;子命令&gt;</c>。</summary>
public static class AuthCommands
{
	/// <summary>覆盖 OAuth 服务器地址，测试即用它指向打桩服务。</summary>
	public const string AuthBaseUrlVariable = "PI_CODEX_AUTH_BASE_URL";

	/// <summary>执行一个凭据命令；不是本模块的命令时返回 <c>null</c>。</summary>
	public static async Task<int?> RunAsync(
		string command,
		IReadOnlyList<string> rest,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		switch (command)
		{
			case "login":
				return await LoginAsync(rest, output, error, cancellationToken).ConfigureAwait(false);

			case "logout":
				return Logout(rest, output, error);

			case "auth":
				return await AuthAsync(rest, output, error, cancellationToken).ConfigureAwait(false);

			default:
				return null;
		}
	}

	private static async Task<int> LoginAsync(
		IReadOnlyList<string> rest,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		var provider = rest.Count > 0 ? rest[0] : CodexAuth.ProviderKey;
		if (provider != CodexAuth.ProviderKey)
		{
			error.WriteLine($"pi: '{provider}' does not use OAuth login. Set its API key instead (see 'pi --help').");
			return 2;
		}

		using var http = new HttpClient();
		var authBaseUrl = Environment.GetEnvironmentVariable(AuthBaseUrlVariable);
		var agentDir = Environment.GetEnvironmentVariable(PiWeft.Ai.Auth.AgentPaths.EnvAgentDir);

		try
		{
			var device = await CodexAuth.StartDeviceAsync(http, authBaseUrl, cancellationToken).ConfigureAwait(false);
			output.WriteLine($"Enter code {device.UserCode} at {device.VerificationUri}");
			output.WriteLine("Waiting for authorization…");

			var tokens = await CodexAuth.PollDeviceAsync(http, device, authBaseUrl, cancellationToken: cancellationToken)
				.ConfigureAwait(false);

			var credentials = CodexCredentials.FromTokens(tokens);
			var accountId = CodexAuth.GetAccountId(credentials.Access);
			CodexAuth.Save(credentials, agentDir);

			output.WriteLine($"Signed in to {CodexAuth.ProviderKey}{(accountId is null ? string.Empty : $" (account {accountId})")}.");
			output.WriteLine($"Credentials written to {CodexAuth.CredentialsPath(agentDir)}");
			return 0;
		}
		catch (CodexAuthException ex)
		{
			error.WriteLine($"pi: {ex.Message}");
			return 1;
		}
	}

	private static int Logout(IReadOnlyList<string> rest, TextWriter output, TextWriter error)
	{
		var provider = rest.Count > 0 ? rest[0] : CodexAuth.ProviderKey;
		if (provider != CodexAuth.ProviderKey)
		{
			error.WriteLine($"pi: no stored OAuth credentials for '{provider}'.");
			return 2;
		}

		var agentDir = Environment.GetEnvironmentVariable(PiWeft.Ai.Auth.AgentPaths.EnvAgentDir);
		var path = CodexAuth.CredentialsPath(agentDir);
		if (!File.Exists(path))
		{
			output.WriteLine($"No {CodexAuth.ProviderKey} credentials to remove.");
			return 0;
		}

		// 只删除该 provider 的条目，其它 provider 的凭据保持不动。
		var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
			?? new System.Text.Json.Nodes.JsonObject();
		if (root.Remove(CodexAuth.ProviderKey))
		{
			File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions(PiJson.NodeOptions) { WriteIndented = true }));
		}

		output.WriteLine($"Removed {CodexAuth.ProviderKey} credentials from {path}");
		return 0;
	}

	/// <summary>
	/// <c>pi auth</c> 命令面，与 pi 的行为对齐：
	/// <list type="bullet">
	/// <item><c>check</c> 输出 <c>ready</c> / <c>not_ready</c> / <c>invalid</c>，退出码 0 / 1 / 2；</item>
	/// <item><c>--json</c> 输出 <c>{{"status":..,"provider":..,"authType":..}}</c>；<c>--credentials</c> 连凭据一起输出；</item>
	/// <item><c>print-api-key</c> / <c>print-bearer-token</c> 打印凭据本身，类型不匹配时报错。</item>
	/// </list>
	/// </summary>
	private static async Task<int> AuthAsync(
		IReadOnlyList<string> rest,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		var kind = rest.Count > 0 ? rest[0] : "help";
		if (kind is "help" or "--help" or "-h")
		{
			PrintAuthUsage(output);
			return 0;
		}

		if (kind is not ("check" or "print-api-key" or "print-bearer-token"))
		{
			error.WriteLine(
				$"pi: Unknown auth command \"{kind}\". Use \"pi auth print-api-key\", " +
				"\"pi auth print-bearer-token\", or \"pi auth check\".");
			return 2;
		}

		var agentDir = Environment.GetEnvironmentVariable(PiWeft.Ai.Auth.AgentPaths.EnvAgentDir);
		var provider = ValueOf(rest, "--provider") ?? ProviderOf(ValueOf(rest, "--model"));

		if (provider is null)
		{
			error.WriteLine("pi: Auth commands require at least one of --provider or --model.");
			return 2;
		}

		// --min-expiry 只被 print-bearer-token 接受，格式与 pi 一致（30m、1h、500ms）。
		var minExpiry = ValueOf(rest, "--min-expiry");
		if (minExpiry is not null)
		{
			if (kind != "print-bearer-token")
			{
				error.WriteLine("pi: --min-expiry is only supported by print-bearer-token");
				return 2;
			}

			if (!IsDuration(minExpiry))
			{
				error.WriteLine("pi: --min-expiry must use a duration such as 30m or 1h");
				return 2;
			}
		}

		switch (kind)
		{
			case "check":
				return AuthCheck(provider, agentDir, rest, output, error, cancellationToken);
			case "print-api-key":
				return PrintApiKey(provider, agentDir, output, error);
			default:
				return await PrintBearerToken(provider, agentDir, output, error, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary><c>pi auth check</c>：只报告状态，除非显式要求凭据。</summary>
	private static int AuthCheck(
		string provider,
		string? agentDir,
		IReadOnlyList<string> rest,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		var json = rest.Contains("--json");
		var withCredentials = rest.Contains("--credentials");
		var noRefresh = rest.Contains("--no-refresh");

		try
		{
			var authType = ResolveAuthType(provider, agentDir);
			var status = authType is null ? "not_ready" : "ready";
			string? credential = null;

			if (status == "ready" && withCredentials)
			{
				credential = ResolveCredential(provider, agentDir, noRefresh, cancellationToken);
				if (credential is null)
				{
					status = "not_ready";
				}
			}

			if (json)
			{
				var node = new System.Text.Json.Nodes.JsonObject
				{
					["status"] = status,
					["provider"] = provider,
				};

				if (authType is not null)
				{
					node["authType"] = authType;
				}

				if (credential is not null)
				{
					node["credentials"] = credential;
				}

				output.WriteLine(node.ToJsonString(PiJson.NodeOptions));
			}
			else
			{
				output.WriteLine(credential ?? status);
			}

			return status == "ready" ? 0 : status == "not_ready" ? 1 : 2;
		}
		catch (Exception ex) when (ex is CodexAuthException or IOException)
		{
			error.WriteLine($"pi: {ex.Message}");
			return 2;
		}
	}

	/// <summary><c>pi auth print-api-key</c>：只对 api_key 型凭据有效。</summary>
	private static int PrintApiKey(string provider, string? agentDir, TextWriter output, TextWriter error)
	{
		if (string.Equals(ResolveAuthType(provider, agentDir), "oauth", StringComparison.Ordinal))
		{
			error.WriteLine($"pi: Provider \"{provider}\" is configured with OAuth, not an API key");
			return 1;
		}

		var key = AuthStore.ReadApiKey(provider, agentDir)
			?? Environment.GetEnvironmentVariable(ProviderEnvironment.KeyName(provider));

		if (string.IsNullOrEmpty(key))
		{
			error.WriteLine("pi: No usable API key is configured");
			return 1;
		}

		output.WriteLine(key);
		return 0;
	}

	/// <summary><c>pi auth print-bearer-token</c>：只对 OAuth 型凭据有效。</summary>
	private static async Task<int> PrintBearerToken(
		string provider,
		string? agentDir,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		if (!string.Equals(provider, CodexAuth.ProviderKey, StringComparison.Ordinal))
		{
			error.WriteLine($"pi: Provider \"{provider}\" is not configured with an OAuth bearer token");
			return 1;
		}

		using var http = new HttpClient();
		try
		{
			var credentials = await CodexAuth
				.ResolveAsync(http, agentDir, Environment.GetEnvironmentVariable(AuthBaseUrlVariable), cancellationToken)
				.ConfigureAwait(false);

			output.WriteLine(credentials.Access);
			return 0;
		}
		catch (CodexAuthException ex)
		{
			error.WriteLine($"pi: {ex.Message}");
			return 1;
		}
	}

	/// <summary>凭据类型：<c>api_key</c>（auth.json 或环境变量）或 <c>oauth</c>；都没有时返回 null。</summary>
	private static string? ResolveAuthType(string provider, string? agentDir)
	{
		if (AuthStore.ReadApiKey(provider, agentDir) is { Length: > 0 } ||
			Environment.GetEnvironmentVariable(ProviderEnvironment.KeyName(provider)) is { Length: > 0 })
		{
			return "api_key";
		}

		if (string.Equals(provider, CodexAuth.ProviderKey, StringComparison.Ordinal) &&
			CodexAuth.Load(agentDir) is not null)
		{
			return "oauth";
		}

		return null;
	}

	/// <summary>取凭据明文：api_key 直接读，oauth 走刷新逻辑。</summary>
	private static string? ResolveCredential(
		string provider,
		string? agentDir,
		bool noRefresh,
		CancellationToken cancellationToken)
	{
		if (ResolveAuthType(provider, agentDir) == "api_key")
		{
			return AuthStore.ReadApiKey(provider, agentDir)
				?? Environment.GetEnvironmentVariable(ProviderEnvironment.KeyName(provider));
		}

		if (noRefresh && CodexAuth.Load(agentDir) is { } stored)
		{
			return stored.Access;
		}

		using var http = new HttpClient();
		var credentials = CodexAuth
			.ResolveAsync(http, agentDir, Environment.GetEnvironmentVariable(AuthBaseUrlVariable), cancellationToken)
			.GetAwaiter()
			.GetResult();

		return credentials.Access;
	}

	/// <summary>把 <c>provider/model</c> 形式的模型标识折成 provider。</summary>
	private static string? ProviderOf(string? model)
	{
		if (model is not { Length: > 0 })
		{
			return null;
		}

		var slash = model.IndexOf('/', StringComparison.Ordinal);
		return slash > 0 ? model[..slash] : model;
	}

	/// <summary><c>30m</c> / <c>1h</c> / <c>500ms</c> 形式的时长。</summary>
	private static bool IsDuration(string text)
	{
		var digits = 0;
		while (digits < text.Length && char.IsAsciiDigit(text[digits]))
		{
			digits++;
		}

		return digits > 0 && text[digits..] is "ms" or "s" or "m" or "h";
	}

	private static void PrintAuthUsage(TextWriter output)
	{
		output.WriteLine(
			"""
			Usage:
			  pi auth print-api-key [--provider <provider>] [--model <model>]
			  pi auth print-bearer-token [--provider <provider>] [--model <model>] [--min-expiry <duration>]
			  pi auth check [--provider <provider>] [--model <model>] [--json] [--credentials] [--no-refresh]

			Auth commands require at least one of --provider or --model. Checks refresh expired OAuth credentials by
			default; --no-refresh prevents this. --credentials emits the credential, or includes it in JSON output.
			""");
	}

	/// <summary>取 <c>--name value</c> 形式的参数值。</summary>
	private static string? ValueOf(IReadOnlyList<string> rest, string name)
	{
		for (var index = 0; index < rest.Count - 1; index++)
		{
			if (string.Equals(rest[index], name, StringComparison.Ordinal))
			{
				return rest[index + 1];
			}
		}

		return null;
	}
}
