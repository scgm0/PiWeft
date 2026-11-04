using System.Net.Http;
using System.Net.Http.Headers;

namespace PiWeft.Ai.Providers;

/// pi 的 provider 级 <c>authHeader</c>：在默认鉴权头之外追加 <c>Authorization: Bearer &lt;key&gt;</c>。
/// Anthropic 兼容网关常用它把 <c>x-api-key</c> 切/补成 Bearer。与 pi 的 withConfiguredAuth
/// 一致：authHeader 是显式配置，会覆盖同一请求里已有的 Authorization。
public static class ProviderAuthHeader
{
	public static void Apply(Model model, string? apiKey, HttpRequestMessage request)
	{
		if (!model.AuthHeader || string.IsNullOrEmpty(apiKey)) return;
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
	}
}
