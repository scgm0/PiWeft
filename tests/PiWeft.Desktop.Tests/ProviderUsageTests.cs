using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class ProviderUsageTests
{
	private static Model Model(string url = "https://api.deepseek.com/v1") => new() { Provider = "test", BaseUrl = url };

	[Theory]
	[InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/user/balance")]
	[InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/key")]
	[InlineData("https://api.moonshot.cn/v1", "https://api.moonshot.cn/users/me/balance")]
	[InlineData("https://api.siliconflow.cn/v1", "https://api.siliconflow.cn/v1/user/info")]
	[InlineData("https://gateway.example/v1", "https://gateway.example/v1/usage")]
	public void BuiltInsUseProviderSpecificEndpoints(string url, string expected)
	{
		var model = Model(url);
		Assert.Equal(expected, ProviderUsageService.Endpoint(model, ProviderUsageService.BuiltIn(model)).AbsoluteUri);
	}

	[Theory]
	[InlineData("https://api.siliconflow.cn/v1", "CNY")]
	[InlineData("https://api.siliconflow.com/v1", "USD")]
	public void SiliconFlowCurrencyMatchesRegionalEndpoint(string url, string currency) =>
		Assert.Equal(currency, ProviderUsageService.BuiltIn(Model(url)).Currency);

	[Fact]
	public void ParsesNumericStringsArraysAndZeroBalance()
	{
		var spec = ProviderUsageService.BuiltIn(Model());
		var result = ProviderUsageService.Parse(JsonNode.Parse("""{"balance_infos":[{"total_balance":"0.00","currency":"CNY"}]}"""), spec);
		Assert.Equal(0m, result.Remaining);
		Assert.Equal("CNY", result.Currency);
	}

	[Fact]
	public void CalculatesRemainingAndScalesNewApiQuota()
	{
		var result = ProviderUsageService.Parse(JsonNode.Parse("""{"data":{"total":"25.5","used":10}}"""),
			new("/usage", "", TotalPath: "data.total", UsedPath: "data.used"));
		Assert.Equal(15.5m, result.Remaining);
		result = ProviderUsageService.Parse(JsonNode.Parse("""{"success":true,"data":{"quota":1000000,"used_quota":500000}}"""), ProviderUsageService.NewApi("token", "42"));
		Assert.Equal(2m, result.Remaining);
		Assert.Equal(1m, result.Used);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"balance\":null}")]
	[InlineData("{\"success\":false,\"balance\":100}")]
	public void MissingOrFailedResultsAreNotMistakenForZero(string json) =>
		Assert.Throws<InvalidOperationException>(() => ProviderUsageService.Parse(JsonNode.Parse(json), new("/usage", "balance")));

	[Fact]
	public void CustomSpecPreservesOtherProvidersAndUnknownFields()
	{
		var original = JsonNode.Parse("""{"extra":42,"providers":{"other":{"unknown":true}}}""")!.AsObject();
		var next = ProviderUsageService.SaveSpec(original, "test", ProviderUsageService.NewApi("token", "42"));
		Assert.Equal(42, next["extra"]!.GetValue<int>());
		Assert.True(next["providers"]!["other"]!["unknown"]!.GetValue<bool>());
		Assert.Null(original["providers"]!["test"]);
		Assert.Equal(500000m, ProviderUsageService.ReadSpec(Model(), next).Scale);
		Assert.Null(ProviderUsageService.SaveSpec(next, "test", null)["providers"]!["test"]);
	}

	[Theory]
	[InlineData("http://remote.example/usage", "token")]
	[InlineData("https://other.example/usage", "")]
	[InlineData("https://user:pass@api.deepseek.com/usage", "token")]
	[InlineData("https://api.deepseek.com/usage?key=secret", "token")]
	public void RejectsInsecureOrImplicitCrossOriginCredentials(string path, string token) =>
		Assert.Throws<ArgumentException>(() => ProviderUsageService.Validate(Model(), new(path, "balance", Token: token)));

	[Fact]
	public async Task QueryUsesKeyAndNeverReturnsRawErrorBody()
	{
		using var handler = new Handler(request =>
		{
			Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
			return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret raw response") };
		});
		using var client = new HttpClient(handler);
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProviderUsageService(client)
			.QueryAsync(Model(), ProviderUsageService.BuiltIn(Model()), "secret", CancellationToken.None));
		Assert.Equal(Strings.UsageHttpError(401), error.Message);
		Assert.DoesNotContain("secret", error.Message);
	}

	[Fact]
	public async Task CookieDoesNotSendBearerOrModelHeadersToAnotherHost()
	{
		using var handler = new Handler(request =>
		{
			Assert.Null(request.Headers.Authorization);
			Assert.False(request.Headers.Contains("X-Secret"));
			Assert.Equal("session=test", request.Headers.GetValues("Cookie").Single());
			return Json("{\"balance\":12.5}");
		});
		using var client = new HttpClient(handler);
		var model = Model() with { Headers = new() { ["X-Secret"] = "model-secret" } };
		var balance = await new ProviderUsageService(client).QueryAsync(model,
			new("https://panel.example/usage", "balance", Cookie: "session=test"), "model-key", CancellationToken.None);
		Assert.Equal(12.5m, balance.Remaining);
	}

	[Fact]
	public async Task OversizedBodyIsRejected()
	{
		using var handler = new Handler(_ => Json(new string('x', ProviderUsageService.MaxResponseBytes + 1)));
		using var client = new HttpClient(handler);
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProviderUsageService(client)
			.QueryAsync(Model(), ProviderUsageService.BuiltIn(Model()), "secret", CancellationToken.None));
		Assert.Equal(Strings.UsageResponseTooLarge, error.Message);
	}

	[Fact]
	public async Task CancellationPropagates()
	{
		using var handler = new WaitingHandler();
		using var client = new HttpClient(handler);
		using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProviderUsageService(client)
			.QueryAsync(Model(), ProviderUsageService.BuiltIn(Model()), "secret", abort.Token));
	}

	private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
	}
	private sealed class WaitingHandler : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
		{
			await Task.Delay(Timeout.Infinite, token);
			return Json("{}");
		}
	}
}
