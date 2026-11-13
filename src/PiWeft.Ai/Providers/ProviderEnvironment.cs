using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.Ai.Providers;

internal static class SharedHttpClient
{
	public static readonly string UserAgent = "PiWeft/0.1.0";

	public static HttpClient Instance { get; } = new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		AutomaticDecompression = DecompressionMethods.All,
		ConnectTimeout = TimeSpan.FromSeconds(30),
	})
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};
}

internal static class ProviderHttp
{
	/// 读取响应体，按 <see cref="StreamOptions.IdleTimeoutMs"/> 包一层空闲超时。
	/// provider 的流式读取统一走这里：直接 <c>ReadAsStreamAsync</c>，供应商挂住连接时整轮会无限等待。
	public static async Task<Stream> ReadBodyAsync(
		HttpResponseMessage response,
		StreamOptions options,
		CancellationToken cancellationToken)
	{
		var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		return options.IdleTimeoutMs is > 0 and var timeout
			? new IdleTimeoutStream(stream, TimeSpan.FromMilliseconds(timeout), options.CancellationToken)
			: stream;
	}

	/// 错误响应体的读取上限。provider 只用前 2000 字符写错误文案，但网关可能返回整页 HTML
	/// 甚至几十 MB 内容；不设上限，一次失败请求就会吃掉一大块内存，而且还在重试循环里。
	private const int MaxErrorBodyBytes = 1 << 20;

	/// 读取错误响应体文本，同样受 <see cref="StreamOptions.IdleTimeoutMs"/> 约束。
	/// 网关也会挂住错误体；只给成功路径设超时，就把挂死风险留给了最常见的失败分支。
	public static async Task<string> ReadBodyTextAsync(
		HttpResponseMessage response,
		StreamOptions options,
		CancellationToken cancellationToken)
	{
		Stream raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		if (options.IdleTimeoutMs is > 0 and var idle)
		{
			raw = new IdleTimeoutStream(raw, TimeSpan.FromMilliseconds(idle), options.CancellationToken);
		}

		await using var stream = raw;
		using var buffer = new MemoryStream();
		var chunk = new byte[16 * 1024];
		while (buffer.Length < MaxErrorBodyBytes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var count = await stream
				.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxErrorBodyBytes - (int)buffer.Length)), cancellationToken)
				.ConfigureAwait(false);
			if (count == 0)
			{
				break;
			}

			buffer.Write(chunk, 0, count);
		}

		var length = (int)buffer.Length;
		var text = Decode(
			new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, length),
			response.Content.Headers.ContentType?.CharSet);
		return length >= MaxErrorBodyBytes ? text + "…（响应体已截断）" : text;
	}

	private static string Decode(ReadOnlyMemory<byte> bytes, string? charset)
	{
		if (string.IsNullOrWhiteSpace(charset))
		{
			return Encoding.UTF8.GetString(bytes.Span);
		}

		try
		{
			return Encoding.GetEncoding(charset.Trim('"', '\'')).GetString(bytes.Span);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return Encoding.UTF8.GetString(bytes.Span);
		}
	}

	public static ProviderResponse FromResponse(HttpResponseMessage response)
	{
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (name, values) in response.Headers)
		{
			headers[name] = string.Join(", ", values);
		}

		foreach (var (name, values) in response.Content.Headers)
		{
			headers[name] = string.Join(", ", values);
		}

		return new ProviderResponse { Status = (int)response.StatusCode, Headers = headers };
	}

	public static string TimeoutMessage(HeaderTimeoutScope scope) =>
		scope.TimedOut ? "Request timed out while waiting for response headers" : "Request timed out";

	/// 开始一段「等响应头」的超时。返回的作用域合并调用方的取消与超时，发送请求时使用；
	/// 正文读取一律用 <c>options.CancellationToken</c>。
	/// 计时从第一次读 <see cref="HeaderTimeoutScope.Token"/>（发请求）开始：
	/// 凭据解析、OAuth 刷新与命令型凭据不占请求超时，失败的解析也不会白建计时器。
	public static HeaderTimeoutScope BeginHeaderTimeout(StreamOptions options)
	{
		// 等响应头也靠空闲超时兜底：连接建立后服务端不回头，这一轮就无限等下去
		// （HttpClient 是 Infinite，正文计时器还没建立）。显式请求超时优先。
		var timeoutMs = options.TimeoutMs is > 0 ? options.TimeoutMs : options.IdleTimeoutMs;
		return new HeaderTimeoutScope(options.CancellationToken, timeoutMs);
	}
}

