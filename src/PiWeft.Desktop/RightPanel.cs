using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>右侧功能面板的分段：文件、Git 与检查点是会话的辅助表面，不再是独立标签页（终端在会话底部）。</summary>
public enum RightPanelSection
{
	Files,
	Git,
	Trajectory,
	Checkpoints,
}

/// <summary>
/// 会话的右侧功能面板（对齐 PiDeck 的右侧 Dock）：顶部一条页签条，
/// 下面按分段挂文件浏览器、Git 或检查点；切换分段不重建其它视图，收起只是隐藏。
/// </summary>
/// <remarks>
/// 每个会话各有一个面板，内容跟着会话的工作目录走，默认收起。
/// 视图按需创建，之后一直保留。面板放在主窗口最右侧的定宽栏里，
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

		// 窄面板（360 是允许的下限）放不下四个「图标 + 文字」页签：低于阈值时只留图标，
		// 完整名称仍在悬停提示里。窗口按钮的右上角预留宽度不变，避免页签钻到按钮下面。
		_view.SizeChanged += args => UpdateTabDensity(args.NewSize.Width);
		UpdateTabs();
		_trajectoryTimer.Tick += TickTrajectory;
	}

	/// <summary>
	/// 绑定轨迹数据源（会话分支 + 聊天流式尾行）。放在构造之后单独绑定：
	/// 会话标签页要先构造自己的 ChatViewModel，右侧面板才能拿到它。
	/// </summary>
	internal void BindTrajectory(Func<TrajectorySource> source) => _trajectorySource = source;

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

		// 轨迹页是唯一需要「边跑边看」的分段：面板开着时每秒推一次进度，
		// 切到别的分段或收起就停，静态分段不占调度。
		_trajectoryTimer.Stop();
		if (section == RightPanelSection.Trajectory)
		{
			_trajectoryTimer.Start();
		}
	}

	/// <summary>收起面板；终端等视图保持存活，再次展开时延续原来的状态。</summary>
	public void Close()
	{
		_open = false;
		_trajectoryTimer.Stop();
		_view.IsVisible(false);
	}

	/// <summary>视觉验收：Git 分段选中第一个改动。</summary>
	public void SelectFirstGitChangeForSmoke() => _git?.SelectFirstChangeForSmoke();

	/// <summary>视觉验收：直接走一次 AI 提交摘要（看进度条与内联失败行是否需要新布局）。</summary>
	public void GenerateGitSummaryForSmoke() => _git?.GenerateSummaryForSmoke();

	/// <summary>视觉验收：把摘要结果打到 stdout。</summary>
	public void ReportGitSummaryForSmoke() => _git?.ReportSummaryForSmoke();

	/// <summary>视觉验收：打印泳道几何。</summary>
	public void ReportGitGraphGeometryForSmoke() => _git?.ReportGraphGeometryForSmoke();

	/// <summary>视觉验收：打印检查点行数与摘要。</summary>
	public void ReportCheckpointsForSmoke() => _checkpoints?.ReportForSmoke();

	/// <summary>视觉验收：选中轨迹里的第一条工具记录，让检查器显示入参与结果。</summary>
	public void SelectTrajectoryRecordForSmoke() => _trajectory?.SelectForSmoke();

	/// <summary>视觉验收：打印轨迹轮数、记录数与当前窗口。</summary>
	public void ReportTrajectoryForSmoke() => _trajectory?.ReportForSmoke();

	/// <summary>视觉验收：展开第一条检查点的变更概览。</summary>
	public void ExpandFirstCheckpointDiffForSmoke() => _checkpoints?.ExpandFirstDiffForSmoke();


	/// <summary>重读当前分段（文件列表 / Git 状态 / 检查点列表）；终端由自己的定时器重绘。</summary>
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

	/// <summary>主题切换后刷新角色文本与图标颜色；已创建的视图各自跟随。</summary>
	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors((Element)View);
		_files?.ApplyTheme();
		_git?.ApplyTheme();
		_trajectory?.ApplyTheme();
		_checkpoints?.ApplyTheme();
		UpdateTabs();
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

	/// <summary>活动页签提亮并浮起，其余安静地留在底色上。</summary>
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
