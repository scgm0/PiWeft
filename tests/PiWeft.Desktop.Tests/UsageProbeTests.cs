using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class UsageProbeTests
{
	private static Model OpenCodeGo(string baseUrl, string api = "openai-completions") => new()
	{
		Id = "glm-5.3",
		Provider = "opencode-go",
		Api = api,
		BaseUrl = baseUrl,
	};

	[Theory]
	[InlineData("https://opencode.ai/inference/go/openai/v1", "openai-completions")]
	[InlineData("https://opencode.ai/inference/go/anthropic", "anthropic-messages")]
	[InlineData("https://opencode.ai/zen/go/v1", "openai-completions")]
	public async Task OpenCodeGoPlanTargetsZenGoUsage(string baseUrl, string api)
	{
		var plan = await ProviderUsageService.BuildPlanAsync(OpenCodeGo(baseUrl, api),
			new UsageProbeProviderConfig(Enabled: true, ApiKey: "oc_sk_test"),
			new UsageProbeDocument(new Dictionary<string, UsageProbeProviderConfig>(), [], [], "usage-probes.json"),
			null, "/tmp", CancellationToken.None);

		Assert.Null(plan.Error);
		var candidate = Assert.Single(plan.Candidates, item => item.TemplateId == "opencode-usage");
		Assert.Equal(["https://opencode.ai/zen/go/v1/usage"], UsageProbes.Urls(candidate, plan.BaseUrl));
	}

	[Fact]
	public void OpenCodeGoResponseMapsRollingWeeklyMonthly()
	{
		var body = JsonNode.Parse("""
			{
			  "usage": {
			    "rolling": { "status": "ok", "percent": 0, "resetsAt": "2026-11-11T17:25:48.000Z" },
			    "weekly": { "status": "rate-limited", "percent": 7, "resetsAt": "2026-11-16T00:00:00.000Z" },
			    "monthly": { "status": "ok", "percent": 14, "resetsAt": "2026-12-03T08:27:06.000Z" }
			  }
			}
			""");

		var parsed = UsageProbes.Parse(body, null);

		Assert.True(parsed.Matched);
		Assert.Equal(ProviderUsageKind.Periods, parsed.Kind);
		Assert.Equal(0, parsed.Periods!.Rolling!.Percent);
		Assert.Equal(7, parsed.Periods.Weekly!.Percent);
		Assert.Equal(14, parsed.Periods.Monthly!.Percent);
		Assert.Equal("rate-limited", parsed.Periods.Weekly.Status);
		Assert.Equal(DateTimeOffset.Parse("2026-11-16T00:00:00Z"), parsed.Periods.Weekly.ResetsAt);
	}

	[Theory]
	[InlineData("https://api.deepseek.com/v1", true)]
	[InlineData("https://api.deepseek.com.evil.test/v1", false)]
	[InlineData("https://evil.test/api.deepseek.com/v1", false)]
	public void BuiltInCandidatesMatchHostNotSubstring(string baseUrl, bool expected)
	{
		var candidate = UsageProbes.Candidates.Single(item => item.TemplateId == "deepseek-balance");
		Assert.Equal(expected, UsageProbes.Applies(candidate, baseUrl, "openai-completions"));
	}

	[Theory]
	[InlineData("https://opencode.ai/inference/go/openai/v1", true)]
	[InlineData("https://opencode.ai.evil.test/inference/go/openai/v1", false)]
	[InlineData("https://evil.test/opencode.ai/inference/go", false)]
	public void OpenCodeGoCandidateMatchesOfficialHostOnly(string baseUrl, bool expected)
	{
		var candidate = UsageProbes.Candidates.Single(item => item.TemplateId == "opencode-usage");
		Assert.Equal(expected, UsageProbes.Applies(candidate, baseUrl, "openai-completions"));
	}

	[Fact]
	public void EmptyUsageProbeFileIsAnEmptyConfig()
	{
		var dir = Path.Combine(Path.GetTempPath(), "piweft-usage-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			File.WriteAllBytes(Path.Combine(dir, UsageProbeStore.FileName), []);
			var document = UsageProbeStore.Load(dir);
			Assert.Empty(document.Errors);
			Assert.Empty(document.Providers);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public async Task OpenCodeGoQuerySendsBearerAndParsesUsage()
	{
		string? requestedUrl = null;
		string? authorization = null;
		using var client = new HttpClient(new StubHandler((request, _) =>
		{
			requestedUrl = request.RequestUri?.ToString();
			authorization = request.Headers.Authorization?.ToString();
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""
					{"usage":{"rolling":{"status":"ok","percent":0,"resetsAt":"2026-11-11T17:25:48.000Z"},"weekly":{"status":"ok","percent":7,"resetsAt":"2026-11-16T00:00:00.000Z"},"monthly":{"status":"ok","percent":14,"resetsAt":"2026-12-03T08:27:06.000Z"}}}
					""", Encoding.UTF8, "application/json"),
			});
		}))
		{ Timeout = Timeout.InfiniteTimeSpan };
		var config = new UsageProbeProviderConfig(Enabled: true, ApiKey: "oc_sk_test", IntervalMinutes: 0);

		var result = await ProviderUsageService.QueryAsync(OpenCodeGo("https://opencode.ai/inference/go/openai/v1"), config,
			new UsageProbeDocument(new Dictionary<string, UsageProbeProviderConfig>(), [], [], "usage-probes.json"),
			null, "/tmp", CancellationToken.None, client);

		Assert.True(result.Success);
		Assert.Equal("https://opencode.ai/zen/go/v1/usage", requestedUrl);
		Assert.Equal("Bearer oc_sk_test", authorization);
		Assert.Equal(14, result.Periods!.Monthly!.Percent);
	}

	[Fact]
	public async Task PreflightFailureIsReportedWithStatus()
	{
		using var client = new HttpClient(new StubHandler((_, _) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
		var model = new Model
		{
			Id = "grok-4.7",
			Provider = "xai",
			Api = "anthropic-messages",
			BaseUrl = "https://api.x.ai/v1",
		};
		var config = new UsageProbeProviderConfig(Enabled: true, ApiKey: "xai_test", IntervalMinutes: 0);

		var result = await ProviderUsageService.QueryAsync(model, config,
			new UsageProbeDocument(new Dictionary<string, UsageProbeProviderConfig>(), [], [], "usage-probes.json"),
			null, "/tmp", CancellationToken.None, client);

		Assert.False(result.Success);
		Assert.Contains("v1/user?include=subscription", result.Detail, StringComparison.Ordinal);
		Assert.Contains("HTTP 401", result.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public async Task QueryUsesOneTimeoutBudgetAndKeepsConfiguredInterval()
	{
		var requests = 0;
		using var client = new HttpClient(new StubHandler(async (_, token) =>
		{
			Interlocked.Increment(ref requests);
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
			return new HttpResponseMessage(HttpStatusCode.OK);
		}))
		{ Timeout = Timeout.InfiniteTimeSpan };
		var model = new Model
		{
			Id = "test",
			Provider = "test",
			Api = "openai-completions",
			BaseUrl = "https://usage.test/v1",
		};
		var document = new UsageProbeDocument(new Dictionary<string, UsageProbeProviderConfig>(),
		[
			new UsageProbeCandidate("/one", BaseUrlContains: ["usage.test"]),
			new UsageProbeCandidate("/two", BaseUrlContains: ["usage.test"]),
		], [], "usage-probes.json");
		var config = new UsageProbeProviderConfig(Enabled: true, ApiKey: "secret", TimeoutSecs: 1, IntervalMinutes: 0);
		var watch = Stopwatch.StartNew();

		var result = await ProviderUsageService.QueryAsync(model, config, document, null, "/tmp", CancellationToken.None, client);

		watch.Stop();
		Assert.False(result.Success);
		Assert.Equal(0, result.IntervalMinutes);
		Assert.Equal(1, requests);
		Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task PlanFailureKeepsConfiguredInterval()
	{
		var result = await ProviderUsageService.QueryAsync(new Model { Id = "test", Provider = "test" },
			new UsageProbeProviderConfig(Enabled: true, ApiKey: "secret", IntervalMinutes: 0),
			new UsageProbeDocument(new Dictionary<string, UsageProbeProviderConfig>(), [], [], "usage-probes.json"),
			null, "/tmp", CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal(0, result.IntervalMinutes);
	}

	private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			send(request, cancellationToken);
	}
}
