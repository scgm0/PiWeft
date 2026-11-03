using System.Net.Http;
using System.Text.RegularExpressions;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using PiWeft.CodingAgent.Models;

namespace PiWeft.Desktop;

/// <summary>登录交互形态：决定登录面板如何展示授权入口与输入。</summary>
internal enum AuthLoginKind
{
	/// <summary>浏览器授权 + 手工粘贴授权码/回调 URL（Anthropic、OpenRouter）。</summary>
	BrowserCode,
	/// <summary>浏览器授权 + 本机回环回调（ChatGPT）。</summary>
	BrowserCallback,
	/// <summary>设备码：展示 verification URL 与 user code 后轮询（xAI、Kimi、Meta、Radius）。</summary>
	DeviceCode,
	/// <summary>GitHub 设备码：先问企业域名，再展示设备码。</summary>
	GitHubDevice,
	/// <summary>OpenAI Codex 设备码（凭据由 CodexAuth 写入同一个 auth.json）。</summary>
	CodexDevice,
}

/// <summary>一个可登录的订阅/OAuth 供应商（内置目录；自定义供应商仍走 API Key 表单）。</summary>
internal sealed record AuthProvider(
	string Id,
	string Name,
	string Description,
	AuthLoginKind Kind,
	bool Subscription = true,
	bool Refreshable = true,
	string DocsUrl = "");

/// <summary>登录凭据的展示状态；只含到期时间/账号等非敏感字段。</summary>
internal sealed record ProviderAuthStatus(
	AuthProvider Provider,
	bool LoggedIn,
	bool Expired,
	DateTimeOffset? ExpiresAt,
	string? AccountId,
	string? BaseUrl,
	string? EnterpriseUrl,
	bool ApiKeyConfigured = false)
{
	public string StateText => LoggedIn
		? Expired ? Strings.AuthExpired
			: ExpiresAt is { } expires ? Strings.AuthExpiresAt(expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
			: Strings.AuthNeverExpires
		: ApiKeyConfigured ? Strings.AuthApiKeyConfigured : Strings.AuthLoggedOut;
}

/// <summary>
/// 桌面端订阅登录：把 CLI 已有的 OAuth 流程接到界面。
/// 流程实现仍在 <c>PiWeft.Ai.Auth</c>（登录/刷新/写 auth.json），本层只做供应商路由与状态读取。
/// </summary>
internal static class ProviderAuth
{
	public const string CodexProviderId = "openai-codex";

	public static readonly IReadOnlyList<AuthProvider> Providers =
	[
		new("anthropic", "Anthropic", "Claude 订阅登录（浏览器授权码）", AuthLoginKind.BrowserCode,
			DocsUrl: "https://pi.dev/docs/latest/providers#anthropic"),
		new("openai", "OpenAI", "ChatGPT 订阅登录（浏览器回调）", AuthLoginKind.BrowserCallback,
			DocsUrl: "https://pi.dev/docs/latest/providers#openai"),
		new(CodexProviderId, "OpenAI Codex", "ChatGPT Codex 设备码登录", AuthLoginKind.CodexDevice,
			DocsUrl: "https://pi.dev/docs/latest/providers#openai-codex"),
		new("github-copilot", "GitHub Copilot", "GitHub 设备码登录（可选企业域名）", AuthLoginKind.GitHubDevice,
			DocsUrl: "https://pi.dev/docs/latest/providers#github-copilot"),
		new("xai", "xAI (Grok)", "Grok 订阅设备码登录", AuthLoginKind.DeviceCode,
			DocsUrl: "https://pi.dev/docs/latest/providers#xai"),
		new("kimi-coding", "Kimi For Coding", "Kimi Code 订阅设备码登录", AuthLoginKind.DeviceCode,
			DocsUrl: "https://pi.dev/docs/latest/providers#kimi-for-coding"),
		new("meta", "Meta (Muse)", "Meta Muse 设备码登录", AuthLoginKind.DeviceCode,
			DocsUrl: "https://pi.dev/docs/latest/providers#meta-muse-subscription"),
		new("radius", "Radius", "Radius 设备码登录", AuthLoginKind.DeviceCode,
			DocsUrl: "https://pi.dev/docs/latest/providers#radius"),
		new("openrouter", "OpenRouter", "浏览器登录换取 OpenRouter API Key", AuthLoginKind.BrowserCode,
			Subscription: false, Refreshable: false, DocsUrl: "https://pi.dev/docs/latest/providers#openrouter"),
	];

	public static AuthProvider? Find(string id) =>
		Providers.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.Ordinal));
}

