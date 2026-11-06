using System.Net.Http;
using System.Text.RegularExpressions;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using PiWeft.Ai.Providers.Bedrock;
using PiWeft.Ai.Providers.Google;
using PiWeft.CodingAgent.Models;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.Desktop;

internal enum AuthLoginKind
{
	BrowserCode,
	BrowserCallback,
	DeviceCode,
	GitHubDevice,
	CodexDevice,
}

internal sealed record AuthProvider(
	string Id,
	string Name,
	string Description,
	AuthLoginKind Kind,
	bool Subscription = true,
	bool Refreshable = true,
	string DocsUrl = "");

/// 从 auth.json 的凭据形状推断该展示、校验哪种 API Key 方式。
/// 状态行和编辑面板共用这套规则，否则会出现「状态按 API Key 校验、面板按 ADC 展示」的分叉。
internal static class ProviderKeySelection
{
	public static ProviderKeyOption Option(ProviderKeyAuth key, AuthStore.StoredApiKey? stored)
	{
		var options = key.Options;
		if (options.Count == 1) return options[0];
		if (stored is null) return options[0];
		if (stored.HasKey) return options.FirstOrDefault(static option => option.HasMainKey) ?? options[0];
		if (!stored.HasEnvironment) return options.FirstOrDefault(static option => option.Ambient) ?? options[0];

		var environment = stored.Environment;
		// Vertex 的 API Key / 访问令牌也可以手工写进 env；此时不需要项目/区域（API key 走 express）。
		// Vertex Express（API key）不需要项目/区域；访问令牌仍按 ADC 方式校验项目/区域。
		if (ProviderEnvironment.EnvironmentValue(environment, "GOOGLE_CLOUD_API_KEY") is not null)
		{
			return options.FirstOrDefault(static option => option.HasMainKey) ?? options[0];
		}

		ProviderKeyOption? best = null;
		var bestMatches = -1;
		foreach (var option in options)
		{
			if (option.EnvironmentFields.Count == 0) continue;
			if (option.EnvironmentFields.Any(item =>
				item.Required && (!environment.TryGetValue(item.Name, out var value) || value.Length == 0))) continue;
			var matches = option.EnvironmentFields.Count(item => environment.ContainsKey(item.Name));
			if (matches <= bestMatches) continue;
			best = option;
			bestMatches = matches;
		}

		// Vertex 的服务账号与 ADC 字段高度重合；凭据文件是服务账号的判据。
		if (environment.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS") &&
			options.FirstOrDefault(static option => option.Id == "service-account") is { } serviceAccount)
		{
			return serviceAccount;
		}

		return best ?? (stored.HasEnvironment
			? options.FirstOrDefault(static option => option.EnvironmentFields.Count > 0) ?? options[0]
			: options[0]);
	}
}

