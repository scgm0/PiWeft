using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 输入区上方的工作台：一条摘要行列出可见扩展条的标题与计数，点某个 chip 才展开那一节。
/// 默认收起；卡片自身的展开状态也默认关闭，由这里在用户选中时一并打开。
internal sealed record WorkbenchSection(
	string Key,
	string Title,
	UIElement View,
	Func<bool> IsShown,
	Func<string> Meta,
	Action? Expand = null,
	string? Icon = null);

/// 工作台摘要行。内容区一次只挂一个 section，避免所有卡片同时抢高度。
internal sealed class ComposerWorkbench
{
	private readonly List<WorkbenchSection> _sections;
	private readonly StackPanel _chips = new();
	private readonly ScrollViewer _chipScroll;
	private readonly StackPanel _content = new();
	private readonly TextBlock _title;
	private readonly Button _titleButton;
	private string? _selected;
	private string? _hosted;
	private int _signature = -1;
	private string? _styledSelected;
	private bool _stylesApplied;
	private readonly List<WorkbenchSection> _visible = [];
	private readonly List<ChipView> _chipViews = [];
	private bool _expanded;

	private sealed record ChipView(
		string Key,
		string? IconName,
		Image? Icon,
		TextBlock Title,
		TextBlock? Detail,
		Button Button);

