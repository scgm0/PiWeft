using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;

namespace PiWeft.Desktop;

/// 输入区底栏「快捷消息」弹层：搜索 + 每页 8 条 + 行尾直发。
/// 与「+」菜单、思考强度同一套原生 <see cref="FadePopup"/>：贴按钮上方，边界翻转、
/// 外点关闭与 Esc 由框架与卡片自己的按键处理负责。
/// Popup 本身不搬键盘焦点（owner 继续收键），打开后要手动对焦搜索框，
/// 与 <see cref="ThinkingPicker"/> 对焦滑条同一个理由。
internal sealed class ComposerQuickMessages : IDisposable
{
	/// 弹层宽度。与 PiDeck 的 26rem 同量级：长条目截断，不靠加宽容纳整句。
	internal const double CardWidth = 420;

	private const double RowHeight = 28;
	private const double RowSpacing = 2;
	private const double SendColumnWidth = 28;

	/// 固定 8 行高度：原生 Popup 只在打开时测量一次，内容随搜索变短时不能改高度，
	/// 否则会留下空白或缺一截。空态与「无匹配」都画在这块固定区域里。
	/// 每页固定 8 条，不需要滚动：留一点余量兜住 StackPanel 末尾也可能带上的间距。
	private const double ListHeight = 8 * (RowHeight + RowSpacing);

	private readonly FadePopup _popup = new();
	private readonly List<Button> _rowButtons = [];
	private readonly List<UIElement> _builtRows = [];
	private readonly List<string> _targets = [];
	private TextBox _query = null!;
	private TextBlock _placeholder = null!;
	private TextBlock _hint = null!;
	private TextBlock _empty = null!;
	private TextBlock _count = null!;
	private StackPanel _pager = null!;
	private StackPanel _rows = null!;
	private IReadOnlyList<string> _all = [];
	private int _page = 1;
	private int _pagerPages;
	private int _styledPage;
	private int _selectedIndex;
	private int _styledIndex = -1;
	private bool _canSend = true;
	private Action<string>? _insert;
	private Action<string>? _send;
	private Action? _manage;
	private Action? _focusOwner;

	public ComposerQuickMessages() => _popup.Closed += OnPopupClosed;

	public bool IsOpen => _popup.IsOpen;

	/// 当前页的条目（过滤、夹紧之后）。
	internal IReadOnlyList<string> Matches => _targets;

	internal int Page => _page;

	/// 每次打开重取一份清单：用户在设置里改完切回来就能看到。
	/// <param name="canSend">直发不可用时行尾按钮置灰；插入草稿不受影响。</param>
	/// <param name="focusOwner">Esc 或再按一次快捷键收起后把焦点交回输入框。</param>
	public void Show(UIElement anchor, IReadOnlyList<string> items, bool canSend,
		Action<string> insert, Action<string> send, Action manage, Action focusOwner)
	{
		// 无头（单测、诊断脚本未 Run）时静默降级，不把一次「点开弹层」升级成异常。
		if (!Application.IsRunning)
		{
			return;
		}

		CloseInstantly();
		if (anchor.FindVisualRoot() is not Window window)
		{
			return;
		}

		_focusOwner = focusOwner;
		// 窄窗口下把弹层收到可视宽度内：Popup 只夹位置，不会帮内容换行。
		var width = Math.Min(CardWidth, Math.Max(260, window.ClientSize.Width - (AppTheme.Space4 * 2)));
		var card = Build(items, canSend, insert, send, manage, width);
		AppStyles.ApplyTextColors(card);
		_popup.Show(card, anchor, HoverHints.Anchor(anchor), PopupAnchorSide.Above);
		// Popup 不搬焦点；不手动对焦，搜索框收不到输入。
		_query.Focus();
	}

	/// 收起并把焦点交回输入框。框架自己关掉弹层（外点、切窗）时不会走这里，
	/// 因此点侧栏之类的操作不会被抢回输入框。
	public void Hide()
	{
		if (!_popup.IsOpen)
		{
			return;
		}

		_popup.Close();
		_focusOwner?.Invoke();
	}