/// 「等待响应头」的超时作用域。pi 1.2.0 起超时只覆盖这一步：长流（长时间思考、慢生成）
/// 不能被固定时限切断，正文停滞交给 HTTP 客户端处理。
public sealed class HeaderTimeoutScope : IDisposable
{
	private readonly CancellationToken _callerToken;
	private readonly int? _timeoutMs;
	private CancellationTokenSource? _timeout;
	private CancellationTokenSource? _linked;
	private CancellationToken _token;
	private bool _started;
	private bool _disposed;

	internal HeaderTimeoutScope(CancellationToken callerToken, int? timeoutMs)
	{
		_callerToken = callerToken;
		_timeoutMs = timeoutMs is > 0 ? timeoutMs : null;
	}

	public CancellationToken Token
	{
		get
		{
			if (_disposed)
			{
				return _callerToken;
			}

			if (!_started)
			{
				_started = true;
				if (_timeoutMs is { } value)
				{
					_timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(value));
					_linked = CancellationTokenSource.CreateLinkedTokenSource(_callerToken, _timeout.Token);
					_token = _linked.Token;
				}
				else
				{
					_token = _callerToken;
				}
			}

			return _token;
		}
	}

	public bool TimedOut => _timeout is { IsCancellationRequested: true };

	/// 响应头已拿到：停止计时并释放定时器，长流期间不再背一个无用的 CTS，
	/// 正文超时也不会被误报成「等响应头超时」。
	public void Stop()
	{
		if (_disposed)
		{
			return;
		}

		_started = true;
		_token = _callerToken;
		_linked?.Dispose();
		_linked = null;
		_timeout?.Dispose();
		_timeout = null;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		Stop();
		_disposed = true;
	}
}

