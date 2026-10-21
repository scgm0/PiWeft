using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 消息区右缘的定位轴：一条真实用户消息一枚刻度，悬浮/键盘聚焦预览，点击跳转。
/// 悬浮预览按 <see cref="HoverHints.Delay"/> 延迟出现（与其它悬停提示一致），
/// 键盘聚焦则立即出现；预览交给 Popup 定位，不参与消息测量，也不拦截正文的鼠标命中。
/// </summary>
internal sealed class ConversationOutline : IDisposable
{
	internal readonly record struct Item(int RowIndex, string Title, string Time);
	private const double RailWidth = 30;
	private readonly Grid _view;
	private readonly StackPanel _tracks;
	private readonly Border _card;
	private readonly TextBlock _title;
	private readonly TextBlock _time;
	private readonly List<Button> _rows = [];
	private readonly List<Border> _bars = [];
	private IReadOnlyList<Item> _items = [];
	private Item[] _rendered = [];
	private int[] _renderedRows = [];
	private double _availableHeight;
	private double _pitch;
	private int _activeSource = -1;
	private int _activeRail = -1;
	private int _previewIndex = -1;
	private int _pendingRail = -1;
	private readonly DispatcherTimer _hoverDelay = new(HoverHints.Delay);
	private Popup? _popup;
	private Window? _window;

