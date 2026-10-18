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
	private readonly StackPanel _list = new();
	private readonly MultiLineTextBox _editor = new();
	private readonly TextBlock _notice = new();
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
		_list.Spacing(1);

		_notice.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap).IsVisible(false);

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
			.Child(new ScrollViewer().SmoothScrolling()
				.Content(_list)
				.AutoVerticalScroll()
				.NoHorizontalScroll());

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
					_notice.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space2)).DockBottom(),
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

		foreach (var child in _list.Children.ToArray())
		{
			UiLifetime.Release(child);
		}

		_list.Clear();

		if (_relativeDir.Length > 0)
		{
			_list.Children(Row("\u2191", Strings.FileBrowserParent, string.Empty, () => Navigate(ParentOf(_relativeDir))));
		}

		var entries = ProjectFiles.List(cwd, _relativeDir, GitStatuses(cwd));
		if (entries.Count == 0)
		{
			_list.Children(new TextBlock().Text(Strings.FileBrowserEmpty).FontSize(AppTheme.FontSizeCaption).MutedText());
			return;
		}

		foreach (var entry in entries)
		{
			var captured = entry;
			_list.Children(Row(
				entry.IsDirectory ? "\u25b8" : " ",
				entry.Name + (entry.IsDirectory ? "/" : string.Empty),
				entry.Status,
				() =>
				{
					if (captured.IsDirectory)
					{
						Navigate(captured.RelativePath);
					}
					else
					{
						OpenFile(captured.RelativePath);
					}
				}));
		}
	}

	/// <summary>一行一个条目：前缀图标、名称撑满、Git 状态贴在右缘。</summary>
	private static UIElement Row(string prefix, string name, string status, Action onClick)
	{
		var content = new DockPanel().LastChildFill();

		if (status.Length > 0)
		{
			content.Children(new TextBlock()
				.Text(status)
				.FontSize(AppTheme.FontSizeMicro)
				.AccentText()
				.VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(AppTheme.Space2, 0, 0, 0))
				.DockRight());
		}

		content.Children(
			new TextBlock()
				.Text(prefix)
				.FontSize(AppTheme.FontSizeMicro)
				.Width(12)
				.AccentText()
				.DockLeft(),
			new TextBlock()
				.Text(name)
				.FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText()
				.TextWrapping(TextWrapping.NoWrap)
				.TextTrimming(TextTrimming.CharacterEllipsis));

		// 列表行用导航行样式：默认不铺底，悬停才浮起一层，窄面板里不会堆成一排卡片。
		return new Button()
			.StyleName(AppStyles.GhostButton)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.OnClick(onClick)
			.Content(content);
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
			Notice(error);
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
		_notice.IsVisible(false);
		Refresh();
	}

	private void Save()
	{
		if (_openFile is not { Length: > 0 } path)
		{
			Notice(Strings.FileBrowserNoFile);
			return;
		}

		var error = ProjectFiles.Write(_cwd(), path, _editor.Text ?? string.Empty, _openTicks);
		if (error is not null)
		{
			Notice(error);
			return;
		}

		// 写回后刷新时间戳，后续保存仍能检测到外部修改。
		var (_, ticks, _) = ProjectFiles.Read(_cwd(), path);
		_openTicks = ticks;
		Notice(Strings.FileBrowserSaved(path));
	}

	private void Notice(string text) => _notice.Text(text).IsVisible(true);
}
