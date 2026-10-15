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
	[Fact]
	public async Task StatisticsHostsBalanceWithRefreshConfigurationAndCachedResult()
	{
		var calls = 0;
		var configured = false;
		var summary = new ProviderUsageSummary(() => FakeStreams.Model(), () => "/project", () => configured = true,
			(_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new ProviderBalance(12.99m, null, null, "USD")); });
		var panel = new SessionStatisticsPanel(() => { });
		panel.SetUsage(summary);
		var owner = new Border();
		var window = new Window().Content(owner);
		panel.Show(owner, new SessionStatisticsDisplay(new SessionStatistics(), 0, 1000, 1), false);
		await Wait(() => Find<TextBlock>(panel.View).Any(text => text.Text.Contains("12.99", StringComparison.Ordinal)));
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
	public async Task ClosedPanelCancelsRequestsAndRejectsLateResults()
	{
		var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var response = new TaskCompletionSource<ProviderBalance>(TaskCreationOptions.RunContinuationsAsynchronously);
		var summary = new ProviderUsageSummary(() => FakeStreams.Model(), () => "/project", () => { }, (_, _, token) =>
		{
			started.TrySetResult(token);
			return response.Task;
		});
		summary.Show();
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		summary.Hide();
		Assert.True(token.IsCancellationRequested);
		response.SetResult(new ProviderBalance(999, null, null, "USD"));
		await Task.Delay(100);
		Assert.DoesNotContain(Find<TextBlock>(summary.View), text => text.Text.Contains("999", StringComparison.Ordinal));
		UiLifetime.Release(summary.View);
	}

	[Fact]
	public async Task ModelChangeDropsThePreviousProvidersBalance()
	{
		var model = FakeStreams.Model();
		var summary = new ProviderUsageSummary(() => model, () => "/project", () => { }, (provider, _, _) =>
			provider.Provider == "test" ? Task.FromResult(new ProviderBalance(12.99m, null, null, "USD"))
				: Task.FromException<ProviderBalance>(new InvalidOperationException("sensitive error text")));
		summary.Show();
		await Wait(() => Find<TextBlock>(summary.View).Any(text => text.Text.Contains("12.99", StringComparison.Ordinal)));
		model = model with { Provider = "other" };
		summary.Show();
		await Wait(() => Find<TextBlock>(summary.View).Any(text => text.Text == Strings.UsageUnavailable));
		Assert.DoesNotContain(Find<TextBlock>(summary.View), text => text.Text.Contains("12.99", StringComparison.Ordinal));
		Assert.DoesNotContain(Find<TextBlock>(summary.View), text => text.Text.Contains("sensitive", StringComparison.Ordinal));
		summary.Hide();
		UiLifetime.Release(summary.View);
	}

	private static async Task Wait(Func<bool> ready)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!ready()) await Task.Delay(10, timeout.Token);
	}
}
