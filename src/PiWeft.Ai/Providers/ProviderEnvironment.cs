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
	/// </summary>
	public static HeaderTimeoutScope BeginHeaderTimeout(StreamOptions options)
	{
		// 等响应头也受空闲超时兜底：连接建立了但服务端一直不回头，否则这一轮会无限等下去
		// （HttpClient 是 Infinite，正文计时器此时还没建立）。显式的请求超时优先。
		var timeoutMs = options.TimeoutMs is > 0 ? options.TimeoutMs : options.IdleTimeoutMs;
		if (timeoutMs is not { } value || value <= 0)
		{
			return new HeaderTimeoutScope(null, null, options.CancellationToken);
		}

		var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(value));
		var linked = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken, timeout.Token);
		return new HeaderTimeoutScope(timeout, linked, linked.Token);
	}
}

/// <summary>
/// 「等待响应头」的超时作用域。pi 1.2.0 起超时只覆盖等响应头这一步：长流（长时间思考、慢生成）
/// 不能被固定时限切断，正文停滞交给 HTTP 客户端自己处理。
/// </summary>
public sealed class HeaderTimeoutScope : IDisposable
{
	private readonly CancellationTokenSource? _timeout;
	private readonly CancellationTokenSource? _linked;

	internal HeaderTimeoutScope(CancellationTokenSource? timeout, CancellationTokenSource? linked, CancellationToken token)
	{
		_timeout = timeout;
		_linked = linked;
		Token = token;
	}

	/// <summary>传给发送请求那一步的令牌。</summary>
	public CancellationToken Token { get; }

	/// <summary>是否是我们自己的超时先触发（而不是调用方取消）。</summary>
	public bool TimedOut => _timeout is { IsCancellationRequested: true };

	public void Dispose()
	{
		_linked?.Dispose();
		_timeout?.Dispose();
	}
}

/// <summary>从环境变量解析 provider 凭据，对应 pi 的 <c>env-api-keys</c>。</summary>
public static class ProviderEnvironment
{
	private static readonly Dictionary<string, string> Overrides = new(StringComparer.Ordinal)
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
	};

	/// <summary>某个 provider 的 API key 对应的环境变量名。</summary>
	public static string KeyName(string provider)
	{
		if (Overrides.TryGetValue(provider, out var name))
		{
			return name;
		}

		var builder = new System.Text.StringBuilder(provider.Length + 8);
		foreach (var c in provider)
		{
			builder.Append(c is '-' or '.' ? '_' : char.ToUpperInvariant(c));
		}

		builder.Append("_API_KEY");
		return builder.ToString();
	}

	private static Auth.OAuthCredential? RefreshOAuth(string provider, Auth.OAuthCredential credential)
	{
		try
		{
			return provider switch
			{
				Auth.AnthropicOAuth.ProviderKey => Auth.AnthropicOAuth.RefreshAsync(credential).GetAwaiter().GetResult(),
				Auth.XaiOAuth.ProviderKey => Auth.XaiOAuth.RefreshAsync(credential).GetAwaiter().GetResult(),
				Auth.KimiOAuth.ProviderKey => Auth.KimiOAuth.RefreshAsync(credential).GetAwaiter().GetResult(),
				Auth.OpenAiChatGptOAuth.ProviderKey => Auth.OpenAiChatGptOAuth.RefreshAsync(credential).GetAwaiter().GetResult(),
				Auth.GitHubCopilotOAuth.ProviderKey => Auth.GitHubCopilotOAuth.RefreshAsync(credential).GetAwaiter().GetResult(),
				Auth.MetaOAuth.ProviderKey => Auth.MetaOAuth.RefreshAsync(credential).GetAwaiter().GetResult(),
				Auth.RadiusOAuth.ProviderKey => Auth.RadiusOAuth.RefreshAsync(credential).GetAwaiter().GetResult(),
				_ => null,
			};
		}
		catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or Auth.CodexAuthException)
		{
			return null;
		}
	}

	/// <summary>
	/// 与 pi 一致的顺序：显式选项 → <c>auth.json</c> 里存储的凭据 → 该 provider 的环境变量 →
	/// 一小组 OpenAI 兼容网关通用的兜底变量。
	/// <para>
	/// 存储优先这一步很关键：<c>pi login</c> 与桌面端都把 key 写进 <c>auth.json</c>，只认环境变量会让
	/// 这些用户收到 401。
	/// </para>
	/// </summary>
	public static string? ResolveApiKey(string provider, StreamOptions options)
	{
		if (!string.IsNullOrEmpty(options.ApiKey))
		{
			return options.ApiKey;
		}

		var stored = Auth.AuthStore.ReadApiKey(provider);
		if (stored is { Length: > 0 })
		{
			return stored;
		}

		// OAuth 凭据（Anthropic / xAI 等）以 access token 形式复用 API key 通道；
		// AnthropicProvider 会按 sk-ant-oat 前缀切换成 Bearer 请求。
		if (Auth.OAuthStore.Read(provider) is { Access.Length: > 0 } oauth)
		{
			if (oauth.ExpiresAt <= DateTimeOffset.UtcNow && oauth.Refresh.Length > 0)
			{
				var refreshed = RefreshOAuth(provider, oauth);
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