/// <summary>订阅登录的执行层：分发到现有 OAuth 类，并处理 Codex 设备码与账号目录刷新。</summary>
internal static class ProviderAuthService
{
	/// <summary>读取每个内置 OAuth 供应商的凭据状态（只读，不含 token）。</summary>
	public static IReadOnlyList<ProviderAuthStatus> Statuses(string agentDir)
	{
		// 一次读盘取全量 OAuth 与 API Key 状态：认证页要显示 9 个 provider，
		// 逐个 Read 会把同一份 auth.json 读近 20 次（网络盘/慢盘上很可观）。
		var oauth = OAuthStore.ReadAll(agentDir);
		var configuredApiKeys = AuthStore.ConfiguredProviders(agentDir);
		var statuses = new List<ProviderAuthStatus>(ProviderAuth.Providers.Count);
		foreach (var provider in ProviderAuth.Providers)
		{
			var hasApiKey = configuredApiKeys.Contains(provider.Id) ||
				!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProviderEnvironment.KeyName(provider.Id)));
			if (provider.Kind == AuthLoginKind.CodexDevice)
			{
				var credentials = LoadCodex(agentDir);
				statuses.Add(new ProviderAuthStatus(provider, credentials is not null,
					credentials is { } codex && (codex.Expires == 0 || codex.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)),
					credentials is { Expires: > 0 } ? credentials.ExpiresAt : null,
					credentials is { } loaded ? CodexAuth.GetAccountId(loaded.Access) : null,
					// Codex 只认 CodexAuth 的 oauth 设备码凭据；同键 API Key 不参与可用性筛选。
					null, null, false));
				continue;
			}

			var credential = oauth.GetValueOrDefault(provider.Id);
			statuses.Add(credential is null
				? new ProviderAuthStatus(provider, false, false, null, null, null, null, hasApiKey)
				: new ProviderAuthStatus(provider, true,
					credential.ExpiresAt > DateTimeOffset.UnixEpoch && credential.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1),
					credential.ExpiresAt > DateTimeOffset.UnixEpoch && credential.Expires != long.MaxValue ? credential.ExpiresAt : null,
					credential.AccountId, credential.BaseUrl, credential.EnterpriseUrl));
		}
		return statuses;
	}

	/// <summary>执行一次登录；成功时凭据已由底层写入 auth.json。</summary>
	public static async Task LoginAsync(AuthProvider provider, string agentDir, TextReader input, TextWriter output,
		CancellationToken cancellationToken)
	{
		switch (provider.Kind)
		{
			case AuthLoginKind.BrowserCode when provider.Id == AnthropicOAuth.ProviderKey:
				await AnthropicOAuth.LoginAsync(input, output, cancellationToken).ConfigureAwait(false);
				break;
			case AuthLoginKind.BrowserCode when provider.Id == OpenRouterOAuth.ProviderKey:
				await OpenRouterOAuth.LoginAsync(input, output, agentDir, cancellationToken).ConfigureAwait(false);
				break;
			case AuthLoginKind.BrowserCallback:
				await OpenAiChatGptOAuth.LoginAsync(input, output, agentDir, cancellationToken).ConfigureAwait(false);
				break;
			case AuthLoginKind.GitHubDevice:
				await GitHubCopilotOAuth.LoginAsync(input, output, agentDir, cancellationToken).ConfigureAwait(false);
				break;
			case AuthLoginKind.CodexDevice:
				await LoginCodexAsync(agentDir, output, cancellationToken).ConfigureAwait(false);
				break;
			case AuthLoginKind.DeviceCode when provider.Id == XaiOAuth.ProviderKey:
				await XaiOAuth.LoginAsync(output, cancellationToken).ConfigureAwait(false);
				break;
			case AuthLoginKind.DeviceCode when provider.Id == KimiOAuth.ProviderKey:
				await KimiOAuth.LoginAsync(output, cancellationToken).ConfigureAwait(false);
				break;
			case AuthLoginKind.DeviceCode when provider.Id == MetaOAuth.ProviderKey:
				await MetaOAuth.LoginAsync(output, agentDir, cancellationToken).ConfigureAwait(false);
				break;
			case AuthLoginKind.DeviceCode when provider.Id == RadiusOAuth.ProviderKey:
				await RadiusOAuth.LoginAsync(output, agentDir, cancellationToken).ConfigureAwait(false);
				break;
			default:
				throw new CodexAuthException(Strings.AuthUnsupportedLogin(provider.Id));
		}

		// 账号级目录（OpenRouter / Copilot / Radius）登录后立刻刷新；失败只提示，不让登录本身失败。
		if (provider.Id is "openrouter" or "github-copilot" or "radius")
		{
			try
			{
				await ModelCatalogStore.RefreshAccountCatalogAsync(provider.Id, agentDir, cancellationToken: cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// 凭据已经写盘：目录刷新失败只提示，不让登录本身失败。
				output.WriteLine(Strings.AuthCatalogRefreshFailed(exception.Message));
			}
		}
	}

	/// <summary>该供应商是否有可被订阅卡片管理的 OAuth 凭据（用于 auth.json 列表去重）。</summary>
	public static bool HasCredential(string providerId, string agentDir) =>
		string.Equals(providerId, ProviderAuth.CodexProviderId, StringComparison.Ordinal)
			? LoadCodex(agentDir) is not null
			: OAuthStore.Read(providerId, agentDir) is not null;

	/// <summary>删除一个供应商的 OAuth 凭据（API Key 条目同键时也一并删除）。返回是否有条目被移除。</summary>
	public static bool Logout(string providerId, string agentDir) => OAuthStore.RemoveProvider(providerId, agentDir);

	/// <summary>强制刷新一次 OAuth 凭据；OpenRouter 的 key 不过期，不需要刷新。</summary>
	public static async Task<bool> RefreshAsync(AuthProvider provider, string agentDir, CancellationToken cancellationToken)
	{
		if (provider.Kind == AuthLoginKind.CodexDevice)
		{
			var stored = LoadCodex(agentDir);
			if (stored is null) return false;
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			var tokens = await CodexAuth.RefreshAsync(http, stored.Refresh,
				Environment.GetEnvironmentVariable("PI_CODEX_AUTH_BASE_URL"), cancellationToken).ConfigureAwait(false);
			CodexAuth.Save(CodexCredentials.FromTokens(tokens), agentDir);
			return true;
		}

		var credential = OAuthStore.Read(provider.Id, agentDir);
		if (credential is null) return false;
		OAuthCredential? refreshed = provider.Id switch
		{
			"anthropic" => await AnthropicOAuth.RefreshAsync(credential, cancellationToken).ConfigureAwait(false),
			"openai" => await OpenAiChatGptOAuth.RefreshAsync(credential, agentDir, cancellationToken).ConfigureAwait(false),
			"github-copilot" => await GitHubCopilotOAuth.RefreshAsync(credential, agentDir, cancellationToken).ConfigureAwait(false),
			"xai" => await XaiOAuth.RefreshAsync(credential, cancellationToken).ConfigureAwait(false),
			"kimi-coding" => await KimiOAuth.RefreshAsync(credential, cancellationToken).ConfigureAwait(false),
			"meta" => await MetaOAuth.RefreshAsync(credential, agentDir, cancellationToken).ConfigureAwait(false),
			"radius" => await RadiusOAuth.RefreshAsync(credential, agentDir, cancellationToken).ConfigureAwait(false),
			_ => null,
		};
		return refreshed is not null;
	}

	private static async Task LoginCodexAsync(string agentDir, TextWriter output, CancellationToken cancellationToken)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		// 与 CLI 共用测试/自建网关覆盖变量。
		var authBaseUrl = Environment.GetEnvironmentVariable("PI_CODEX_AUTH_BASE_URL");
		var device = await CodexAuth.StartDeviceAsync(http, authBaseUrl, cancellationToken).ConfigureAwait(false);
		// 与 OAuth 类保持同一行格式：登录面板据此提取授权页与验证码。
		output.WriteLine($"在 {device.VerificationUri} 输入代码 {device.UserCode}");
		var tokens = await CodexAuth.PollDeviceAsync(http, device, authBaseUrl, cancellationToken: cancellationToken).ConfigureAwait(false);
		CodexAuth.Save(CodexCredentials.FromTokens(tokens), agentDir);
		output.WriteLine("OpenAI Codex 登录成功，凭据已写入 auth.json。");
	}

	private static CodexCredentials? LoadCodex(string agentDir)
	{
		try { return CodexAuth.Load(agentDir); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
		{
			return null;
		}
	}
}

/// <summary>从 OAuth 流程的输出里提取授权页与设备码（只做展示，不参与认证逻辑）。</summary>
internal static partial class AuthFlowParser
{
	[GeneratedRegex(@"在\s+(?<url>https?://\S+?)\s+输入代码\s+(?<code>[A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
	private static partial Regex DevicePattern();

	[GeneratedRegex(@"(?<url>https?://[^\s""'<>]+)", RegexOptions.CultureInvariant)]
	private static partial Regex UrlPattern();

	/// <summary>返回日志中第一次出现的授权 URL 与设备码；没有则为 null。</summary>
	public static (string? Url, string? Code) Parse(string log)
	{
		if (string.IsNullOrEmpty(log)) return (null, null);
		var device = DevicePattern().Match(log);
		if (device.Success) return (Trim(device.Groups["url"].Value), device.Groups["code"].Value);
		var url = UrlPattern().Match(log);
		return (url.Success ? Trim(url.Groups["url"].Value) : null, null);
	}

	private static string Trim(string url) => url.TrimEnd('.', ',', '，', '。', ')', '）', ';', '；');
}
