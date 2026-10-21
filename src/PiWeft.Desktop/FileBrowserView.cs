using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// D-02：项目文件浏览与内置编辑器，单列形态，适合右侧功能面板的窄宽度。
/// 列表里点开文件就换成编辑器（顶栏变成文件路径 + 保存），返回键回到目录列表。
/// 保存时用时间戳检测外部修改，避免覆盖别人的改动。
/// </summary>
public sealed class FileBrowserView
{
	private readonly Func<string> _cwd;
	private readonly ItemsControl _list = new();
	private readonly TextBlock _empty = new();
	private readonly MultiLineTextBox _editor = new();
	private readonly TextBlock _title = new();
	private readonly Button _save;
	private readonly Button _back;
	private readonly Button _refresh;
	private readonly Border _listHost;
	private readonly Border _editorHost;

	private string _relativeDir = string.Empty;
	private string? _openFile;
	private long _openTicks;

	public FileBrowserView(Func<string> cwd)
	{
		_cwd = cwd;
		// 目录可能有上千个条目：只实现视口附近的行，虚拟化行复用按钮与文本控件。
		_list.FixedHeightPresenter(28).ItemPadding(Thickness.Zero).BorderThickness(0)
			.Background(AppTheme.Transparent).Padding(Thickness.Zero).SmoothScrolling();
		_list.ItemTemplate = new DelegateTemplate<FileRow>(
			_ => RowView(),
			(view, row, _, _) => BindRow((Button)view, row));
		_empty.FontSize(AppTheme.FontSizeCaption).MutedText().IsVisible(false);

		// 顶栏是唯一的位置提示：列表模式显示目录，编辑模式显示文件；完整路径放悬停提示。
		_title.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);

		// 列表模式只显示刷新；打开文件后换成「返回 / 保存」。
		_back = AppIcons.Button("arrow-left", Strings.FileBrowserBack, CloseEditor).IsVisible(false);
		_refresh = AppIcons.Button("refresh-cw", Strings.FileBrowserRefresh, Refresh);
		_save = AppIcons.Button("check", Strings.FileBrowserSave, Save).IsVisible(false);

		var actions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.Children(_back, _refresh, _save);

		var toolbar = new DockPanel()
			.LastChildFill()
			.Children(actions.DockLeft(), _title.Margin(new Thickness(AppTheme.Space1, 0, 0, 0)));