/// 认证页的一行：OAuth 与 API Key 两种凭据合并成同一个供应商视图。
/// auth.json 一个 provider 只存一份凭据，两种状态要一起展示，
/// 否则「OAuth 未登录」与「API Key 已配置」看起来像两个供应商。
internal sealed record ProviderAuthStatus(
	string Id,
	string Name,
	bool BuiltIn,
	AuthProvider? OAuth,
	ProviderKeyAuth? Key,
	string DocsUrl,
	bool OAuthConfigured,
	bool OAuthExpired,
	DateTimeOffset? ExpiresAt,
	string? AccountId,
	string? BaseUrl,
	string? EnterpriseUrl,
	AuthStore.StoredApiKey? StoredKey,
	string? EnvironmentSource,
	bool AmbientConfigured = false,
	string? AmbientLabel = null)
{
	private bool? _keyIncomplete;
	private bool? _ambientMissing;
	private bool? _adcFileMissing;

	public bool KeyStored => StoredKey is not null;

	public bool KeyConfigured => (StoredKey?.IsConfigured == true && !AmbientMissing && !AdcFileMissing) ||
		EnvironmentSource is not null || AmbientConfigured;

	public bool LoggedIn => OAuthConfigured || KeyConfigured;

	/// 能被「退出登录 / 清除凭据」删掉的只有 auth.json 里的条目；进程环境变量不能从界面删除。
	public bool CanForget => OAuthConfigured || KeyStored;

	public bool CanLoginOAuth => OAuth is not null;

	public bool CanRefresh => OAuthConfigured && OAuth is { Refreshable: true };

	public bool CanSetKey => Key is not null;

	/// 已保存/已从环境读取 Key，但缺少该方式所需的 env（例如 Cloudflare 账户/网关），请求仍会失败。
	public bool KeyIncomplete => _keyIncomplete ??=
		(StoredKey is { HasKey: true } || StoredKey is { HasKey: false, HasEnvironment: true } ||
			(StoredKey is null && EnvironmentSource is not null)) &&
		MissingRequiredFields.Length > 0;

	/// Bedrock / Vertex 的 ambient 标记只有在进程环境或默认 ADC 真能提供凭据时才算可用。
	public bool AmbientMissing => _ambientMissing ??= StoredKey is { IsAmbientMarker: true } &&
		((string.Equals(Id, "amazon-bedrock", StringComparison.Ordinal) &&
			!AwsCredentials.FromEnvironment().IsValid) ||
		 (string.Equals(Id, "google-vertex", StringComparison.Ordinal) &&
			!GoogleAdc.HasConfiguredCredentials(stored: null)));

	/// Vertex 的 ADC/服务账号已保存字段，但凭据文件不存在（默认路径也没找到）。
	public bool AdcFileMissing => _adcFileMissing ??=
		string.Equals(Id, "google-vertex", StringComparison.Ordinal) &&
		StoredKey is { HasKey: false, HasEnvironment: true } stored &&
		!GoogleAdc.HasConfiguredCredentials(stored.Environment);

	public bool KeyError => KeyIncomplete || AmbientMissing || AdcFileMissing ||
		StoredKey is { IsTemplate: true, HasUsableKey: false };

	public string OAuthStateText => field ??= BuildOAuthStateText();

	private string BuildOAuthStateText() => !OAuthConfigured
		? Strings.AuthLoggedOut
		: OAuthExpired ? Strings.AuthExpired
			: ExpiresAt is { } expires ? Strings.AuthExpiresAt(expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
			: Strings.AuthNeverExpires;

	public string KeyStateText => field ??= BuildKeyStateText();

	private string BuildKeyStateText()
	{
		if (StoredKey is { IsTemplate: true, HasUsableKey: false }) return Strings.AuthKeyTemplateMissing;
		if (StoredKey is not null && KeyIncomplete)
		{
			return Strings.AuthKeyMissingFields(string.Join(" · ", MissingRequiredFields));
		}
		if (StoredKey is null && EnvironmentSource is { Length: > 0 } source && KeyIncomplete)
		{
			return Strings.AuthKeyMissingEnvironmentFields(source, string.Join(" · ", MissingRequiredFields));
		}
		if (AdcFileMissing) return Strings.AuthKeyAdcFileMissing;
		if (StoredKey is { IsCommand: true }) return Strings.AuthKeyCommand;
		if (StoredKey is { HasKey: true }) return KeyEnvironmentDetail(Strings.AuthKeyStored);
		if (StoredKey is { HasEnvironment: true }) return KeyEnvironmentDetail(Strings.AuthKeyFieldsStored);
		if (StoredKey is { IsAmbientMarker: true })
		{
			return AmbientMissing ? Strings.AuthKeyExternalMissing : Strings.AuthKeyExternal;
		}
		if (EnvironmentSource is { Length: > 0 } variable) return Strings.AuthKeyFromEnv(variable);
		if (AmbientConfigured) return AmbientLabel ?? Strings.AuthKeyExternal;
		return Strings.AuthKeyNotConfigured;
	}

	public string StateText => LoggedIn
		? OAuthConfigured ? OAuthStateText : KeyStateText
		: KeyStateText;

	private ProviderKeyOption? KeyOption => field ??= Key is null ? null : ProviderKeySelection.Option(Key, StoredKey);

	private IReadOnlyDictionary<string, string>? KeyEnvironment =>
		StoredKey is { HasEnvironment: true } stored ? stored.Environment : null;

	private string[] MissingRequiredFields => field ??= KeyOption is null ? [] :
	[
		.. KeyOption.EnvironmentFields
			.Where(item => item.Required && ProviderEnvironment.EnvironmentValue(KeyEnvironment, item.Name) is null)
			.Select(item => item.Label),
	];

	private string KeyEnvironmentDetail(string label) =>
		StoredKey is { HasEnvironment: true } credential
			? label + " · " + string.Join(" · ", credential.Environment.Keys)
			: label;
}

/// 桌面端订阅登录：把 CLI 已有的 OAuth 流程接到界面。
/// 流程实现仍在 <c>PiWeft.Ai.Auth</c>（登录/刷新/写 auth.json），本层只做供应商路由与状态读取。
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

internal static class ProviderAuthService
{
	public static IReadOnlyList<ProviderAuthStatus> Statuses(string agentDir, string? cwd = null)
	{
		// 一次读盘取全量 OAuth 与 API Key 状态：认证页要显示 40+ 个 provider，
		// 逐个 Read 会把同一份 auth.json 读近 100 次（网络盘/慢盘上很可观）。
		var oauth = OAuthStore.ReadAll(agentDir);
		var storedKeys = AuthStore.ReadAllStoredApiKeys(agentDir);
		var codex = LoadCodex(agentDir);
		var builtInIds = ProviderAuthCatalog.BuiltInIds;
		var rows = new List<ProviderAuthStatus>(ProviderAuthCatalog.BuiltIns.Count + 4);

		foreach (var descriptor in ProviderAuthCatalog.BuiltIns)
		{
			var oauthProvider = ProviderAuth.Find(descriptor.Id);
			var isCodex = string.Equals(descriptor.Id, ProviderAuth.CodexProviderId, StringComparison.Ordinal);
			// Codex 的凭据模型更严格（需要 refresh/设备码）；Load 失败时不能按普通 OAuth 显示已登录。
			var credential = isCodex && codex is null ? null : oauth.GetValueOrDefault(descriptor.Id);
			var stored = storedKeys.GetValueOrDefault(descriptor.Id);
			if (isCodex)
			{
				rows.Add(Build(descriptor, true, oauthProvider, credential, codex, null, null));
				continue;
			}

			rows.Add(Build(descriptor, true, oauthProvider, credential, null, stored,
				EnvironmentSource(descriptor.Id, descriptor.ApiKey, stored)));
		}

		// auth.json 里不属于 pi 内置目录的条目（models.json 自定义供应商、手工写入的 provider）
		// 仍然要能编辑/删除：它们不能出现在订阅登录目录里，但仍是用户真实的凭据。
		var customIds = new HashSet<string>(storedKeys.Keys, StringComparer.Ordinal);
		customIds.UnionWith(oauth.Keys);
		customIds.ExceptWith(builtInIds);
		var customNames = customIds.Count == 0
			? new Dictionary<string, string>(StringComparer.Ordinal)
			: CustomProviderNames(agentDir, cwd);
		var customRows = new List<ProviderAuthStatus>();
		foreach (var id in customIds)
		{
			var name = customNames.GetValueOrDefault(id) ?? id;
			var descriptor = new ProviderAuthDescriptor(id, name,
				ProviderAuthCatalog.StandardKey("API Key", ProviderEnvironment.KeyName(id)), DocsUrl: "");
			var stored = storedKeys.GetValueOrDefault(id);
			customRows.Add(Build(descriptor, false, null, oauth.GetValueOrDefault(id), null, stored,
				EnvironmentSource(descriptor.Id, descriptor.ApiKey, stored)));
		}

		return [.. rows, .. customRows.OrderBy(static status => status.Name, StringComparer.OrdinalIgnoreCase)];
	}

	private static ProviderAuthStatus Build(
		ProviderAuthDescriptor descriptor,
		bool builtIn,
		AuthProvider? oauthProvider,
		OAuthCredential? credential,
		CodexCredentials? codex,
		AuthStore.StoredApiKey? stored,
		string? environmentSource)
	{
		// Codex 只认 CodexAuth 的 oauth 设备码凭据；同键 API Key 不参与可用性筛选。
		if (codex is not null)
		{
			var accountId = CodexAuth.GetAccountId(codex.Access);
			return new ProviderAuthStatus(descriptor.Id, oauthProvider?.Name ?? descriptor.Name, builtIn,
				oauthProvider, descriptor.ApiKey, Docs(descriptor, oauthProvider),
				true, codex.Expires == 0 || codex.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1),
				codex.Expires > 0 ? codex.ExpiresAt : null, accountId, null, null, stored, environmentSource);
		}

		// Vertex 的 ADC 默认文件没有环境变量可指向；状态行直接说明来源，不写「未配置」。
		var ambient = environmentSource is null && stored is null &&
			string.Equals(descriptor.Id, "google-vertex", StringComparison.Ordinal) &&
			GoogleAdc.HasConfiguredCredentials(stored: null);
		var expires = credential is { Expires: > 0 } && credential.Expires != long.MaxValue ? credential.ExpiresAt : (DateTimeOffset?)null;
		var expired = credential is { ExpiresAt: var expiry } && expiry > DateTimeOffset.UnixEpoch &&
			expiry <= DateTimeOffset.UtcNow.AddMinutes(1);
		return new ProviderAuthStatus(descriptor.Id, oauthProvider?.Name ?? descriptor.Name, builtIn,
			oauthProvider, descriptor.ApiKey, Docs(descriptor, oauthProvider),
			credential is not null, expired, expires, credential?.AccountId, credential?.BaseUrl,
			credential?.EnterpriseUrl, stored, environmentSource, ambient, ambient ? Strings.AuthKeyAdc : null);
	}

	private static string Docs(ProviderAuthDescriptor descriptor, AuthProvider? oauth) =>
		oauth?.DocsUrl is { Length: > 0 } docs ? docs : descriptor.DocsUrl;

	private static string? EnvironmentSource(string providerId, ProviderKeyAuth? key, AuthStore.StoredApiKey? stored)
	{
		// 存储凭据优先于进程环境；只有完全没有存储条目时才报告环境变量来源。
		if (key is null || stored is not null) return null;
		if (string.Equals(providerId, "amazon-bedrock", StringComparison.Ordinal))
		{
			// Bedrock 需要 access key 与 secret 同时存在；只有其中一个时不能算已配置。
			return AwsCredentials.FromEnvironment().IsValid ? "AWS_ACCESS_KEY_ID" : null;
		}
		foreach (var name in key.EnvironmentNames)
		{
			if (Environment.GetEnvironmentVariable(name) is not { Length: > 0 }) continue;
			// Vertex 访问令牌仍需要项目/区域；只有 API Key 的 express 模式可以独立使用。
			if (string.Equals(providerId, "google-vertex", StringComparison.Ordinal) &&
				string.Equals(name, GoogleAdc.AccessTokenVariable, StringComparison.Ordinal) &&
				!GoogleAdc.HasConfiguredCredentials(stored: null))
			{
				return null;
			}

			return name;
		}

		return null;
	}

	private static IReadOnlyDictionary<string, string> CustomProviderNames(string agentDir, string? cwd)
	{
		var names = new Dictionary<string, string>(StringComparer.Ordinal);
		if (string.IsNullOrEmpty(cwd)) return names;
		try
		{
			var applied = ModelsFileLoader.Load(cwd, agentDir);
			foreach (var (id, entry) in applied.ProviderEntries)
			{
				if (entry.Name is { Length: > 0 } name) names[id] = name;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			// 读取自定义供应商名称是装饰信息；models.json 损坏时仍应显示 auth.json 条目。
		}

		return names;
	}

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

	/// 保存 API Key 凭据；`overwriteOAuth` 必须由界面在用户明确确认后传入。
	public static void SaveKey(
		string providerId,
		string? key,
		IReadOnlyDictionary<string, string>? environment,
		string agentDir,
		bool overwriteOAuth = false) =>
		AuthStore.SaveApiKeyCredential(providerId, key, environment, agentDir, overwriteOAuth);

	public static bool HasCredential(string providerId, string agentDir) =>
		string.Equals(providerId, ProviderAuth.CodexProviderId, StringComparison.Ordinal)
			? LoadCodex(agentDir) is not null
			: AuthStore.ReadStoredApiKey(providerId, agentDir) is not null ||
				OAuthStore.Read(providerId, agentDir) is not null;

	public static bool HasStoredKey(string providerId, string agentDir) =>
		AuthStore.ReadStoredApiKey(providerId, agentDir) is not null;

	public static bool Logout(string providerId, string agentDir) =>
		AuthStore.RemoveCredential(providerId, agentDir);

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

internal static partial class AuthFlowParser
{
	[GeneratedRegex(@"在\s+(?<url>https?://\S+?)\s+输入代码\s+(?<code>[A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
	private static partial Regex DevicePattern();

	[GeneratedRegex(@"(?<url>https?://[^\s""'<>]+)", RegexOptions.CultureInvariant)]
	private static partial Regex UrlPattern();

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