public static partial class ProviderEnvironment
{
	private static readonly FrozenDictionary<string, string> Overrides =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["huggingface"] = "HF_TOKEN",
			["moonshotai"] = "MOONSHOT_API_KEY",
			["moonshotai-cn"] = "MOONSHOT_API_KEY",
			["zai"] = "ZAI_API_KEY",
			["zai-coding-cn"] = "ZAI_CODING_CN_API_KEY",
			["xai"] = "XAI_API_KEY",
			["opencode"] = "OPENCODE_API_KEY",
			["opencode-go"] = "OPENCODE_API_KEY",
			["kimi-coding"] = "KIMI_API_KEY",
			["github-copilot"] = "COPILOT_GITHUB_TOKEN",
			["azure"] = "AZURE_OPENAI_API_KEY",
			["google"] = "GEMINI_API_KEY",
			["google-vertex"] = "GOOGLE_CLOUD_API_KEY",
			["vercel-ai-gateway"] = "AI_GATEWAY_API_KEY",
			["cloudflare-workers-ai"] = "CLOUDFLARE_API_KEY",
			["cloudflare-ai-gateway"] = "CLOUDFLARE_API_KEY",
		}.ToFrozenDictionary(StringComparer.Ordinal);

	private static readonly ConcurrentDictionary<string, string> KeyNames = new(StringComparer.Ordinal);

	/// 进程环境中该 provider 的 API Key；主变量优先，再查兼容旧变量名。
	public static string? ResolveEnvironmentKey(string provider)
	{
		// pi 1.5.2+：Qwen Token Plan 拆成 Team / Individual 各自的变量，且新变量优先于旧变量。
		// 必须放在 KeyName 之前：旧变量是 KeyName 推导出的主名字，先查主名字会让它反超新变量。
		var splitPlanKey = provider switch
		{
			"qwen-token-plan" => Environment.GetEnvironmentVariable("QWEN_TOKEN_PLAN_TEAM_API_KEY") ??
				Environment.GetEnvironmentVariable("QWEN_TOKEN_PLAN_API_KEY"),
			"qwen-token-plan-cn" => Environment.GetEnvironmentVariable("QWEN_TOKEN_PLAN_TEAM_CN_API_KEY") ??
				Environment.GetEnvironmentVariable("QWEN_TOKEN_PLAN_CN_API_KEY"),
			"qwen-token-plan-individual" => Environment.GetEnvironmentVariable("QWEN_TOKEN_PLAN_INDIVIDUAL_API_KEY") ??
				Environment.GetEnvironmentVariable("QWEN_TOKEN_PLAN_API_KEY"),
			"qwen-token-plan-individual-cn" => Environment.GetEnvironmentVariable("QWEN_TOKEN_PLAN_INDIVIDUAL_CN_API_KEY") ??
				Environment.GetEnvironmentVariable("QWEN_TOKEN_PLAN_CN_API_KEY"),
			_ => null,
		};
		if (!string.IsNullOrEmpty(splitPlanKey)) return splitPlanKey;

		var primary = Environment.GetEnvironmentVariable(KeyName(provider));
		if (!string.IsNullOrEmpty(primary)) return primary;
		return provider switch
		{
			"azure" => Environment.GetEnvironmentVariable("AZURE_API_KEY"),
			"zai-coding-cn" => Environment.GetEnvironmentVariable("ZAI_API_KEY"),
			_ => null,
		};
	}

	public static string KeyName(string provider) => KeyNames.GetOrAdd(provider, static name =>
	{
		if (Overrides.TryGetValue(name, out var overridden))
		{
			return overridden;
		}

		var builder = new StringBuilder(name.Length + 8);
		foreach (var c in name)
		{
			builder.Append(c is '-' or '.' ? '_' : char.ToUpperInvariant(c));
		}

		builder.Append("_API_KEY");
		return builder.ToString();
	});

	/// 每个 provider 一把刷新锁：并发发现 token 过期时只让一个去换，
	/// 其余等它写回 auth.json 后复用新令牌。否则多个线程拿同一个 refresh token 竞争，
	/// 在轮换 refresh token 的供应商上会互相作废凭据。
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> OAuthRefreshGates = new(StringComparer.Ordinal);

	private static Task<Auth.OAuthCredential?> RefreshProviderAsync(
		string provider,
		Auth.OAuthCredential credential,
		CancellationToken cancellationToken) =>
		provider switch
		{
			Auth.AnthropicOAuth.ProviderKey => Auth.AnthropicOAuth.RefreshAsync(credential, cancellationToken),
			Auth.XaiOAuth.ProviderKey => Auth.XaiOAuth.RefreshAsync(credential, cancellationToken),
			Auth.KimiOAuth.ProviderKey => Auth.KimiOAuth.RefreshAsync(credential, cancellationToken),
			Auth.OpenAiChatGptOAuth.ProviderKey => Auth.OpenAiChatGptOAuth.RefreshAsync(credential, cancellationToken: cancellationToken),
			Auth.GitHubCopilotOAuth.ProviderKey => Auth.GitHubCopilotOAuth.RefreshAsync(credential, cancellationToken: cancellationToken),
			Auth.MetaOAuth.ProviderKey => Auth.MetaOAuth.RefreshAsync(credential, cancellationToken: cancellationToken),
			Auth.RadiusOAuth.ProviderKey => Auth.RadiusOAuth.RefreshAsync(credential, cancellationToken: cancellationToken),
			_ => Task.FromResult<Auth.OAuthCredential?>(null),
		};

	private static async Task<Auth.OAuthCredential?> RefreshOAuthAsync(
		string provider,
		Auth.OAuthCredential credential,
		CancellationToken cancellationToken)
	{
		var gate = OAuthRefreshGates.GetOrAdd(provider, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			// 等锁期间可能已被别的请求刷新：重新读盘，未过期就直接用。
			var current = await Auth.OAuthStore
				.ReadAsync(provider, agentDir: null, cancellationToken)
				.ConfigureAwait(false);
			if (current is { } refreshed && refreshed.ExpiresAt > DateTimeOffset.UtcNow)
			{
				return refreshed;
			}

			var toRefresh = current ?? credential;
			// 刷新开始后不跟随请求取消：供应商可能已轮换 refresh token，中途取消会丢掉新令牌。
			// 网络等待由各家 HttpClient 的 30 秒超时兜底。
			return await RefreshProviderAsync(provider, toRefresh, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or Auth.CodexAuthException)
		{
			return null;
		}
		finally
		{
			gate.Release();
		}
	}

	[GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
	private static partial Regex EnvironmentTemplate();

	private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// provider 级 env（auth.json 的 <c>api_key.env</c>）。Cloudflare 的账户/网关 ID、
	/// Vertex 的项目/区域与 AWS 访问密钥都存在这里；请求时优先于进程环境。
	public static IReadOnlyDictionary<string, string> StoredEnvironment(string provider, string? agentDir = null) =>
		Auth.AuthStore.ReadStoredApiKey(provider, agentDir)?.Environment ?? EmptyEnvironment;

	public static string? EnvironmentValue(string provider, string name) =>
		EnvironmentValue(StoredEnvironment(provider), name);

	public static string? EnvironmentValue(IReadOnlyDictionary<string, string>? stored, string name)
	{
		if (stored is not null && stored.TryGetValue(name, out var storedValue) && storedValue.Length > 0)
		{
			return storedValue;
		}

		return Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromProcess ? fromProcess : null;
	}

	/// 展开模型目录里的 <c>{ENV_NAME}</c>（Cloudflare 账户/网关 ID 等）。
	/// 缺值直接报错：留着花括号会请求一个必然 404 的地址，更难排查。
	public static string ExpandBaseUrl(string provider, string baseUrl)
	{
		if (!baseUrl.Contains('{', StringComparison.Ordinal)) return baseUrl;
		var stored = StoredEnvironment(provider);
		return EnvironmentTemplate().Replace(baseUrl, match =>
		{
			var name = match.Groups[1].Value;
			return EnvironmentValue(stored, name)
				?? throw new InvalidOperationException($"Missing environment variable {name} for provider {provider}");
		});
	}

	/// 顺序与 pi 一致：显式选项 → <c>auth.json</c> 凭据 → provider 环境变量 → OpenAI 兼容网关的兜底变量。
	/// 存储排在环境变量前：<c>pi login</c> 与桌面端都写 <c>auth.json</c>，只认环境变量会让这些用户收到 401。
	/// 读盘、命令型凭据与 OAuth 刷新全走异步；等刷新闸门时响应取消，刷新本身不跟随取消，
	/// 否则轮换型 refresh token 会丢。
	public static async Task<string?> ResolveApiKeyAsync(
		string provider,
		StreamOptions options,
		CancellationToken cancellationToken = default)
	{
		if (!cancellationToken.CanBeCanceled)
		{
			cancellationToken = options.CancellationToken;
		}

		cancellationToken.ThrowIfCancellationRequested();

		if (!string.IsNullOrEmpty(options.ApiKey))
		{
			return options.ApiKey;
		}

		var root = await Auth.AuthStore
			.ReadRootAsync(agentDir: null, cancellationToken)
			.ConfigureAwait(false);
		var stored = await Auth.AuthStore
			.ReadApiKeyFromRootAsync(root, provider, cancellationToken)
			.ConfigureAwait(false);
		if (stored is { Length: > 0 })
		{
			return stored;
		}

		// OAuth 凭据以 access token 复用 API key 通道，AnthropicProvider 按 sk-ant-oat 前缀切 Bearer。
		// 与 API Key 共用一次读盘/解析，auth.json 只读一遍。
		var oauth = Auth.OAuthStore.ReadCredential(root?[provider]);
		if (oauth is { Access.Length: > 0 })
		{
			if (oauth.ExpiresAt <= DateTimeOffset.UtcNow && oauth.Refresh.Length > 0)
			{
				var refreshed = await RefreshOAuthAsync(provider, oauth, cancellationToken).ConfigureAwait(false);
				if (refreshed is { Access.Length: > 0 } updated)
				{
					return updated.Access;
				}
			}

			return oauth.Access;
		}

		return ResolveEnvironmentKey(provider);
	}
}
