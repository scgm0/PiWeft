using PiWeft.CodingAgent.Stats;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-07：跨会话用量统计面板的轻量渲染与数值缩写。</summary>
[Collection("mewui-controls")]
public sealed class UsageStatsViewTests
{
	[Theory]
	[InlineData(0, "0")]
	[InlineData(999, "999")]
	[InlineData(1_500, "1.5K")]
	[InlineData(2_000_000, "2M")]
	[InlineData(3_000_000_000, "3B")]
	public void FormatTokensUsesReadableMagnitudes(long tokens, string expected) =>
		Assert.Equal(expected, UsageStatsView.FormatTokens(tokens));

	[Fact]
	public void ShowsAnEmptyStateWhenThereIsNoUsage()
	{
		var view = new UsageStatsView(() => Path.Combine(Path.GetTempPath(), "piweft-usage-missing-" + Guid.NewGuid().ToString("N")[..8]));
		var texts = Find<Aprillz.MewUI.Controls.TextBlock>(view.View).Select(static block => block.Text).ToList();

		Assert.Contains(Strings.UsageStatsTitle, texts);
		// 首次扫描是异步的，但标题与刷新按钮必须已在树上。
		Assert.NotNull(view.View);
		view.Close();
	}

	[Fact]
	public void ClosesWithoutThrowingWhileAScanIsRunning()
	{
		var view = new UsageStatsView(() => UsageStats.DefaultRoot());
		view.Close();
		Assert.NotNull(view.View);
	}
}
