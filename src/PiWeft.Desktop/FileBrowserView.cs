using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// D-02：项目文件浏览与内置编辑器。左侧按目录导航（带 Git 状态标记），右侧编辑文本并保存；
/// 保存时用时间戳检测外部修改，避免覆盖别人的改动。
/// </summary>
public sealed class FileBrowserView
{
	private readonly Func<string> _cwd;
	private readonly StackPanel _list = new();
	private readonly MultiLineTextBox _editor = new();
	private readonly TextBlock _notice = new();
	private readonly Button _save;

	private string _relativeDir = string.Empty;
	private string? _openFile;
	private long _openTicks;

	public FileBrowserView(Func<string> cwd)
	{
		_cwd = cwd;
		_list.Spacing(2);

		_notice.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);
		_save = AppIcons.Button("check", Strings.FileBrowserSave, Save);
		var refresh = AppIcons.Button("refresh-cw", Strings.FileBrowserRefresh, Refresh);

		var left = new Border()
			.StyleName(AppStyles.Panel)
			.Width(300)
			.Padding(new Thickness(AppTheme.Space2))
			.Child(new ScrollViewer().SmoothScrolling()
				.Content(_list)
				.AutoVerticalScroll()
				.NoHorizontalScroll());

		_editor
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeCaption)
			.CornerRadius(AppTheme.RadiusMd)
			.StyleName(AppStyles.TextInput);

		var right = new DockPanel()
			.LastChildFill()
			.Children(
				new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(AppTheme.Space2)
					.Children(refresh, _save)
					.DockTop(),
				_notice.Margin(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2)).DockBottom(),
				_editor);

		View = new Border()
			.StyleName(AppStyles.Window)
			.Child(new DockPanel()
				.LastChildFill()
				.Children(
					left.Margin(new Thickness(AppTheme.Space3)).DockLeft(),
					right.Margin(new Thickness(0, AppTheme.Space3, AppTheme.Space3, AppTheme.Space3))));

		Refresh();
	}

	public UIElement View { get; }

	/// <summary>重新列举当前目录。</summary>
	public void Refresh()
	{
		var cwd = _cwd();
		var path = _relativeDir.Length == 0 ? "/" : "/" + _relativeDir;

		foreach (var child in _list.Children.ToArray())
		{
			UiLifetime.Release(child);
		}

		_list.Clear();
		_list.Children(new TextBlock().Text(path).FontSize(AppTheme.FontSizeMicro).SubtleText());

		if (_relativeDir.Length > 0)
		{
			var parent = ParentOf(_relativeDir);
			_list.Children(Row("..", Strings.FileBrowserParent, string.Empty, () => Navigate(parent)));
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
				entry.IsDirectory ? "▸" : " ",
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

	private static UIElement Row(string prefix, string name, string status, Action onClick)
	{
		var cells = new List<UIElement>
		{
			new TextBlock().Text(prefix).FontSize(AppTheme.FontSizeMicro).Width(14).AccentText(),
		};

		if (status.Length > 0)
		{
			cells.Add(new TextBlock().Text(status).FontSize(AppTheme.FontSizeMicro).AccentText());
		}

		cells.Add(new TextBlock()
			.Text(name)
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis));

		return new Button()
			.StyleName(AppStyles.CardMutedButton)
			.Padding(new Thickness(AppTheme.Space2, 3, AppTheme.Space2, 3))
			.OnClick(onClick)
			.Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children([.. cells]));
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
			_openFile = null;
			_notice.Text(error);
			return;
		}

		_openFile = relativePath;
		_openTicks = ticks;
		_editor.Text = content ?? string.Empty;
		_notice.Text(Strings.FileBrowserOpened(relativePath));
	}

	private void Save()
	{
		if (_openFile is not { Length: > 0 } path)
		{
			_notice.Text(Strings.FileBrowserNoFile);
			return;
		}

		var error = ProjectFiles.Write(_cwd(), path, _editor.Text ?? string.Empty, _openTicks);
		if (error is not null)
		{
			_notice.Text(error);
			return;
		}

		// 写回后刷新时间戳，后续保存仍能检测到外部修改。
		var (_, ticks, _) = ProjectFiles.Read(_cwd(), path);
		_openTicks = ticks;
		_notice.Text(Strings.FileBrowserSaved(path));
	}
}
