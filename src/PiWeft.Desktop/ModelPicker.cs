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
	private readonly HashSet<(string Provider, string Id)> _recent = [];
	private ProviderGroup[] _groups = [];
	private IReadOnlyList<TreeViewNode> _matches = [];
	private int _total;
	private string _currentProvider = string.Empty;
	private string _currentId = string.Empty;
	private Window? _window;
	private int _index;
	private readonly OverlayFade _fade;

	/// 一条模型在快照里的全部派生数据：节点（跨按键复用）与归一化搜索文本。
	private sealed record Entry(Model Model, TreeViewNode Node, string Search);

	/// 一个供应商分组。节点在弹窗生命周期内复用，只替换 Children：
	/// 每次按键都造新节点会让 TreeView 的展开状态表按按键数增长。
	private sealed record ProviderGroup(TreeViewNode Node, Entry[] Entries);

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
					foreach (var group in _groups.Where(group => group.Node.Children.Contains(_matches[_index]))) _tree.Expand(group.Node);
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
				check.IsVisible(IsCurrentModel(model));
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

	/// 换模型源（认证变化后可能整份目录都变了）。弹窗开着就立即重建快照。
	public void SetSource(Func<IReadOnlyList<Model>> models)
	{
		_models = models;
		if (IsOpen) Reload();
	}

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (IsOpen)
		{
			if (_fade.IsClosing) _fade.Reopen();
			_query.Focus();
			return;
		}

		_window = window;
		Rebuild();
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

	/// 立即收起，不做淡出：标签页关闭或会话切走时，淡出只会让浮层多挂在窗口上。
	public void CloseInstantly()
	{
		if (!IsOpen) return;
		_fade.Cancel();
		CloseNow();
	}

	private void CloseNow()
	{
		View.IsVisible(false);
		if (_window is { } window) TransientOverlay.Remove(window, View);
		_window = null;
		_tree.ItemsSource(Array.Empty<TreeViewNode>());
		_matches = [];
		_groups = [];
		_total = 0;
		Closed?.Invoke();
	}

	private void Reload()
	{
		Rebuild();
		Refresh();
	}

	/// 重建目录快照：分组顺序、组内顺序与搜索文本都在这里算一次，
	/// 之后每次按键只做子串比对，不再格式化字符串。
	private void Rebuild()
	{
		var current = _current();
		_currentProvider = current.Provider;
		_currentId = current.Id;

		_recent.Clear();
		foreach (var key in DesktopPreference.Load().RecentModels)
		{
			var slash = key.IndexOf('/');
			if (slash > 0 && slash + 1 < key.Length)
			{
				_recent.Add((key[..slash], key[(slash + 1)..]));
			}
		}

		var models = _models().DistinctBy(static model => (model.Provider, model.Id)).ToArray();
		_total = models.Length;
		// 当前模型所在的供应商排在最前，其余按字母序；组内当前模型、最近使用、字母序。
		_groups = [.. models
			.GroupBy(static model => model.Provider)
			.OrderBy(group => string.Equals(group.Key, _currentProvider, StringComparison.Ordinal) ? 0 : 1)
			.ThenBy(static group => group.Key, StringComparer.Ordinal)
			.Select(group => new ProviderGroup(new TreeViewNode(group.Key),
			[
				.. group
					.OrderBy(model => IsCurrentModel(model) ? 0 : _recent.Contains((model.Provider, model.Id)) ? 1 : 2)
					.ThenBy(static model => model.Id, StringComparer.Ordinal)
					.Select(EntryFor)
			]))];
	}

	private static Entry EntryFor(Model model) => new(
		model,
		new TreeViewNode(model.Provider + "/" + model.Id, tag: model),
		// 能力文案也进搜索词：搜「图片」「推理」能挑出对应模型。
		SearchText.Normalize($"{model.Provider} {model.Id} {model.Name} {Strings.ModelMeta(model)}"));

	private void Refresh()
	{
		var words = SearchText.Words(_query.Text);
		var roots = new List<TreeViewNode>(_groups.Length);
		var matches = new List<TreeViewNode>();
		foreach (var group in _groups)
		{
			// 匹配为空的组不建子列表：一次按键里大部分组都是空的，白造 10 个 List 不划算。
			List<TreeViewNode>? children = null;
			foreach (var entry in group.Entries)
			{
				if (!SearchText.ContainsAll(entry.Search, words))
				{
					continue;
				}

				(children ??= new List<TreeViewNode>(group.Entries.Length)).Add(entry.Node);
			}

			group.Node.Children = children ?? [];
			if (children is null)
			{
				continue;
			}

			roots.Add(group.Node);
			matches.AddRange(children);
		}

		_tree.ItemsSource(roots);
		var filtering = words.Length > 0;
		foreach (var node in roots)
		{
			if (filtering || ReferenceEquals(node, roots[0])) _tree.Expand(node);
		}

		_matches = matches;
		_index = 0;
		_tree.SelectedNode = _matches.Count > 0 ? _matches[0] : null;
		// 过滤重建后旧偏移会落在新列表的中段甚至末尾；搜索/刷新都要从第一行开始。
		_tree.ScrollIntoView(0);
		_count.Text(Strings.ModelResultCount(matches.Count, _total)).IsVisible(matches.Count > 0);
		_tree.IsVisible(matches.Count > 0);
		_empty.Text(_total == 0 ? Strings.ModelNoneAvailable : Strings.ModelNoMatch).IsVisible(matches.Count == 0);
	}

	/// 单条模型的匹配判定：与 <see cref="Refresh"/> 走同一套归一化，供测试与排查用。
	internal static bool Matches(Model model, string? query) =>
		SearchText.ContainsAll(
			SearchText.Normalize($"{model.Provider} {model.Id} {model.Name} {Strings.ModelMeta(model)}"),
			SearchText.Words(query));

	private bool IsCurrentModel(Model model) =>
		string.Equals(model.Id, _currentId, StringComparison.Ordinal) &&
		string.Equals(model.Provider, _currentProvider, StringComparison.Ordinal);

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
