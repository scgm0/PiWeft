namespace PiWeft.Mcp;

/// <summary>
/// 基于 <see cref="McpOAuthStore"/> 的令牌提供者：首次请求时读取（必要时刷新）访问令牌，
/// 收到 401 后强制刷新。
/// </summary>
public sealed class McpOAuthTokenProvider : IMcpAccessTokenProvider
{
	/// <summary>元数据与令牌请求共用的客户端；连接池按进程复用。</summary>
	private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		ConnectTimeout = TimeSpan.FromSeconds(30),
	})
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	private readonly HttpClient _http;
	private readonly string _endpoint;
	private readonly string? _agentDir;
	private readonly Lock _gate = new();
	private McpOAuthToken? _cached;

	public McpOAuthTokenProvider(string endpoint, string? agentDir = null, HttpClient? http = null)
	{
		_http = http ?? SharedHttp;
		_endpoint = endpoint;
		_agentDir = agentDir;
	}

	/// <summary>最近一次解析到的令牌；供 <c>list_mcp</c> 展示到期时间。</summary>
	public McpOAuthToken? Cached
	{
		get
		{
			lock (_gate)
			{
				return _cached;
			}
		}
	}

	public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			// 缓存里的令牌临近到期时必须重新解析：长时间运行时每请求都用过期令牌会一直 401。
			if (_cached is not null && !_cached.IsExpiring(DateTimeOffset.UtcNow, McpOAuthStore.ExpirySkew))
			{
				return _cached.Access;
			}
		}

		var token = await McpOAuth.ResolveAsync(_http, _endpoint, _agentDir, cancellationToken).ConfigureAwait(false);
		lock (_gate)
		{
			_cached = token;
		}

		return token?.Access;
	}

	public async Task<string?> RefreshAsync(CancellationToken cancellationToken)
	{
		McpOAuthToken? current;
		lock (_gate)
		{
			current = _cached ?? McpOAuthStore.Load(_endpoint, _agentDir);
		}

		if (current is null)
		{
			// 还没有任何令牌：再走一次完整解析（可能是首次授权）。
			return await GetTokenAsync(cancellationToken).ConfigureAwait(false);
		}

		var refreshed = await McpOAuth.RefreshAsync(_http, current, cancellationToken).ConfigureAwait(false);
		if (!refreshed.Ok)
		{
			return null;
		}

		McpOAuthStore.Save(_endpoint, refreshed.Token!, _agentDir);
		lock (_gate)
		{
			_cached = refreshed.Token;
		}

		return refreshed.Token!.Access;
	}
}
