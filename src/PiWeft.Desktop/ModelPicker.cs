using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// <summary>按供应商分组的可用模型目录；树行虚拟化，搜索覆盖整个目录。</summary>
internal sealed class ModelPicker
{
	private readonly Func<IReadOnlyList<Model>> _models;
	private readonly Func<Model> _current;
	private readonly Action<Model> _pick;
	private readonly TreeView _tree = new();
	private readonly TextBox _query;
	private readonly TextBlock _count = new();
	private readonly TextBlock _empty = new();
	private IReadOnlyList<Model> _snapshot = [];
	private IReadOnlyList<TreeViewNode> _matches = [];
	private IReadOnlyList<TreeViewNode> _groups = [];
	private Window? _window;
	private int _index;
	private IReadOnlyList<string> _recent = [];

	public ModelPicker(Func<IReadOnlyList<Model>> models, Func<Model> current, Action<Model> pick)
	{
		_models = models;
		_current = current;
		_pick = pick;
		_query = new TextBox().PrimarySelection().Placeholder(Strings.ModelSearchPlaceholder)
			.OnTextChanged(_ => Refresh()).OnKeyDown(key =>
			{
				if (key.Key == Key.Enter) { PickSelected(); key.Handled = true; }
				else if (key.Key is Key.Down or Key.Up && _matches.Count > 0)
				{
					_index = Math.Clamp(_index + (key.Key == Key.Down ? 1 : -1), 0, _matches.Count - 1);
					foreach (var group in _groups.Where(group => group.Children.Contains(_matches[_index]))) _tree.Expand(group);
					_tree.SelectedNode = _matches[_index];
					key.Handled = true;
				}
			});
		_tree.SmoothScrolling().MaxHeight(360);
		_tree.HorizontalScroll = ScrollMode.Disabled;
		_tree.ItemHeight = 48;
		_tree.ItemTemplate<TreeViewNode>(context =>
		{
			var title = new TextBlock().FontSize(AppTheme.FontSizeControl).PrimaryText().TextTrimming(TextTrimming.CharacterEllipsis);
			var detail = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText().TextTrimming(TextTrimming.CharacterEllipsis);
			context.Register("title", title);
			context.Register("detail", detail);
			var button = new Button().Ghost().HorizontalAlignment(HorizontalAlignment.Stretch)
				.Content(new StackPanel().Spacing(2).Children(title, detail));
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
			var detail = context.Get<TextBlock>("detail")!;
			title.Text(node.Text).FontWeight(node.Tag is Model ? FontWeight.Normal : FontWeight.Bold);
			detail.IsVisible(node.Tag is Model);
			if (node.Tag is Model model)
			{
				detail.Text($"{model.Name} · {Strings.ModelCapabilities(model)}");
				row.HoverTip($"{model.Provider}/{model.Id}\n{model.Name} · {Strings.ModelCapabilities(model)}");
			}
			else row.HoverTip(node.Text);
		});
		_tree.OnKeyDown(key => { if (key.Key == Key.Enter) { PickSelected(); key.Handled = true; } });
		var content = new StackPanel().Spacing(AppTheme.Space2).Children(
			new DockPanel().LastChildFill().Children(
				AppIcons.Button("x", Strings.Cancel, Hide).DockRight(),
				AppIcons.Button("refresh-cw", Strings.RefreshModels, Reload).DockRight(),
				new TextBlock().Text(Strings.SelectModel).FontWeight(FontWeight.Bold).PrimaryText()),
			_query, _tree, _empty.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap),
			_count.FontSize(AppTheme.FontSizeMicro).SubtleText());
		View = new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0x80, 0, 0, 0)).BorderThickness(0).CornerRadius(0)
				.Cursor(CursorType.Arrow).Focusable(false).OnClick(Hide),
			new ModelSurface().MaxWidth(640).HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(AppTheme.Space3)).Content(
					new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd).Padding(new Thickness(AppTheme.Space3)).Child(content)))
			.OnKeyDown(key => { if (key.Key == Key.Escape) { Hide(); key.Handled = true; } });
	}

	public UIElement View { get; }
	public bool IsOpen => View.IsVisible;
	public event Action? Closed;

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (IsOpen) { _query.Focus(); return; }
		_window = window;
		_recent = DesktopPreference.Load().RecentModels;
		_snapshot = [.. _models().DistinctBy(static model => (model.Provider, model.Id))];
		_query.Text = string.Empty;
		Refresh();
		TransientOverlay.Open(window, View, Hide);
		AppStyles.ApplyTextColors(View);
		View.IsVisible(true);
		_query.Focus();
	}

	public void Hide()
	{
		if (!IsOpen) return;
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
		var words = _query.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		var recent = _recent;
		var matches = _snapshot.Where(model => words.All(word =>
			model.Id.Contains(word, StringComparison.OrdinalIgnoreCase) || model.Provider.Contains(word, StringComparison.OrdinalIgnoreCase) ||
			model.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || Strings.ModelCapabilities(model).Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
		var nodes = matches.GroupBy(static model => model.Provider)
			.OrderBy(group => group.Key == current.Provider ? 0 : 1).ThenBy(static group => group.Key, StringComparer.Ordinal)
			.Select(group => new TreeViewNode($"{group.Key} · {group.Count()} {Strings.ModelsCountUnit}",
				[.. group.OrderBy(model => model.Id == current.Id && model.Provider == current.Provider ? 0 :
					recent.Contains($"{model.Provider}/{model.Id}") ? 1 : 2).ThenBy(static model => model.Id, StringComparer.Ordinal)
					.Select(model => new TreeViewNode($"{(model.Id == current.Id && model.Provider == current.Provider ? "✓ " : "")}{model.Provider}/{model.Id}", tag: model))]))
			.ToArray();
		_tree.ItemsSource(nodes);
		foreach (var node in nodes)
		{
			if (words.Length > 0 || ReferenceEquals(node, nodes[0])) _tree.Expand(node);
		}
		_groups = nodes;
		_matches = [.. nodes.SelectMany(static node => node.Children)];
		_index = 0;
		_tree.SelectedNode = _matches.Count > 0 ? _matches[0] : null;
		_count.Text(Strings.ModelResultCount(matches.Length, _snapshot.Count));
		_tree.IsVisible(matches.Length > 0);
		_empty.Text(_snapshot.Count == 0 ? Strings.ModelNoneAvailable : Strings.ModelNoMatch).IsVisible(matches.Length == 0);
	}

	private void PickSelected()
	{
		if (_tree.SelectedNode?.Tag is Model selected) Pick(selected);
		else if (_tree.SelectedNode is { } group)
		{
			if (_tree.IsExpanded(group)) _tree.Collapse(group); else _tree.Expand(group);
		}
		else if (_matches.Count > 0 && _matches[_index].Tag is Model model) Pick(model);
	}

	private void Pick(Model model) { Hide(); _pick(model); }

	/// <summary>保持目录宽度稳定，同时适应窄窗口。</summary>
	private sealed class ModelSurface : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var width = Math.Min(640, availableSize.Width);
			var measured = base.MeasureContent(new Size(width, availableSize.Height));
			return new Size(width, measured.Height);
		}
	}
}
