using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 技能与提示词选择器：从「+」菜单打开，搜索后点条目把斜杠命令追加到输入框。
/// 数据在打开时取一次快照，过滤只读快照；按键路径复用已建好的行，不重建控件树。
internal sealed class ComposerPicker
{
	private const double RowHeight = 50;
	private const double RowSpacing = 2;
	private const double PanelWidth = 560;
	private const int MaxRows = 200;

	private readonly TextBlock _title = new();
	private readonly TextBox _query;
	private readonly TextBlock _hint;
	private readonly StackPanel _results = new();
	private readonly ScrollViewer _scroll;
	private readonly TextBlock _empty = new();
	private readonly List<Button> _rows = [];
	private readonly List<Item> _targets = [];
	/// 行按条目缓存：过滤只调整可见性顺序，同一会话内反复搜索不再造控件树。
	private readonly Dictionary<Item, Button> _rowCache = [];
	private IReadOnlyList<Item> _all = [];
	private int _selectedIndex;
	private int _styledIndex = -1;
	private Window? _window;
	private Action<Item>? _pick;
	private string _emptyText = string.Empty;
	private string _noMatchText = string.Empty;
	private string _shownEmpty = string.Empty;
	private readonly OverlayFade _fade;

	/// 选择器条目。Title 是插入输入框的斜杠命令，Search 是建条目时算好的归一化搜索文本。
	internal sealed record Item(string Icon, string Token, string Name, string Description, string Origin)
	{
		public string Title => $"/{Token}";

		public string Search { get; } = SearchText.Normalize($"{Token} {Name} {Description} {Origin}");
	}

