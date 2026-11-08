using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 技能与提示词选择器：从「+」菜单打开，搜索后点条目把斜杠命令追加到输入框。
/// 数据在打开时取一次快照，过滤只读快照，不重新发现资源；键盘语义与命令面板一致。
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
	private readonly List<Button> _rows = [];
	private readonly List<Item> _targets = [];
	private readonly TextBlock _empty = new();
	private IReadOnlyList<Item> _all = [];
	private IReadOnlyList<Item> _matches = [];
	private int _selectedIndex;
	private Window? _window;
	private Action<Item>? _pick;
	private string _emptyText = string.Empty;
	private string _noMatchText = string.Empty;
	private readonly OverlayFade _fade;

	/// 选择器条目。Title 是插入输入框的斜杠命令，Origin 回答「这个技能/模板从哪来」。
	internal sealed record Item(string Icon, string Token, string Name, string Description, string Origin)
	{
		public string Title => $"/{Token}";
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
				new TextBlock().Text(Strings.ComposerInsertHint)
					.FontSize(AppTheme.FontSizeMicro)
					.SubtleText()));

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

	public void Show(UIElement owner, string title, string placeholder, string emptyText, string noMatchText, IReadOnlyList<Item> items, Action<Item> pick)
	{
		if (owner.FindVisualRoot() is not Window window)
		{
			return;
		}

		if (IsOpen)
		{
			if (_fade.IsClosing) _fade.Reopen();
			_query.Focus();
			return;
		}

		_title.Text(title);
		_hint.Text(placeholder);
		_emptyText = emptyText;
		_noMatchText = noMatchText;
		_pick = pick;
		_all = items;
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

	private void CloseNow()
	{
		View.IsVisible(false);
		if (_window is { } window)
		{
			TransientOverlay.Remove(window, View);
			_window = null;
		}

		_all = [];
		_matches = [];
		_rows.Clear();
		_targets.Clear();
		_results.Clear();
		_pick = null;
		Closed?.Invoke();
	}

	/// 过滤规则：空格分隔的每个词都要命中名称、描述或来源，与命令面板的语义一致。
	/// 没有查询词时保留资源发现顺序（项目优先）。
	internal static IReadOnlyList<Item> Filter(IReadOnlyList<Item> items, string? query)
	{
		var text = query?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			return items;
		}

		var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		return
		[
			.. items.Where(item => words.All(word =>
				item.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ||
				item.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
				item.Description.Contains(word, StringComparison.OrdinalIgnoreCase) ||
				item.Origin.Contains(word, StringComparison.OrdinalIgnoreCase)))
		];
	}

	/// 把斜杠命令追加到草稿尾部，与 PiDeck 的 appendSlashCommandToDraft 同义。
	/// 草稿最后一个词已经是半截斜杠命令时替换它，不拼成「/ /skill:x」。
	internal static string AppendCommand(string draft, string token)
	{
		var trimmed = draft.TrimEnd();
		if (trimmed.Length > 0)
		{
			var start = trimmed.LastIndexOfAny([' ', '\n', '\t']);
			if (trimmed[(start + 1)..].StartsWith('/'))
			{
				return $"{trimmed[..(start + 1)]}/{token} ";
			}
		}

		return trimmed.Length == 0 ? $"/{token} " : $"{trimmed} /{token} ";
	}

	private void Refresh()
	{
		_matches = Filter(_all, _query.Text);
		_selectedIndex = 0;
		_results.Clear();
		_rows.Clear();
		_targets.Clear();
		_scroll.SetScrollOffsets(0, 0);
		_empty.IsVisible(_matches.Count == 0);
		_empty.Text(_all.Count == 0 ? _emptyText : _noMatchText);
		if (_matches.Count == 0)
		{
			_scroll.IsVisible(false);
			return;
		}

		_scroll.IsVisible(true);
		foreach (var item in _matches.Take(MaxRows))
		{
			var row = Row(item);
			_rows.Add(row);
			_targets.Add(item);
			_results.Children(row);
		}

		Select(0);
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

		_selectedIndex = Math.Clamp(index, 0, _targets.Count - 1);
		for (var position = 0; position < _rows.Count; position++)
		{
			_rows[position].StyleName(position == _selectedIndex ? AppStyles.SelectedButton : AppStyles.GhostButton);
		}

		var top = _selectedIndex * (RowHeight + RowSpacing);
		if (top < _scroll.VerticalOffset)
		{
			_scroll.SetScrollOffsets(0, top);
		}
		else if (top + RowHeight > _scroll.VerticalOffset + _scroll.ViewportHeight)
		{
			_scroll.SetScrollOffsets(0, top + RowHeight - _scroll.ViewportHeight);
		}
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
