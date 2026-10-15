using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>命令面板里的一条命令。</summary>
public sealed record PaletteCommand(string Title, string Detail, Action Run);

/// <summary>可搜索、用方向键选择并执行的命令面板。</summary>
public sealed class CommandPalette
{
	private const double RowHeight = 54;
	private readonly Func<IReadOnlyList<PaletteCommand>> _commands;
	private readonly TextBox _query;
	private readonly StackPanel _results = new();
	private readonly ScrollViewer _scroll;
	private readonly List<Button> _rows = [];
	private readonly TextBlock _count = new();
	private readonly Func<int, int, string>? _countText;
	private readonly string _emptyText;
	private IReadOnlyList<PaletteCommand> _all = [];
	private IReadOnlyList<PaletteCommand> _matches = [];
	private int _selectedIndex;
	private Window? _window;

	public event Action? Closed;

	public CommandPalette(Func<IReadOnlyList<PaletteCommand>> commands, string? title = null,
		string? placeholder = null, string? emptyText = null, Func<int, int, string>? countText = null)
	{
		_commands = commands;
		_countText = countText;
		_emptyText = emptyText ?? Strings.PaletteNoMatch;
		_query = new TextBox().PrimarySelection()
			.Placeholder(placeholder ?? Strings.PalettePlaceholder)
			.OnTextChanged(_ => Refresh())
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
						if (_matches.Count > 0)
						{
							Run(_matches[_selectedIndex]);
						}
						break;
					case Key.Down:
					case Key.Up:
						key.Handled = true;
						Select(_selectedIndex + (key.Key == Key.Down ? 1 : -1));
						break;
				}
			});

		_results.Spacing(2);
		_scroll = new ScrollViewer().SmoothScrolling().Content(_results).AutoVerticalScroll().NoHorizontalScroll().MaxHeight(360);
		var content = new StackPanel().Spacing(AppTheme.Space2);
		if (title is not null)
		{
			content.Children(new DockPanel().LastChildFill().Children(
				AppIcons.Button("x", Strings.Cancel, Hide).DockRight(),
				new TextBlock().Text(title).FontSize(AppTheme.FontSizeControl).FontWeight(FontWeight.Bold).PrimaryText()));
		}

		content.Children(_query, _scroll);
		if (countText is not null)
		{
			content.Children(_count.FontSize(AppTheme.FontSizeMicro).SubtleText());
		}

		var panel = new Border()
			.StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3))
			.Child(content);
		var surface = new PaletteSurface()
			.MaxWidth(560)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Top)
			.Margin(new Thickness(AppTheme.Space6, 60, AppTheme.Space6, AppTheme.Space6))
			.Content(panel);

		// 单独的遮罩按钮负责空白处关闭，避免 Border 的空白命中穿透到会话内容。
		View = new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0x80, 0x00, 0x00, 0x00))
				.Cursor(CursorType.Arrow).Focusable(false)
				.BorderThickness(0).CornerRadius(0).OnClick(Hide),
			surface).OnKeyDown(key =>
			{
				if (key.Key == Key.Escape)
				{
					key.Handled = true;
					Hide();
				}
			});
	}

	public UIElement View { get; }
	public bool IsOpen => View.IsVisible;

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (IsOpen)
		{
			_query.Focus();
			return;
		}

		// 一次打开只取一次快照，输入每个字符无需重新发现技能或构造命令闭包。
		_all = _commands();
		_query.Text = string.Empty;
		Refresh();

		_window = window;
		TransientOverlay.Open(window, View, Hide);
		AppStyles.ApplyTextColors(View);
		View.IsVisible(true);
		_query.Focus();
	}

	public void Hide()
	{
		if (!IsOpen)
		{
			return;
		}

		View.IsVisible(false);
		if (_window is { } window)
		{
			TransientOverlay.Remove(window, View);
			_window = null;
		}
		_all = [];
		_matches = [];
		_rows.Clear();
		_results.Clear();
		Closed?.Invoke();
	}

	public void Toggle(UIElement owner)
	{
		if (IsOpen)
		{
			Hide();
		}
		else
		{
			Show(owner);
		}
	}

	private void Run(PaletteCommand command)
	{
		Hide();
		command.Run();
	}

	/// <summary>所有关键字都需命中标题或说明，标题前缀优先，结果数量有界。</summary>
	public static IReadOnlyList<PaletteCommand> Search(IReadOnlyList<PaletteCommand> commands, string? query)
	{
		var text = query?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			return [.. commands.Take(12)];
		}

		var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		return [.. commands
			.Where(command => words.All(word => command.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ||
				command.Detail.Contains(word, StringComparison.OrdinalIgnoreCase)))
			.OrderByDescending(command => command.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase))
			.ThenBy(static command => command.Title.Length)
			.Take(12)];
	}

	private void Refresh()
	{
		_matches = Search(_all, _query.Text);
		_count.Text(_countText?.Invoke(_matches.Count, _all.Count) ?? string.Empty);
		_selectedIndex = 0;
		_results.Clear();
		_rows.Clear();
		_scroll.SetScrollOffsets(0, 0);
		if (_matches.Count == 0)
		{
			_results.Children(new TextBlock().Text(_emptyText)
				.FontSize(AppTheme.FontSizeCaption).MutedText());
			return;
		}

		foreach (var command in _matches)
		{
			var row = new Button()
				.Height(RowHeight)
				.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
				.HoverTip($"{command.Title}\n{command.Detail}")
				.Content(new StackPanel().Spacing(2).Children(
					new TextBlock().Text(command.Title).FontSize(AppTheme.FontSizeControl).PrimaryText(),
					new TextBlock().Text(command.Detail).FontSize(AppTheme.FontSizeMicro).SubtleText()))
				.OnClick(() => Run(command));
			_rows.Add(row);
			_results.Children(row);
		}

		Select(0);
	}

	/// <summary>按可用宽度测量，不随搜索结果的文字长度收缩，也不超出窄窗口。</summary>
	private sealed class PaletteSurface : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var width = Math.Min(560, availableSize.Width);
			var measured = base.MeasureContent(new Size(width, availableSize.Height));
			return new Size(width, measured.Height);
		}
	}

	private void Select(int index)
	{
		if (_matches.Count == 0)
		{
			return;
		}

		_selectedIndex = Math.Clamp(index, 0, _matches.Count - 1);
		for (var position = 0; position < _rows.Count; position++)
		{
			_rows[position].StyleName(position == _selectedIndex ? AppStyles.SelectedButton : AppStyles.GhostButton);
		}

		var top = _selectedIndex * (RowHeight + 2);
		if (top < _scroll.VerticalOffset)
		{
			_scroll.SetScrollOffsets(0, top);
		}
		else if (top + RowHeight > _scroll.VerticalOffset + _scroll.ViewportHeight)
		{
			_scroll.SetScrollOffsets(0, top + RowHeight - _scroll.ViewportHeight);
		}
	}
}