	/// 立即收起，不做淡出：标签页正在关闭或切走时淡出只会把弹层多留在窗口上。
	public void CloseInstantly() => _popup.CloseInstantly();

	/// 换肤时浮层不一定在窗口的常规视觉树里，颜色要单独刷一遍。
	public void ApplyTheme()
	{
		if (_popup.Content is { } content)
		{
			AppStyles.ApplyTextColors(content);
		}
	}

	public void Dispose()
	{
		_focusOwner = null;
		_popup.Dispose();
		GC.SuppressFinalize(this);
	}

	internal UIElement BuildForTests(IReadOnlyList<string> items, bool canSend,
		Action<string> insert, Action<string> send, Action manage) =>
		Build(items, canSend, insert, send, manage, CardWidth);

	private UIElement Build(IReadOnlyList<string> items, bool canSend,
		Action<string> insert, Action<string> send, Action manage, double width)
	{
		_all = items;
		_canSend = canSend;
		_insert = insert;
		_send = send;
		_manage = manage;
		_page = 1;
		_pagerPages = 0;
		_selectedIndex = 0;
		_styledIndex = -1;

		// 上一份内容已由 FadePopup 释放，这里只丢引用，不能再 Release 一遍。
		_targets.Clear();
		_rowButtons.Clear();
		_builtRows.Clear();

		// 占位文本自己画：框架的 Placeholder 聚焦即隐藏，而弹层一打开焦点就在搜索框。
		_placeholder = new TextBlock()
			.Text(Strings.QuickMessagesSearch)
			.FontSize(AppTheme.FontSizeControl)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.IsHitTestVisible(false);
		_query = new TextBox().PrimarySelection()
			.FontSize(AppTheme.FontSizeControl)
			.Background(AppTheme.Transparent)
			.BorderThickness(0)
			.Padding(Thickness.Zero)
			.OnTextChanged(value =>
			{
				var empty = value.Length == 0;
				if (empty != _placeholder.IsVisible) _placeholder.IsVisible(empty);
				// 换搜索词回到第一页：否则停在搜完旧词才存在的页码上。
				_page = 1;
				Refresh();
			})
			.OnKeyDown(OnQueryKeyDown);

		var searchIcon = AppIcons.Icon("search", AppStyles.RoleSubtle, 15)
			.Margin(new Thickness(0, 0, AppTheme.Space2 - 2, 0));
		var searchFrame = new Border()
			.Height(AppTheme.ControlHeightMd)
			.CornerRadius(AppTheme.RadiusSm)
			.BorderThickness(1)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.WithTheme((theme, border) =>
			{
				border.Background = AppTheme.For(theme).CardBackgroundAlt;
				border.BorderBrush = AppTheme.For(theme).Border;
			})
			.Child(new DockPanel().LastChildFill().Children(
				searchIcon.DockLeft(),
				new Grid().Children(_query, _placeholder)));
		var manageButton = AppIcons.Button("settings", Strings.QuickMessagesManage, ChooseManage, size: 14)
			.Width(AppTheme.ControlHeightMd).Height(AppTheme.ControlHeightMd)
			.Margin(new Thickness(AppTheme.Space2 - 2, 0, 0, 0))
			.Padding(Thickness.Zero);
		var searchRow = new DockPanel().LastChildFill().Children(manageButton.DockRight(), searchFrame);

		// 没有条目时这行直接说去哪里加：空清单再讲「点条目插入」是误导。
		_hint = new TextBlock()
			.Text(items.Count == 0 ? Strings.QuickMessagesEmpty : Strings.QuickMessagesHint)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextWrapping(TextWrapping.Wrap);

		_rows = new StackPanel().Spacing(RowSpacing).VerticalAlignment(VerticalAlignment.Top);
		_empty = new TextBlock()
			.Text(Strings.QuickMessagesNoMatch)
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.TextWrapping(TextWrapping.Wrap)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center)
			.IsVisible(false);
		// 固定高度：原生弹层只在打开时测量一次，行数变化不能让卡片改高。
		// 不做滚动：每页固定 8 条，分页已经兜住更多条目，滚动条只会碍眼。
		var listHost = new Grid().Height(ListHeight).ClipToBounds(true).Children(_rows, _empty);

