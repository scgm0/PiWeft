using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class ProviderUsageTests
{
	private static Model Model(string url = "https://api.deepseek.com/v1", string api = "openai-completions") =>
		new() { Provider = "test", BaseUrl = url, Api = api };

	private static UsageProbeCandidate Candidate(string templateId) =>
		UsageProbes.Candidates.First(candidate => candidate.TemplateId == templateId);

	private static UsageProbeDocument EmptyDocument() =>
		new(new Dictionary<string, UsageProbeProviderConfig>(), [], [], "usage-probes.json");

	private static HttpResponseMessage Json(string body) =>
		new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	[Theory]
	[InlineData("https://api.deepseek.com/v1", "deepseek-balance", "https://api.deepseek.com/user/balance")]
	[InlineData("https://openrouter.ai/api/v1", "openrouter-credits", "https://openrouter.ai/api/v1/key")]
	[InlineData("https://api.moonshot.cn/v1", "moonshot-balance", "https://api.moonshot.cn/users/me/balance")]
	[InlineData("https://api.siliconflow.cn/v1", "siliconflow-balance", "https://api.siliconflow.cn/v1/user/info")]
	public void BuiltInsUseProviderSpecificEndpoints(string url, string template, string expected) =>
		Assert.Contains(expected, UsageProbes.Urls(Candidate(template), url));

	[Fact]
	public void GenericOpenAiFallbackOnlyAppliesToOpenAiApis()
	{
		var generic = UsageProbes.Candidates.Last();
		Assert.True(UsageProbes.Applies(generic, "https://gateway.example/v1", "openai-completions"));
		Assert.False(UsageProbes.Applies(generic, "https://gateway.example/v1", "anthropic-messages"));
		Assert.Contains("https://gateway.example/v1/usage", UsageProbes.Urls(generic, "https://gateway.example/v1"));
	}

	[Theory]
	[InlineData("https://api.deepseek.com/v1", "deepseek-balance")]
	[InlineData("https://open.bigmodel.cn/api/paas/v4", "zhipu-quota")]
	[InlineData("https://opencode.ai/zen/v1", "opencode-usage")]
	[InlineData("https://api.moonshot.ai/v1", "moonshot-balance")]
	public void RecognitionMatchesBuiltInCandidates(string url, string expected) =>
		Assert.Equal(expected, UsageProbeStore.Recognize(Model(url))?.TemplateId);

	[Fact]
	public void ParsesNumericStringsArraysAndZeroBalance()
	{
		var parse = new UsageProbeParse(UsageParseKind.Balance,
			ValuePath: "balance_infos[0].total_balance", CurrencyPath: "balance_infos[0].currency");
		var result = UsageProbes.Parse(JsonNode.Parse("""{"balance_infos":[{"total_balance":"0.00","currency":"CNY"}]}"""), parse);
		Assert.True(result.Matched);
		Assert.Equal(0, result.Balance);
		Assert.Equal("CNY", result.Currency);
	}

	[Fact]
	public async Task CalculatesRemainingAndScalesNewApiQuota()
	{
		var result = UsageProbes.Parse(JsonNode.Parse("""{"data":{"total":"25.5","used":10}}"""),
			new UsageProbeParse(UsageParseKind.Credits, TotalPath: "data.total", UsedPath: "data.used"));
		Assert.Equal(15.5, result.Credits!.Remaining);

		var plan = await ProviderUsageService.BuildPlanAsync(Model(),
			new UsageProbeProviderConfig(Enabled: true, Template: "newapi", AccessToken: "token", UserId: "42"),
			EmptyDocument(), null, "/project", CancellationToken.None);
		Assert.Null(plan.Error);
		var parsed = UsageProbes.Parse(JsonNode.Parse("""{"success":true,"data":{"quota":1000000,"used_quota":500000}}"""),
			plan.Candidates[0].Parse);
		Assert.Equal(2, parsed.Credits!.Remaining);
		Assert.Equal(1, parsed.Credits.Used);
	}

	[Fact]
	public void ParsesCreditWindowsPeriodsAndBoosters()
	{
		var windows = UsageProbes.Parse(JsonNode.Parse("""
			{"data":{"limits":[{"type":"TOKENS_LIMIT","unit":3,"percentage":12.5},{"type":"TOKENS_LIMIT","unit":6,"usage":100,"currentValue":80}]}}
			"""), new UsageProbeParse(UsageParseKind.Credits,
			Windows:
			[
				new UsageProbeWindow("fiveHour", "usage", "currentValue", PercentPath: "percentage",
					ListPath: "data.limits",
					Where: [new UsageProbeWhere("type", JsonValue.Create("TOKENS_LIMIT")), new UsageProbeWhere("unit", JsonValue.Create(3))]),
				new UsageProbeWindow("weekly", "usage", "currentValue", PercentPath: "percentage",
					ListPath: "data.limits",
					Where: [new UsageProbeWhere("type", JsonValue.Create("TOKENS_LIMIT")), new UsageProbeWhere("unit", JsonValue.Create(6))]),
			]));
		Assert.True(windows.Matched);
		Assert.Equal(2, windows.Credits!.Windows!.Count);
		Assert.Equal(12.5, windows.Credits.Windows[0].Percent);
		Assert.Equal(80, windows.Credits.Windows[1].Used);

		var periods = UsageProbes.Parse(JsonNode.Parse("""{"usage":{"rolling":{"percent":12},"weekly":{"percent":40,"resetsAt":"2030-01-01T00:00:00Z"}}}"""),
			new UsageProbeParse());
		Assert.Equal(12, periods.Periods!.Rolling!.Percent);
		Assert.NotNull(periods.Periods.Weekly!.ResetsAt);
	}

	[Fact]
	public void CustomResolversCoverCodexKimiMinimaxAndSub2Api()
	{
		var codex = UsageProbeResolvers.Parse("codex-usage", JsonNode.Parse("""
			{"rate_limit":{"primary_window":{"used_percent":23.5},"secondary_window":{"used_percent":88}},"credits":{"has_credits":true,"balance":42}}
			"""));
		Assert.Equal(2, codex.Credits!.Windows!.Count);
		Assert.Equal(42, codex.Credits.Remaining);

		var kimi = UsageProbeResolvers.Parse("kimi-credits", JsonNode.Parse("""
			{"usage":{"limit":100,"used":40},"boosterWallet":{"balance":{"amountLeft":1230000000,"amount":2000000000},"monthlyChargeLimitEnabled":false}}
			"""));
		Assert.Equal(60, kimi.Credits!.Remaining);
		Assert.Equal("weekly", kimi.Credits.Windows![0].Key);
		Assert.Equal(12.3, kimi.Booster!.Balance, 3);
		Assert.True(kimi.Booster.UnlimitedMonthly);

		var minimax = UsageProbeResolvers.Parse("minimax-plan", JsonNode.Parse("""
			{"base_resp":{"status_code":0},"model_remains":[{"model_name":"video","current_interval_remaining_percent":99},{"model_name":"general","current_interval_remaining_percent":75,"current_weekly_remaining_percent":10}]}
			"""));
		Assert.Equal(25, minimax.Periods!.Rolling!.Percent);
		Assert.Equal(90, minimax.Periods.Weekly!.Percent);

		var sub2api = UsageProbeResolvers.Parse("sub2api-usage", JsonNode.Parse("""{"balance":12.5,"unit":"USD"}"""));
		Assert.Equal(ProviderUsageKind.Balance, sub2api.Kind);
		Assert.Equal(12.5, sub2api.Balance);
	}

	[Fact]
	public void DisplayFormatsMoneyWindowsAndRelativeTime()
	{
		Assert.Equal("$12.99", ProviderUsageDisplay.Money(12.99, "USD"));
		Assert.Equal("¥10", ProviderUsageDisplay.Money(10, "CNY"));
		Assert.Equal("12.5 XYZ", ProviderUsageDisplay.Money(12.5, "XYZ"));
		Assert.Equal(95, ProviderUsageDisplay.WindowPercent(new UsageWindow("w", 100, 95)));
		Assert.Equal(70, ProviderUsageDisplay.WindowPercent(new UsageWindow("w", Total: 100, Remaining: 30)));
		Assert.Equal(55, ProviderUsageDisplay.WindowPercent(new UsageWindow("w", Percent: 55)));
		Assert.Equal(ProviderUsageDisplay.Tone.Empty, ProviderUsageDisplay.PercentTone(95));
		Assert.Equal(ProviderUsageDisplay.Tone.Low, ProviderUsageDisplay.PercentTone(75));
		Assert.Equal("刚刚", ProviderUsageDisplay.RelativeTime(DateTimeOffset.UtcNow));
		Assert.Equal("2 分钟前", ProviderUsageDisplay.RelativeTime(DateTimeOffset.UtcNow.AddMinutes(-2)));
	}

	[Fact]
	public void SegmentsExposeEveryWindowAndBalance()
	{
		var result = new ProviderUsageResult(true, ProviderUsageKind.Credits,
			Credits: new UsageCredits(Windows:
			[
				new UsageWindow("fiveHour", 100, 20),
				new UsageWindow("weekly", 100, 92),
			]), At: DateTimeOffset.UtcNow);
		var segments = ProviderUsageDisplay.Segments(result)!;
		Assert.Equal(2, segments.Count);
		Assert.Equal("20%", segments[0].Text);
		Assert.Equal(ProviderUsageDisplay.Tone.Empty, segments[1].Tone);

		var balance = ProviderUsageDisplay.Segments(new ProviderUsageResult(true, ProviderUsageKind.Balance,
			Balance: 3.5, Currency: "CNY", At: DateTimeOffset.UtcNow))!;
		Assert.Equal("¥3.5", balance[0].Text);
	}

	[Fact]
	public void HeadersReplacePlaceholdersAndRespectNoBearer()
	{
		var candidate = new UsageProbeCandidate("/usage",
			Headers: new Dictionary<string, string> { ["Authorization"] = "{{apiKey}}" },
			NoBearer: true);
		var headers = UsageProbes.BuildHeaders(candidate, "secret", null, sameOrigin: false);
		Assert.Equal("secret", headers["Authorization"]);
		Assert.False(headers.ContainsKey("X-Secret"));

		var cookie = UsageProbes.BuildHeaders(new UsageProbeCandidate("/usage", NoBearer: true,
			Headers: new Dictionary<string, string> { ["Cookie"] = "a=1" }), "secret", null, sameOrigin: false);
		Assert.Equal("a=1", cookie["Cookie"]);
		Assert.False(cookie.ContainsKey("Authorization"));

		// 占位头没有可用 key 时整条省略，让上层按「缺少凭据」提示。
		var missing = UsageProbes.BuildHeaders(new UsageProbeCandidate("/usage",
			Headers: new Dictionary<string, string> { ["Authorization"] = "{{apiKey}}" }), "", null, sameOrigin: true);
		Assert.False(missing.ContainsKey("Authorization"));
	}

	[Fact]
	public void UserProbesStayRelativeToTheirBaseUrl()
	{
		var probe = new UsageProbeCandidate("/custom/usage", BaseUrlContains: ["gateway.example"]);
		var urls = UsageProbes.Urls(probe, "https://gateway.example/proxy/v1");
		Assert.Equal(["https://gateway.example/proxy/v1/custom/usage"], urls);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"balance\":null}")]
	public void MissingResultsAreNotMistakenForZero(string json) =>
		Assert.False(UsageProbes.Parse(JsonNode.Parse(json), new UsageProbeParse(UsageParseKind.Balance, "balance")).Matched);

	[Fact]
	public void DeclarativeConfigValidatesAndOnlyWritesRelevantFields()
	{
		UsageProbeStore.Validate(new UsageProbeProviderConfig(Enabled: true, Template: "newapi", AccessToken: "t", UserId: "1"));
		Assert.Throws<ArgumentException>(() => UsageProbeStore.Validate(new UsageProbeProviderConfig(Template: "unknown")));
		Assert.Throws<ArgumentException>(() => UsageProbeStore.Validate(new UsageProbeProviderConfig(TimeoutSecs: 0)));
		Assert.Throws<ArgumentException>(() => UsageProbeStore.Validate(new UsageProbeProviderConfig(CookiePath: "relative")));
		Assert.Throws<ArgumentException>(() => UsageProbeStore.Validate(new UsageProbeProviderConfig(BaseUrl: "http://remote.example")));
		Assert.Equal(Strings.UsageNewApiNeeds, UsageProbeStore.TemplateError(new UsageProbeProviderConfig(Template: "newapi")));
		Assert.Equal(Strings.UsageCookieNeeds, UsageProbeStore.TemplateError(new UsageProbeProviderConfig(Template: "cookie")));
		Assert.Null(UsageProbeStore.TemplateError(new UsageProbeProviderConfig(Template: "general")));

		var json = UsageProbeStore.ToJson(new UsageProbeProviderConfig(Enabled: false, Template: "general", ApiKey: "key"));
		Assert.False(json["enabled"]!.GetValue<bool>());
		Assert.Equal("general", json["template"]!.GetValue<string>());
		Assert.Null(json["cookie"]);
		Assert.Equal(UsageProbeStore.DefaultTimeoutSeconds, json["timeoutSecs"]!.GetValue<int>());
	}

	[Fact]
	public void LegacyProbesArrayIsNormalizedAndInvalidEntriesAreSkipped()
	{
		var errors = new List<string>();
		var candidates = UsageProbeStore.ReadProbes(JsonNode.Parse("""
			{"probes":[
				{"match":{"baseUrlContains":["api.example.com"]},"request":{"path":"/balance"},"parse":{"kind":"balance","valuePath":"data.balance"}},
				{"match":{},"request":{"path":"/bad"}},
				{"match":{"baseUrlContains":["custom"]},"request":{"path":"/credits"},"parse":{"kind":"credits","totalPath":"t","usedPath":"u","scale":500000}}
			]}
			""")!["probes"], errors);
		Assert.Equal(2, candidates.Count);
		Assert.Single(errors);
		Assert.Equal(UsageParseKind.Balance, candidates[0].Parse!.Kind);
		Assert.Equal(500000, candidates[1].Parse!.Scale);
	}

	[Fact]
	public void LegacyUsageQueriesFileIsConvertedForMigration()
	{
		var dir = Directory.CreateTempSubdirectory("piweft-usage-");
		try
		{
			File.WriteAllText(Path.Combine(dir.FullName, UsageProbeStore.LegacyFileName), """
				{"providers":{"test":{"path":"/api/wallet","valuePath":"data.balance","currency":"CNY","cookie":"session=1"}}}
				""");
			var candidate = UsageProbeStore.LoadLegacyCandidate(dir.FullName, "test");
			Assert.NotNull(candidate);
			Assert.Equal("/api/wallet", candidate!.Path);
			Assert.True(candidate.NoBearer);
			Assert.Equal("CNY", candidate.Parse!.Currency);
			Assert.Equal("session=1", candidate.Headers!["Cookie"]);
		}
		finally { dir.Delete(recursive: true); }
	}

	[Fact]
	public void ConfigFileRoundTripsProvidersAndPreservesProbes()
	{
		var dir = Directory.CreateTempSubdirectory("piweft-usage-");
		try
		{
			var path = Path.Combine(dir.FullName, UsageProbeStore.FileName);
			File.WriteAllText(path, """
				{"providers":{"test":{"enabled":true,"template":"cookie","cookie":"a=1","cookiePath":"/usage","valuePath":"data.balance"}},"probes":[{"match":{"baseUrlContains":["x"]},"request":{"path":"/y"},"parse":{"kind":"balance","valuePath":"b"}}]}
				""");
			var document = UsageProbeStore.Load(dir.FullName);
			Assert.Empty(document.Errors);
			Assert.True(document.Providers["test"].Enabled);
			Assert.Single(document.Probes);
			Assert.Equal("/usage", document.Providers["test"].CookiePath);
		}
		finally { dir.Delete(recursive: true); }
	}

	[Fact]
	public async Task QueryUsesKeyAndNeverReturnsRawErrorBody()
	{
		using var handler = new Handler(request =>
		{
			Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
			return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret raw response") };
		});
		using var client = new HttpClient(handler);
		var result = await ProviderUsageService.QueryAsync(Model(),
			new UsageProbeProviderConfig(Enabled: true, ApiKey: "secret", TimeoutSecs: 5),
			EmptyDocument(), null, "/project", CancellationToken.None, client);
		Assert.False(result.Success);
		Assert.Equal(Strings.UsageUnavailable, result.Error);
		Assert.Contains("HTTP 401", result.Detail);
		Assert.DoesNotContain("secret raw", result.Detail);
	}

	[Fact]
	public async Task CookieTemplateDoesNotSendBearerOrModelHeadersToAnotherHost()
	{
		using var handler = new Handler(request =>
		{
			Assert.Null(request.Headers.Authorization);
			Assert.False(request.Headers.Contains("X-Secret"));
			Assert.Equal("session=test", request.Headers.GetValues("Cookie").Single());
			return Json("{\"balance\":12.5}");
		});
		using var client = new HttpClient(handler);
		var model = Model("https://api.deepseek.com/v1") with { Headers = new() { ["X-Secret"] = "model-secret" } };
		var result = await ProviderUsageService.QueryAsync(model,
			new UsageProbeProviderConfig(Enabled: true, Template: "cookie", Cookie: "session=test",
				CookiePath: "/api/wallet/summary", ValuePath: "balance", BaseUrl: "https://panel.example", TimeoutSecs: 5),
			EmptyDocument(), null, "/project", CancellationToken.None, client);
		Assert.True(result.Success);
		Assert.Equal(12.5, result.Balance);
	}

	[Fact]
	public async Task OversizedBodyIsRejected()
	{
		using var handler = new Handler(_ => Json(new string('x', ProviderUsageService.MaxResponseBytes + 1)));
		using var client = new HttpClient(handler);
		var result = await ProviderUsageService.QueryAsync(Model(),
			new UsageProbeProviderConfig(Enabled: true, ApiKey: "secret", TimeoutSecs: 5),
			EmptyDocument(), null, "/project", CancellationToken.None, client);
		Assert.Equal(Strings.UsageResponseTooLarge, result.Error);
	}

	[Fact]
	public async Task CancellationPropagates()
	{
		using var handler = new WaitingHandler();
		using var client = new HttpClient(handler);
		using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProviderUsageService.QueryAsync(Model(),
			new UsageProbeProviderConfig(Enabled: true, ApiKey: "secret", TimeoutSecs: 30),
			EmptyDocument(), null, "/project", abort.Token, client));
	}

	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
			Task.FromResult(respond(request));
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
