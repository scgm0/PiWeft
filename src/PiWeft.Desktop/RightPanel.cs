using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

public enum RightPanelSection
{
	Files,
	Git,
	Trajectory,
	Checkpoints,
}

/// 会话的右侧功能面板（对齐 PiDeck 的右侧 Dock）：顶部一条页签条，
/// 下面按分段挂文件浏览器、Git 或检查点；切换分段不重建其它视图，收起只是隐藏。
/// 每个会话各有一个面板，内容跟着会话的工作目录走，默认收起。
/// 视图按需创建，之后一直保留。面板放在主窗口最右侧的定宽栏里，
/// 由默认分隔条拖拽调宽，宽度是窗口级偏好（<see cref="DesktopPreference.RightPanelWidth"/>）。
public sealed class RightPanel : IDisposable
{
	private readonly Func<string> _cwd;
	private readonly Grid _host = new();
	private readonly Border _view;
	private readonly Border _header;
	private readonly List<TabView> _tabs = [];
	private readonly Dictionary<RightPanelSection, UIElement> _views = [];
	private readonly GitCommitAssistant? _gitAssistant;
	private readonly Action? _openGitSettings;
	private readonly DispatcherTimer _trajectoryTimer = new(TimeSpan.FromSeconds(1));
	private Func<TrajectorySource>? _trajectorySource;
	private FileBrowserView? _files;
	private GitView? _git;
	private TrajectoryView? _trajectory;
	private CheckpointsView? _checkpoints;
	private RightPanelSection _section;
	private double _headerRightInset = AppTheme.Space1;
	private bool _open;
	private bool _compactTabs;
	private bool _disposed;

	public RightPanel(
		Func<string> cwd,
		GitCommitAssistant? gitAssistant = null,
		Action? openGitSettings = null,
		RightPanelSection section = RightPanelSection.Files)
	{
		_cwd = cwd;
		_gitAssistant = gitAssistant;
		_openGitSettings = openGitSettings;
		_section = section;

		var tabs = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center);

		// 顺序固定为「文件 → Git → 轨迹 → 检查点」：文件是打开面板最常用的入口，
		// 轨迹是需要盯着的会话复盘面，检查点是最低频的回退动作。
		foreach (var option in new[]
		{
			(Section: RightPanelSection.Files, Icon: "file-text", Title: Strings.FileBrowser),
			(Section: RightPanelSection.Git, Icon: "git-branch", Title: Strings.SidebarGit),
			(Section: RightPanelSection.Trajectory, Icon: "activity", Title: Strings.Trajectory),
			(Section: RightPanelSection.Checkpoints, Icon: "clock", Title: Strings.Checkpoints),
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

		// 窄面板（下限 360）放不下四个「图标 + 文字」页签，低于阈值只留图标，
		// 完整名称放在悬停提示里。窗口按钮的右上角预留宽度不变，免得页签钻到按钮下面。
		_view.SizeChanged += args => UpdateTabDensity(args.NewSize.Width);
		UpdateTabs();
		_trajectoryTimer.Tick += TickTrajectory;
	}

	/// 绑定轨迹数据源（会话分支 + 聊天流式尾行）。放在构造之后单独绑定：
	/// 会话标签页要先构造自己的 ChatViewModel，右侧面板才能拿到它。
	internal void BindTrajectory(Func<TrajectorySource> source) => _trajectorySource = source;

	public UIElement View => _view;

	/// 页签条右侧预留的宽度：窗口按钮浮在面板列右上角（X11 自绘 / Win 原生），
	/// 避让后三段页签不会被按钮压住。由主窗口在窗框刷新时统一写入。
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

		// 轨迹页是唯一要边跑边看的分段：面板开着时每秒推一次进度，切走或收起就停。
		_trajectoryTimer.Stop();
		if (section == RightPanelSection.Trajectory)
		{
			_trajectoryTimer.Start();
		}
	}

	public void Close()
	{
		_open = false;
		_trajectoryTimer.Stop();
		_view.IsVisible(false);
	}

	public void SelectFirstGitChangeForSmoke() => _git?.SelectFirstChangeForSmoke();

	public void GenerateGitSummaryForSmoke() => _git?.GenerateSummaryForSmoke();

	public void ReportGitSummaryForSmoke() => _git?.ReportSummaryForSmoke();

	public void ReportGitGraphGeometryForSmoke() => _git?.ReportGraphGeometryForSmoke();

	public void ReportCheckpointsForSmoke() => _checkpoints?.ReportForSmoke();

	public void SelectTrajectoryRecordForSmoke() => _trajectory?.SelectForSmoke();

	public void ReportTrajectoryForSmoke() => _trajectory?.ReportForSmoke();

	public void ExpandFirstCheckpointDiffForSmoke() => _checkpoints?.ExpandFirstDiffForSmoke();


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

			case RightPanelSection.Trajectory:
				_trajectory?.Refresh();
				break;

			case RightPanelSection.Checkpoints:
				_checkpoints?.Refresh();
				break;
		}
	}

	private void TickTrajectory()
	{
		if (_disposed || !_open || _section != RightPanelSection.Trajectory)
		{
			_trajectoryTimer.Stop();
			return;
		}

		// 面板跟着会话切换只切可见性（不 Close）：隐藏时跳过取数，切回来下一次秒表继续，
		// 不必在 MainWindow 里为「活动会话变了」再加一条恢复路径。
		if (!_view.IsVisible)
		{
			return;
		}

		_trajectory?.Refresh();
	}

	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors((Element)View);
		_files?.ApplyTheme();
		_git?.ApplyTheme();
		_trajectory?.ApplyTheme();
		_checkpoints?.ApplyTheme();
		UpdateTabs();
	}

	private bool EnsureView(RightPanelSection section)
	{
		var created = false;
		if (!_views.ContainsKey(section))
		{
			UIElement view = section switch
			{
				RightPanelSection.Files => (_files = new FileBrowserView(_cwd)).View,
				RightPanelSection.Git => (_git = new GitView(_cwd, _gitAssistant, _openGitSettings)).View,
				RightPanelSection.Trajectory => (_trajectory = new TrajectoryView(ProjectTrajectory)).View,
				_ => (_checkpoints = new CheckpointsView(_cwd)).View,
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

	private void UpdateTabs()
	{
		foreach (var tab in _tabs)
		{
			var active = tab.Section == _section;
			tab.Button.StyleName(active ? AppStyles.SegmentButtonActive : AppStyles.SegmentButton);
			tab.Label.IsVisible(!_compactTabs);
			tab.Button.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0));
			if (tab.Button.Content is StackPanel content)
			{
				content.Spacing(_compactTabs ? 0 : 5);
			}

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

	private void UpdateTabDensity(double width)
	{
		var compact = width > 0 && width < 410;
		if (compact == _compactTabs)
		{
			return;
		}

		_compactTabs = compact;
		UpdateTabs();
	}

	private TrajectorySource ProjectTrajectory() => _trajectorySource?.Invoke() ?? new TrajectorySource([], [], false);

	private static string TitleFor(RightPanelSection section) => section switch
	{
		RightPanelSection.Git => Strings.SidebarGit,
		RightPanelSection.Trajectory => Strings.Trajectory,
		RightPanelSection.Checkpoints => Strings.Checkpoints,
		_ => Strings.FileBrowser,
	};

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_trajectoryTimer.Stop();
		_trajectory?.Dispose();
		_git?.Dispose();
	}

	private sealed record TabView(
		RightPanelSection Section,
		string IconName,
		Button Button,
		Image Icon,
		TextBlock Label);
}
