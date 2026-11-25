using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 会话统计弹窗：与「+」菜单同一套原生浮层，锚定在统计入口上方，外点、切窗或 Esc 收起。
/// 内容只建一次：统计数据与余额摘要跨开关复用，关闭只卸载；FadePopup 因此不接管释放。
public sealed class SessionStatisticsPanel : IDisposable
{
	private const double CardWidth = 420;

	// 与「+」菜单同一道缝：锚点上下各留 6，弹窗不贴着按钮边。
	private const double AnchorGap = 6;

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
	private readonly FadePopup _popup = new() { ReleaseContentOnClose = false };
	private SessionStatisticsDisplay? _display;
	private bool _showDetails;

	public SessionStatisticsPanel(Action compact)
	{
		_compact = AppIcons.PrimaryButton("square-slash", Strings.CompactContext, compact)
			.Margin(new Thickness(0, 0, AppTheme.ScrollBarSlot, 0));
		_overview = AppIcons.TextButton("eye", Strings.StatisticsOverview, () => SetDetails(false));
		_details = AppIcons.TextButton("settings", Strings.StatisticsDetails, () => SetDetails(true));
		_overview.StyleName(AppStyles.SelectedButton);
		var scroll = new ScrollViewer().SmoothScrolling().Content(new StackPanel().Spacing(AppTheme.Space2).Children(_body, _usage))
			.AutoVerticalScroll().NoHorizontalScroll()
			// 滚动条覆盖在内容上，贴着视口右缘画。卡片右内边距只留 6，滚动体自己补一格命中区：
			// 内容右缘与标题、按钮一样停在 16 DIP，左右不再一宽一窄。
			.Padding(new Thickness(0, 0, AppTheme.ScrollBarSlot, 0))
			.MaxHeight(480);
		// 标题区与 PiDeck 的上下文弹窗同结构：先给结论（已用百分比 + ~已用/窗口），
		// 再用一条三段占用条与图例拆开系统、工具、对话；只看一个百分比无法判断
		// 是上下文真的长，还是工具 schema 很大。
		var heading = new StackPanel().Spacing(AppTheme.Space2)
			.Margin(new Thickness(0, 0, AppTheme.ScrollBarSlot, 0)).Children(
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
		// 宽度定死：浮层按内容测量，行尾数值与标题才对得齐；高度交给滚动体，窗口矮时由浮层收口。
		View = new Border().Width(CardWidth).MaxWidth(CardWidth)
			.StyleName(AppStyles.PopupCard)
			.Padding(new Thickness(AppTheme.Space4, AppTheme.Space4, AppTheme.Space4 - AppTheme.ScrollBarSlot, AppTheme.Space4))
			.Child(content)
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Escape) return;
				key.Handled = true;
				Hide();
			});
		_popup.Closed += OnPopupClosed;
	}

	public UIElement View { get; }
	public bool IsOpen => _popup.IsOpen;
	public event Action? Closed;

	/// 打开在锚点上方。无后端（单测、诊断脚本未 Run）时只更新内容，不弹窗，与「+」菜单一致。
	public void Show(UIElement anchor, SessionStatisticsDisplay display, bool busy)
	{
		Update(display, busy);
		if (!Application.IsRunning)
		{
			// 无后端（单测、诊断脚本未 Run）：内容照常更新，不弹窗。
			_usageSummary?.Show();
			return;
		}
		if (anchor.FindVisualRoot() is not Window) return;
		_usageSummary?.Show();
		if (IsOpen)
		{
			FocusActiveTab();
			return;
		}
		// 统计入口就在输入区下方：提示属于入口却不归浮层管，会叠在刚打开的卡片上。
		HoverHints.Cancel(anchor);
		_popup.Show(View, anchor, Anchor(anchor), PopupAnchorSide.Above);
		FocusActiveTab();
	}

	/// 锚点矩形与卡片同宽并贴住按钮右缘：Popup 的横向夹取以矩形左缘为起点，
	/// 这样大卡片右缘停在按钮右缘，不会被推到屏幕外。
	private static Rect Anchor(UIElement anchor)
	{
		var bounds = anchor.Bounds;
		return new Rect(Math.Max(0, bounds.Right - CardWidth), bounds.Y - AnchorGap, CardWidth, bounds.Height + (AnchorGap * 2));
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
		// 键盘焦点也交给新页签，键盘操作从当前页签继续。
		if (IsOpen) FocusActiveTab();
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

	/// 焦点跟着当前页签：明细态重新打开时，键盘操作从明细继续，不回落到概览。
	internal void FocusActiveTab() => (_showDetails ? _details : _overview).Focus();

	public void Hide()
	{
		_usageSummary?.Hide();
		_popup.Close();
	}

	/// 立即收起，不做淡出：切标签时浮层要跟着旧页签一起走。
	public void CloseInstantly()
	{
		_usageSummary?.Hide();
		_popup.CloseInstantly();
	}

	public void Dispose()
	{
		_popup.Closed -= OnPopupClosed;
		_usageSummary?.Hide();
		_popup.Dispose();
		UiLifetime.Release(View);
	}

	// 框架自己关掉的路径（外点、切窗）也要停掉余额查询，并让标签页把焦点收回输入区。
	private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
	{
		_usageSummary?.Hide();
		Closed?.Invoke();
	}
}
