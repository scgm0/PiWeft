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
	string? Icon = null);

/// 工作台摘要行。内容区一次只挂一个 section，避免所有卡片同时抢高度。
internal sealed class ComposerWorkbench
{
	private readonly List<WorkbenchSection> _sections;
	private readonly StackPanel _chips = new();
	private readonly ScrollViewer _chipScroll;
	private readonly StackPanel _content = new();
	private readonly StackPanel _statusHost = new();
	private readonly TextBlock _title;
	private readonly GlyphElement _chevron;
	private readonly Button _fold;
	private UIElement? _status;
	private string? _selected;
	private string? _hosted;
	private int _signature = -1;
	private readonly List<WorkbenchSection> _visible = [];
	private bool _expanded;

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
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0))
			.VerticalAlignment(VerticalAlignment.Center);
		_title = new TextBlock()
			.Text(Strings.WorkbenchTitle)
			.FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0));

		_chevron = new GlyphElement()
			.Kind(GlyphKind.ChevronDown)
			.GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextSubtle);
		_fold = new Button()
			.Ghost()
			.Content(_chevron)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Width(24)
			.Height(24)
			.OnClick(Toggle)
			.HoverTip(Strings.WorkbenchExpand);

		var header = new Grid()
			.Columns("Auto,Auto,*,Auto")
			.Children(
				icon.Column(0),
				_title.Column(1),
				_chipScroll.Column(2),
				_fold.Column(3));

		_content.IsVisible(false);
		_statusHost.IsVisible(false);

		View = new StackPanel()
			.Spacing(AppTheme.Space2)
			.IsVisible(false)
			.Children(header, _statusHost, _content);
	}

	public StackPanel View { get; }

	public bool IsShown
	{
		get
		{
			foreach (var section in _sections)
			{
				if (section.IsShown()) return true;
			}

			return _status?.IsVisible == true;
		}
	}

	public bool IsExpanded => _expanded;

	/// 执行中状态块挂在摘要行下方，收起时也可见；工作台自己不另画一行。
	public void AttachStatus(UIElement status)
	{
		_status = status;
		_statusHost.Children(status);
	}

	public void Refresh()
	{
		_visible.Clear();
		foreach (var section in _sections)
		{
			if (section.IsShown()) _visible.Add(section);
		}

		if (_selected is null || !VisibleContains(_selected))
		{
			_selected = _visible.Count == 0 ? null : _visible[0].Key;
		}

		// 每帧都要算摘要：用 HashCode 累加，避免给稳定态拼签名字符串。
		var hash = new HashCode();
		hash.Add(_expanded);
		hash.Add(_selected);
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

		if (_selected is null)
		{
			_expanded = false;
		}

		_content.IsVisible(_expanded);
		_chevron.Kind(_expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown);
		_fold.HoverTip(_expanded ? Strings.WorkbenchCollapse : Strings.WorkbenchExpand);

		if (_expanded && _selected is not null)
		{
			var section = Find(_selected);
			if (section is not null && _hosted != section.Key)
			{
				_hosted = section.Key;
				Clear(_content);
				_content.Children(section.View);
			}
		}
		else if (!_expanded && _hosted is not null)
		{
			_hosted = null;
			Clear(_content);
		}

		_statusHost.IsVisible(_status?.IsVisible == true);
		View.IsVisible(IsShown);
	}

	// 中文界面按字宽估算：24 字左右约 220 DIP，超出再交给 hover tip 看全文。
	private static string ClipMeta(string meta) =>
		meta.Length <= 24 ? meta : string.Concat(meta.AsSpan(0, 24), "…");

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

	// Panel.Children 是只读视图；清空只能逐个 Remove。
	private static void Clear(StackPanel panel)
	{
		foreach (var child in panel.Children.ToArray())
		{
			panel.Remove(child);
		}
	}

	private void RebuildChips(IReadOnlyList<WorkbenchSection> visible)
	{
		Clear(_chips);
		foreach (var section in visible)
		{
			var selected = section.Key == _selected;
			var meta = section.Meta();
			// 选中只改文字颜色，不铺底色：工作台是文本摘要行，按参考项目的卡头保持轻。
			var title = new TextBlock()
				.Text(section.Title)
				.FontSize(AppTheme.FontSizeCaption)
				.VerticalAlignment(VerticalAlignment.Center);
			// 不靠 TextBlock.MaxWidth 截断：MewUI 会按上限报宽度，短摘要后面留一段空白。
			// 先按字符预算裁剪，按钮宽度就只包住实际文字。
			var detail = new TextBlock()
				.Text(ClipMeta(meta))
				.FontSize(AppTheme.FontSizeCaption)
				.VerticalAlignment(VerticalAlignment.Center);
			if (selected)
			{
				title.PrimaryText();
				detail.PrimaryText();
			}
			else
			{
				title.SubtleText();
				detail.FaintText();
			}

			var content = new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space1)
				.VerticalAlignment(VerticalAlignment.Center);
			if (section.Icon is { Length: > 0 } icon)
			{
				content.Children(AppIcons.Icon(icon, selected ? AppStyles.RoleInfo : AppStyles.RoleSubtle, 13));
			}

			content.Children(title);
			if (meta.Length > 0)
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
		}
	}

	private void Select(string key)
	{
		if (_selected == key && _expanded)
		{
			_expanded = false;
			Refresh();
			return;
		}

		_selected = key;
		_expanded = true;
		// 选中只把这一节的卡头放进内容区；卡体仍保持收起，由用户再点卡头展开。
		Refresh();
	}

	private void Toggle()
	{
		_expanded = !_expanded;
		Refresh();
	}
}
