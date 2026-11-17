using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// 使用统计页签：把「应用用量」面板放进可滚动的主区。
/// 设置弹窗复用同一个面板，只是不再显示标题（分区标题已经在弹窗顶部）。
internal sealed class UsageStatsView
{
	private readonly UsageStatsPanel _panel;

	public UsageStatsView(Func<string> sessionsRoot, Func<string, string, ModelCostRates?>? rates = null, UsageStatsCache? cache = null)
	{
		_panel = new UsageStatsPanel(sessionsRoot, rates, showHeading: true, cache: cache);
		View = new Border()
			.StyleName(AppStyles.Window)
			.Child(new ScrollViewer().SmoothScrolling()
				.Content(new Border()
					.Padding(new Thickness(AppTheme.Space6))
					.HorizontalAlignment(HorizontalAlignment.Center)
					.Child(_panel.View))
				.AutoVerticalScroll()
				.NoHorizontalScroll());
		_ = _panel.RefreshIfStaleAsync();
	}

	public UIElement View { get; }

	public void ApplyTheme() => _panel.ApplyTheme();

	public void Close() => _panel.Close();

	public Task RefreshAsync() => _panel.RefreshAsync();

	public Task RefreshIfStaleAsync() => _panel.RefreshIfStaleAsync();
}
