using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class SessionStatisticsDisplayTests
{
	[Fact]
	public void MissingTimingsStayMissingAndContextOverflowIsVisible()
	{
		var display = new SessionStatisticsDisplay(new SessionStatistics(), 1500, 1000, 3);
		Assert.Equal(150, display.ContextPercent);
		Assert.Contains("150%", display.ContextLabel);
		Assert.StartsWith("3 轮", display.Summary());
		Assert.Equal("-", display.Rows().Single(row => row.Label == Strings.StatisticsFirstToken).Value);
		Assert.Equal("-", display.Rows().Single(row => row.Label == Strings.StatisticsSpeedHintLabel).Value);
		Assert.Null((display with { ContextWindow = 0 }).ContextPercent);
	}

	[Fact]
	public void SummaryAndRowsSeparateLatestFromAverageAndStreamingFromEndToEnd()
	{
		var stats = new SessionStatistics
		{
			Input = 1000, Output = 200, CacheRead = 9000, CacheWrite = 500, Cost = 1,
			LatestCacheHitPercent = 90, AverageCacheHitPercent = 45, CacheHitSamples = 2,
			LatestReply = new ReplyPerformance(1000, 5000, 4000, 200),
			SessionDurationMs = 8000, SessionStreamingMs = 4000, SessionStreamingOutput = 400,
		};
		var display = new SessionStatisticsDisplay(stats, 12800, 128000, 2);
		var summary = display.Summary();

		// 最近一次回复的三个量 + 整段会话的均速必须同时可读，且各自带口径标签。
		Assert.Contains("流式 50 tok/s", summary);
		Assert.Contains("均速 100 tok/s", summary);
		Assert.Contains("缓存命中 90%", summary);
		Assert.Contains("输入 1K · 输出 200 tok", summary);
		var rows = display.Rows();
		Assert.Equal("90%", rows.Single(row => row.Label == Strings.StatisticsLatestHit).Value);
		Assert.Equal("45% (2 条样本)", rows.Single(row => row.Label == Strings.StatisticsAverageHit).Value);
		Assert.Equal("40 tok/s", rows.Single(row => row.Label == Strings.StatisticsEndToEndSpeed).Value);
		Assert.Equal("100 tok/s", rows.Single(row => row.Label == Strings.StatisticsSessionSpeed).Value);
		Assert.Equal("25 tok/s", rows.Single(row => row.Label == Strings.StatisticsSessionEndToEndSpeed).Value);
		Assert.Equal("¥7.20", rows.Single(row => row.Label == Strings.StatisticsCny).Value);
		Assert.Equal("1B", SessionStatisticsDisplay.Compact(1_000_000_000));
	}

	[Fact]
	public void PanelUsesWindowOverlayAndCanUpdateAndCloseWithoutDuplicatingIt()
	{
		var owner = new Border();
		var window = new Window().Content(owner);
		var panel = new SessionStatisticsPanel(static () => { });
		var display = new SessionStatisticsDisplay(new SessionStatistics(), 10, 100, 1);
		panel.Show(owner, display, false);
		panel.Show(owner, display, true);
		Assert.True(panel.IsOpen);
		Assert.True(window.OverlayLayer.Contains(panel.View));
		Assert.Equal(1, window.OverlayLayer.Count);
		panel.Update(display with { ContextTokens = 50 }, false);
		panel.Hide();
		Assert.False(panel.IsOpen);
		Assert.Null(panel.View.Parent);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void ClickingOutsideTheStatisticsCardClosesItWithoutDimming()
	{
		var owner = new Border();
		var window = new Window().Content(owner);
		var panel = new SessionStatisticsPanel(static () => { });
		panel.Show(owner, new SessionStatisticsDisplay(new SessionStatistics(), 10, 100, 1), false);
		panel.View.Measure(new Size(720, 520));
		panel.View.Arrange(new Rect(0, 0, 720, 520));
		var backdrop = Assert.IsType<Button>(panel.View.HitTest(new Point(40, 40)));
		Assert.Equal(0, backdrop.Background.A);
		Assert.NotSame(backdrop, panel.View.HitTest(new Point(600, 400)));
		ImagePreviewTests.Click(backdrop);
		Assert.False(panel.IsOpen);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void StatisticsOverviewKeepsAllDetailsAvailableWithoutRecreatingRowsOnUpdate()
	{
		var panel = new SessionStatisticsPanel(static () => { });
		var display = new SessionStatisticsDisplay(new SessionStatistics { Cost = 0.01 }, 500, 1000, 2);
		panel.Update(display, false);
		var overview = VisualTree.Find<TextBlock>(panel.View).Select(text => text.Text).ToArray();
		Assert.Contains(overview, text => text.Contains("50%", StringComparison.Ordinal));
		Assert.Contains(overview, text => text.Contains("~500 / 1K", StringComparison.Ordinal));
		Assert.DoesNotContain(Strings.StatisticsWeightedHit, overview);
		panel.ShowDetails();
		var texts = VisualTree.Find<TextBlock>(panel.View).ToArray();
		Assert.Contains(texts, text => text.Text == Strings.StatisticsWeightedHit);
		panel.Update(display with { ContextTokens = 800 }, true);
		Assert.Equal(texts, VisualTree.Find<TextBlock>(panel.View).ToArray());
		Assert.Contains(texts, text => text.Text.Contains("80%", StringComparison.Ordinal));
	}

	[Fact]
	public void StatisticsMeasuresWithoutMutatingLayoutPropertiesAndKeepsActionsInView()
	{
		var panel = new SessionStatisticsPanel(static () => { });
		panel.Update(new SessionStatisticsDisplay(new SessionStatistics(), 10, 1000, 1), false);
		panel.View.IsVisible = true;
		panel.ShowDetails();
		var scroll = VisualTree.Find<ScrollViewer>(panel.View).Single();
		var maximum = scroll.MaxHeight;
		panel.View.Measure(new Size(720, 520));
		Assert.Equal(maximum, scroll.MaxHeight);
		Assert.False(panel.View.IsMeasureDirty);
		panel.View.Arrange(new Rect(0, 0, 720, 520));
		var compact = VisualTree.Find<Button>(panel.View).Single(button =>
			VisualTree.Find<TextBlock>(button).Any(text => text.Text == Strings.CompactContext));
		Assert.True(scroll.ViewportHeight > 0);
		Assert.True(scroll.Bounds.Bottom <= compact.Bounds.Top);
		Assert.True(compact.Bounds.Bottom <= 520);
		UiLifetime.Release(panel.View);
	}

	[Fact]
	public void UsageConfigurationUsesNativeLayoutConstraintsWithoutMeasureSideEffects()
	{
		var panel = new ProviderUsagePanel(() => FakeStreams.Model(), () => Environment.CurrentDirectory);
		var scroll = VisualTree.Find<ScrollViewer>(panel.View).Single();
		var body = Assert.IsType<StackPanel>(scroll.Content);
		body.Children(new Border().Height(1200));
		panel.View.IsVisible = true;
		var maximum = scroll.MaxHeight;
		panel.View.Measure(new Size(500, 420));
		Assert.Equal(maximum, scroll.MaxHeight);
		Assert.False(panel.View.IsMeasureDirty);
		panel.View.Arrange(new Rect(0, 0, 500, 420));
		Assert.True(scroll.ViewportHeight > 0);
		Assert.True(scroll.Bounds.Bottom <= 420);
		Assert.True(scroll.Bounds.Right <= 500);
		UiLifetime.Release(panel.View);
	}

	[Fact]
	public void NavigationRefreshesChatRowsAndOldSnapshotsRemainStable()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var first = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("first") });
		manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = "reply" }] });
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = FakeStreams.Model(),
			StreamFn = static (_, _, _) => throw new InvalidOperationException(), Tools = [], Skills = [], PromptTemplates = [],
		});
		using var chat = new ChatViewModel();
		chat.Attach(session);
		var old = chat.Entries();
		Assert.Same(old, chat.Entries());
		session.NavigateTo(first);
		Assert.Equal("first", Assert.Single(chat.Entries()).Text);
		Assert.Equal(2, old.Count);
		Assert.Equal("reply", old[1].Text);
		chat.Attach(session);
		Assert.Single(chat.Entries());
	}
}
