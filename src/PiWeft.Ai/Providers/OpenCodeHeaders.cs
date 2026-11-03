using System.Net.Http;

namespace PiWeft.Ai.Providers;

/// <summary>
/// OpenCode（Zen / Go）请求元数据：网关要求 <c>x-opencode-session</c> 才能把请求路由到会话，
/// 缺失时直接返回 400 MissingSessionID。<c>x-opencode-client</c> 只是客户端标识，便于上游归因。
/// </summary>
/// <remarks>
/// 判定规则对齐 PiDeck 的 dshOpenCodeHeaders：
/// - 官方目录 provider（<c>opencode</c> / <c>opencode-go</c>）；
/// - 或 baseUrl 的主机名恰好是 <c>opencode.ai</c>（用户改过 provider 名的官方端点）。
/// 不做子域/子串模糊匹配，避免把会话 ID 发给无关网关。
/// 显式请求头优先（大小写不敏感，空值也算显式覆盖）；没显式配置时才补自动值。
/// </remarks>
public static class OpenCodeHeaders
{
	public const string SessionHeader = "x-opencode-session";
	public const string ClientHeader = "x-opencode-client";
	public const string ClientName = "piweft";

	/// <summary>
	/// 没有会话上下文的一次性请求（连接探测、Git 摘要等）仍需要一个稳定标识：
	/// 进程内共用同一个宿主 ID，避免每个请求都在上游新建一个路由会话。
	/// </summary>
	private static readonly string FallbackSessionId = "piweft-" + Guid.NewGuid().ToString("N");

	public static bool IsRoute(Model model)
	{
		if (model.Provider is "opencode" or "opencode-go") return true;
		// 常见 provider 的 baseUrl 不含该域名：先用序数忽略大小写的子串检查挡住 Uri 分配。
		if (!model.BaseUrl.Contains("opencode.ai", StringComparison.OrdinalIgnoreCase)) return false;
		return Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri) &&
			uri.Scheme is "http" or "https" &&
			uri.Host.Equals("opencode.ai", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>在模型/供应商自定义头都写完之后调用，让显式头覆盖自动值。</summary>
	public static void Apply(Model model, StreamOptions options, HttpRequestMessage request)
	{
		if (!IsRoute(model)) return;
		if (!request.Headers.Contains(SessionHeader))
		{
			var sessionId = string.IsNullOrWhiteSpace(options.SessionId) ? FallbackSessionId : options.SessionId;
			request.Headers.TryAddWithoutValidation(SessionHeader, sessionId);
		}
		if (!request.Headers.Contains(ClientHeader))
			request.Headers.TryAddWithoutValidation(ClientHeader, ClientName);
	}
}
