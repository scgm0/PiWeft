using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Readable overview by default; detailed counters are opt-in and updated in place.</summary>
public sealed class SessionStatisticsPanel
{
	private readonly StackPanel _body = new();
	private readonly Border _usage = new Border().IsVisible(false);
	private ProviderUsageSummary? _usageSummary;
	private readonly List<TextBlock> _values = [];
	private readonly TextBlock _percent = new();
	private readonly TextBlock _capacity = new();
	private readonly ContextBreakdownBar _segments = new();
	private readonly TextBlock _cost = new();
	private readonly TextBlock _turns = new();
	private readonly Button _compact;
	private readonly Button _overview;
	private readonly Button _details;
	private Window? _window;
	private SessionStatisticsDisplay? _display;
	private bool _showDetails;

	public SessionStatisticsPanel(Action compact)
	{
		_body.Margin(new Thickness(0, 0, AppTheme.Space3, 0));
		_compact = AppIcons.PrimaryButton("square-slash", Strings.CompactContext, compact);
		_overview = AppIcons.TextButton("eye", Strings.StatisticsOverview, () => SetDetails(false));
		_details = AppIcons.TextButton("settings", Strings.StatisticsDetails, () => SetDetails(true));
		_overview.StyleName(AppStyles.SelectedButton);
		var scroll = new ScrollViewer().SmoothScrolling().Content(new StackPanel().Spacing(AppTheme.Space2).Children(_body, _usage))
			.AutoVerticalScroll().NoHorizontalScroll().MaxHeight(480);
		// 标题区与 PiDeck 的上下文弹窗同结构：先给结论（已用百分比 + ~已用/窗口），
		// 再用一条三段占用条与图例拆开系统 / 工具 / 对话——只看一个百分比无法判断
		// 是上下文真的长，还是工具 schema 很大。
		var heading = new StackPanel().Spacing(AppTheme.Space2).Children(
			new DockPanel().LastChildFill().Children(
				AppIcons.Button("x", Strings.Cancel, Hide).DockRight(),
				new TextBlock().Text(Strings.SessionStatistics).FontSize(AppTheme.FontSizeTitle).FontWeight(FontWeight.Bold).PrimaryText()),
			new DockPanel().LastChildFill().Children(
				_capacity.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).MutedText().DockRight(),
				_percent.FontSize(AppTheme.FontSizeControl).FontWeight(FontWeight.Bold).PrimaryText()),
			_segments.View,
			new DockPanel().LastChildFill().Children(_cost.FontSize(AppTheme.FontSizeTitle).FontWeight(FontWeight.Bold).PrimaryText().DockRight(),
				_turns.FontSize(AppTheme.FontSizeControl).MutedText()),
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(_overview, _details));
		var content = new DockPanel().LastChildFill().Spacing(AppTheme.Space3)
			.Children(heading.DockTop(), _compact.DockBottom(), scroll);
		// 卡片是右下角带结构线与落影的一块，背景不变暗；第一层的透明捕获层只做一件事：
		// 点面板以外的空白处收起浮层（不吞掉外观，也不变暗）。关闭另有 × 与 Esc。
		View = new Grid().IsVisible(false).Children(
			new Button().Background(AppTheme.Transparent).Cursor(CursorType.Arrow)
				.Focusable(false).BorderThickness(0).CornerRadius(0).OnClick(Hide),
			new ShadowDecorator()
				.BlurRadius(14).OffsetY(4).CornerRadius(AppTheme.RadiusMd)
				.WithTheme((theme, shadow) => shadow.ShadowColor = theme.IsDark
					? new Color(0xB4, 0x00, 0x00, 0x00)
					: new Color(0x3C, 0x00, 0x00, 0x00))
				.HorizontalAlignment(HorizontalAlignment.Right).VerticalAlignment(VerticalAlignment.Bottom)
				.Margin(new Thickness(AppTheme.Space1))
				.Child(new Border().Width(420).MaxWidth(420)
					.StyleName(AppStyles.PopupCard)
					.Padding(new Thickness(AppTheme.Space4)).Child(content)))
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Escape) return;
				key.Handled = true;
				Hide();
			});
	}

	public UIElement View { get; }
	public bool IsOpen => View.IsVisible;
	public event Action? Closed;

	public void Show(UIElement owner, SessionStatisticsDisplay display, bool busy)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		Update(display, busy);
		if (IsOpen) { _usageSummary?.Show(); return; }
		_window = window;
		if (View is FrameworkElement element)
		{
			element.Margin = window is MainWindow main
				? MainWindow.OverlayInsetFor(main.WindowState)
				: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		}
		window.OverlayLayer.Add(View);
		View.IsVisible(true);
		AppStyles.ApplyTextColors(View);
		_overview.Focus();
		_usageSummary?.Show();
	}

	internal void SetUsage(ProviderUsageSummary summary)
	{
		_usageSummary = summary;
		_usage.Child(summary.View).IsVisible(true);
	}

	public void ShowDetails() => SetDetails(true);

	private void SetDetails(bool details)
	{
		if (_showDetails == details) return;
		_showDetails = details;
		_overview.StyleName(details ? AppStyles.GhostButton : AppStyles.SelectedButton);
		_details.StyleName(details ? AppStyles.SelectedButton : AppStyles.GhostButton);
		foreach (var child in _body.Children.ToArray()) UiLifetime.Release(child);
		_body.Clear();
		_values.Clear();
		if (_display is { } display) UpdateRows(display);
	}

	public void Update(SessionStatisticsDisplay display, bool busy)
	{
		_compact.IsEnabled = !busy && display.Turns > 0;
		if (ReferenceEquals(_display, display)) return;
		_display = display;
		_segments.Update(display);
		_percent.Text(display.ContextUsedLabel);
		_capacity.Text(display.ContextCapacity).HoverTip(Strings.StatisticsContextHint);
		_cost.Text(display.CostValue).HoverTip(Strings.StatisticsUsageHint);
		_turns.Text(Strings.StatisticsTurnCount(display.Turns));
		UpdateRows(display);
	}

	private void UpdateRows(SessionStatisticsDisplay display)
	{
		var rows = _showDetails ? display.Rows() : display.OverviewRows();
		if (_values.Count == 0)
		{
			string? group = null;
			foreach (var row in rows)
			{
				if (row.Group != group)
				{
					group = row.Group;
					_body.Children(new TextBlock().Text(group).FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Bold).SubtleText()
						.Margin(new Thickness(0, AppTheme.Space3, 0, AppTheme.Space2)));
				}
				var value = new TextBlock().FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).PrimaryText();
				_values.Add(value);
				_body.Children(new DockPanel().LastChildFill().Margin(new Thickness(0, 4, 0, 4)).HoverTip(row.Hint)
					.Children(value.DockRight(), new TextBlock().Text(row.Label).FontSize(AppTheme.FontSizeCaption).MutedText()));
			}
		}
		for (var index = 0; index < rows.Count; index++) _values[index].Text(rows[index].Value);
	}

	public void Hide()
	{
		_usageSummary?.Hide();
		if (!IsOpen) return;
		View.IsVisible(false);
		_window?.OverlayLayer.Remove(View);
		_window = null;
		Closed?.Invoke();
	}
}