	public ConversationOutline()
	{
		_title = new TextBlock().FontSize(AppTheme.FontSizeCaption).PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis).TextWrapping(TextWrapping.NoWrap);
		_time = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText();
		_card = new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(12)).Width(260).IsHitTestVisible(false)
			.Child(new StackPanel().Spacing(5).Children(_title, _time));
		_tracks = new StackPanel().VerticalAlignment(VerticalAlignment.Center);
		_view = new Grid().Width(RailWidth).HorizontalAlignment(HorizontalAlignment.Right)
			.Margin(new Thickness(0, AppTheme.Space2, 14, AppTheme.Space2))
			.Children(_tracks);
		_view.Tag = this;
		_view.MouseLeave += HidePreview;
		_view.MouseWheel += _ => HidePreview();
		_hoverDelay.Tick += () =>
		{
			_hoverDelay.Stop();
			var rail = _pendingRail;
			_pendingRail = -1;
			// 指针已经离开或移到别的刻度：这一刻不需要再弹预览。
			if (rail >= 0 && rail == _previewIndex) ShowPreview(rail);
		};
		_view.SizeChanged += args =>
		{
			if (Math.Abs(args.NewSize.Height - _availableHeight) < 0.5) return;
			_availableHeight = args.NewSize.Height;
			Rebuild();
		};
	}

	public FrameworkElement View => _view;
	public event Action<int>? Jump;
	internal int TickCount => _rows.Count;
	internal IReadOnlyList<int> RenderedRows => _renderedRows;
	internal int ActiveRow => _activeRail >= 0 ? _renderedRows[_activeRail] : -1;
	internal bool IsPreviewOpen => _popup?.IsOpen == true;
	internal string PreviewTitle => _title.Text;
	internal string PreviewTime => _time.Text;
	internal IEnumerable<(int Row, Rect Bounds)> Ticks => _rows.Select((row, index) => (_renderedRows[index], row.Bounds));

	public void SetVisible(bool visible)
	{
		if (!visible) HidePreview();
		_view.IsVisible = visible;
	}

	public void SetItems(IReadOnlyList<Item> items)
	{
		if (_items.SequenceEqual(items)) return;
		_items = items.ToArray();
		Rebuild();
	}

	public void SetActiveRow(int rowIndex)
	{
		if (rowIndex == _activeSource) return;
		_activeSource = rowIndex;
		var previous = _activeRail;
		_activeRail = rowIndex < 0 ? -1 : OutlineRailTicks.NearestRendered(_renderedRows, rowIndex);
		RefreshTick(previous);
		RefreshTick(_activeRail);
	}

	private void Rebuild()
	{
		var plan = OutlineRailTicks.Build(_items, _availableHeight);
		if (_pitch == plan.Size && _rendered.SequenceEqual(plan.Items)) return;
		HidePreview();
		foreach (var row in _rows)
		{
			_tracks.Remove(row);
			UiLifetime.Release(row);
		}
		_rows.Clear();
		_bars.Clear();
		_pitch = plan.Size;
		_rendered = plan.Items.ToArray();
		_renderedRows = _rendered.Select(item => item.RowIndex).ToArray();
		_activeRail = _activeSource < 0 ? -1 : OutlineRailTicks.NearestRendered(_renderedRows, _activeSource);
		for (var index = 0; index < _rendered.Length; index++)
		{
			var railIndex = index;
			var bar = new Border().Height(1).HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center).IsHitTestVisible(false)
				.WithTheme((theme, border) => border.Background = railIndex == _activeRail || railIndex == _previewIndex
					? AppTheme.For(theme).TextPrimary : AppTheme.For(theme).TextFaint);
			// 整格可点击，不要求用户精确点中 1px 细线；保留 Button 的键盘激活与焦点反馈。
			var row = new Button().Ghost().Padding(0).MinHeight(0).Height(_pitch).Width(RailWidth)
				.Cursor(CursorType.Hand).Content(bar).OnClick(() => Select(railIndex));
			row.Tag = _rendered[index];
			row.MouseEnter += () => Hover(railIndex);
			row.MouseLeave += HidePreview;
			// 键盘导航本来就慢，聚焦立即给预览，不叠加鼠标那套延迟。
			row.GotFocus += () => Preview(railIndex);
			row.LostFocus += HidePreview;
			row.KeyDown += args => Navigate(args, railIndex);
			_rows.Add(row);
			_bars.Add(bar);
			_tracks.Children(row);
			RefreshTick(index);
		}
	}

	private void RefreshTick(int index)
	{
		if (index < 0 || index >= _bars.Count) return;
		var active = index == _activeRail;
		var hot = index == _previewIndex;
		_bars[index].Width = active || hot ? 22 : 12;
		_bars[index].Background = active || hot ? AppTheme.TextPrimary : AppTheme.TextFaint;
	}

	internal void Select(int railIndex)
	{
		if (railIndex < 0 || railIndex >= _rendered.Length) return;
		HidePreview();
		var rowIndex = _rendered[railIndex].RowIndex;
		SetActiveRow(rowIndex);
		Jump?.Invoke(rowIndex);
	}

	private void Navigate(KeyEventArgs args, int index)
	{
		var next = args.Key switch
		{
			Key.Up => Math.Max(0, index - 1),
			Key.Down => Math.Min(_rows.Count - 1, index + 1),
			Key.Home => 0,
			Key.End => _rows.Count - 1,
			_ => -1,
		};
		if (args.Key == Key.Escape) { HidePreview(); args.Handled = true; }
		if (next < 0) return;
		args.Handled = true;
		_rows[next].Focus();
	}

	/// <summary>指针悬浮：刻度立即高亮（与按钮悬停一致），文字预览等 <see cref="HoverHints.Delay"/>。</summary>
	internal void Hover(int railIndex)
	{
		if (!SelectRail(railIndex)) return;
		_pendingRail = railIndex;
		_hoverDelay.Stop();
		_hoverDelay.Start();
	}

	/// <summary>键盘聚焦与测试入口：立即显示预览，不叠加鼠标的悬停延迟。</summary>
	internal void Preview(int railIndex)
	{
		if (!SelectRail(railIndex)) return;
		_pendingRail = -1;
		_hoverDelay.Stop();
		ShowPreview(railIndex);
	}

	/// <summary>是否有一个悬浮预览正在等待延迟到达。</summary>
	internal bool PreviewPending => _pendingRail >= 0;

	/// <summary>记录当前刻度并更新标题/时间与高亮；越界时返回 false。</summary>
	private bool SelectRail(int railIndex)
	{
		if (railIndex < 0 || railIndex >= _rendered.Length) return false;
		var previous = _previewIndex;
		_previewIndex = railIndex;
		RefreshTick(previous);
		RefreshTick(railIndex);
		_title.Text(_rendered[railIndex].Title);
		_time.Text(_rendered[railIndex].Time);
		return true;
	}

	private void ShowPreview(int railIndex)
	{
		if (!Application.IsRunning || _view.FindVisualRoot() is not Window window || window.Handle == 0) return;
		if (!ReferenceEquals(_window, window))
		{
			if (_window is not null) _window.Deactivated -= HidePreview;
			_window = window;
			window.Deactivated += HidePreview;
		}
		_popup ??= new Popup().IsHitTestVisible(false).Content(_card);
		// Popup 接收窗口坐标，直接使用刻度的 Bounds；自动夹入屏幕并处理边界翻转。
		var bounds = _rows[railIndex].Bounds;
		_popup.ShowAt(_rows[railIndex], new Rect(bounds.X - 8, bounds.Y, bounds.Width + 8, bounds.Height), PopupAnchorSide.Left);
	}

	public void HidePreview()
	{
		_hoverDelay.Stop();
		_pendingRail = -1;
		var previous = _previewIndex;
		_previewIndex = -1;
		_popup?.Close();
		RefreshTick(previous);
	}

	public void Dispose()
	{
		HidePreview();
		_hoverDelay.Dispose();
		if (_window is not null) _window.Deactivated -= HidePreview;
		UiLifetime.Release(_card);
		_popup?.Dispose();
		_popup = null;
		_window = null;
	}
}
