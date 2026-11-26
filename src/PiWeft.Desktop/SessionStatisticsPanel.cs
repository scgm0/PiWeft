using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 会话统计弹窗：走窗口的 TransientOverlay，卡片锚在统计入口上方，外点、切窗或 Esc 收起。
/// 内容只建一次：统计数据与余额摘要跨开关复用，收起只摘层。
/// 卡片跟着窗口一起重绘，切页签改高度时不会碰到原生窗口重设尺寸那一帧。
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
	private readonly ShadowDecorator _shadow;
	private readonly Grid _overlay;
	private readonly OverlayFade _fade;
	private SessionStatisticsDisplay? _display;
	private Window? _window;
	private UIElement? _anchor;
	private bool _showDetails;
	private bool _open;

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
		// 卡片与窗口一起排布：高度变了只重画窗口，不再有原生弹层窗口改尺寸时先露一帧旧像素的问题。
		_shadow = new ShadowDecorator()
			.BlurRadius(8)
			.OffsetY(4)
			.CornerRadius(AppTheme.RadiusMd)
			// 与 PopupChrome 的落影同参：8 模糊、下移 4，深色 50% / 浅色 25%。
			.WithTheme((theme, shadow) => shadow.ShadowColor = theme.IsDark
				? new Color(0x80, 0x00, 0x00, 0x00)
				: new Color(0x40, 0x00, 0x00, 0x00))
			.HorizontalAlignment(HorizontalAlignment.Right)
			.VerticalAlignment(VerticalAlignment.Bottom);
		_fade = OverlayFade.PopupLike(_shadow);
		// 空白处的透明捕获层只负责收起；卡片在它上面，不受影响。
		var scrim = OverlayScrim.Create(Hide, ScrimStrength.None);
		_overlay = new Grid().Children(scrim, _shadow);
	}

	public UIElement View { get; }
	public bool IsOpen => _open;
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
		if (anchor.FindVisualRoot() is not Window window) return;
		_usageSummary?.Show();
		if (_open)
		{
			FocusActiveTab();
			return;
		}
		// 统计入口就在输入区下方：提示属于入口却不归浮层管，会叠在刚打开的卡片上。
		HoverHints.Cancel(anchor);
		if (_window is null)
		{
			window.Deactivated += OnWindowDeactivated;
			window.WindowStateChanged += OnWindowStateChanged;
		}
		_window = window;
		_anchor = anchor;
		_shadow.Child = View;
		Place(anchor, window);
		TransientOverlay.Open(window, _overlay, Hide);
		_open = true;
		if (_fade.IsClosing) _fade.Reopen();
		else _fade.Open();
		FocusActiveTab();
	}

	// 卡片贴锚点右缘、底边停在锚点上方；窗口里放不下时压矮卡片，交给它自己的滚动区。
	// 用右/下边距定位：窗口尺寸变化时卡片跟着右缘与底边，和输入区一起走。
	private void Place(UIElement anchor, Window window)
	{
		var (margin, maxHeight) = Placement(anchor.Bounds, window.ClientSize,
			OverlayInsetFollower.InsetFor(window), _shadow.ShadowPadding);
		_shadow.Margin(margin);
		_shadow.MaxHeight(maxHeight);
	}

	/// 窗口内浮层的摆放尺寸：返回卡片容器的外边距与最大高度。
	/// 边距以窗口级浮层的可见区（窗口减去标题栏与阴影边）的右/下缘为基准，
	/// 锚点随输入区贴着这两条边时卡片就能跟着走。
	internal static (Thickness Margin, double MaxHeight) Placement(
		Rect anchor, Size client, Thickness inset, Thickness shadowPadding)
	{
		var gridRight = Math.Max(0, client.Width - inset.Right);
		var gridBottom = Math.Max(0, client.Height - inset.Bottom);
		var margin = new Thickness(0, 0,
			Math.Max(0, gridRight - (anchor.Right + shadowPadding.Right)),
			Math.Max(0, gridBottom - (anchor.Y - AnchorGap + shadowPadding.Bottom)));
		// 卡片最高长到浮层可见区的顶边（标题栏下方），再高就压矮滚动。
		var maxHeight = Math.Max(0, anchor.Y - AnchorGap - inset.Top - shadowPadding.Top) + shadowPadding.VerticalThickness;
		return (margin, maxHeight);
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
		if (!_open) return;
		_open = false;
		_fade.Close(CloseNow);
	}

	/// 立即收起，不做淡出：切标签时浮层要跟着旧页签一起走。
	public void CloseInstantly()
	{
		_usageSummary?.Hide();
		if (!_open && !_fade.IsClosing) return;
		_open = false;
		_fade.Cancel();
		CloseNow();
	}

	public void Dispose()
	{
		_fade.Cancel();
		_open = false;
		Detach();
		_usageSummary?.Hide();
		UiLifetime.Release(View);
		UiLifetime.Release(_overlay);
	}

	// 外点/切窗收起时也要停掉余额查询，并让标签页把焦点收回输入区。
	private void CloseNow()
	{
		Detach();
		Closed?.Invoke();
	}

	private void Detach()
	{
		if (_window is { } window)
		{
			window.Deactivated -= OnWindowDeactivated;
			window.WindowStateChanged -= OnWindowStateChanged;
			TransientOverlay.Remove(window, _overlay);
			_window = null;
		}
		_anchor = null;
		_shadow.Child = null;
	}

	private void OnWindowDeactivated() => CloseInstantly();

	// 最大化/还原会换一套标题栏让位量；遮罩由 TransientOverlay 跟着走，卡片得按新边距再摆一次。
	private void OnWindowStateChanged(WindowState _)
	{
		if (_window is { } window && _anchor is { } anchor) Place(anchor, window);
	}
}
