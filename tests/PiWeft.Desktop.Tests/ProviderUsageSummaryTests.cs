using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ProviderUsageSummaryTests
{
	private static ProviderUsageResult Balance(double value, string currency = "USD") =>
		new(true, ProviderUsageKind.Balance, Balance: value, Currency: currency, At: DateTimeOffset.UtcNow, IntervalMinutes: 0);

	[Fact]
	public async Task StatisticsHostsBalanceWithRefreshConfigurationAndCachedResult()
	{
		var calls = 0;
		var configured = false;
		var summary = new ProviderUsageSummary(() => FakeStreams.Model(), () => "/project", () => configured = true,
			(_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Balance(12.99)); });
		var panel = new SessionStatisticsPanel(() => { });
		panel.SetUsage(summary);
		var owner = new Border();
		_ = new Window().Content(owner);
		panel.Show(owner, new SessionStatisticsDisplay(new SessionStatistics(), 0, 1000, 1), false);
		await Wait(() => Find<TextBlock>(panel.View).Any(text => text.Text.Contains("$12.99", StringComparison.Ordinal)));
		Assert.Equal(1, calls);
		Assert.Contains(Find<Button>(panel.View), button => HoverHints.Text(button) == Strings.UsageRefresh);
		ImagePreviewTests.Click(Find<Button>(panel.View).Single(button => HoverHints.Text(button) == Strings.UsageConfigure));
		Assert.True(configured);
		panel.Hide();
		panel.Show(owner, new SessionStatisticsDisplay(new SessionStatistics(), 10, 1000, 1), false);
		Assert.Equal(1, calls);
		await summary.RefreshAsync();
		Assert.Equal(2, calls);
		panel.Hide();
		UiLifetime.Release(panel.View);
	}

	[Fact]
	public async Task SameShapeRefreshReusesValueControls()
	{
		var value = 12.99;
		var summary = new ProviderUsageSummary(() => FakeStreams.Model(), () => "/project", () => { }, (_, _, _) =>
			Task.FromResult(Balance(value)));
		summary.Show();
		await Wait(() => Find<TextBlock>(summary.View).Any(text => text.Text.Contains("$12.99", StringComparison.Ordinal)));
		var block = Find<TextBlock>(summary.View).Single(text => text.Text.Contains("$12.99", StringComparison.Ordinal));

		value = 13.99;
		await summary.RefreshAsync();
		await Wait(() => block.Text.Contains("$13.99", StringComparison.Ordinal));
		Assert.Contains(block, Find<TextBlock>(summary.View));

		summary.Hide();
		UiLifetime.Release(summary.View);
	}

	[Fact]
	public async Task InvalidateRefreshesAnAlreadyOpenSummary()
	{
		var calls = 0;
		var summary = new ProviderUsageSummary(() => FakeStreams.Model(), () => "/project", () => { }, (_, _, _) =>
		{
			Interlocked.Increment(ref calls);
			return Task.FromResult(Balance(12.99));
		});
		summary.Show();
		await Wait(() => calls == 1);
		summary.Invalidate();
		await Wait(() => calls == 2);
		summary.Hide();
		UiLifetime.Release(summary.View);
	}

	[Fact]
	public async Task ClosedPanelCancelsRequestsAndRejectsLateResults()
	{
		var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var response = new TaskCompletionSource<ProviderUsageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var summary = new ProviderUsageSummary(() => FakeStreams.Model(), () => "/project", () => { }, (_, _, token) =>
		{
			started.TrySetResult(token);
			return response.Task;
		});
		summary.Show();
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		summary.Hide();
		Assert.True(token.IsCancellationRequested);
		response.SetResult(Balance(999));
		await Task.Delay(100);
		Assert.DoesNotContain(Find<TextBlock>(summary.View), text => text.Text.Contains("999", StringComparison.Ordinal));
		UiLifetime.Release(summary.View);
	}

	[Fact]
	public async Task ModelChangeDropsThePreviousProvidersBalance()
	{
		var model = FakeStreams.Model();
		var summary = new ProviderUsageSummary(() => model, () => "/project", () => { }, (provider, _, _) =>
			provider.Provider == "test" ? Task.FromResult(Balance(12.99))
				: Task.FromResult(ProviderUsageResult.Failure(Strings.UsageUnavailable)));
		summary.Show();
		await Wait(() => Find<TextBlock>(summary.View).Any(text => text.Text.Contains("$12.99", StringComparison.Ordinal)));
		model = model with { Provider = "other" };
		summary.Show();
		await Wait(() => Find<TextBlock>(summary.View).Any(text => text.Text == Strings.UsageUnavailable));
		Assert.DoesNotContain(Find<TextBlock>(summary.View), text => text.Text.Contains("12.99", StringComparison.Ordinal));
		Assert.DoesNotContain(Find<TextBlock>(summary.View), text => text.Text.Contains("sensitive", StringComparison.Ordinal));
		summary.Hide();
		UiLifetime.Release(summary.View);
	}

	[Fact]
	public async Task RendersEveryWindowAndAcceptsTestResults()
	{
		var summary = new ProviderUsageSummary(() => FakeStreams.Model(), () => "/project", () => { }, (_, _, _) =>
			Task.FromResult(new ProviderUsageResult(true, ProviderUsageKind.Periods,
				Periods: new UsagePeriods(new UsagePeriod(12), new UsagePeriod(92)), At: DateTimeOffset.UtcNow, IntervalMinutes: 0)));
		summary.Show();
		await Wait(() => Find<TextBlock>(summary.View).Any(text => text.Text == "92%"));
		Assert.Contains(Find<TextBlock>(summary.View), text => text.Text == "12%");
		summary.ApplyResult(new ProviderUsageResult(true, ProviderUsageKind.Credits,
			Credits: new UsageCredits(Windows: [new UsageWindow("fiveHour", 100, 20), new UsageWindow("weekly", Remaining: 3.5)]),
			At: DateTimeOffset.UtcNow, IntervalMinutes: 0));
		await Wait(() => Find<TextBlock>(summary.View).Any(text => text.Text == "20%"));
		Assert.Contains(Find<TextBlock>(summary.View), text => text.Text == "3.5");
		summary.Hide();
		UiLifetime.Release(summary.View);
	}

	[Fact]
	public async Task DisabledStateExplainsWhyNothingWasQueried()
	{
		var calls = 0;
		var summary = new ProviderUsageSummary(() => FakeStreams.Model(), () => "/project", () => { }, (_, _, _) =>
		{
			Interlocked.Increment(ref calls);
			return Task.FromResult(ProviderUsageResult.DisabledResult());
		});
		summary.Show();
		await Wait(() => Find<TextBlock>(summary.View).Any(text => text.Text == Strings.UsageDisabled));
		Assert.Equal(1, calls);
		summary.Hide();
		UiLifetime.Release(summary.View);
	}

	private static async Task Wait(Func<bool> ready)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!ready()) await Task.Delay(10, timeout.Token);
	}
}
