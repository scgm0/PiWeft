using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using System.Text;

namespace PiWeft.Ai.Providers;

/// <summary>provider 未自行提供时使用的共享 HTTP 客户端。连接池在进程内共用。</summary>
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

/// <summary>各 provider 适配器共用的 HTTP 辅助方法。</summary>
internal static class ProviderHttp
{
	/// <summary>
	/// 读取响应体，并按 <see cref="StreamOptions.IdleTimeoutMs"/> 包一层空闲超时。
	/// 所有 provider 的流式读取都必须走这里：直接 <c>ReadAsStreamAsync</c> 的话，
	/// 供应商把连接挂着不发也不关时这一轮会永远等下去（HttpClient 是 Infinite 超时）。
	/// </summary>
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

	/// <summary>
	/// 错误响应体的读取上限。各 provider 只把前 2000 字符写进错误文案，但网关偶尔会返回
	/// 整页 HTML 甚至几十 MB 的诊断内容：不设上限就等于「一次失败的请求先吃掉一大块内存」，
	/// 而且这笔分配还在自动重试的循环里。
	/// </summary>
	private const int MaxErrorBodyBytes = 1 << 20;

	/// <summary>
	/// 读取响应体文本（错误响应说明等），同样受 <see cref="StreamOptions.IdleTimeoutMs"/> 约束。
	/// 错误体也可能被网关挂着不发：只给成功路径设空闲超时，等于把挂死风险留给最常见的失败分支。
	/// </summary>
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

	/// <summary>按响应声明的字符集解码；字符集缺失或不认识时按 UTF-8（与 pi 的行为一致）。</summary>
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

	/// <summary>为 <see cref="ProviderRequestOptions.OnResponse"/> 抓取响应的状态行与头部快照。</summary>
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

	/// <summary>超时错误的文案：区分「等响应头超时」与其它取消。</summary>
	public static string TimeoutMessage(HeaderTimeoutScope scope) =>
		scope.TimedOut ? "Request timed out while waiting for response headers" : "Request timed out";

	/// <summary>
	/// 开始一段「等响应头」的超时。返回的作用域把调用方的取消与超时合并，供发送请求时使用；
	/// 正文读取请一律使用 <c>options.CancellationToken</c>。
	/// <para>
	/// 计时从第一次读取 <see cref="HeaderTimeoutScope.Token"/>（真正发请求）时才开始：
	/// 凭据解析、OAuth 刷新与命令型凭据不消耗请求超时，也避免为失败的解析白建计时器。
	/// </para>
	/// </summary>
	public static HeaderTimeoutScope BeginHeaderTimeout(StreamOptions options)
	{
		// 等响应头也受空闲超时兜底：连接建立了但服务端一直不回头，否则这一轮会无限等下去
		// （HttpClient 是 Infinite，正文计时器此时还没建立）。显式的请求超时优先。
		var timeoutMs = options.TimeoutMs is > 0 ? options.TimeoutMs : options.IdleTimeoutMs;
		return new HeaderTimeoutScope(options.CancellationToken, timeoutMs);
	}
}

/// <summary>
/// 「等待响应头」的超时作用域。pi 1.2.0 起超时只覆盖等响应头这一步：长流（长时间思考、慢生成）
/// 不能被固定时限切断，正文停滞交给 HTTP 客户端自己处理。
/// </summary>
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

	/// <summary>传给发送请求那一步的令牌；第一次读取时才启动计时器。</summary>
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

	/// <summary>是否是我们自己的超时先触发（而不是调用方取消）。</summary>
	public bool TimedOut => _timeout is { IsCancellationRequested: true };

	/// <summary>
	/// 响应头已经拿到：停止计时并释放定时器。长流期间不再背着一个无用的 CTS，
	/// 后续正文超时也不会被误报成「等响应头超时」。
	/// </summary>
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

/// <summary>从环境变量解析 provider 凭据，对应 pi 的 <c>env-api-keys</c>。</summary>
public static class ProviderEnvironment
{
	private static readonly FrozenDictionary<string, string> Overrides =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["huggingface"] = "HF_TOKEN",
			["moonshotai"] = "MOONSHOT_API_KEY",
			["moonshotai-cn"] = "MOONSHOT_API_KEY",
			["zai"] = "ZAI_API_KEY",
			["zai-coding-cn"] = "ZAI_API_KEY",
			["xai"] = "XAI_API_KEY",
			["opencode"] = "OPENCODE_API_KEY",
			["opencode-go"] = "OPENCODE_API_KEY",
			["kimi-coding"] = "KIMI_API_KEY",
			["google"] = "GEMINI_API_KEY",
			["google-vertex"] = "GOOGLE_CLOUD_API_KEY",
			["vercel-ai-gateway"] = "AI_GATEWAY_API_KEY",
			["cloudflare-workers-ai"] = "CLOUDFLARE_API_KEY",
			["cloudflare-ai-gateway"] = "CLOUDFLARE_API_KEY",
		}.ToFrozenDictionary(StringComparer.Ordinal);

	/// <summary>provider → 环境变量名。每次请求都会查一次，缓存掉重复的 StringBuilder 拼接。</summary>
	private static readonly ConcurrentDictionary<string, string> KeyNames = new(StringComparer.Ordinal);

	/// <summary>某个 provider 的 API key 对应的环境变量名。</summary>
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

	/// <summary>
	/// 每个 provider 一把刷新锁：并发请求同时发现 access token 过期时只让一个去换，
	/// 其余等它写回 auth.json 后直接复用新令牌。否则多个线程拿同一个 refresh token 竞争，
	/// 在会轮转 refresh token 的供应商上会把凭据互相作废。
	/// </summary>
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
			// 刷新一旦开始就不再跟随请求取消：供应商可能已轮换 refresh token，
			// 中途取消会让刚下发的新令牌丢失。真正的网络等待由各家 HttpClient 的 30 秒超时兜底。
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

	/// <summary>
	/// 与 pi 一致的顺序：显式选项 → <c>auth.json</c> 里存储的凭据 → 该 provider 的环境变量 →
	/// 一小组 OpenAI 兼容网关通用的兜底变量。
	/// <para>
	/// 存储优先这一步很关键：<c>pi login</c> 与桌面端都把 key 写进 <c>auth.json</c>，只认环境变量会让
	/// 这些用户收到 401。
	/// </para>
	/// <para>
	/// 读盘、命令型凭据与 OAuth 刷新全部走异步链路；等待刷新闸门时响应取消，
	/// 但刷新本身不跟随取消，避免轮换型 refresh token 被丢掉。
	/// </para>
	/// </summary>
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

		// OAuth 凭据（Anthropic / xAI 等）以 access token 形式复用 API key 通道；
		// AnthropicProvider 会按 sk-ant-oat 前缀切换成 Bearer 请求。
		// 与 API Key 共用同一次读盘/解析，避免同一份 auth.json 读两遍。
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

		var direct = Environment.GetEnvironmentVariable(KeyName(provider));
		if (!string.IsNullOrEmpty(direct))
		{
			return direct;
		}

		return provider switch
		{
			"openai" => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
			_ => null,
		};
	}
}
