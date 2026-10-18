using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>右侧功能面板的分段：文件、Git 与终端都是会话的辅助表面，不再是独立标签页。</summary>
public enum RightPanelSection
{
	Files,
	Git,
	Terminal,
}

/// <summary>
/// 会话的右侧功能面板（对齐 PiDeck 的右侧 Dock）：顶部一条页签条，
/// 下面按分段挂文件浏览器、Git 或终端；切换分段不重建其它视图，收起只是隐藏。
/// </summary>
/// <remarks>
/// 每个会话各有一个面板，内容跟着会话的工作目录走，默认收起。
/// 视图按需创建：终端会真的启动伪终端，所以第一次切到终端时才建，之后一直保留，
/// 收起再展开不会丢掉 shell 的历史与工作目录。面板放在主窗口最右侧的定宽栏里，
/// 由默认分隔条拖拽调宽，宽度是窗口级偏好（<see cref="DesktopPreference.RightPanelWidth"/>）。
/// </remarks>
public sealed class RightPanel : IDisposable
{
	private readonly Func<string> _cwd;
	private readonly Grid _host = new();
	private readonly Border _view;
	private readonly Border _header;
	private readonly List<TabView> _tabs = [];
	private readonly Dictionary<RightPanelSection, UIElement> _views = [];
	private TerminalDock? _terminal;
	private FileBrowserView? _files;
	private GitView? _git;
	private RightPanelSection _section;
	private double _headerRightInset = AppTheme.Space1;
	private bool _open;
	private bool _disposed;

	public RightPanel(Func<string> cwd, RightPanelSection section = RightPanelSection.Files)
	{
		_cwd = cwd;
		_section = section;

		var tabs = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center);

		// 顺序固定为「文件 → Git → 终端」：文件是打开面板最常用的入口。
		foreach (var option in new[]
		{
			(Section: RightPanelSection.Files, Icon: "file-text", Title: Strings.FileBrowser),
			(Section: RightPanelSection.Git, Icon: "git-branch", Title: Strings.SidebarGit),
			(Section: RightPanelSection.Terminal, Icon: "terminal", Title: Strings.Terminal),
		})
		{
			var icon = new Image()
				.Width(14)
				.Height(14)
				.VerticalAlignment(VerticalAlignment.Center);
			var label = new TextBlock()
				.FontSize(AppTheme.FontSizeCaption)
				.VerticalAlignment(VerticalAlignment.Center);
			var button = new Button()
				.StyleName(AppStyles.SegmentButton)
				.Height(26)
				.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
				.HoverTip(option.Title)
				.Content(new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(5)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(icon, label))
				.OnClick(() => Show(option.Section));

			tabs.Children(button);
			_tabs.Add(new TabView(option.Section, option.Icon, button, icon, label));
		}

		// 面板自己不放收起键：唤醒 / 收起统一由标题栏上那个跟着面板左缘走的键负责。
		// 页签条与主标题栏同高（40），这样面板列从窗口顶部开始，右侧与主区共用一行窗框。
		_header = new Border { ClipToBounds = true }
			.StyleName(AppStyles.RightPanelHeader)
			.Height(AppTheme.TitleBarHeight)
			.Padding(new Thickness(AppTheme.Space2, 0, _headerRightInset, 0))
			.Child(tabs);

		_host.ClipToBounds = true;

		_view = new Border()
			.StyleName(AppStyles.RightPanel)
			.IsVisible(false)
			.Child(new DockPanel()
				.LastChildFill()
				.Children(_header.DockTop(), _host));

