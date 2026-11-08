using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// 模型选择弹窗。搜索框常驻提示行，供应商是可展开分组，模型行单行显示
/// <c>provider/名称</c>，右侧是上下文窗口与能力，当前模型打勾。
/// 选择器挂在窗口浮层上，点空白处或 Esc 关闭；刷新只重读目录，不改搜索词。
internal sealed class ModelPicker
{
	private const double RowHeight = 38;
	private const double PanelWidth = 680;

	/// 当前模型那一行右侧的勾。测试靠这个 Tag 找到勾，不去猜图标顺序。
	internal const string CurrentCheckTag = "model-current-check";

	private Func<IReadOnlyList<Model>> _models;
	private readonly Func<Model> _current;
	private readonly Action<Model> _pick;
	private readonly TreeView _tree = new();
	private readonly TextBox _query;
	private readonly TextBlock _hint;
	private readonly TextBlock _count = new();
	private readonly TextBlock _empty = new();
	private IReadOnlyList<Model> _snapshot = [];
	private IReadOnlyList<TreeViewNode> _matches = [];
	private IReadOnlyList<TreeViewNode> _groups = [];
	private Window? _window;
	private int _index;
	private IReadOnlyList<string> _recent = [];
	private readonly OverlayFade _fade;

	public ModelPicker(Func<IReadOnlyList<Model>> models, Func<Model> current, Action<Model> pick)
	{
		_models = models;
		_current = current;
		_pick = pick;

		// 提示文本自己画：框架的 Placeholder 聚焦即隐藏，而弹窗一打开就聚焦在输入框上。
		_hint = new TextBlock()
			.Text(Strings.ModelSearchPlaceholder)
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
				if (key.Key == Key.Enter)
				{
					PickSelected();
					key.Handled = true;
				}
				else if (key.Key is Key.Down or Key.Up && _matches.Count > 0)
				{
					_index = Math.Clamp(_index + (key.Key == Key.Down ? 1 : -1), 0, _matches.Count - 1);
					foreach (var group in _groups.Where(group => group.Children.Contains(_matches[_index]))) _tree.Expand(group);
					_tree.SelectedNode = _matches[_index];
					key.Handled = true;
				}
			});

		var searchIcon = AppIcons.Icon("search", AppStyles.RoleSubtle, 16)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0));
		var esc = new Border()
			.Padding(new Thickness(6, 1, 6, 1))
			.CornerRadius(AppTheme.RadiusXs)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, badge) => badge.Background = AppTheme.For(theme).CardBackgroundAlt)
			.Child(new TextBlock().Text("Esc").FontSize(AppTheme.FontSizeMicro).SubtleText());
		var searchRow = new DockPanel().LastChildFill().Children(
			esc.DockRight(),
			new DockPanel().LastChildFill().Children(searchIcon.DockLeft(), new Grid().Children(_query, _hint)));

		_tree.SmoothScrolling().MaxHeight(360);
		_tree.HorizontalScroll = ScrollMode.Disabled;
		_tree.ItemHeight = RowHeight;
		_tree.ItemPadding = new Thickness(6, 0, 6, 0);
		_tree.ItemTemplate<TreeViewNode>(context =>
		{
			var title = new TextBlock()
				.FontSize(AppTheme.FontSizeControl)
				.PrimaryText()
				.TextTrimming(TextTrimming.CharacterEllipsis)
				.VerticalAlignment(VerticalAlignment.Center);
			var meta = new TextBlock()
				.FontSize(AppTheme.FontSizeMicro)
				.SubtleText()
				.TextTrimming(TextTrimming.CharacterEllipsis)
				.VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(AppTheme.Space2, 0, 0, 0));
			var check = AppIcons.Icon("check", AppStyles.RolePrimary, 14).IsVisible(false);
			check.Tag = CurrentCheckTag;
			check.HorizontalAlignment(HorizontalAlignment.Right);
			context.Register("title", title);
			context.Register("meta", meta);
			context.Register("check", check);
			var button = new Button().Ghost()
				// 行容器已经画了悬停/选中底色；按钮本身再画一层就成了内外两个悬浮框。
				// 本地值优先于 Ghost 的 hover trigger，按钮保留点击与模板，只去掉自己的底色。
				.Background(AppTheme.Transparent)
				.HorizontalAlignment(HorizontalAlignment.Stretch)
				.Height(RowHeight)
				.Padding(new Thickness(2, 0, 2, 0))
				.Content(new Grid()
					.Columns(GridLength.Stars(1), GridLength.Auto, GridLength.Pixels(24))
					.Children(title.Column(0), meta.Column(1), new Grid().Width(24).Children(check).Column(2)));
			button.OnClick(() =>
			{
				if (button.Tag is TreeViewNode { Tag: Model model }) Pick(model);
				else if (button.Tag is TreeViewNode node)
				{
					if (_tree.IsExpanded(node)) _tree.Collapse(node); else _tree.Expand(node);
				}
			});
			return button;
		}, (row, node, _, context) =>
		{
			row.Tag = node;
			var title = context.Get<TextBlock>("title")!;
			var meta = context.Get<TextBlock>("meta")!;
			var check = context.Get<Image>("check")!;
			if (node.Tag is Model model)
			{
				title.Text($"{model.Provider}/{(model.Name.Length > 0 ? model.Name : model.Id)}")
					.FontFamily(AppTheme.MonoFont)
					.FontWeight(FontWeight.Normal);
				meta.Text(Strings.ModelMeta(model)).IsVisible(true);
				check.IsVisible(IsCurrent(model));
				row.HoverTip($"{model.Provider}/{model.Id}\n{model.Name} · {Strings.ModelCapabilities(model)}");
			}
			else
			{
				title.Text(node.Text)
					.FontFamily(AppFonts.Ui)
					.FontWeight(FontWeight.Medium);
				meta.Text($"{node.Children.Count} {Strings.ModelsCountUnit}").IsVisible(true);
				check.IsVisible(false);
				row.HoverTip(node.Text);
			}
		});
		_tree.OnKeyDown(key => { if (key.Key == Key.Enter) { PickSelected(); key.Handled = true; } });

		var header = new DockPanel().LastChildFill().Children(
			AppIcons.Button("x", Strings.Cancel, Hide).DockRight(),
			AppIcons.Button("refresh-cw", Strings.RefreshModels, Reload).DockRight(),
			new TextBlock().Text(Strings.SelectModel).FontSize(AppTheme.FontSizeControl)
				.FontWeight(FontWeight.Bold).PrimaryText());

		_empty.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap);
		_count.FontSize(AppTheme.FontSizeMicro).SubtleText();

		var content = new StackPanel().Spacing(AppTheme.Space2).Children(header, searchRow, _tree, _empty, _count);
		View = new Grid { StyleSheet = RowStyles() }.IsVisible(false).Children(
			new Button().Background(new Color(0x80, 0, 0, 0)).BorderThickness(0).CornerRadius(0)
				.Cursor(CursorType.Arrow).Focusable(false).OnClick(Hide),
			new ModelSurface().MaxWidth(PanelWidth).HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(AppTheme.Space3)).Content(
					new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
						.Padding(new Thickness(AppTheme.Space3)).Child(content)))
			.OnKeyDown(key => { if (key.Key == Key.Escape) { Hide(); key.Handled = true; } });
		_fade = new OverlayFade((FrameworkElement)View);
	}

	public UIElement View { get; }
	public bool IsOpen => View.IsVisible;
	public event Action? Closed;

	/// 打开时按可用宽度定宽；窄窗口里跟随可用宽度。
	public void SetSource(Func<IReadOnlyList<Model>> models)
	{
		_models = models;
		if (IsOpen) Reload();
	}

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (IsOpen) { if (_fade.IsClosing) _fade.Reopen(); _query.Focus(); return; }
		_window = window;
		_recent = DesktopPreference.Load().RecentModels;
		_snapshot = [.. _models().DistinctBy(static model => (model.Provider, model.Id))];
		_query.Text = string.Empty;
		_hint.IsVisible(true);
		Refresh();
		TransientOverlay.Open(window, View, Hide);
		AppStyles.ApplyTextColors(View);
		View.IsVisible(true);
		_fade.Open();
		_query.Focus();
	}

	public void Hide()
	{
		if (!IsOpen || _fade.IsClosing) return;
		_fade.Close(CloseNow);
	}

	private void CloseNow()
	{
		View.IsVisible(false);
		if (_window is { } window) TransientOverlay.Remove(window, View);
		_window = null;
		_tree.ItemsSource(Array.Empty<TreeViewNode>());
		_matches = [];
		_groups = [];
		_snapshot = [];
		_recent = [];
		Closed?.Invoke();
	}

	private void Reload()
	{
		_snapshot = [.. _models().DistinctBy(static model => (model.Provider, model.Id))];
		Refresh();
	}

	private void Refresh()
	{
		var current = _current();
		var matches = _snapshot.Where(model => Matches(model, _query.Text)).ToArray();
		// 当前模型所在的供应商排在最前，其余按字母序；组内当前模型、最近使用、字母序。
		var nodes = matches.GroupBy(static model => model.Provider)
			.OrderBy(group => group.Key == current.Provider ? 0 : 1).ThenBy(static group => group.Key, StringComparer.Ordinal)
			.Select(group => new TreeViewNode(group.Key,
				[.. group.OrderBy(model => model.Id == current.Id && model.Provider == current.Provider ? 0 :
					_recent.Contains($"{model.Provider}/{model.Id}") ? 1 : 2).ThenBy(static model => model.Id, StringComparer.Ordinal)
					.Select(model => new TreeViewNode(model.Provider + "/" + model.Id, tag: model))]))
			.ToArray();
		_tree.ItemsSource(nodes);
		foreach (var node in nodes)
		{
			if (_query.Text.Trim().Length > 0 || ReferenceEquals(node, nodes[0])) _tree.Expand(node);
		}
		_groups = nodes;
		_matches = [.. nodes.SelectMany(static node => node.Children)];
		_index = 0;
		_tree.SelectedNode = _matches.Count > 0 ? _matches[0] : null;
		// 过滤重建后旧偏移会落在新列表的中段甚至末尾；搜索/刷新都要从第一行开始。
		_tree.ScrollIntoView(0);
		_count.Text(Strings.ModelResultCount(matches.Length, _snapshot.Count)).IsVisible(matches.Length > 0);
		_tree.IsVisible(matches.Length > 0);
		_empty.Text(_snapshot.Count == 0 ? Strings.ModelNoneAvailable : Strings.ModelNoMatch).IsVisible(matches.Length == 0);
	}

	/// 搜索：空格分隔的词都要命中，比较前去掉分隔符（gpt4o 也能命中 gpt-4o）。
	internal static bool Matches(Model model, string? query)
	{
		var text = query?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			return true;
		}

		var haystack = Normalize($"{model.Provider} {model.Id} {model.Name} {Strings.ModelMeta(model)}");
		var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		return words.All(word => haystack.Contains(Normalize(word), StringComparison.OrdinalIgnoreCase));
	}

	private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit));

	private bool IsCurrent(Model model) => model.Id == _current().Id && model.Provider == _current().Provider;

	private void PickSelected()
	{
		if (_tree.SelectedNode?.Tag is Model selected) Pick(selected);
		else if (_tree.SelectedNode is { } group)
		{
			if (_tree.IsExpanded(group)) _tree.Collapse(group); else _tree.Expand(group);
		}
		else if (_matches.Count > 0 && _matches[_index].Tag is Model model) Pick(model);
	}

	private void Pick(Model model)
	{
		// 淡出这 120ms 里视图还在：连点两行不能挑两次模型。
		if (_fade.IsClosing) return;
		Hide();
		_pick(model);
	}

	/// 模型行的选中底色用调色板的 AccentSoft，不跟随框架默认的 SelectionBackground
	///（浅色高亮在深色主题里太跳）；只作用于这个弹窗，侧栏树保持原样。
	private static StyleSheet RowStyles()
	{
		var sheet = new StyleSheet();
		sheet.Define<ItemContainer>(new Style(typeof(ItemContainer))
		{
			Triggers =
			[
				new StateTrigger
				{
					Match = VisualStateFlags.Hot,
					Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(theme => AppTheme.For(theme).HoverBackground))],
				},
				new StateTrigger
				{
					Match = VisualStateFlags.Selected,
					Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(theme => AppTheme.For(theme).AccentSoft))],
				},
			],
		});
		return sheet;
	}

	private sealed class ModelSurface : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var width = Math.Min(PanelWidth, availableSize.Width);
			var measured = base.MeasureContent(new Size(width, availableSize.Height));
			return new Size(width, measured.Height);
		}
	}
}