		_editor
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeCaption)
			.CornerRadius(AppTheme.RadiusSm)
			.StyleName(AppStyles.TextInput);

		_listHost = new Border()
			.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, 0))
			.Child(new Grid().Children(_list, _empty.VerticalAlignment(VerticalAlignment.Top)));

		_editorHost = new Border()
			.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space3))
			.IsVisible(false)
			.Child(_editor);

		// 底色由右侧面板统一给（与左侧栏同色），这里不再铺主区底色。
		View = new Border()
			.Child(new DockPanel()
				.LastChildFill()
				.Children(
					toolbar.Margin(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2)).DockTop(),
					new Grid().Children(_listHost, _editorHost)));

		Refresh();
	}

	public UIElement View { get; }

	/// <summary>主题切换后刷新角色文本颜色；图形在各自 WithTheme 里跟随。</summary>
	public void ApplyTheme() => AppStyles.ApplyTextColors(View);

	/// <summary>重新列举当前目录。</summary>
	public void Refresh()
	{
		var cwd = _cwd();

		// 编辑模式下顶栏是文件名：后台刷新（面板重新展开）不能把它改回目录。
		if (_openFile is null)
		{
			var path = _relativeDir.Length == 0 ? "/" : "/" + _relativeDir;
			_title.Text(path);
			_title.HoverTip(_relativeDir.Length == 0 ? cwd : Path.Combine(cwd, _relativeDir));
		}

		var rows = new List<FileRow>();
		if (_relativeDir.Length > 0)
		{
			rows.Add(new FileRow("\u2191", Strings.FileBrowserParent, string.Empty, string.Empty));
		}

		var entries = ProjectFiles.List(cwd, _relativeDir, GitStatuses(cwd));
		foreach (var entry in entries)
		{
			rows.Add(new FileRow(
				entry.IsDirectory ? "\u25b8" : " ",
				entry.Name + (entry.IsDirectory ? "/" : string.Empty),
				entry.Status,
				entry.RelativePath));
		}

		_list.ItemsSource = ItemsView.Create(rows, static row => row.Name);
		_list.IsVisible(rows.Count > 0);
		_empty.Text(Strings.FileBrowserEmpty).IsVisible(rows.Count == 0);
		// 进入目录/刷新是换一份列表，从顶部开始；同一目录的手动刷新也回到顶部。
		_list.SetScrollOffsets(0, 0);
	}

	/// <summary>虚拟化行的骨架：前缀、名称、Git 状态只建一次，绑定只改文本。</summary>
	private Button RowView()
	{
		var prefix = new TextBlock().FontSize(AppTheme.FontSizeMicro).Width(12).AccentText();
		var name = new TextBlock().FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption)
			.MutedText().TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis);
		var status = new TextBlock().FontSize(AppTheme.FontSizeMicro).AccentText()
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space2, 0, 0, 0)).IsVisible(false).DockRight();
		var parts = new RowParts(prefix, name, status);
		var button = new Button()
			.StyleName(AppStyles.GhostButton)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.OnClick(() => Activate(parts.Row));
		button.Tag = parts;
		button.Content(new DockPanel().LastChildFill().Children(status, prefix.DockLeft(), name));
		return button;
	}

	private static void BindRow(Button button, FileRow row)
	{
		var parts = (RowParts)button.Tag!;
		parts.Row = row;
		parts.Prefix.Text(row.Prefix);
		parts.Name.Text(row.Name);
		parts.Status.Text(row.Status).IsVisible(row.Status.Length > 0);
		button.HoverTip(row.Name);
	}

	private void Activate(FileRow? row)
	{
		if (row is null) return;
		if (row.RelativePath.Length == 0)
		{
			Navigate(ParentOf(_relativeDir));
		}
		else if (row.Name.EndsWith('/'))
		{
			Navigate(row.RelativePath);
		}
		else
		{
			OpenFile(row.RelativePath);
		}
	}

	/// <summary>一行一个条目：父目录（空路径）、目录（名称以 / 结尾）或文件。</summary>
	private sealed record FileRow(string Prefix, string Name, string Status, string RelativePath);

	private sealed record RowParts(TextBlock Prefix, TextBlock Name, TextBlock Status)
	{
		public FileRow? Row { get; set; }
	}

	/// <summary>Git 状态标记：相对路径 → 单字状态。</summary>
	private static IReadOnlyDictionary<string, string> GitStatuses(string cwd)
	{
		var status = GitStatusReader.Read(cwd, TimeSpan.FromSeconds(5));
		if (!status.IsRepository)
		{
			return new Dictionary<string, string>();
		}

		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var change in status.Changes)
		{
			map[change.Path] = change.Label;
		}

		return map;
	}

	private void Navigate(string relativeDir)
	{
		_relativeDir = relativeDir.Trim('/');
		Refresh();
	}

	private static string ParentOf(string relativeDir)
	{
		var index = relativeDir.LastIndexOf('/');
		return index < 0 ? string.Empty : relativeDir[..index];
	}

	private void OpenFile(string relativePath)
	{
		var (content, ticks, error) = ProjectFiles.Read(_cwd(), relativePath);
		if (error is not null)
		{
			Notice(error, ToastKind.Error);
			return;
		}

		_openFile = relativePath;
		_openTicks = ticks;
		_editor.Text = content ?? string.Empty;
		Notice(Strings.FileBrowserOpened(relativePath));
		EnterEditor(relativePath);
	}

	/// <summary>切换成编辑器：列表让位，顶栏换成文件路径与保存键。</summary>
	private void EnterEditor(string relativePath)
	{
		_listHost.IsVisible(false);
		_editorHost.IsVisible(true);
		_back.IsVisible(true);
		_refresh.IsVisible(false);
		_save.IsVisible(true);
		_title.Text(relativePath);
		_title.HoverTip(Path.Combine(_cwd(), relativePath));
		if (_editor.FindVisualRoot() is not null)
		{
			_editor.Focus();
		}
	}

	/// <summary>回到目录列表：重新列举目录，顺带刷新文件的 Git 状态。</summary>
	private void CloseEditor()
	{
		_openFile = null;
		_editorHost.IsVisible(false);
		_listHost.IsVisible(true);
		_back.IsVisible(false);
		_refresh.IsVisible(true);
		_save.IsVisible(false);
		Refresh();
	}

	private void Save()
	{
		if (_openFile is not { Length: > 0 } path)
		{
			Notice(Strings.FileBrowserNoFile, ToastKind.Error);
			return;
		}

		var error = ProjectFiles.Write(_cwd(), path, _editor.Text ?? string.Empty, _openTicks);
		if (error is not null)
		{
			Notice(error, ToastKind.Error);
			return;
		}

		// 写回后刷新时间戳，后续保存仍能检测到外部修改。
		var (_, ticks, _) = ProjectFiles.Read(_cwd(), path);
		_openTicks = ticks;
		Notice(Strings.FileBrowserSaved(path), ToastKind.Success);
	}

	private void Notice(string text, ToastKind kind = ToastKind.Info) => Toasts.Show(View, text, kind);
}