	public ComposerPicker()
	{
		_title.FontSize(AppTheme.FontSizeControl).FontWeight(FontWeight.Bold).PrimaryText();

		// 提示文本自己画：框架的 Placeholder 聚焦即隐藏，而弹窗一打开就聚焦在输入框上。
		_hint = new TextBlock()
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
				_hint.IsVisible(value.Length == 0);
				Refresh();
			})
			.OnKeyDown(key =>
			{
				switch (key.Key)
				{
					case Key.Escape:
						key.Handled = true;
						Hide();
						break;
					case Key.Enter:
						key.Handled = true;
						if (_targets.Count > 0)
						{
							Choose(_targets[_selectedIndex]);
						}
						break;
					case Key.Down:
					case Key.Up:
						key.Handled = true;
						Select(_selectedIndex + (key.Key == Key.Down ? 1 : -1));
						break;
				}
			});

		var icon = AppIcons.Icon("search", AppStyles.RoleSubtle, 16)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0));
		var esc = new Border()
			.Padding(new Thickness(6, 1, 6, 1))
			.CornerRadius(AppTheme.RadiusXs)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, badge) => badge.Background = AppTheme.For(theme).CardBackgroundAlt)
			.Child(new TextBlock().Text("Esc").FontSize(AppTheme.FontSizeMicro).SubtleText());
		var searchRow = new DockPanel().LastChildFill().Children(
			esc.DockRight(),
			new DockPanel().LastChildFill().Children(icon.DockLeft(), new Grid().Children(_query, _hint)));

		_results.Spacing(RowSpacing);
		_scroll = new ScrollViewer()
			.SmoothScrolling()
			.Content(_results)
			.AutoVerticalScroll()
			.NoHorizontalScroll()
			.MaxHeight(360);
		_empty.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap).IsVisible(false);

		var header = new DockPanel().LastChildFill().Children(
			AppIcons.Button("x", Strings.Cancel, Hide).DockRight(),
			_title);

		var panel = new Border()
			.StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3))
			.Child(new StackPanel().Spacing(AppTheme.Space3).Children(
				header,
				searchRow,
				_scroll,
				_empty,
				new TextBlock().Text(Strings.ComposerInsertHint).FontSize(AppTheme.FontSizeMicro).SubtleText()));

		// 与命令面板同一套：空白处点一下收起，卡片本身拿键盘事件。
		View = new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0x80, 0, 0, 0))
				.Cursor(CursorType.Arrow)
				.Focusable(false)
				.BorderThickness(0)
				.CornerRadius(0)
				.OnClick(Hide),
			new PickerSurface()
				.MaxWidth(PanelWidth)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(AppTheme.Space6))
				.Content(panel))
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Escape) return;
				key.Handled = true;
				Hide();
			});
		_fade = new OverlayFade((FrameworkElement)View);
	}

	public UIElement View { get; }

	public bool IsOpen => View.IsVisible;

	internal IReadOnlyList<Item> Matches => _targets;

	public event Action? Closed;

	/// 打开或换内容。标题不同即视为换一类条目（技能 ↔ 提示词）：标题相同只刷新快照，
	/// 保留用户的搜索词与滚动位置。
	public void Show(UIElement owner, string title, string placeholder, string emptyText, string noMatchText,
		IReadOnlyList<Item> items, Action<Item> pick)
	{
		if (owner.FindVisualRoot() is not Window window)
		{
			return;
		}

		var sameContent = IsOpen && string.Equals(_title.Text, title, StringComparison.Ordinal);
		if (IsOpen)
		{
			// 淡出途中又要显示：接回不透明度，否则上一段关闭动画会把刚换好内容的浮层收走。
			if (_fade.IsClosing) _fade.Reopen();
			if (!sameContent)
			{
				// 换内容等于换一份列表：旧行不再用得上，留着只是占内存。
				ResetRows();
			}
		}

		_title.Text(title);
		_hint.Text(placeholder);
		_emptyText = emptyText;
		_noMatchText = noMatchText;
		_pick = pick;
		_all = items;

		if (IsOpen)
		{
			if (!sameContent)
			{
				_query.Text = string.Empty;
				_hint.IsVisible(true);
			}

			Refresh();
			_query.Focus();
			return;
		}

		_query.Text = string.Empty;
		_hint.IsVisible(true);
		Refresh();
		_window = window;
		TransientOverlay.Open(window, View, Hide);
		AppStyles.ApplyTextColors(View);
		View.IsVisible(true);
		_fade.Open();
		_query.Focus();
	}

	public void Hide()
	{
		if (!IsOpen || _fade.IsClosing)
		{
			return;
		}

		_fade.Close(CloseNow);
	}

	/// 立即收起，不做淡出：标签页正在关闭时淡出没有意义，还会让浮层多挂在窗口上。
	public void CloseInstantly()
	{
		if (!IsOpen)
		{
			return;
		}

		_fade.Cancel();
		CloseNow();
	}

	private void CloseNow()
	{
		View.IsVisible(false);
		if (_window is { } window)
		{
			TransientOverlay.Remove(window, View);
			_window = null;
		}

		ResetRows();
		_all = [];
		_pick = null;
		_shownEmpty = string.Empty;
		Closed?.Invoke();
	}

	/// 清空当前列表与行缓存。行已从面板摘下，再逐个释放，避免缓存与控件树各留一份。
	private void ResetRows()
	{
		_targets.Clear();
		_rows.Clear();
		_results.Clear();
		ReleaseRows();
		_selectedIndex = 0;
		_styledIndex = -1;
	}

	/// 过滤规则：空格分隔的每个词都要命中名称、描述或来源，与命令面板的语义一致。
	/// 没有查询词时保留资源发现顺序（项目优先）。
	internal static IReadOnlyList<Item> Filter(IReadOnlyList<Item> items, string? query) =>
		Filter(items, SearchText.Words(query));

	/// 把斜杠命令追加到草稿尾部，与 PiDeck 的 appendSlashCommandToDraft 同义。
	/// 草稿最后一个词已经是半截斜杠命令时替换它，不拼成「/ /skill:x」。
	internal static string AppendCommand(string draft, string token)
	{
		var trimmed = draft.AsSpan().TrimEnd();
		if (trimmed.IsEmpty)
		{
			return string.Concat("/", token, " ");
		}

		var start = trimmed.LastIndexOfAny(' ', '\n', '\t');
		if (trimmed[(start + 1)..].StartsWith('/'))
		{
			return string.Concat(trimmed[..(start + 1)], "/", token, " ");
		}

		return string.Concat(trimmed, " /", token, " ");
	}

	private static IReadOnlyList<Item> Filter(IReadOnlyList<Item> items, ReadOnlySpan<string> words)
	{
		if (words.Length == 0)
		{
			return items;
		}

		var matches = new List<Item>(Math.Min(items.Count, MaxRows));
		foreach (var item in items)
		{
			if (!SearchText.ContainsAll(item.Search, words))
			{
				continue;
			}

			matches.Add(item);
			if (matches.Count == MaxRows)
			{
				break;
			}
		}

		return matches;
	}

	private void Refresh()
	{
		var matches = Filter(_all, SearchText.Words(_query.Text));
		if (!SameVisible(matches))
		{
			// 旧高亮行先回普通样式：行是复用的，留着会和新列表的选中行同时高亮。
			ClearStyledRow();
			_targets.Clear();
			_results.Clear();
			_rows.Clear();
			foreach (var item in matches)
			{
				var row = RowFor(item);
				_targets.Add(item);
				_rows.Add(row);
				_results.Children(row);
			}

			_selectedIndex = 0;
			_scroll.SetScrollOffsets(0, 0);
		}

		var empty = _targets.Count == 0;
		if (empty != _empty.IsVisible)
		{
			_empty.IsVisible(empty);
		}

		var emptyText = _all.Count == 0 ? _emptyText : _noMatchText;
		if (!string.Equals(_shownEmpty, emptyText, StringComparison.Ordinal))
		{
			_shownEmpty = emptyText;
			_empty.Text(emptyText);
		}

		_scroll.IsVisible(!empty);
		Select(empty ? 0 : Math.Min(_selectedIndex, _targets.Count - 1));
	}

	/// 列表没变就不动控件树：连续输入同一个词、或删掉不影响命中的字符时省掉整轮重建。
	/// <paramref name="matches"/> 已由 <see cref="Filter"/> 封顶，这里按实际条数比。
	private bool SameVisible(IReadOnlyList<Item> matches)
	{
		if (_targets.Count != matches.Count)
		{
			return false;
		}

		for (var index = 0; index < matches.Count; index++)
		{
			if (!ReferenceEquals(_targets[index], matches[index]))
			{
				return false;
			}
		}

		return true;
	}

	private Button RowFor(Item item)
	{
		if (!_rowCache.TryGetValue(item, out var row))
		{
			row = Row(item);
			_rowCache.Add(item, row);
		}

		return row;
	}

	private Button Row(Item item)
	{
		var tile = new Border()
			.Width(28)
			.Height(28)
			.CornerRadius(AppTheme.RadiusSm)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).CardBackgroundAlt)
			.Child(AppIcons.Icon(item.Icon, AppStyles.RoleSubtle, 14)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center));

		var title = new TextBlock()
			.Text(item.Title)
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeControl)
			.PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis);
		var description = new TextBlock()
			.Text(item.Description)
			.FontSize(AppTheme.FontSizeMicro)
			.MutedText()
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var origin = new TextBlock()
			.Text(item.Origin)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center);
		origin.IsVisible(item.Origin.Length > 0);
		var detail = new DockPanel().LastChildFill().Spacing(8).Children(origin.DockRight(), description);

		return new Button()
			.Ghost()
			.Height(RowHeight)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space3, 0))
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.HoverTip(item.Description.Length > 0 ? $"{item.Title}\n{item.Description}" : item.Title)
			.Content(new Grid()
				.Columns(GridLength.Pixels(28), GridLength.Stars(1))
				.Children(tile.Column(0), new StackPanel().Spacing(2).VerticalAlignment(VerticalAlignment.Center).Children(title, detail).Column(1)))
			.OnClick(() => Choose(item));
	}

	private void Choose(Item item)
	{
		// 淡出这 120ms 里视图还在：双击一行不能把同一条斜杠命令插两遍。
		if (_fade.IsClosing) return;
		var pick = _pick;
		Hide();
		pick?.Invoke(item);
	}

	private void Select(int index)
	{
		if (_targets.Count == 0)
		{
			return;
		}

		var next = Math.Clamp(index, 0, _targets.Count - 1);
		if (next != _styledIndex)
		{
			// 只改换位的两行：整表重写会把上百个按钮的样式重新解析一遍。
			ClearStyledRow();
			_rows[next].StyleName(AppStyles.SelectedButton);
			_styledIndex = next;
		}

		_selectedIndex = next;
		// 视口还没量出来（首次打开、刚换过内容）时不滚动：此时 extent 为空，滚动量会被夹成 0。
		if (_scroll.ViewportHeight <= 0)
		{
			return;
		}

		var top = next * (RowHeight + RowSpacing);
		if (top < _scroll.VerticalOffset)
		{
			_scroll.SetScrollOffsets(0, top);
		}
		else if (top + RowHeight > _scroll.VerticalOffset + _scroll.ViewportHeight)
		{
			_scroll.SetScrollOffsets(0, top + RowHeight - _scroll.ViewportHeight);
		}
	}

	private void ReleaseRows()
	{
		foreach (var row in _rowCache.Values)
		{
			UiLifetime.Release(row);
		}

		_rowCache.Clear();
		_styledIndex = -1;
	}

	/// 把当前高亮行改回普通样式。行会被复用，不清理就会在新列表里留下第二枚高亮。
	private void ClearStyledRow()
	{
		if (_styledIndex >= 0 && _styledIndex < _rows.Count)
		{
			_rows[_styledIndex].StyleName(AppStyles.GhostButton);
		}

		_styledIndex = -1;
	}

	private sealed class PickerSurface : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var width = Math.Min(PanelWidth, availableSize.Width);
			var measured = base.MeasureContent(new Size(width, availableSize.Height));
			return new Size(width, measured.Height);
		}
	}
}
