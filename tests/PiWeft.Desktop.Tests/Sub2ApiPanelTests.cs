using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// Sub2API 面板余额与续期：PiDeck 0.12 用网页后台 JWT 追加钱包余额，refresh token 单次轮转。
public sealed class Sub2ApiPanelTests
{
	private sealed class Handler : HttpMessageHandler
	{
		public List<(string Url, string? Authorization, string? Body)> Requests { get; } = [];

		public Func<string, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
			return Responder(request.RequestUri!.AbsolutePath);
		}
	}

	private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	/// 造一个只带 exp 的 JWT（签名段留空，解析侧不验签）。
	private static string Jwt(DateTimeOffset expiry)
	{
		static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');
		return $"{Encode("{\"alg\":\"none\"}")}.{Encode($"{{\"sub\":\"u\",\"exp\":{expiry.ToUnixTimeSeconds()}}}")}.";
	}

	[Fact]
	public void JwtExpiryAndRefreshDecision()
	{
		var now = DateTimeOffset.UtcNow;
		var token = Jwt(now.AddMinutes(10));
		Assert.Equal(now.AddMinutes(10).ToUnixTimeSeconds() * 1000, Sub2ApiPanel.DecodeJwtExpiry(token));
		Assert.False(Sub2ApiPanel.NeedsRefresh(token, now));

		// 提前 60 秒视作过期：掐着 exp 发请求会稳定换来一次 401。
		Assert.True(Sub2ApiPanel.NeedsRefresh(Jwt(now.AddSeconds(30)), now));
		Assert.True(Sub2ApiPanel.NeedsRefresh(Jwt(now.AddMinutes(-5)), now));

		// 不透明 token / 没有 exp：不下判断，交给服务端 401。
		Assert.Null(Sub2ApiPanel.DecodeJwtExpiry("opaque-token"));
		Assert.False(Sub2ApiPanel.NeedsRefresh("opaque-token", now));
	}

	[Fact]
	public void RefreshAndBalanceResponsesAreParsed()
	{
		var refreshed = Sub2ApiPanel.ParseRefresh(JsonNode.Parse(
			"""{"code":0,"data":{"access_token":"new-jwt","refresh_token":"rt_new","expires_in":3600}}"""));
		Assert.NotNull(refreshed);
		Assert.Equal("new-jwt", refreshed!.AccessToken);
		Assert.Equal("rt_new", refreshed.RefreshToken);

		// 扁平结构与「不带新 refresh token」都要接受：有些版本只下发 access_token。
		var flat = Sub2ApiPanel.ParseRefresh(JsonNode.Parse("""{"access_token":"flat"}"""));
		Assert.Equal("flat", flat!.AccessToken);
		Assert.Null(flat.RefreshToken);

		var balance = Sub2ApiPanel.ParseBalance(JsonNode.Parse("""{"data":{"balance":12.5,"unit":"CNY"}}"""));
		Assert.Equal(12.5, balance!.Value);
		Assert.Equal("CNY", balance.Currency);
		Assert.Null(Sub2ApiPanel.ParseBalance(JsonNode.Parse("""{"data":{}}""")));
	}

	[Fact]
	public async Task ExpiredTokenIsRefreshedBeforeReadingTheBalance()
	{
		var handler = new Handler();
		handler.Responder = path => path switch
		{
			"/api/v1/auth/refresh" => Json("""{"data":{"access_token":"fresh","refresh_token":"rt_new"}}"""),
			"/api/v1/auth/me" => Json("""{"data":{"balance":42,"unit":"USD"}}"""),
			_ => Json("{}", HttpStatusCode.NotFound),
		};
		var client = new HttpClient(handler);

		var (balance, rotated) = await Sub2ApiPanel.FetchBalanceAsync(
			client,
			"https://gateway.example.com/v1",
			new Sub2ApiPanel.Tokens(Jwt(DateTimeOffset.UtcNow.AddMinutes(-1)), "rt_old"),
			TimeSpan.FromSeconds(5),
			DateTimeOffset.UtcNow,
			CancellationToken.None);

		Assert.Equal(42, balance!.Value);
		Assert.Equal("USD", balance.Currency);
		Assert.Equal("fresh", rotated!.AccessToken);
		Assert.Equal("rt_new", rotated.RefreshToken);
		// 续期请求带旧 refresh token；余额请求带新 access token，且只打 origin（不带 /v1）。
		Assert.Contains(handler.Requests, request =>
			request.Url == "https://gateway.example.com/api/v1/auth/refresh" && request.Body!.Contains("rt_old"));
		Assert.Contains(handler.Requests, request =>
			request.Url == "https://gateway.example.com/api/v1/auth/me" && request.Authorization == "Bearer fresh");
	}

	[Fact]
	public async Task UnauthorizedBalanceTriggersOneRefreshAndRetry()
	{
		var handler = new Handler();
		var calls = 0;
		handler.Responder = path =>
		{
			if (path == "/api/v1/auth/refresh") return Json("""{"data":{"access_token":"fresh"}}""");
			calls++;
			return calls == 1 ? Json("{}", HttpStatusCode.Unauthorized) : Json("""{"data":{"balance":7}}""");
		};
		var client = new HttpClient(handler);

		// exp 还早：本地不续期，靠 401 触发一次。
		var (balance, rotated) = await Sub2ApiPanel.FetchBalanceAsync(
			client,
			"https://gateway.example.com",
			new Sub2ApiPanel.Tokens(Jwt(DateTimeOffset.UtcNow.AddHours(1)), "rt_old"),
			TimeSpan.FromSeconds(5),
			DateTimeOffset.UtcNow,
			CancellationToken.None);

		Assert.Equal(7, balance!.Value);
		Assert.NotNull(rotated);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task WithoutRefreshTokenAnExpiredLoginJustMissesTheBalance()
	{
		var handler = new Handler();
		handler.Responder = path => path == "/api/v1/auth/me"
			? Json("{}", HttpStatusCode.Unauthorized)
			: Json("{}", HttpStatusCode.NotFound);
		var client = new HttpClient(handler);

		var (balance, rotated) = await Sub2ApiPanel.FetchBalanceAsync(
			client,
			"https://gateway.example.com",
			new Sub2ApiPanel.Tokens("opaque", null),
			TimeSpan.FromSeconds(5),
			DateTimeOffset.UtcNow,
			CancellationToken.None);

		Assert.Null(balance);
		Assert.Null(rotated);
		Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("/refresh"));
	}
}