	public ComposerWorkbench(IReadOnlyList<WorkbenchSection> sections)
	{
		_sections = [.. sections];
		_chips.Orientation(Orientation.Horizontal).Spacing(AppTheme.Space1)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.VerticalAlignment(VerticalAlignment.Center);
		_chipScroll = new ScrollViewer()
			.Content(_chips)
			.AutoHorizontalScroll()
			.NoVerticalScroll()
			.MaxHeight(26)
			.VerticalAlignment(VerticalAlignment.Center);

		var icon = AppIcons.Icon("layout-grid", AppStyles.RoleSubtle, 14)
			.VerticalAlignment(VerticalAlignment.Center);
		_title = new TextBlock()
			.Text(Strings.WorkbenchTitle)
			.FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center);
		// 图标与「工作台」整块可点：这是摘要行最自然的折叠入口，不必非点最右侧箭头。
		_titleButton = new Button()
			.Ghost()
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(icon, _title))
			// 悬停底要包住图标和文字，留出左右内边距；不然 ghost 的圆角底贴字，像被裁过。
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.Height(26)
			.CornerRadius(AppTheme.RadiusSm)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(0, 0, AppTheme.Space1, 0))
			.OnClick(Toggle);

		var header = new Grid()
			.Columns("Auto,*")
			.Children(
				_titleButton.Column(0),
				_chipScroll.Column(1));

		_content.IsVisible(false);

		View = new StackPanel()
			.Spacing(AppTheme.Space2)
			.IsVisible(false)
			.Children(header, _content);
	}

	public StackPanel View { get; }

	internal Button TitleButtonForTests => _titleButton;

	internal Button ChipButtonForTests(string key)
	{
		foreach (var chip in _chipViews)
		{
			if (chip.Key == key) return chip.Button;
		}

		throw new InvalidOperationException($"chip 不存在：{key}");
	}

	public bool IsShown
	{
		get
		{
			foreach (var section in _sections)
			{
				if (section.IsShown()) return true;
			}

			return false;
		}
	}

	public bool IsExpanded => _expanded;

	internal string? SelectedKeyForTests => _selected;

	public void Refresh()
	{
		_visible.Clear();
		foreach (var section in _sections)
		{
			if (section.IsShown()) _visible.Add(section);
		}

		if (_selected is not null && !VisibleContains(_selected))
		{
			_selected = null;
			_expanded = false;
		}

		if (!_expanded)
		{
			_selected = null;
		}

		// 结构签名只包含可见集合与文字；选中/展开只改颜色，不重建按钮，保住键盘焦点。
		var hash = new HashCode();
		foreach (var section in _visible)
		{
			hash.Add(section.Key);
			hash.Add(section.Title);
			hash.Add(section.Meta());
		}

		var signature = hash.ToHashCode();
		if (signature != _signature)
		{
			_signature = signature;
			RebuildChips(_visible);
		}
		else if (!_stylesApplied || !string.Equals(_styledSelected, _selected, StringComparison.Ordinal))
		{
			ApplyChipStyles();
		}

		if (_selected is null)
		{
			_expanded = false;
		}

		_content.IsVisible(_expanded);
		_titleButton.HoverTip(_selected is null ? null : Strings.WorkbenchCollapse);

		if (_expanded && _selected is not null)
		{
			var section = Find(_selected);
			if (section is not null && _hosted != section.Key)
			{
				_hosted = section.Key;
				_content.Clear();
				_content.Children(section.View);
			}
		}
		else if (!_expanded && _hosted is not null)
		{
			_hosted = null;
			_content.Clear();
		}

		View.IsVisible(IsShown);
	}

	// 中文界面按字宽估算：32 字能放下「2 个会话 · 1 个正在运行 · 1 个改过文件」，
	// 再长才交给 hover tip 看全文。
	private static string ClipMeta(string meta) =>
		meta.Length <= 32 ? meta : string.Concat(meta.AsSpan(0, 32), "…");

	private bool VisibleContains(string key)
	{
		foreach (var section in _visible)
		{
			if (section.Key == key) return true;
		}

		return false;
	}

	private WorkbenchSection? Find(string key)
	{
		foreach (var section in _sections)
		{
			if (section.Key == key) return section;
		}

		return null;
	}

	/// 测试与截图走同一条路径：选中这一节并展开内容。
	internal void SelectForTests(string key) => Select(key);

	private void RebuildChips(IReadOnlyList<WorkbenchSection> visible)
	{
		_chips.Clear();
		_chipViews.Clear();
		foreach (var section in visible)
		{
			var meta = section.Meta();
			var title = new TextBlock()
				.Text(section.Title)
				.FontSize(AppTheme.FontSizeCaption)
				.VerticalAlignment(VerticalAlignment.Center);
			// 不靠 TextBlock.MaxWidth 截断：MewUI 会按上限报宽度，短摘要后面留一段空白。
			// 先按字符预算裁剪，按钮宽度就只包住实际文字。
			var detail = meta.Length == 0
				? null
				: new TextBlock()
					.Text(ClipMeta(meta))
					.FontSize(AppTheme.FontSizeCaption)
					.VerticalAlignment(VerticalAlignment.Center);
			var content = new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space1)
				.VerticalAlignment(VerticalAlignment.Center);
			var icon = section.Icon is { Length: > 0 } iconName
				? AppIcons.Icon(iconName, AppStyles.RoleSubtle, 13)
				: null;
			if (icon is not null) content.Children(icon);
			content.Children(title);
			if (detail is not null)
			{
				content.Children(new TextBlock().Text("·").FontSize(AppTheme.FontSizeCaption)
					.FaintText().VerticalAlignment(VerticalAlignment.Center));
				content.Children(detail);
			}

			var chip = new Button()
				.Content(content)
				// 不设固定高度：让内容按自己的行高居中，和左侧工作台图标落在同一条基线上。
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Height(22)
				// 卡头文字比纯文本行低约 1–2 px，轻抬一下与左侧工作台图标对齐。
				.Margin(new Thickness(0, -1, 0, 0))
				.HorizontalAlignment(HorizontalAlignment.Left)
				.VerticalAlignment(VerticalAlignment.Center)
				.Ghost()
				.OnClick(() => Select(section.Key))
				.HoverTip(meta.Length == 0 ? section.Title : $"{section.Title} · {meta}");
			_chips.Children(chip);
			_chipViews.Add(new ChipView(section.Key, section.Icon, icon, title, detail, chip));
		}

		ApplyChipStyles();
	}

	private void ApplyChipStyles()
	{
		_styledSelected = _selected;
		_stylesApplied = true;
		foreach (var chip in _chipViews)
		{
			var selected = chip.Key == _selected;
			if (selected)
			{
				chip.Title.PrimaryText();
				chip.Detail?.PrimaryText();
			}
			else
			{
				chip.Title.SubtleText();
				chip.Detail?.FaintText();
			}

			if (chip.Icon is not null && chip.IconName is { Length: > 0 } iconName)
			{
				chip.Icon.Source(AppIcons.Get(iconName,
					AppStyles.ColorForRole(selected ? AppStyles.RoleInfo : AppStyles.RoleSubtle)));
			}
		}
	}

	private void Select(string key)
	{
		if (_selected == key)
		{
			_selected = null;
			_expanded = false;
			Refresh();
			return;
		}

		_selected = key;
		_expanded = true;
		// 卡头已由摘要 chip 取代，选中时直接打开这一节的卡体。
		foreach (var section in _sections)
		{
			if (section.Key == key)
			{
				section.Expand?.Invoke();
				break;
			}
		}

		Refresh();
	}

	private void Toggle()
	{
		// 默认没有选中项；只有已经选中时，标题区才承担「取消选中并收起」。
		if (_selected is null)
		{
			return;
		}

		_selected = null;
		_expanded = false;
		Refresh();
	}
}
