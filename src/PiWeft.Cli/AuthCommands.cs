using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;

using PiWeft.Ai.Json;
namespace PiWeft.Cli;

public static class AuthCommands
{
	public const string AuthBaseUrlVariable = "PI_CODEX_AUTH_BASE_URL";

	public static async Task<int?> RunAsync(
		string command,
		IReadOnlyList<string> rest,
		TextWriter output,
		TextWriter error,
		TextReader input,
		CancellationToken cancellationToken)
	{
		switch (command)
		{
			case "login":
				return await LoginAsync(rest, output, error, input, cancellationToken).ConfigureAwait(false);

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
		TextReader input,
		CancellationToken cancellationToken)
	{
		var provider = rest.Count > 0 ? rest[0] : CodexAuth.ProviderKey;
		if (!ModelRegistry.LoadDefault().Providers.Contains(provider, StringComparer.Ordinal))
		{
			error.WriteLine($"pi: Unknown provider '{provider}'.");
			return 2;
		}

		if (string.Equals(provider, AnthropicOAuth.ProviderKey, StringComparison.Ordinal))
		{
			try
			{
				await AnthropicOAuth.LoginAsync(input, output, cancellationToken).ConfigureAwait(false);
				return 0;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException)
			{
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

		if (string.Equals(provider, XaiOAuth.ProviderKey, StringComparison.Ordinal))
		{
			try
			{
				await XaiOAuth.LoginAsync(output, cancellationToken).ConfigureAwait(false);
				return 0;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException)
			{
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

		if (string.Equals(provider, KimiOAuth.ProviderKey, StringComparison.Ordinal))
		{
			try
			{
				await KimiOAuth.LoginAsync(output, cancellationToken).ConfigureAwait(false);
				return 0;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException)
			{
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

		if (string.Equals(provider, OpenAiChatGptOAuth.ProviderKey, StringComparison.Ordinal))
		{
			try
			{
				await OpenAiChatGptOAuth.LoginAsync(input, output, cancellationToken: cancellationToken).ConfigureAwait(false);
				return 0;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException)
			{
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

		if (string.Equals(provider, GitHubCopilotOAuth.ProviderKey, StringComparison.Ordinal))
		{
			try
			{
				await GitHubCopilotOAuth.LoginAsync(input, output, cancellationToken: cancellationToken).ConfigureAwait(false);
				await PiWeft.CodingAgent.Models.ModelCatalogStore
					.RefreshAccountCatalogAsync("github-copilot", cancellationToken: cancellationToken)
					.ConfigureAwait(false);
				return 0;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException)
			{
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

		if (string.Equals(provider, OpenRouterOAuth.ProviderKey, StringComparison.Ordinal))
		{
			try
			{
				await OpenRouterOAuth.LoginAsync(input, output, cancellationToken: cancellationToken).ConfigureAwait(false);
				await PiWeft.CodingAgent.Models.ModelCatalogStore
					.RefreshAccountCatalogAsync("openrouter", cancellationToken: cancellationToken)
					.ConfigureAwait(false);
				return 0;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException)
			{
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

		if (string.Equals(provider, MetaOAuth.ProviderKey, StringComparison.Ordinal))
		{
			try
			{
				await MetaOAuth.LoginAsync(output, cancellationToken: cancellationToken).ConfigureAwait(false);
				return 0;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException)
			{
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

		if (string.Equals(provider, RadiusOAuth.ProviderKey, StringComparison.Ordinal))
		{
			try
			{
				await RadiusOAuth.LoginAsync(output, cancellationToken: cancellationToken).ConfigureAwait(false);
				await PiWeft.CodingAgent.Models.ModelCatalogStore
					.RefreshAccountCatalogAsync("radius", cancellationToken: cancellationToken)
					.ConfigureAwait(false);
				return 0;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException)
			{
				error.WriteLine($"pi: {ex.Message}");
				return 1;
			}
		}

		if (provider != CodexAuth.ProviderKey)
		{
			// 其余 Provider 走 API key 登录：安全地写入 auth.json，不经过 shell 历史。
			output.Write($"请输入 {provider} 的 API key（以 ! 开头表示命令型凭据）：");
			output.Flush();
			var key = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(key))
			{
				error.WriteLine("pi: API key 为空，登录取消。");
				return 1;
			}

			try
			{
				AuthStore.SaveApiKey(provider, key.Trim());
			}
			catch (CodexAuthException exception)
			{
				error.WriteLine($"pi: {exception.Message}");
				return 1;
			}

			output.WriteLine($"凭据已写入 {AuthStore.Path()}（provider: {provider}）。");
			return 0;
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
		var agentDir = Environment.GetEnvironmentVariable(PiWeft.Ai.Auth.AgentPaths.EnvAgentDir);
		if (!OAuthStore.RemoveProvider(provider, agentDir))
		{
			error.WriteLine($"pi: no stored credentials for '{provider}'.");
			return 1;
		}

		output.WriteLine($"Removed {provider} credentials from {OAuthStore.Path(agentDir)}");
		return 0;
	}

	/// <c>pi auth</c> 命令面，与 pi 的行为对齐：
	/// <list type="bullet">
	/// <item><c>check</c> 输出 <c>ready</c> / <c>not_ready</c> / <c>invalid</c>，退出码 0 / 1 / 2；</item>
	/// <item><c>--json</c> 输出 <c>{{"status":..,"provider":..,"authType":..}}</c>；<c>--credentials</c> 连凭据一起输出；</item>
	/// <item><c>print-api-key</c> / <c>print-bearer-token</c> 打印凭据本身，类型不匹配时报错。</item>
	/// </list>
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
				return await AuthCheck(provider, agentDir, rest, output, error, cancellationToken).ConfigureAwait(false);
			case "print-api-key":
				return await PrintApiKey(provider, agentDir, output, error, cancellationToken).ConfigureAwait(false);
			default:
				return await PrintBearerToken(provider, agentDir, output, error, cancellationToken).ConfigureAwait(false);
		}
	}

	private static async Task<int> AuthCheck(
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
			var authType = await ResolveAuthTypeAsync(provider, agentDir, cancellationToken).ConfigureAwait(false);
			var status = authType is null ? "not_ready" : "ready";
			string? credential = null;

			if (authType is not null)
			{
				// 凭据存在就要校验可用性：命令型凭据此时执行命令，OAuth 按需刷新。
				// 取不到值报告 invalid（凭据配置坏了），与「没配置」的 not_ready 区分开。
				try
				{
					credential = await ResolveCredentialAsync(provider, agentDir, noRefresh, cancellationToken).ConfigureAwait(false);
				}
				catch (CodexAuthException)
				{
					credential = null;
				}

				if (credential is not { Length: > 0 })
				{
					status = "invalid";
					credential = null;
				}
				else if (!withCredentials)
				{
					credential = null;
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

	private static async Task<int> PrintApiKey(
		string provider,
		string? agentDir,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		if (string.Equals(await ResolveAuthTypeAsync(provider, agentDir, cancellationToken).ConfigureAwait(false), "oauth", StringComparison.Ordinal))
		{
			error.WriteLine($"pi: Provider \"{provider}\" is configured with OAuth, not an API key");
			return 1;
		}

		var key = await AuthStore.ReadApiKeyAsync(provider, agentDir, cancellationToken).ConfigureAwait(false)
			?? Environment.GetEnvironmentVariable(ProviderEnvironment.KeyName(provider));

		if (string.IsNullOrEmpty(key))
		{
			error.WriteLine("pi: No usable API key is configured");
			return 1;
		}

		output.WriteLine(key);
		return 0;
	}

	private static async Task<int> PrintBearerToken(
		string provider,
		string? agentDir,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		try
		{
			var token = await ResolveCredentialAsync(provider, agentDir, noRefresh: false, cancellationToken).ConfigureAwait(false);
			if (token is not { Length: > 0 })
			{
				error.WriteLine($"pi: Provider \"{provider}\" is not configured with an OAuth bearer token");
				return 1;
			}

			output.WriteLine(token);
			return 0;
		}
		catch (CodexAuthException ex)
		{
			error.WriteLine($"pi: {ex.Message}");
			return 1;
		}
	}

	private static async Task<string?> ResolveAuthTypeAsync(string provider, string? agentDir, CancellationToken cancellationToken)
	{
		var (stored, isCommand) = await AuthStore.ReadCredentialStateAsync(provider, agentDir, cancellationToken).ConfigureAwait(false);
		if (stored is { Length: > 0 } ||
			isCommand ||
			Environment.GetEnvironmentVariable(ProviderEnvironment.KeyName(provider)) is { Length: > 0 })
		{
			return "api_key";
		}

		if (await OAuthStore.ReadAsync(provider, agentDir, cancellationToken).ConfigureAwait(false) is not null ||
			(string.Equals(provider, CodexAuth.ProviderKey, StringComparison.Ordinal) &&
				await CodexAuth.LoadAsync(agentDir, cancellationToken).ConfigureAwait(false) is not null))
		{
			return "oauth";
		}

		return null;
	}

	private static async Task<string?> ResolveCredentialAsync(
		string provider,
		string? agentDir,
		bool noRefresh,
		CancellationToken cancellationToken)
	{
		if (await ResolveAuthTypeAsync(provider, agentDir, cancellationToken).ConfigureAwait(false) == "api_key")
		{
			return await AuthStore.ReadApiKeyAsync(provider, agentDir, cancellationToken).ConfigureAwait(false)
				?? Environment.GetEnvironmentVariable(ProviderEnvironment.KeyName(provider));
		}

		using var http = new HttpClient();
		if (string.Equals(provider, CodexAuth.ProviderKey, StringComparison.Ordinal))
		{
			if (noRefresh && await CodexAuth.LoadAsync(agentDir, cancellationToken).ConfigureAwait(false) is { } stored)
			{
				return stored.Access;
			}

			return (await CodexAuth
				.ResolveAsync(http, agentDir, Environment.GetEnvironmentVariable(AuthBaseUrlVariable), cancellationToken)
				.ConfigureAwait(false))
				.Access;
		}

		var credential = await OAuthStore.ReadAsync(provider, agentDir, cancellationToken).ConfigureAwait(false);
		if (credential is null)
		{
			return null;
		}

		if (!noRefresh && credential.ExpiresAt <= DateTimeOffset.UtcNow && credential.Refresh.Length > 0)
		{
			if (string.Equals(provider, AnthropicOAuth.ProviderKey, StringComparison.Ordinal))
			{
				return (await AnthropicOAuth.RefreshAsync(credential, cancellationToken).ConfigureAwait(false))?.Access
					?? credential.Access;
			}

			if (string.Equals(provider, XaiOAuth.ProviderKey, StringComparison.Ordinal))
			{
				return (await XaiOAuth.RefreshAsync(credential, cancellationToken).ConfigureAwait(false))?.Access
					?? credential.Access;
			}

			if (string.Equals(provider, KimiOAuth.ProviderKey, StringComparison.Ordinal))
			{
				return (await KimiOAuth.RefreshAsync(credential, cancellationToken).ConfigureAwait(false))?.Access
					?? credential.Access;
			}

			if (string.Equals(provider, OpenAiChatGptOAuth.ProviderKey, StringComparison.Ordinal))
			{
				return (await OpenAiChatGptOAuth.RefreshAsync(credential, cancellationToken: cancellationToken).ConfigureAwait(false))?.Access
					?? credential.Access;
			}

			if (string.Equals(provider, GitHubCopilotOAuth.ProviderKey, StringComparison.Ordinal))
			{
				return (await GitHubCopilotOAuth.RefreshAsync(credential, cancellationToken: cancellationToken).ConfigureAwait(false))?.Access
					?? credential.Access;
			}

			if (string.Equals(provider, MetaOAuth.ProviderKey, StringComparison.Ordinal))
			{
				return (await MetaOAuth.RefreshAsync(credential, cancellationToken: cancellationToken).ConfigureAwait(false))?.Access
					?? credential.Access;
			}

			if (string.Equals(provider, RadiusOAuth.ProviderKey, StringComparison.Ordinal))
			{
				return (await RadiusOAuth.RefreshAsync(credential, cancellationToken: cancellationToken).ConfigureAwait(false))?.Access
					?? credential.Access;
			}

			if (string.Equals(provider, OpenRouterOAuth.ProviderKey, StringComparison.Ordinal))
			{
				return credential.Access;
			}
		}

		return credential.Access;
	}


	private static string? ProviderOf(string? model)
	{
		if (model is not { Length: > 0 })
		{
			return null;
		}

		var slash = model.IndexOf('/', StringComparison.Ordinal);
		return slash > 0 ? model[..slash] : model;
	}

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