		UpdateTabs();
	}

	/// <summary>面板根元素：由主窗口放进内容区最右侧的定宽列，从窗框顶部一直铺到底。</summary>
	public UIElement View => _view;

	/// <summary>
	/// 页签条右侧预留的宽度：窗口按钮浮在面板列右上角（X11 自绘 / Win 原生），
	/// 避让后三段页签不会被按钮压住。由主窗口在窗框刷新时统一写入。
	/// </summary>
	public void SetHeaderRightInset(double inset)
	{
		if (Math.Abs(_headerRightInset - inset) <= 0.5)
		{
			return;
		}

		_headerRightInset = inset;
		_header.Padding(new Thickness(AppTheme.Space2, 0, inset, 0));
	}

	public RightPanelSection Section => _section;

	public bool IsOpen => _open;

	/// <summary>展开面板并切到指定分段。</summary>
	public void Show(RightPanelSection section)
	{
		if (_disposed)
		{
			return;
		}

		_section = section;
		_open = true;
		var created = EnsureView(section);
		_view.IsVisible(true);
		UpdateTabs();

		// 新建的视图在构造时已经读过一遍；只有切回已有分段才需要重新取状态。
		if (!created)
		{
			Refresh();
		}

		if (section == RightPanelSection.Terminal)
		{
			FocusTerminalSoon();
		}
	}

	/// <summary>收起面板；终端等视图保持存活，再次展开时延续原来的状态。</summary>
	public void Close()
	{
		_open = false;
		_view.IsVisible(false);
	}

	/// <summary>重读当前分段（文件列表 / Git 状态）；终端由自己的定时器重绘。</summary>
	public void Refresh()
	{
		if (_disposed || !_open)
		{
			return;
		}

		switch (_section)
		{
			case RightPanelSection.Files:
				_files?.Refresh();
				break;

			case RightPanelSection.Git:
				_git?.Refresh();
				break;
		}
	}

	/// <summary>主题切换后刷新角色文本与图标颜色；已创建的视图各自跟随。</summary>
	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors((Element)View);
		_terminal?.ApplyTheme();
		_files?.ApplyTheme();
		_git?.ApplyTheme();
		UpdateTabs();
	}

	/// <summary>面板里的终端最近可见时把键盘焦点交给它。</summary>
	public void FocusTerminal()
	{
		if (_terminal is { } terminal && _section == RightPanelSection.Terminal && _open)
		{
			terminal.FocusTerminal();
		}
	}

	private void FocusTerminalSoon()
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null)
		{
			FocusTerminal();
			return;
		}

		dispatcher.BeginInvoke(FocusTerminal);
	}

	/// <summary>只创建一次视图并挂进内容区；之后切换只改可见性。返回本次是否新建。</summary>
	private bool EnsureView(RightPanelSection section)
	{
		var created = false;
		if (!_views.ContainsKey(section))
		{
			UIElement view = section switch
			{
				RightPanelSection.Files => (_files = new FileBrowserView(_cwd)).View,
				RightPanelSection.Git => (_git = new GitView(_cwd)).View,
				_ => (_terminal = new TerminalDock(_cwd, compact: true)).View,
			};

			_views[section] = view;
			_host.Children(view);
			created = true;
		}

		foreach (var (key, view) in _views)
		{
			view.IsVisible(key == section);
		}

		return created;
	}

	/// <summary>活动页签提亮并浮起，其余安静地留在底色上。</summary>
	private void UpdateTabs()
	{
		foreach (var tab in _tabs)
		{
			var active = tab.Section == _section;
			tab.Button.StyleName(active ? AppStyles.SegmentButtonActive : AppStyles.SegmentButton);
			tab.Label.Text(TitleFor(tab.Section));
			if (active)
			{
				tab.Label.PrimaryText();
			}
			else
			{
				tab.Label.MutedText();
			}

			tab.Icon.Source(AppIcons.Get(tab.IconName, active ? AppTheme.TextPrimary : AppTheme.TextMuted));
		}
	}

	private static string TitleFor(RightPanelSection section) => section switch
	{
		RightPanelSection.Git => Strings.SidebarGit,
		RightPanelSection.Terminal => Strings.Terminal,
		_ => Strings.FileBrowser,
	};

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_terminal?.Dispose();
	}

	private sealed record TabView(
		RightPanelSection Section,
		string IconName,
		Button Button,
		Image Icon,
		TextBlock Label);
}