		_count = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText()
			.VerticalAlignment(VerticalAlignment.Center);
		_pager = new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
			.HorizontalAlignment(HorizontalAlignment.Right);
		var footer = new Border()
			.Padding(new Thickness(0, AppTheme.Space2, 0, 0))
			.BorderThickness(new Thickness(0, 1, 0, 0))
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).BorderSubtle)
			.Child(new DockPanel().LastChildFill().Children(_count.DockLeft(), _pager));

		var card = new Border()
			.StyleName(AppStyles.PopupCard)
			.Width(width)
			.Padding(new Thickness(AppTheme.Space3))
			.OnKeyDown(OnPopupKeyDown)
			.Child(new StackPanel().Spacing(AppTheme.Space2).Children(searchRow, _hint, listHost, footer));
		// 点空白/说明行时把焦点拉回搜索框：否则方向键与回车都到不了弹层。
		// 行、页码、齿轮自己是按钮，会先拿到焦点再执行点击。
		card.MouseDown += down =>
		{
			if (down.Button == MouseButton.Left) _query.Focus();
		};

		Refresh();
		return card;
	}

	private void OnQueryKeyDown(KeyEventArgs key)
	{
		switch (key.Key)
		{
			case Key.Escape:
				key.Handled = true;
				Hide();
				break;
			case Key.Enter:
				// 输入法组字期间的回车是选字，不能当成「插入首条」。
				if (_query is ITextCompositionClient { IsComposing: true })
				{
					return;
				}

				key.Handled = true;
				Choose(_selectedIndex);
				break;
			case Key.Down:
			case Key.Up:
				key.Handled = true;
				Select(_selectedIndex + (key.Key == Key.Down ? 1 : -1));
				break;
		}
	}

	/// 焦点被 Tab 带到页码或齿轮上时，Esc 也要能收起。
	private void OnPopupKeyDown(KeyEventArgs key)
	{
		if (key.Key != Key.Escape)
		{
			return;
		}

		key.Handled = true;
		Hide();
	}

	private void Refresh()
	{
		var filtered = QuickMessages.Filter(_all, _query.Text);
		var (pageItems, page, totalPages) = QuickMessages.Paginate(filtered, _page);
		_page = page;

		// 连续输入时命中集合常常不变（打了「推」再打空格），行没换就不重建控件树。
		if (!SameRows(pageItems))
		{
			ClearRows();
			foreach (var item in pageItems)
			{
				var row = Row(item, out var mainButton);
				_targets.Add(item);
				_rowButtons.Add(mainButton);
				_builtRows.Add(row);
				_rows.Children(row);
			}

			_selectedIndex = _targets.Count > 0 ? 0 : -1;
			Select(_selectedIndex);
		}

		var noMatch = _targets.Count == 0 && _all.Count > 0;
		if (noMatch != _empty.IsVisible)
		{
			_empty.IsVisible(noMatch);
		}

		_rows.IsVisible(!noMatch);
		var countText = Strings.QuickMessagesCount(filtered.Count);
		if (!string.Equals(_count.Text, countText, StringComparison.Ordinal))
		{
			_count.Text = countText;
		}

		BuildPager(totalPages);
		_pager.IsVisible(totalPages > 1);
	}

	private bool SameRows(IReadOnlyList<string> items)
	{
		if (_targets.Count != items.Count)
		{
			return false;
		}

		for (var index = 0; index < items.Count; index++)
		{
			if (!string.Equals(_targets[index], items[index], StringComparison.Ordinal))
			{
				return false;
			}
		}

		return true;
	}

	private void BuildPager(int totalPages)
	{
		if (_pagerPages != totalPages)
		{
			_pagerPages = totalPages;
			_styledPage = _page;
			_pager.Clear();
			for (var page = 1; page <= totalPages; page++)
			{
				var target = page;
				_pager.Children(new Button()
					.Content(new TextBlock().Text(page.ToString()).FontSize(AppTheme.FontSizeMicro)
						.HorizontalAlignment(HorizontalAlignment.Center)
						.VerticalAlignment(VerticalAlignment.Center))
					.Width(24).Height(24).Padding(Thickness.Zero).CornerRadius(AppTheme.RadiusXs)
					.StyleName(page == _page ? AppStyles.SelectedButton : AppStyles.GhostButton)
					.OnClick(() =>
					{
						_page = target;
						Refresh();
					}));
			}

			return;
		}

		if (_styledPage == _page)
		{
			return;
		}

		// 页码点击后只改样式：在按钮自己的点击回调里把它摘掉会丢掉这次点击。
		_styledPage = _page;
		for (var index = 0; index < _pager.Children.Count; index++)
		{
			if (_pager.Children[index] is Button button)
			{
				button.StyleName(index + 1 == _page ? AppStyles.SelectedButton : AppStyles.GhostButton);
			}
		}
	}

	private UIElement Row(string item, out Button mainButton)
	{
		var label = new TextBlock()
			.Text(item)
			.FontSize(AppTheme.FontSizeControl)
			.PrimaryText()
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);

		var main = new Button()
			.Ghost()
			.Height(RowHeight)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.HoverTip(item)
			.Content(label)
			.OnClick(() => ChooseInsert(item));
		mainButton = main;

		var send = AppIcons.Button("send-horizontal",
				_canSend ? Strings.QuickMessagesSend : Strings.QuickMessagesSendUnavailable,
				() => ChooseSend(item), size: 14)
			.Width(SendColumnWidth).Height(RowHeight).Padding(Thickness.Zero)
			.IsEnabled(_canSend);

		return new Grid()
			.Columns(GridLength.Stars(1), GridLength.Pixels(SendColumnWidth))
			.Children(main.Column(0), send.Column(1));
	}

	private void Choose(int index)
	{
		if (index < 0 || index >= _targets.Count)
		{
			return;
		}

		ChooseInsert(_targets[index]);
	}

	private void ChooseInsert(string item)
	{
		var insert = _insert;
		_popup.Close();
		insert?.Invoke(item);
	}

	/// 直发后把焦点交回输入框：用户下一句通常接着打，而不是停在弹层按钮上。
	private void ChooseSend(string item)
	{
		if (!_canSend)
		{
			return;
		}

		var send = _send;
		_popup.Close();
		send?.Invoke(item);
		_focusOwner?.Invoke();
	}

	private void ChooseManage()
	{
		var manage = _manage;
		_popup.Close();
		manage?.Invoke();
	}

	private void Select(int index)
	{
		if (_targets.Count == 0)
		{
			ClearStyledRow();
			return;
		}

		var next = Math.Clamp(index, 0, _targets.Count - 1);
		if (next == _styledIndex)
		{
			_selectedIndex = next;
			return;
		}

		ClearStyledRow();
		_rowButtons[next].StyleName(AppStyles.SelectedButton);
		_styledIndex = next;
		_selectedIndex = next;
	}

	private void ClearStyledRow()
	{
		if (_styledIndex >= 0 && _styledIndex < _rowButtons.Count)
		{
			_rowButtons[_styledIndex].StyleName(AppStyles.GhostButton);
		}

		_styledIndex = -1;
	}

	/// 过滤/翻页时重建当前页的行：一页最多 8 条，重建比维护「旧行 → 新条目」的映射便宜。
	private void ClearRows()
	{
		_targets.Clear();
		_rowButtons.Clear();
		_rows.Clear();
		foreach (var row in _builtRows)
		{
			UiLifetime.Release(row);
		}

		_builtRows.Clear();
		_styledIndex = -1;
	}

	private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
	{
		// 弹层关掉后旧回调不再有调用方；下一次 Show 会重新设置。
		_insert = null;
		_send = null;
		_manage = null;
	}
}
