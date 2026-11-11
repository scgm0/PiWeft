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
}
