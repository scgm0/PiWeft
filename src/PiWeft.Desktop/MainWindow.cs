using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.Agent;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Stats;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Themes;
using PiWeft.Mcp;
using ZLogger;

namespace PiWeft.Desktop;

/// <summary>
/// 桌面外壳主窗口：自定义标题栏、左侧会话栏、多标签会话主区。
/// 每个标签是一个独立的 <see cref="SessionTab"/>（各自持有会话与输入区），因此可以并行开着多个会话。
/// 会话文件与 CLI、pi 完全互通。
/// </summary>
public sealed partial class MainWindow : Window
{
	private readonly string _cwd;
	private readonly SessionCatalog _catalog = new();
	private CliArguments _arguments;
	/// <summary>与 TabControl 一一对应的标签内容：<see cref="SessionTab"/> 或 Git/文件/终端等辅助页。</summary>
	private readonly List<object> _tabEntries = [];
	private McpConnection? _mcp;
	private IReadOnlyList<AgentTool> _mcpTools = [];
	/// <summary>标签标题文本，主题/名称变化时只改这一个控件。</summary>
	private SettingsView? _settingsView;

	/// <summary>设置是窗口级弹窗（与 PiDeck 一致），不占用主区标签。</summary>
	private SettingsDialog? _settingsDialog;

	/// <summary>
	/// 每个会话各有一个右侧面板（文件 / Git / 终端）：内容跟着会话的工作目录，
	/// 默认收起；宽度是窗口级偏好，切换会话时沿用。
	/// </summary>
	private readonly Dictionary<SessionTab, RightPanel> _sessionPanels = [];
	/// <summary>用户偏好宽度：只由拖拽分隔条/关窗写入；窗口缩放造成的夹取不覆盖它。</summary>
	private double _rightPanelWidth = AppTheme.RightPanelWidth;
	private SplitPanel _panelSplit = null!;
	private Grid _panelHost = null!;
	private SessionTab? _lastSessionTab;
	private CommandPalette? _palette;
	/// <summary>主题目录监视器：主题文件变化时即时换肤（PI-10）。</summary>
	private ThemeWatcher? _themeWatcher;

	private UsageStatsView? _usageStatsView;
	private string? _pendingNotice;
	private DesktopPreference? _queuedThemePreference;
	private DispatcherTimer? _themeApplyGate;
	private readonly Dictionary<string, CliEnvironment> _environments = new(StringComparer.Ordinal);
	private CliEnvironment? _environment;
	private IReadOnlyList<SessionListItem> _sessions = [];
	private bool _restoringSelection;
	private Border _sidebar = null!;
	private SplitPanel _workspace = null!;
	private Button _backButton = null!;
	private bool _sidebarPreferred;
	private TreeView _chatsTree = null!;
	private Button _sessionsTabButton = null!;
	private Button _projectsTabButton = null!;
	private Button _newSessionButton = null!;
	private Button _addProjectButton = null!;
	private bool _projectsTab;
	private string? _renamingPath;
	private readonly HashSet<string> _removedProjects = new(StringComparer.Ordinal);
	// 正在后台解析的会话文件：同一份文件只允许一次打开，连点也不会排队重复读几十 MB。
	private readonly HashSet<string> _openingSessions = new(StringComparer.Ordinal);
	// 启动时提前并行解析的首个会话：侧栏摘要扫描和它读的是同一份文件，别让打开流程再读一遍。
	private (string Path, SessionManager Manager)? _prefetchedOpen;
	// 侧栏摘要扫描按需触发：收起状态下根本不扫（省掉全部读盘），展开或搜索时才加载。
	private Task? _sessionsLoad;
	private bool _sessionsLoaded;
	// 侧栏里展开过的项目（工作目录）。默认折叠，只有这里记录过的才自动展开。
	private readonly HashSet<string> _expandedProjects = new(StringComparer.Ordinal);
	// 准备阶段会话面板里的加载段落（打开会话 / 写回会话名称），以及窗口关闭时中断后台读取的令牌源。
	private readonly SessionLoadingPanel _sessionLoading = new();
	private TextBlock _welcomeTitle = null!;
	private TextBlock _welcomeBody = null!;
	private readonly CancellationTokenSource _sessionAbort = new();
	// 窗口已关闭：后台打开可能在取消落地前就跑完了，不能再往死窗口里塞标签页。
	private bool _sessionHostClosed;
	// 在途的后台打开数量；自检与无人值守演练用它等标签页就位，而不是猜固定延时。
	private int _pendingSessionOpens;
	private IReadOnlyList<SessionListItem> _allSessions = [];
	private IReadOnlyList<Model> _models = [];

	/// <summary>「对话」分段的工作目录：这里开出的会话不属于任何项目。</summary>
	private readonly string _chatWorkspace;

	/// <summary>「对话」分段的节点，重建后用来恢复选中。</summary>
	private IReadOnlyList<TreeViewNode> _chatNodes = [];

	/// <summary>「对话」为空时的提示；列表铺满剩余高度，提示叠在列表上方。</summary>
	private TextBlock _chatsEmpty = null!;

	private SessionSearchPalette _sessionSearch = null!;
	private SessionImportDialog _importDialog = null!;
	private Button _searchButton = null!;
	private TextBox _nameBox = null!;
	private TreeView _sessionTree = null!;

	/// <summary>当前的快捷键绑定（启动时读一次，改键后提示重启生效）。</summary>
	private ShortcutMap _shortcuts = new();

	/// <summary>当前的会话树节点，便于在重建后恢复选中。</summary>
	private IReadOnlyList<TreeViewNode> _sessionNodes = [];
	private TabControl _tabControl = null!;
	private Border _welcome = null!;
	private TextBlock _banner = null!;
	private TextBlock _status = null!;

	/// <summary>是否在启动后自动切换一次主题然后退出（用于无人值守验证运行时换肤）。</summary>
	public bool ThemeSmoke { get; init; }

	/// <summary>启动后自动打开的标签（<c>settings</c> / <c>git</c>），用于无人值守截图检查这些面板。</summary>
	public string? SmokeTab { get; init; }

	/// <summary>启动后自动打开某个视图并停留，供截图验证（git/settings/branch/search）。</summary>
	public string? UiSmoke { get; init; }

	public MainWindow(string cwd)
	{
		_cwd = cwd;
		var preference = DesktopPreference.Load();
		// 诊断要覆盖启动本身：环境构建、首个会话解析与标签页布局都在开启之后才发生。
		Diagnostics.SetEnabled(preference.Diagnostics);
		// 帧档：MewUI 每帧渲染后都无条件填好 LastFrameStats，这里只把它读进定长环形缓冲。
		FrameRendered += ObserveDiagnosticsFrame;
		// 输入→绘制：没被下游处理掉的按键与指针事件会冒泡到窗口根，够判断「点了但半天没反应」。
		// 滚轮通常已被文本控件或滚动级联处理掉，不在此处重复记一次。
		PreviewKeyDown += _ => Diagnostics.NoteInput();
		MouseDown += _ => Diagnostics.NoteInput();
		foreach (var expandedProject in preference.ExpandedProjects) _expandedProjects.Add(expandedProject);
		_chatWorkspace = preference.ResolveChatWorkspace();
		this.Resizable(preference.WindowWidth ?? 1280, preference.WindowHeight ?? 820);
		this.Padding(Thickness.Zero);
		this.AllowDrop();
		// 窗口本身显式使用内嵌 Regular：默认样式、继承文本与框架对话框都从这里拿到同一张脸。
		this.FontFamily = AppFonts.DefaultUi;
		this.FontWeight = FontWeight.Normal;
		// 自定义标题栏必须在显示原生窗口之前定型：Win32/macOS 走客户区延伸，X11 走无边框。
		ConfigureWindowFrame();

		// 模型与会话都由界面决定，因此这里只用最少的默认开关。
		_arguments = CliArguments.Parse(["--mode", "text"]).Arguments!;
	}

	/// <summary>把这一帧的帧时与绘制量交给诊断的帧档；关闭时内部立即返回。</summary>
	private void ObserveDiagnosticsFrame() => Diagnostics.ObserveFrame(LastFrameStats);

	protected override void OnBuild()
	{
		base.OnBuild();

		// 命名样式挂在窗口上：颜色由主题解析，因此换肤时 MewUI 自动重绘，不需要重建界面。
		this.StyleSheet(AppStyles.Create());

		RegisterShortcuts();
		BuildUi();
		this.OnLoaded(Startup);
	}

	/// <summary>
	/// 注册窗口级快捷键。绑定来自 <see cref="ShortcutMap"/>（内置默认值 + <c>desktop.json</c> 里的覆盖），
	/// 因此用户可以改键；Ctrl+1…9 是范围型动作，固定不参与改键。
	/// </summary>
	private void RegisterShortcuts()
	{
		_shortcuts = new ShortcutMap(DesktopPreference.Load().Shortcuts);

		this.OnKeyDown(key =>
		{
			// 设置面板正在等待用户按下新组合键：优先把这次按键交给它。
			// 走窗口级处理（而不是输入框）是刻意的 —— 输入法会吞掉合成出来的可打印字符。
			if (_settingsView?.CapturingId is not null)
			{
				if (key.Key == Key.Escape)
				{
					key.Handled = true;
					_settingsView.CancelCapture();
					return;
				}

				if (_settingsView.CaptureBinding(key.Key, key.Modifiers))
				{
					key.Handled = true;
					return;
				}
			}

			// Ctrl+1…9 直接切到第 N 个标签。
			if (key.ControlKey && !key.ShiftKey && !key.AltKey && key.Key is >= Key.D1 and <= Key.D9)
			{
				var index = key.Key - Key.D1;
				if (index < _tabEntries.Count)
				{
					key.Handled = true;
					_tabControl.SelectedIndex = index;
					UpdateStatus();
				}

				return;
			}

			foreach (var definition in ShortcutMap.Definitions)
			{
				if (_shortcuts.Get(definition.Id) is not { } binding || !binding.Matches(key))
				{
					continue;
				}

				key.Handled = true;
				InvokeShortcut(definition.Id);
				return;
			}
		});
	}

	/// <summary>执行一个快捷键动作。</summary>
	private void InvokeShortcut(string id)
	{
		switch (id)
		{
			case ShortcutActions.Palette:
				_palette?.Toggle(this);
				break;

			case ShortcutActions.NewSession:
				NewSessionFromCurrentView();
				break;

			case ShortcutActions.Models:
				CurrentTab()?.ShowModelPicker();
				break;

			case ShortcutActions.OpenProject:
				_ = ChooseProjectAsync();
				break;

			case ShortcutActions.SearchSessions:
				_sidebar.IsVisible(true);
				_sessionSearch.Toggle(this);
				break;

			case ShortcutActions.CloseTab:
				CloseCurrentTab();

				break;

			case ShortcutActions.Settings:
				OpenSettingsDialog();
				break;

			case ShortcutActions.Git:
				ShowGitPanel();
				break;

			case ShortcutActions.Files:
				ShowFilesPanel();
				break;

			case ShortcutActions.Terminal:
				ToggleTerminalPanel();
				break;

			case ShortcutActions.RightPanel:
				ToggleRightPanel();
				break;

			case ShortcutActions.ToggleTheme:
				ToggleTheme();
				break;

			case ShortcutActions.Refresh:
				RefreshSessions();
				break;

			case ShortcutActions.Send:
				if (_tabControl.SelectedIndex >= 0 && _tabControl.SelectedIndex < _tabEntries.Count &&
					_tabEntries[_tabControl.SelectedIndex] is SessionTab sendTo)
				{
					sendTo.SendFromShortcut();
				}

				break;

			case ShortcutActions.Branches:
				CurrentTab()?.ShowBranches();
				break;
		}
	}

	/// <summary>
	/// 构建静态外壳一次；主题切换只重解析样式与更新动态内容，不替换会话与输入控件。
	/// </summary>
	private void BuildUi()
	{
		_banner = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText();
		_status = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText();

		// ------------------------------------------------------------ 外壳
		// 窗口标题栏（品牌 + 当前语境 + 窗口按钮）由 BuildChromeShell 组装；
		// 主区仍直接是内容，不新增全局工具栏。

		// ------------------------------------------------------------ 侧栏
		// 新建会话 + 搜索会话（两条与侧栏同色的导航行）+ 会话列表；全局动作（面板/Git/设置/主题）收进底部一行。
		// 「搜索会话」不是常驻输入框：点击打开搜索弹窗（Ctrl+K），结果按名称、路径与正文过滤。
		_sessionSearch = new SessionSearchPalette(
			() => _allSessions, OpenSessionFromSearch, () => !_sessionsLoaded);
		_sessionSearch.Closed += FocusActiveEntry;

		// 导入外部会话：导入后立刻刷新侧栏，新会话马上能打开。
		_importDialog = new SessionImportDialog(() => RefreshSessions(force: true));
		_importDialog.Closed += FocusActiveEntry;
		_searchButton = AppIcons.TextButton("search", Strings.SessionSearch, () => _sessionSearch.Toggle(this), strong: true)
			.MinHeight(AppTheme.ControlHeightLg)
			.CornerRadius(AppTheme.RadiusMd);

		_nameBox = new TextBox().PrimarySelection().FontSize(AppTheme.FontSizeControl)
			.Placeholder(Strings.SessionNamePlaceholder)
			.IsVisible(false)
			.OnKeyDown(key =>
			{
				if (key.Key == Key.Enter)
				{
					key.Handled = true;
					RenameSelected();
				}
			});

		// 「项目」视图：工作区列表（文件夹 + 行级 ⋯ 菜单）。行内边距在 ConfigureSessionRows 里定。
		_sessionTree = new TreeView().FontSize(AppTheme.FontSizeControl)
			.BorderThickness(0).Background(AppTheme.Transparent);
		ConfigureSessionRows();
		_sessionTree.SelectedNodeChanged += OnSessionSelected;

		// 「对话」tab：聊天目录里的会话（不属于任何项目）；「项目」tab：按项目分组的任务树。
		_chatsTree = new TreeView().FontSize(AppTheme.FontSizeControl)
			.BorderThickness(0).Background(AppTheme.Transparent);
		// 项目的展开/折叠是用户显式动作：记下来，下次启动按记录恢复（没记录过的一律折叠）。
		// 程序化重建时的同值调用不会触发这两个事件，因此不会把默认状态写进偏好。
		_sessionTree.Expanding += args => RememberProjectExpanded(args.Item, expanded: true);
		_sessionTree.Collapsing += args => RememberProjectExpanded(args.Item, expanded: false);
		ConfigureChatsRows();
		_chatsTree.SelectedNodeChanged += OnSessionSelected;

		// 聊天目录还没有会话时给一句引导，而不是一片空白；列表铺满剩余高度，提示叠在列表上方。
		_chatsEmpty = new TextBlock()
			.Text(Strings.ChatsEmpty)
			.FontSize(AppTheme.FontSizeCaption)
			.SubtleText()
			.TextWrapping(TextWrapping.Wrap)
			.Margin(new Thickness(10, AppTheme.Space2, 6, 0))
			.VerticalAlignment(VerticalAlignment.Top)
			.IsHitTestVisible(false)
			.IsVisible(false);

		_banner.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap);

		// 顶部「对话 | 项目」切换：两列等分侧栏宽度，侧栏拖宽时按钮跟着变宽；
		// TextAlignment 保证文字始终落在各自按钮正中。
		_sessionsTabButton = new Button().Ghost()
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Content(new TextBlock().Text(Strings.SidebarTabChats).FontSize(AppTheme.FontSizeCaption).PrimaryText()
				.TextAlignment(TextAlignment.Center))
			.OnClick(() => ShowSidebarView(projects: false));
		_projectsTabButton = new Button().Ghost()
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Content(new TextBlock().Text(Strings.SidebarTabProjects).FontSize(AppTheme.FontSizeCaption).PrimaryText()
				.TextAlignment(TextAlignment.Center))
			.OnClick(() => ShowSidebarView(projects: true));

		// 「新建 / 添加项目」与侧栏同色、悬停才浮起（PiDeck 式导航行）：默认不铺底色，行宽撑满侧栏。
		// 「新建」属于「对话」分段，落点在聊天目录，不会混进当前项目。
		_newSessionButton = AppIcons.TextButton("plus", Strings.NewSession,
			NewChatSession, strong: true)
			.MinHeight(AppTheme.ControlHeightLg)
			.CornerRadius(AppTheme.RadiusMd);
		_addProjectButton = AppIcons.TextButton("folder-open", Strings.AddProject,
			() => _ = ChooseProjectAsync(), strong: true)
			.MinHeight(AppTheme.ControlHeightLg)
			.CornerRadius(AppTheme.RadiusMd);

		var sidebarTop = new StackPanel().Spacing(AppTheme.Space2);
		sidebarTop.Children(
			new Grid().Columns("*,*").Spacing(4)
				.Children(_sessionsTabButton.Column(0), _projectsTabButton.Column(1)),
			new DockPanel().LastChildFill().Children(_addProjectButton.DockRight(), _newSessionButton),
			_searchButton,
			_nameBox,
			_banner);

		var git = AppIcons.Button("git-branch", Strings.Git, ShowGitPanel, size: AppIcons.ToolbarSize);
		var palette = AppIcons.Button("terminal", Strings.PaletteButton, () => _palette?.Toggle(this), size: AppIcons.ToolbarSize);
		var theme = AppIcons.Button("contrast", Strings.ToggleTheme, ToggleTheme, size: AppIcons.ToolbarSize);
		var settings = AppIcons.Button("settings", Strings.Settings, OpenSettingsDialog, size: AppIcons.ToolbarSize);

		// 底部工具行：图标 + 小字标签（命令面板/Git/设置/主题），参照 Codex 桌面端的安静页脚。
		static StackPanel FooterItem(string label, Button button)
		{
			// 按钮收窄一档（图标本身不变）：内容窄了，格间才有余量留给间距。
			button.Padding(new Thickness(6, 4, 6, 4));
			return new StackPanel()
				.Spacing(2)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.Children(button.HorizontalAlignment(HorizontalAlignment.Center),
					new TextBlock().Text(label).FontSize(11).SubtleText()
						.HorizontalAlignment(HorizontalAlignment.Center));
		}

		// 页脚入口：四个内容列按各自宽度排（文字不会被裁），剩余宽度由三个弹性列平分 ——
		// 侧栏越宽间距越大，越窄间距越小，但入口本身始终保持完整。侧栏折叠入口移到标题栏。
		var sidebarFooter = new Border()
			.BorderBrush(AppTheme.BorderSubtle).BorderThickness(new Thickness(0, 1, 0, 0))
			.Margin(new Thickness(0, AppTheme.Space2, 0, 0))
			.Padding(new Thickness(0, AppTheme.Space2, 0, 0))
			.Child(new Grid().Columns("auto,*,auto,*,auto,*,auto").Children(
				FooterItem(Strings.PaletteButton, palette).Column(0),
				FooterItem(Strings.SidebarGit, git).Column(2),
				FooterItem(Strings.ToggleTheme, theme).Column(4),
				FooterItem(Strings.Settings, settings).Column(6)));

		// 列表要吃掉剩余高度，所以这里用 DockPanel；StackPanel 会把 ListBox 压成内容高度。
		var sidebar = _sidebar = new Border()
			.StyleName(AppStyles.Sidebar)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space3, AppTheme.Space3, AppTheme.Space2))
			.Child(new DockPanel()
				.LastChildFill()
				.Children(
				sidebarFooter.DockBottom(),
				new DockPanel().LastChildFill()
					.Children(sidebarTop.DockTop(),
						new Border()
							.Background(AppTheme.Transparent)
							.Margin(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2))
							.Child(new Grid().Children(_sessionTree, _chatsTree.IsVisible(false), _chatsEmpty)))));

		// ------------------------------------------------------------ 主区
		_tabControl = new TabControl().BorderThickness(0).CornerRadius(0).Padding(Thickness.Zero)
			.WithTheme((theme, tabs) => tabs.Background = AppTheme.For(theme).WindowBackground);
		_tabControl.SelectionChanged += _ => UpdateStatus();

		// 准备阶段的会话面板：加载提示不是叠在上面的卡片，而是这个面板自己的内容之一，
		// 加载中用进度段落替掉欢迎文案，结束后再换回来。
		_welcomeTitle = new TextBlock()
			.Text(Strings.WelcomeTitle)
			.FontSize(20)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.HorizontalAlignment(HorizontalAlignment.Center);
		_welcomeBody = new TextBlock()
			.Text(Strings.WelcomeBody)
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.TextAlignment(TextAlignment.Center)
			.MaxWidth(420)
			.TextWrapping(TextWrapping.Wrap)
			.HorizontalAlignment(HorizontalAlignment.Center);
		_sessionLoading.Changed = SyncWelcomeLoading;
		_sessionLoading.View.IsVisible(false);
		_welcome = new Border()
			.StyleName(AppStyles.Window)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).WindowBackground)
			.Padding(new Thickness(AppTheme.Space6))
			.Child(new StackPanel()
				.Spacing(AppTheme.Space3)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(_welcomeTitle, _welcomeBody, _sessionLoading.View,
					_status.HorizontalAlignment(HorizontalAlignment.Center)));

		_palette = new CommandPalette(PaletteCommands);
		_palette.Closed += FocusActiveEntry;

		var main = new Grid();
		main.Children(_tabControl);

		// 辅助页（用量统计）左上角的返回工作区入口。
		_backButton = AppIcons.Button("arrow-left", Strings.BackToWorkspace, BackToWorkspace, size: AppIcons.ToolbarSize)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.VerticalAlignment(VerticalAlignment.Top)
			.Margin(new Thickness(AppTheme.Space2))
			.IsVisible(false);
		// TabControl 自带的页签条仍隐藏（标题栏右段另绘）；它会给选中页画一个小底座，用主区同色遮罩盖住。
		main.Children(new Border()
			.Height(30)
			.VerticalAlignment(VerticalAlignment.Top)
			.WithTheme((theme, element) => element.Background = AppTheme.For(theme).WindowBackground)
			.IsHitTestVisible(false));
		main.Children(_welcome);
		main.Children(_backButton);

		// ------------------------------------------------------------ 右侧功能面板
		// 面板属于会话：每个标签在 AttachTab 里挂一个 RightPanel 进这个宿主。
		// 主区与面板之间用默认分隔条（与左侧同一套 SplitPanel）：主区星号、面板定宽，
		// 窗口缩放后重新钉回用户宽度，拖拽时只在鼠标按下的帧里记录实际宽度。
		var preference = DesktopPreference.Load();
		_rightPanelWidth = preference.RightPanelWidth;
		_panelHost = new Grid { ClipToBounds = true }.IsVisible(false);


		// ------------------------------------------------------------ 窗框
		// PiDeck 式连续窗框：左段与侧栏同色；主区与右侧面板共用顶部一行窗框，
		// 面板列从窗口顶部一直铺到底，页签条与主标题栏同高（40），不再是标题栏下方的一行。
		// 窗口按钮先建好：标题栏与面板页签条都要按它的宽度让位（浮层在窗框模板里）。
		_windowActionsOverlay = BuildWindowActionsOverlay();

		var sidebarColumn = _sidebarColumn = new DockPanel().LastChildFill()
			.Children(BuildSidebarTitleBar().DockTop(), sidebar);
		var mainColumn = new DockPanel().LastChildFill()
			.Children(BuildMainTitleBar().DockTop(), main);

		_panelSplit = new SplitPanel().Horizontal().SplitterThickness(4)
			.MinFirst(AppTheme.MinChatWidth)
			.MinSecond(AppTheme.RightPanelMinWidth)
			.MaxSecond(AppTheme.RightPanelMaxWidth)
			.FirstLength(GridLength.Stars(1))
			.SecondLength(GridLength.Pixels(_rightPanelWidth))
			.First(mainColumn).Second(_panelHost);

		// 侧栏与主区之间可以拖拽调宽。
		_sidebarPreferred = !preference.SidebarCollapsed;
		_sidebarColumn.IsVisible(_sidebarPreferred);
		UpdateSidebarChrome();
		_workspace = new SplitPanel().Horizontal().SplitterThickness(4)
			.MinFirst(200).MinSecond(520)
			.FirstLength(GridLength.Pixels(preference.SidebarWidth))
			.SecondLength(GridLength.Stars(1)).First(sidebarColumn).Second(_panelSplit);
		// 窗框由 Window.Template 拥有（MewUI 文档的 custom chrome 模式）：Content 只放工作区，
		// 模板通过 ContentPresenter 投影它；重建窗框不会替换会话与输入控件。
		Content = _workspace;
		Template = new DelegateControlTemplate<MainWindow>((window, _) => window.BuildChromeShell());

		// 侧栏停在哪个分段与 PiDeck 一样记忆：对话用于日常聊天，项目用于工作区任务。
		ShowSidebarView(preference.SidebarProjects, persist: false);

	}

	/// <summary>把标题栏左段的项目铭牌换成指定文本，并同步完整路径提示。</summary>
	private void SetProjectCaption(string text, string? tip = null)
	{
		_tabCaption.Text(text);
		_tabCaption.HoverTip(tip);
	}

	// ------------------------------------------------------------------ 启动

	private async void Startup()
	{
		try
		{
			// 默认策略为 ask 且存在受保护项目资源时，先弹卡片问是否信任（选择会记住）。
			if (ProjectTrust.HasPendingDecision(_cwd, trustOverride: _arguments.ProjectTrustOverride))
			{
				var host = new DesktopUiHost();
				host.Attach(this);
				var trusted = await ProjectTrust.PromptAsync(_cwd, host).ConfigureAwait(true);
				_arguments = _arguments with { ProjectTrustOverride = trusted };
			}

			StartupCore();
		}
		catch (Exception ex)
		{
			_banner.Text(Strings.StartupFailed(ex.ToString()));
			_status.Text(ex.Message);
			AppLog.Failure(AppLog.For<MainWindow>(), "Window startup", ex);
			Console.Error.WriteLine($"piweft-desktop: {Strings.StartupFailed(ex.ToString())}");
		}
	}

	private void StartupCore()
	{
		this.Title(Strings.WindowTitle(_cwd));

		// 主题目录热重载：受信项目才监视项目主题；回调切回 UI 线程后再换肤。
		StartThemeWatcher();

		// 尺寸在创建原生窗口前配置；这里恢复已加载窗口的位置。
		var preference = DesktopPreference.Load();

		if (preference.WindowLeft is { } left && preference.WindowTop is { } top)
		{
			try
			{
				MoveTo(left, top);
			}
			catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
			{
				// 位置恢复失败不影响使用。
			}
		}

		// 关闭时记住窗口状态，下次打开原位恢复。
		this.OnClosed(() =>
		{
			// 窗口都没了，后台读取与投影不必再跑完。
			_sessionHostClosed = true;
			_sessionAbort.Cancel();
			// 落盘并释放诊断文件句柄；开关状态仍留在偏好里，下次启动继续采。
			Diagnostics.Close();
			_usageStatsView?.Close();
			_themeWatcher?.Dispose();
			// 每个会话面板里的伪终端都要显式收掉，否则窗口关闭后 shell 子进程可能继续挂着。
			foreach (var panel in _sessionPanels.Values) panel.Dispose();
			foreach (var tab in _tabEntries.OfType<SessionTab>()) tab.Dispose();
			CapturePanelWidth();
			SaveWindowState();
		});
		this.OnClosing(args =>
		{
			if (_settingsView is not { IsDirty: true } settings) return;
			var deferral = args.GetDeferral();
			_ = ConfirmSettingsCloseAsync(settings, args, deferral);
		});

		// 启动最贵的三段（环境构建、模型清单、侧栏摘要扫描）都是纯 IO/解析：
		// 放后台跑完再回到 UI 线程建会话，窗口能立刻可交互，也不再阻塞首帧。
		_ = BootstrapAsync(preference);
	}

	/// <summary>
	/// 启动后半程：后台准备环境与侧栏数据，回 UI 线程挂首个会话与演练入口。
	/// </summary>
	private async Task BootstrapAsync(DesktopPreference preference)
	{
		CliEnvironment? environment = null;
		string? startupError = null;
		var watch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;

		// 三条互不依赖的后台任务：环境、侧栏摘要扫描、首个会话的解析。
		// 关键是它们不能串成一条链 —— 侧栏在会话多的机器上要扫上百 MB（实测 78 份 / 304 MB / 约 900 ms），
		// 而开会话只需要环境；以前把三者打包进一个 Task.Run，等于让首个会话陪着扫完整个目录。
		var prefetchPath = preference.Pinned.Count == 0 ? _catalog.MostRecentPath() : null;
		var prefetch = prefetchPath is null ? null : Task.Run(() => _catalog.Open(prefetchPath, _cwd));
		try
		{
			var (created, error) = await Task.Run(() => CliEnvironment.Create(_arguments, _cwd)).ConfigureAwait(true);
			environment = created;
			startupError = error;

			if (prefetch is not null && prefetchPath is not null)
			{
				try
				{
					_prefetchedOpen = (prefetchPath, await prefetch.ConfigureAwait(true));
				}
				catch (Exception ex)
				{
					// 预解析失败不影响启动：后面按正常路径再开一次。
					AppLog.Failure(AppLog.For<MainWindow>(), "Prefetch newest session", ex);
				}
			}
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Prepare desktop environment", ex);
			startupError = ex.Message;
		}

		if (watch is not null)
		{
			Diagnostics.Record("environment.create", watch, $"tools={environment?.Tools.Count ?? 0}");
		}

		if (environment is null)
		{
			_banner.Text(Strings.StartupFailed(startupError ?? Strings.NoSession));
			_status.Text(startupError ?? Strings.NoSession);
			return;
		}

		try
		{
			preference.RememberProject(_cwd).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Save recent project", ex);
		}

		_environment = environment;
		_environments[Path.GetFullPath(_cwd)] = environment;
		_models = environment.ListAvailableModels(_arguments.ApiKey);
		AppLog.For<MainWindow>().ZLogInformation($"Desktop ready: models={_models.Count}, tools={environment.Tools.Count}");

		// 侧栏收起时完全不扫摘要：首个会话用 stat 出来的路径直接开，列表等展开或搜索时再加载。
		// 侧栏展开（或存在置顶会话，需要按置顶顺序选首个）时才加载。
		if (preference.Pinned.Count > 0)
		{
			await EnsureSessionsLoadedAsync().ConfigureAwait(true);
		}

		var opening = _prefetchedOpen is not null;
		if (opening)
		{
			OpenSessionPath(_prefetchedOpen!.Value.Path);
		}

		if (!preference.SidebarCollapsed)
		{
			await EnsureSessionsLoadedAsync().ConfigureAwait(true);
		}

		if (opening && _sessionsLoaded)
		{
			// 侧栏行在会话之后才到：用它把选中项补上高亮。
			RestoreSelection(CurrentTab()?.Session.Session.SessionId ?? string.Empty);
		}

		// MCP 服务器在后台连接：连上后把工具注入所有已打开的会话，设置面板同步显示状态。
		_ = ConnectMcpAsync();

		if (!opening && _sessions.Count > 0)
		{
			OpenSessionPath(_sessions[0].Path);
		}
		else if (!opening && preference.SidebarProjects)
		{
			NewSessionIn(_cwd);
		}
		else if (!opening)
		{
			// 首次启动停在「对话」：在聊天目录里开一个，而不是往当前项目里塞一个空会话。
			NewChatSession();
		}

		if (SmokeTab is { Length: > 0 } smokeTab)
		{
			_ = RunSmokeTabAsync(smokeTab);
		}

		if (ThemeSmoke)
		{
			_ = RunThemeSmokeAsync();
		}

		if (UiSmoke is { Length: > 0 } view)
		{
			_ = RunUiSmokeAsync(view);
		}
	}

	// ------------------------------------------------------------- 会话列表

	/// <summary>
	/// 外部工具（xdotool 等）往这个后端注入键盘/指针事件并不可靠，
	/// 因此把「打开某个面板」做成启动参数，便于无人值守截图检查。
	/// </summary>
	private async Task RunSmokeTabAsync(string smokeTab)
	{
		// 面板要挂在自动打开的会话上，先等它在后台建好。
		await WaitForSidebarAsync().ConfigureAwait(false);
		await WaitForSessionOpensAsync().ConfigureAwait(false);
		Dispatch(() =>
		{
			if (string.Equals(smokeTab, "git", StringComparison.OrdinalIgnoreCase))
			{
				ShowGitPanel();
			}
			else
			{
				OpenSettingsDialog();
			}
		});
	}

	/// <summary>重建侧栏列表。目录快照始终包含全部会话，侧栏再按聊天目录分成「对话」与「项目」两段。</summary>
	/// <param name="force">
	/// 明确要求刷新（改名、删除、归档、手动刷新）时即使侧栏还没加载过也要加载；
	/// 流式结束这类后台刷新则不主动付这笔扫描成本。
	/// </param>
	private void RefreshSessions(bool force = false)
	{
		if (!_sessionsLoaded)
		{
			if (force) _ = EnsureSessionsLoadedAsync();
			return;
		}

		var watch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		// 搜索弹窗需要全部会话；侧栏的两个分段在 UpdateSessionList 里分流。
		_allSessions = _catalog.List();

		// 置顶的会话排在最前，其余保持最近修改优先。
		var preference = DesktopPreference.Load();
		_allSessions =
		[
			.. _allSessions.Where(item => preference.IsPinned(item.Id)),
			.. _allSessions.Where(item => !preference.IsPinned(item.Id)),
		];

		FilterSessions(preference);
		if (watch is not null)
		{
			Diagnostics.Record("sidebar.refresh", watch, $"sessions={_allSessions.Count}");
		}
	}

	/// <summary>侧栏列表与目录快照同步；正文搜索移到了「搜索会话」弹窗里。</summary>
	private void FilterSessions(DesktopPreference? preference = null)
	{
		_sessions = _allSessions;
		UpdateSessionList(preference ?? DesktopPreference.Load());
	}

	/// <summary>重建侧栏列表（内容命中分组已随搜索弹窗移走）。</summary>
	private void UpdateSessionList(DesktopPreference preference)
	{
		var selectedId = SelectedSession()?.Id ?? CurrentTab()?.Session.Session.SessionId;
		var rows = new List<SessionListItem>(_sessions);

		// 「对话」= 聊天目录里的会话。与 PiDeck 的内置 Chat 项目一致：它不是全部会话的平铺，
		// 否则同一批会话会在两个分段里重复出现，项目视图失去区分度。
		var chats = SessionGroups.Chats(rows, _chatWorkspace);

		// 按工作目录分组：项目是父节点，会话是子节点（树默认展开），与 PiDeck 的项目视图一致。
		// 聊天目录里的会话只属于「对话」，不再作为项目出现在这里。
		var nodes = SessionGroups.Projects(rows, _chatWorkspace)
			.DistinctBy(static item => item.Path)
			.GroupBy(static item => item.Cwd)
			.OrderByDescending(static group => group.Max(static item => item.Modified))
			.Select(group => new TreeViewNode(
				ProjectLabel(group.Key, group.Count()),
				[.. group
					.OrderByDescending(item => preference.IsPinned(item.Id))
					.ThenByDescending(static item => item.Modified)
					.Select(item => new TreeViewNode(SessionLine(item, preference), tag: item))],
				tag: group.Key))
			.ToList();

		_pinnedSessionIds = preference.Pinned.ToHashSet(StringComparer.Ordinal);
		_sessionNodes = nodes;
		_sessionTree.ItemsSource(nodes);

		// 默认折叠：只展开「上次展开过」的项目，以及当前选中会话所在的项目
		// （否则高亮项被折在看不见的地方，用户会以为没选中）。
		var expandedProjects = preference.ExpandedProjects;
		foreach (var node in nodes)
		{
			var expand = ShouldExpandProject(
				node.Tag as string,
				ContainsSession(node, CurrentTab()?.Session.Session.SessionFile),
				expandedProjects);
			if (expand)
			{
				_sessionTree.Expand(node);
			}
			else
			{
				_sessionTree.Collapse(node);
			}
		}

		// 「对话」分段：聊天目录里的会话按置顶、最近修改平铺，不分组。
		_chatNodes =
		[
			.. chats
				.DistinctBy(static item => item.Path)
				.OrderByDescending(item => preference.IsPinned(item.Id))
				.ThenByDescending(static item => item.Modified)
				.Select(item => new TreeViewNode(SessionLine(item, preference), tag: item)),
		];
		_chatsTree.ItemsSource(_chatNodes);
		_chatsEmpty.IsVisible(!_projectsTab && _chatNodes.Count == 0);

		if (selectedId is not null)
		{
			RestoreSelection(selectedId);
		}

		if (_allSessions.Count == 0)
		{
			_banner.Text(Strings.NoSessions);
		}
		else
		{
			_banner.Text(string.Empty);
		}
	}

	/// <summary>项目节点的标题：目录短名 + 会话数（完整路径放在提示里）。</summary>
	private static string ProjectLabel(string cwd, int count) =>
		count > 1 ? $"{ShortCwd(cwd)} ({count})" : ShortCwd(cwd);

	/// <summary>会话节点的标题：置顶标记、标题、时间。</summary>
	private string SessionLine(SessionListItem item, DesktopPreference preference)
	{
		var pin = preference.IsPinned(item.Id) ? $"{Strings.PinnedMark} " : string.Empty;

		return $"{pin}{item.Display} · {TimeAgo(item.Modified)}";
	}

	/// <summary>两个分段共用同一个处理器：只对会话行打开会话，项目分组行忽略。</summary>
	private void OnSessionSelected(object? item)
	{
		if (_restoringSelection)
		{
			return;
		}

		// 事件带的是刚选中的节点；不能去读另一个（可能隐藏的）树的选中项，
		// 否则在「对话」里点一行会被忽略。
		if (item is not TreeViewNode { Tag: SessionListItem selected })
		{
			return;
		}

		_nameBox.Text = selected.Name;

		// 打开失败由会话面板上的加载卡片就地显示，这里不抢话。
		OpenSessionPath(selected.Path);
	}

	/// <summary>「搜索会话」弹窗里选中的会话：按路径打开，已打开则切到那个标签。</summary>
	private void OpenSessionFromSearch(SessionListItem item)
	{
		_nameBox.Text = item.Name;

		OpenSessionPath(item.Path);
	}

	/// <summary>侧栏当前选中的会话：取可见分段的选中行，避免命中另一棵树的旧选择。</summary>
	private SessionListItem? SelectedSession()
	{
		var tree = _projectsTab ? _sessionTree : _chatsTree;
		if (tree.SelectedNode?.Tag is not SessionListItem selected)
		{
			return null;
		}

		// 列表重建或分段切换后，旧节点可能已不属于当前分段：只接受属于它的会话。
		return SessionGroups.IsChat(selected, _chatWorkspace) == !_projectsTab ? selected : null;
	}

	/// <summary>把输入框里的名称写给选中的会话，并刷新列表与标签标题。</summary>
	private void RenameSelected() => _ = RenameSelectedAsync();

	/// <summary>
	/// 改名要读完整份会话才能追加一条 <c>session_info</c>，大文件不能在 UI 线程读；
	/// 只有确实需要等一会儿（>2 MB）才弹加载提示，小会话直接后台跑完避免闪烁。
	/// </summary>
	private async Task RenameSelectedAsync()
	{
		var path = _renamingPath ?? SelectedSession()?.Path;
		if (path is null)
		{
			_banner.Text(Strings.SelectSessionFirst);
			return;
		}

		var name = _nameBox.Text?.Trim();
		// 小会话瞬间就完成，不必闪一下卡片；出错时才把错误显示到会话面板上。
		var card = File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024;
		var shown = false;
		try
		{
			if (card) _sessionLoading.Begin(Strings.RenamingSession);
			await Task.Run(() => _catalog.Rename(path, name)).ConfigureAwait(true);
			_renamingPath = null;
			RefreshSessions(force: true);

			foreach (var tab in _tabEntries.OfType<SessionTab>().Where(tab => tab.Session.Session.SessionFile == path))
			{
				tab.RefreshTitle();
			}

			_nameBox.IsVisible(false);
			UpdateTabHeaders();
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Rename session", ex);
			_sessionLoading.Fail(Strings.RenameSessionFailedTitle, ex.Message);
			shown = true;
		}
		finally
		{
			if (card && !shown) _sessionLoading.Complete();
		}
	}

	/// <summary>删除会话前先确认；文件移到 <c>.trash</c> 而不是直接抹掉。</summary>
	private async Task DeleteSelectedAsync()
	{
		var selected = SelectedSession();
		if (selected is null)
		{
			_banner.Text(Strings.SelectSessionFirst);
			return;
		}

		if (!await MessageBox.ConfirmAsync(Strings.DeleteConfirm(selected.Display)))
		{
			return;
		}

		try
		{
			// 先关掉对应标签，避免继续往已移走的文件追加。
			var open = _tabEntries.OfType<SessionTab>().Where(tab => tab.Session.Session.SessionFile == selected.Path).ToList();
			foreach (var tab in open)
			{
				CloseTab(tab);
			}

			var moved = _catalog.Delete(selected.Path);
			RefreshSessions(force: true);
			_banner.Text(Strings.Deleted(moved));
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Archive session", ex);
			_banner.Text(Strings.OpenSessionFailed(ex.Message));
		}
	}

	/// <summary>关闭标签并释放它持有的会话。</summary>
	private void CloseTab(SessionTab tab) => _ = CloseTabAsync(tab);

	/// <summary>流式运行中先中止并等这轮结果落盘，再移除标签与会话。</summary>
	private async Task CloseTabAsync(SessionTab tab)
	{
		var index = _tabEntries.IndexOf(tab);
		if (index < 0)
		{
			return;
		}

		if (ReferenceEquals(_lastSessionTab, tab))
		{
			_lastSessionTab = _tabEntries.OfType<SessionTab>().FirstOrDefault(other => !ReferenceEquals(other, tab));
		}

		_tabEntries.RemoveAt(index);

		if (index < _tabControl.Tabs.Count)
		{
			_tabControl.RemoveTabAt(index);
		}

		// 关掉这个会话自己的面板：从宿主摘下并释放它可能启动的伪终端。
		if (_sessionPanels.Remove(tab, out var panel))
		{
			panel.View.IsVisible(false);
			_panelHost.Remove(panel.View);
			panel.Dispose();
		}

		await tab.CloseAsync();
		UpdateStatus();
	}

	// --------------------------------------------------------------- 标签页

	/// <summary>先选中已打开的标签，避免重复反序列化整个 JSONL 文件。</summary>
	private void OpenSessionPath(string path) => _ = OpenSessionPathAsync(path);

	/// <summary>
	/// 仅供 --ui-smoke scroll-sweep：打开 fixture 目录里最新的会话，然后让会话标签分步滚动，
	/// 供外部脚本逐帧截图排查「长消息部分情况下渲染消失」。
	/// </summary>
	private async Task RunScrollSweepSmokeAsync()
	{
		var dir = SessionPaths.GetDefaultSessionDir(_cwd);
		var file = Directory.Exists(dir)
			? new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(static item => item.Name, StringComparer.Ordinal).FirstOrDefault()
			: null;
		if (file is null)
		{
			Console.WriteLine("sweep-missing-session");
			return;
		}
		await OpenSessionPathAsync(file.FullName);
		if (CurrentTab() is { } tab)
		{
			tab.PrepareScrollSweepSmoke();
		}
	}

	/// <summary>
	/// 打开会话。长会话（几十 MB、数万行）解析要近一秒，聊天记录投影再要 200+ 毫秒，
	/// 这些都放到后台线程，期间用 BusyIndicator 显示读取进度；UI 线程只建标签页。
	/// </summary>
	private async Task OpenSessionPathAsync(string path)
	{
		var resolved = Path.GetFullPath(path);
		if (SelectOpenTab(resolved) is not null)
		{
			return;
		}

		// 解析期间重复点击同一份文件：忽略，不排队再读一遍。
		if (!_openingSessions.Add(resolved))
		{
			return;
		}

		Interlocked.Increment(ref _pendingSessionOpens);

		var name = Path.GetFileNameWithoutExtension(resolved);
		// 加载提示就在会话面板中央，不带中止按钮；只有窗口关闭才中断后台读取。
		var token = _sessionAbort.Token;
		_sessionLoading.Begin(Strings.OpeningSession(name));
		// 中途取消或报错时，已经建好的会话要释放（它持有会话文件的写入句柄）。
		(CodingAgentSession Session, ChatViewModel Chat, SessionStatisticsDisplay Statistics)? prepared = null;
		var shown = false;
		try
		{
			var progress = new UiProgress<SessionReadProgress>(value =>
			{
				// 长度未知时用不定进度，但行数仍然汇报，别让卡片看起来卡死。
				_sessionLoading.Update(
					value.TotalBytes <= 0
						? Strings.OpeningSessionLines(value.Lines)
						: Strings.OpeningSessionProgress(name, (int)Math.Round(value.Fraction * 100), value.Lines),
					value.TotalBytes <= 0 ? null : value.Fraction);
			});

			// 外部会话的头部可能没有 cwd；桌面用当前项目兜底，而不是守护进程的启动目录。
			var lines = 0;
			var counting = new UiProgress<SessionReadProgress>(value => Volatile.Write(ref lines, value.Lines));
			var read = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
			// 启动预解析命中同一个文件时直接接手，不再重读几十 MB。
			SessionManager? manager = null;
			if (_prefetchedOpen is { } prefetched && string.Equals(prefetched.Path, resolved, StringComparison.Ordinal))
			{
				_prefetchedOpen = null;
				manager = prefetched.Manager;
				if (read is not null)
				{
					Diagnostics.Record("session.open.read", 0, "prefetched=true");
				}
			}

			manager ??= await Task.Run(
				() =>
				{
					var opened = _catalog.Open(resolved, _cwd, Composite(progress, counting), token);
					if (read is not null)
					{
						Diagnostics.Record("session.open.read", read,
							$"bytes={new FileInfo(resolved).Length}, lines={Volatile.Read(ref lines)}, entries={opened.Entries.Count}");
					}

					return opened;
				}, token).ConfigureAwait(true);

			// 环境解析会写横幅，留在 UI 线程；建会话与投影聊天记录放后台。
			var environment = EnvironmentFor(manager.Cwd);
			var extraTools = McpToolsFor(manager.Cwd);
			// 问询卡片需要标签的视图来定位窗口，因此先建宿主、构造会话，再把视图挂回去。
			var uiHost = new DesktopUiHost();
			_sessionLoading.Update(Strings.PreparingSession, null);
			var prepare = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
			prepared = await Task.Run(
				() =>
				{
					var session = environment.CreateSession(
						_arguments, manager.Cwd, session: manager, extraTools: extraTools,
						recordInitialThinkingLevel: false, uiHost: uiHost);
					// 聊天记录投影是纯模型工作（无 UI 依赖），提前在后台做完，标签页直接接手。
					var chat = new ChatViewModel();
					chat.Attach(session);
					var rows = chat.Entries().Count;
					// 状态条那一段（真实用量、上下文估算、轮数）以前在 UI 线程首帧算，长会话要 150+ 毫秒；
					// 现在连着投影一起在后台算好，标签页接手就是现成的。
					var statistics = SessionStatisticsDisplay.Create(session);
					if (prepare is not null)
					{
						Diagnostics.Record("session.open.prepare", prepare,
							$"entries={rows}, messages={session.Messages.Count}");
					}

					return (session, chat, statistics);
				},
				token).ConfigureAwait(true);

			OpenPreparedSession(prepared.Value.Session, uiHost, prepared.Value.Chat, prepared.Value.Statistics);
			prepared = null;
		}
		catch (OperationCanceledException)
		{
			// 关窗导致的中断：没有界面可读，只记日志。
			AppLog.For<MainWindow>().ZLogInformation($"Session open cancelled: {resolved}");
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Open session", ex);
			// 错误就地显示在准备阶段的会话面板上，不再挤到横幅里。
			_sessionLoading.Fail(Strings.OpenSessionFailedTitle, ex.Message);
			shown = true;
		}
		finally
		{
			if (prepared is { } leftover)
			{
				leftover.Chat.Dispose();
				leftover.Session.Dispose();
			}

			// shown 时 Fail 已经落账，这里不再减一次。
			if (!shown) _sessionLoading.Complete();
			_openingSessions.Remove(resolved);
			Interlocked.Decrement(ref _pendingSessionOpens);
		}
	}

	/// <summary>
	/// 需要侧栏数据时才加载（展开侧栏、打开搜索、或需要按置顶顺序选首个会话）。
	/// 重复调用共用同一个在途任务，展开-收起-再展开不会重复扫描。
	/// </summary>
	private Task EnsureSessionsLoadedAsync()
	{
		if (_sessionsLoaded) return Task.CompletedTask;
		if (_sessionsLoad is { } running) return running;
		var task = LoadSessionsAsync(DesktopPreference.Load());
		_sessionsLoad = task;
		return task;
	}

	/// <summary>扫描并挂上侧栏摘要；失败只记日志并把提示改成「暂无会话」，不影响已打开的会话。</summary>
	private async Task LoadSessionsAsync(DesktopPreference preference)
	{
		// 扫描期间先说明在干什么，避免展开侧栏看到空列表以为没有会话。
		if (!_projectsTab) _chatsEmpty.Text(Strings.SessionsScanning).IsVisible(true);
		IReadOnlyList<SessionListItem> listed;
		try
		{
			listed = await Task.Run(() => _catalog.List()).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "List sessions", ex);
			_sessionsLoad = null;
			if (!_projectsTab) _chatsEmpty.Text(Strings.ChatsEmpty);
			return;
		}

		_sessionsLoaded = true;
		_sessionsLoad = null;
		if (!_projectsTab) _chatsEmpty.Text(Strings.ChatsEmpty);

		_allSessions = listed;
		// 置顶的会话排在最前，其余保持最近修改优先。
		_allSessions =
		[
			.. _allSessions.Where(item => preference.IsPinned(item.Id)),
			.. _allSessions.Where(item => !preference.IsPinned(item.Id)),
		];
		FilterSessions(preference);

		// 搜索可能先于列表打开：拿到数据后按当前关键字重跑一次。
		_sessionSearch.Reload();
	}

	/// <summary>等待侧栏扫描落地（自检与演练用；正常交互不需要等）。</summary>
	private async Task WaitForSidebarAsync(int timeoutMs = 30_000)
	{
		await Task.WhenAny(EnsureSessionsLoadedAsync(), Task.Delay(timeoutMs)).ConfigureAwait(false);
	}

	/// <summary>
	/// 等待在途的后台打开结束。会话打开改成异步后，启动时「立刻就有标签页」不再成立；
	/// 自检与演练流程必须显式等待，否则在满载机器上会读到空标签页。
	/// </summary>
	private async Task WaitForSessionOpensAsync(int timeoutMs = 30_000)
	{
		var waited = 0;
		while (Volatile.Read(ref _pendingSessionOpens) > 0 && waited < timeoutMs)
		{
			await Task.Delay(25).ConfigureAwait(false);
			waited += 25;
		}
	}

	/// <summary>
	/// 把多个进度消费者合成一个：<c>Diagnostics</c> 需要行数，界面需要百分比，
	/// 但 <see cref="SessionManager"/> 只接受一个 <see cref="IProgress{T}"/>。
	/// </summary>
	private static IProgress<SessionReadProgress> Composite(
		IProgress<SessionReadProgress> first, IProgress<SessionReadProgress> second) =>
		new Combiner(first, second);

	private sealed class Combiner(
		IProgress<SessionReadProgress> first, IProgress<SessionReadProgress> second) : IProgress<SessionReadProgress>
	{
		public void Report(SessionReadProgress value)
		{
			first.Report(value);
			second.Report(value);
		}
	}

	/// <summary>命中已打开的标签就选中它并返回，避免重复反序列化整个 JSONL 文件。</summary>
	private SessionTab? SelectOpenTab(string path)
	{
		var existing = _tabEntries.OfType<SessionTab>().FirstOrDefault(tab => tab.Session.Session.SessionFile == path);
		if (existing is null)
		{
			return null;
		}

		_tabControl.SelectedIndex = _tabEntries.IndexOf(existing);
		existing.FocusComposer();
		return existing;
	}

	/// <summary>打开一个会话：已打开则选中，否则新建标签页。</summary>
	private void OpenSession(SessionManager manager)
	{
		if (_environment is null)
		{
			return;
		}

		// 同一个会话文件只开一个标签。
		var path = manager.SessionFile;
		if (path is not null && SelectOpenTab(path) is not null)
		{
			return;
		}

		// 会话可能属于别的项目：为该工作目录准备（并缓存）一套环境，工具与会话一样以它的 cwd 为基准。
		var environment = EnvironmentFor(manager.Cwd);
		// 问询卡片需要标签的视图来定位窗口，因此先建宿主、构造会话，再把视图挂回去。
		var uiHost = new DesktopUiHost();
		OpenPreparedSession(
			environment.CreateSession(_arguments, manager.Cwd, session: manager, extraTools: McpToolsFor(manager.Cwd),
				recordInitialThinkingLevel: false, uiHost: uiHost),
			uiHost);
	}

	/// <summary>把已经建好的会话挂成标签页；<paramref name="session"/> 可以在后台线程创建。</summary>
	private void OpenPreparedSession(
		CodingAgentSession session, DesktopUiHost uiHost, ChatViewModel? chat = null,
		SessionStatisticsDisplay? statistics = null)
	{
		var tabWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		// 取消令牌可能在投影跑完后才落地：窗口已经关了，直接释放，不建标签页。
		if (_sessionHostClosed)
		{
			chat?.Dispose();
			session.Dispose();
			return;
		}

		var tab = AttachTab(session, chat, statistics);
		uiHost.Attach(tab);
		tab.UiHost = uiHost;
		if (tabWatch is not null)
		{
			Diagnostics.Record("session.open.tab", tabWatch, $"messages={session.Messages.Count}");
		}

		AppLog.For<MainWindow>().ZLogInformation($"Session opened: session={session.Session.SessionId}");
		_tabControl.SelectedIndex = _tabEntries.Count - 1;
		UpdateTabHeaders();
		UpdateStatus();
		tab.FocusComposer();
	}

	/// <summary>为已有会话创建一个标签页；<paramref name="chat"/> 是后台已经投影好的聊天记录。</summary>
	private SessionTab AttachTab(
		CodingAgentSession session, ChatViewModel? chat = null, SessionStatisticsDisplay? statistics = null)
	{
		// 选择器与会话沿用同一启动环境，避免热重载后列出当前会话尚不能调用的新端点。
		var environment = EnvironmentFor(session.Cwd);
		var tab = new SessionTab(session, environment.ListAvailableModels(_arguments.ApiKey), SessionTab.TitleFor(session),
			() => environment.ListAvailableModels(_arguments.ApiKey), chat, statistics);
		tab.TitleChanged += _ => UpdateTabHeaders();
		tab.StateChanged += _ =>
		{
			UpdateTabHeaders();
			if (!tab.IsStreaming)
			{
				RefreshSessions();
			}
		};
		tab.Forked += _ => RefreshSessions(force: true);

		_tabEntries.Add(tab);
		AddTab(header: null!, content: tab.View, entry: tab);

		// 这个会话自己的右侧面板：文件 / Git / 终端都以它的工作目录为基准，默认收起。
		// 面板视图挂在共享宿主里，切换会话只切可见性，终端等状态不会丢。
		var panel = new RightPanel(() => session.Cwd, CreateGitAssistant(session, environment), OpenGitAssistantSettings);
		panel.SetHeaderRightInset(_panelHeaderRightInset);
		_sessionPanels[tab] = panel;
		_panelHost.Children(panel.View);
		panel.View.IsVisible(false);

		return tab;
	}

	/// <summary>
	/// 登记标签页。TabControl 自带的页签头不渲染：标题栏右段另绘 PiDeck 式页签，
	/// 切换/关闭都在那里完成；命令面板里也保留同一组「关闭当前/其他/全部标签」动作。
	/// </summary>
	private void AddTab(UIElement header, UIElement content, object entry)
	{
		// TabItem 强制要求 Header；给一个 0×0 空元素，隐藏 TabControl 自带的页签条。
		_tabControl.AddTab(new TabItem().Header(new Border().Width(0).Height(0)).Content(content));
	}

	/// <summary>会话标题变化或标签集合变化后，按标题/选中项同步标题栏页签。</summary>
	private void UpdateTabHeaders()
	{
		SyncTitleBarTabs();
	}

	/// <summary>关闭 Git/文件/终端这类辅助标签。</summary>
	private void CloseAuxTab(object entry)
	{
		CloseAuxTabCore(entry);
	}

	private static async Task ConfirmSettingsCloseAsync(SettingsView settings, ClosingEventArgs args, ClosingDeferral deferral)
	{
		using (deferral)
		{
			try { args.Cancel = !await settings.CanCloseAsync(); }
			catch (InvalidOperationException) { args.Cancel = true; }
		}
	}

	private void CloseAuxTabCore(object entry)
	{
		var index = _tabEntries.IndexOf(entry);
		if (index < 0)
		{
			return;
		}

		_tabEntries.RemoveAt(index);

		if (index < _tabControl.Tabs.Count)
		{
			_tabControl.RemoveTabAt(index);
		}

		if (ReferenceEquals(entry, _usageStatsView))
		{
			_usageStatsView?.Close();
			_usageStatsView = null;
		}

		UpdateStatus();
	}

	/// <summary>加载段落与欢迎文案互斥；状态变化后重算面板可见性。</summary>
	private void SyncWelcomeLoading()
	{
		var showing = _sessionLoading.Showing;
		_welcomeTitle.IsVisible(!showing);
		_welcomeBody.IsVisible(!showing);
		_sessionLoading.View.IsVisible(showing);
		UpdateStatus();
	}

	private void UpdateStatus()
	{
		var index = _tabControl.SelectedIndex;
		var entry = index >= 0 && index < _tabEntries.Count ? _tabEntries[index] : null;
		if (_lastSessionTab is { } previous && !ReferenceEquals(entry, previous)) previous.HideTransientPanels();

		// 加载中（或停在错误态）时准备阶段面板必须可见，加载段落才有地方显示；
		// 同时旧会话视图整体让位——这段时间屏幕上只该有加载提示，不该透出上一个会话。
		var loading = _sessionLoading.Showing;
		_welcome.IsVisible(entry is null || loading);
		_tabControl.IsVisible(entry is not null && !loading);
		// 加载提示是这个阶段主区里唯一的内容，欢迎页的状态行也一起让位。
		_status.IsVisible(!loading);
		ApplySidebarLayout();
		SyncPanelLayout();
		SyncTitleBarTabs();

		switch (entry)
		{
			case null:
				_status.Text(Strings.NoOpenSession);
				UpdateProjectCaption();
				return;

			case UsageStatsView:
				return;

			case SessionTab active:
				_lastSessionTab = active;
				RestoreSelection(active.Session.Session.SessionId);
				UpdateTabHeaders();
				UpdateProjectCaption();
				return;
		}
	}

	/// <summary>
	/// 无人值守的界面演练：启动后打开指定视图并停留若干秒（外部脚本趁机截图），随后退出。
	/// 键盘/鼠标注入在 Wayland 会话里不可靠，这种方式能稳定覆盖各个视图。
	/// </summary>
	private async Task RunUiSmokeAsync(string view)
	{
		// 首个会话在后台解析：先等它建好标签页再演练。
		// 侧栏摘要按需加载，演练不主动等它（需要它的演练目标自己展开侧栏）。
		await WaitForSessionOpensAsync().ConfigureAwait(false);
		if (view == "loading")
		{
			// 这个演练要「在已有标签页之上再开一个会话」，得先有会话列表才能挑目标。
			await EnsureSessionsLoadedAsync().ConfigureAwait(false);
		}

		await Task.Delay(1500).ConfigureAwait(false);

		var loadingTarget = _sessions.FirstOrDefault(item => item.Path != CurrentTab()?.Session.Session.SessionFile);
		Dispatch(() =>
		{
			switch (view)
			{
				case "hints":
					PrepareHoverSmoke();
					break;
				case "loading":
					// 在已有标签页之上再打开一个会话：后台解析期间主区必须只剩加载提示，
					// 不能把上一个会话透出来（历史缺陷：加载段落直接浮在旧会话正文上）。
					if (loadingTarget is { } other)
					{
						_ = OpenSessionPathAsync(other.Path);
						Console.WriteLine(
							$"piweft-desktop: loading in-flight tabVisible={_tabControl.IsVisible} " +
							$"welcomeVisible={_welcome.IsVisible} prompt={_sessionLoading.Showing} " +
							$"panels={_panelHost.IsVisible}");
					}
					else
					{
						Console.Error.WriteLine("piweft-desktop: loading smoke 没有第二个会话");
						Environment.ExitCode = 1;
					}

					break;
				case "references":
					// 视觉验收入口：同时展示草稿引用条、历史文件 chip 和技能 chip。
					CurrentTab()?.SetExtensionText("请检查 @\"src/App.cs\" 和 @docs/。");
					Console.WriteLine("reference-ready");
					break;
				case "process":
					CurrentTab()?.PrepareProcessSmoke();
					break;
				case "wheel":
					// 滚动性能采样演练：展开执行过程后持续滚轮压测，供外部剖析对照。
					CurrentTab()?.PrepareWheelSmoke();
					break;
				case "collapse":
					CurrentTab()?.PrepareCollapseSmoke();
					break;
				case "theme-churn":
					// 连续快速切换主题：复现代码块丢文本渲染。
					var churnThemeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150));
					var churnThemeTick = 0;
					churnThemeTimer.Tick += () =>
					{
						churnThemeTick++;
						var light = churnThemeTick % 2 == 0;
						(DesktopPreference.Load() with
						{
							Theme = light ? DesktopPreference.LightTheme : DesktopPreference.DarkTheme,
						}).Save();
						Application.Current.SetTheme(light ? ThemeVariant.Light : ThemeVariant.Dark);
						if (churnThemeTick >= 8)
						{
							churnThemeTimer.Stop();
							Console.WriteLine("theme-churn-done");
						}
					};
					churnThemeTimer.Start();
					break;
				case "toast-demo":
					var demoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1600));
					demoTimer.Tick += () =>
					{
						demoTimer.Stop();
						this.ShowToast("演示 toast 通知。");
						Console.WriteLine("toast-shown");
					};
					demoTimer.Start();
					break;
				case "theme-notice":
					// 延后一点再切：外部脚本要在 toast 停留期内截图。
					var noticeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1600));
					noticeTimer.Tick += () =>
					{
						noticeTimer.Stop();
						ToggleTheme();
						Console.WriteLine($"theme-toggled: toasts={Toasts.Recent.Count}");
					};
					noticeTimer.Start();
					break;
				case "latest-theme":
					CurrentTab()?.PrepareLatestThemeSmoke();
					break;
				case "latest":
					CurrentTab()?.PrepareLatestSmoke();
					break;
				case "scroll-sweep":
					_ = RunScrollSweepSmokeAsync();
					break;
				case "latest-churn":
					CurrentTab()?.PrepareLatestChurnSmoke();
					break;
				case "interactions":
					CurrentTab()?.PrepareInteractionSmoke();
					break;
				case "outline":
					CurrentTab()?.PrepareOutlineSmoke();
					break;
				case "primary":
					CurrentTab()?.PreparePrimarySmoke();
					break;
				case "palette":
					_palette?.Show(this);
					break;
				case "model":
					CurrentTab()?.ShowModelPicker();
					break;
				case "import":
					// 导入弹窗的视觉验收：列表与空态都要能截图。
					_importDialog.Show(this);
					break;
				case "git":
					ShowGitPanel();
					// 视觉验收也要看到 diff 区：选中第一个改动。
					if (CurrentTab() is { } gitTab && _sessionPanels.TryGetValue(gitTab, out var gitPanel))
					{
						gitPanel.SelectFirstGitChangeForSmoke();
					}

					break;
				case "git-settings":
					OpenGitAssistantSettings();
					break;
				case "terminal":
					ToggleTerminalPanel();
					break;
				case "files":
					ShowFilesPanel();
					break;
				case "panel":
					ToggleRightPanel();
					break;
				case "config":
					OpenSettingsDialog();
					_settingsView?.ShowConfiguration();
					break;
				case "security":
					OpenSettingsDialog();
					if (_settingsView is { } security) _ = security.SelectSectionAsync("security");
					break;
				case "security-edit":
					// 视觉验收：选中安全门分区并展开一档编辑器，让动作矩阵与目录表一起入镜。
					OpenSettingsDialog();
					if (_settingsView is { } editing) _ = editing.SelectSectionForSecurityEditAsync();
					break;
				case "appearance":
					OpenSettingsDialog();
					if (_settingsView is { } appearance) _ = appearance.SelectSectionAsync("appearance");
					break;
				case "agent":
				case "mcp":
					OpenSettingsDialog();
					_settingsView?.ShowConfiguration();
					if (_settingsView is { } configuration) _ = configuration.SelectSectionAsync(view);
					break;
				case "sidebar":
					ToggleSidebar();
					break;
				case "settings":
					OpenSettingsDialog();
					break;
				case "resources":
					OpenSettingsDialog();
					if (_settingsView is { } resourceView) _ = resourceView.SelectSectionAsync("resources");
					break;
				case "diagnostics":
					// 性能诊断分区：截图验收要看的是真实采样出来的耗时，不是空壳。
					OpenSettingsDialog();
					if (_settingsView is { } diagnosticsView) _ = diagnosticsView.SelectSectionAsync("diagnostics");
					break;
				case "branch":
					CurrentTab()?.ShowBranches();
					break;
				case "balance":
					CurrentTab()?.ShowProviderUsage();
					break;
				case "statistics-details":
					CurrentTab()?.ShowStatisticsDetails();
					break;
				case "statistics":
					CurrentTab()?.ShowStatistics();
					break;
				case "chat":
					CurrentTab()?.ShowChat();
					break;
				case "search":
					CurrentTab()?.ToggleSearchFromSmoke();
					break;
				case "session-search":
					_sessionSearch.Show(this);
					break;
				default:
					Console.Error.WriteLine($"piweft-desktop: 未知的界面演练目标：{view}");
					break;
			}

			Console.WriteLine($"piweft-desktop: ui smoke 已打开 {view}，标签数={_tabEntries.Count}");
		});

		// latest 供外部脚本连续滚动/截图，需要比单张截图更长的驻留窗口。
		// scroll-sweep 从会话底部按 0.9 s 一步扫完整条会话，需要更长的驻留。
		await Task.Delay(view is "scroll-sweep" ? 50000
			: view is "interactions" or "latest" or "latest-churn" or "latest-theme" or "outline" or "wheel" ? 30000
			: 7000).ConfigureAwait(false);
		// 加载提示会阻止关窗；退出前先确认没有在途的打开。
		await WaitForSessionOpensAsync().ConfigureAwait(false);
		Dispatch(() =>
		{
			if (view == "primary") CurrentTab()?.ReportPrimarySmoke();
			if (view == "chat") CurrentTab()?.ReportConversationSmoke();
			if (view == "resources" && _settingsView?.LastSectionError is { Length: > 0 } error)
			{
				// 资源页构建失败（例如引用了不存在的内嵌图标）必须在无人值守演练里暴露出来。
				Console.Error.WriteLine($"piweft-desktop: resources smoke failed: {error}");
				Environment.ExitCode = 1;
			}

			if (view == "loading")
			{
				Console.WriteLine(
					$"piweft-desktop: loading settled tabs={_tabEntries.Count} " +
					$"welcomeVisible={_welcome.IsVisible} prompt={_sessionLoading.Showing} " +
					$"tabVisible={_tabControl.IsVisible}");
			}

			Console.WriteLine("piweft-desktop: ui smoke ok");
			Application.Shutdown();
		});
	}

	/// <summary>
	/// 无人值守的主题演练：等界面稳定后换肤，验证选择颜色与草稿状态，再退出。
	/// </summary>
	private async Task RunThemeSmokeAsync()
	{
		Color oldSelection = default;
		Color oldPickerColor = default;
		SessionTab? originalTab = null;
		try
		{
			// 启动时自动打开的会话与侧栏都在后台准备，先等它们落地再演练换肤。
			await WaitForSidebarAsync().ConfigureAwait(false);
			await WaitForSessionOpensAsync().ConfigureAwait(false);
			// 固定从深色开始，这样「切换后是浅色」才有意义（否则起点可能是跟随系统）。
			await Task.Delay(800).ConfigureAwait(false);
			Dispatch(() =>
			{
				(DesktopPreference.Load() with { Theme = DesktopPreference.DarkTheme }).Save();
				Application.Current.SetTheme(ThemeVariant.Dark);
			});

			await Task.Delay(800).ConfigureAwait(false);
			Dispatch(() =>
			{
				var started = Stopwatch.StartNew();
				Console.WriteLine($"piweft-desktop: 切换前 主题={(AppTheme.IsLightTheme ? "浅色" : "深色")}，标签数={_tabEntries.Count}");
				oldSelection = Application.Current.Theme.Palette.SelectionBackground;
				originalTab = CurrentTab();
				originalTab?.PrepareThemeSmoke();
				oldPickerColor = originalTab?.ThemeSmokePickerColor ?? default;
				ToggleTheme();
				started.Stop();
				Console.WriteLine($"piweft-desktop: 换肤耗时 {started.Elapsed.TotalMilliseconds:0.0} ms");
			});

			await Task.Delay(1500).ConfigureAwait(false);
			Dispatch(() =>
			{
				Console.WriteLine($"piweft-desktop: 切换后 主题={(AppTheme.IsLightTheme ? "浅色" : "深色")}，标签数={_tabEntries.Count}");
				var valid = oldSelection != Application.Current.Theme.Palette.SelectionBackground &&
					ReferenceEquals(originalTab, CurrentTab()) && originalTab?.VerifyThemeSmoke(oldPickerColor) == true;
				Console.WriteLine(valid ? "theme controls ok: selection, picker, border, draft, caret" : "theme controls FAILED");
				Console.WriteLine(valid ? "piweft-desktop: theme smoke ok" : "piweft-desktop: theme smoke failed");
				if (!valid) Environment.ExitCode = 1;
			});

			// 加载提示会阻止关窗：等在途的打开结束再退出。
			await WaitForSessionOpensAsync().ConfigureAwait(false);
			Dispatch(() => Application.Shutdown());
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"piweft-desktop: theme smoke failed: {ex}");
			Dispatch(() => Application.Shutdown());
		}
	}

	/// <summary>监视主题目录：防抖后的变更回调切回 UI 线程重新解析并换肤。</summary>
	private void StartThemeWatcher()
	{
		try
		{
			var projectTrusted = ProjectTrust.Resolve(
				_cwd,
				_arguments.ProjectTrustOverride,
				CodingAgentSettings.GlobalDefaultProjectTrust());
			_themeWatcher = ThemeWatcher.Create(_cwd, includeProject: projectTrusted);
			_themeWatcher.Changed += () => Dispatch(ReapplyThemeFromDisk);
		}
		catch (Exception ex)
		{
			// 监视失败只损失热重载能力，不影响主题本身的加载。
			AppLog.Failure(AppLog.For<MainWindow>(), "Theme watcher", ex);
			Console.Error.WriteLine($"piweft-desktop: 主题监视不可用：{ex.Message}");
		}
	}

	/// <summary>主题文件变化后重新解析当前主题并应用；坏文件保留现有外观并记录诊断。</summary>
	private void ReapplyThemeFromDisk()
	{
		try
		{
			var preference = DesktopPreference.Load();
			var resolved = DesktopTheme.Resolve(preference.Theme, _cwd, SessionPaths.GetAgentDir());
			var isBuiltin = preference.Theme is DesktopPreference.LightTheme or DesktopPreference.DarkTheme or DesktopPreference.SystemTheme;
			if (!isBuiltin && resolved.Light is null && resolved.Dark is null)
			{
				// 自定义主题文件此刻解析失败（保存到一半或语法错误）：保留旧外观。
				Console.Error.WriteLine($"piweft-desktop: 主题 {preference.Theme} 解析失败，保留当前外观");
				return;
			}

			DesktopTheme.Apply(resolved);
			if (!Application.IsRunning)
			{
				return;
			}

			// SetTheme 对相同变体可能不触发 OnThemeChanged；无论事件是否到达都轻量刷新一遍。
			AppStyles.ApplyTextColors(this);
			AppIcons.RefreshAll();
			_nativeBorderColor = null;
			UpdateChrome();
			foreach (var tab in _tabEntries.OfType<SessionTab>())
			{
				tab.ApplyTheme();
			}

			_settingsView?.ApplyTheme();
			_settingsDialog?.ApplyTheme();
			foreach (var panel in _sessionPanels.Values) panel.ApplyTheme();
			_usageStatsView?.ApplyTheme();
			AppStyles.ApplyTextColors(_sessionSearch.View);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Theme hot reload", ex);
			Console.Error.WriteLine($"piweft-desktop: 主题热重载失败：{ex.Message}");
		}
	}

	/// <summary>换肤只更新颜色，正文、Markdown、分支树与 Git 快照均保留。</summary>
	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme)
	{
		base.OnThemeChanged(oldTheme, newTheme);

		if (ReferenceEquals(oldTheme, newTheme))
		{
			return;
		}

		try
		{
			AppStyles.ApplyTextColors(this);
			if (_palette is { IsOpen: true }) AppStyles.ApplyTextColors(_palette.View);
			AppIcons.RefreshAll();
			_nativeBorderColor = null;
			UpdateChrome();

			foreach (var tab in _tabEntries.OfType<SessionTab>())
			{
				tab.ApplyTheme();
			}

			_settingsView?.ApplyTheme();
			_settingsDialog?.ApplyTheme();
			foreach (var panel in _sessionPanels.Values) panel.ApplyTheme();
			_usageStatsView?.ApplyTheme();
			AppStyles.ApplyTextColors(_sessionSearch.View);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Theme refresh", ex);
			Console.Error.WriteLine($"piweft-desktop: 换肤刷新失败：{ex}");
		}
	}

	/// <summary>把窗口尺寸与位置写进偏好文件。</summary>
	private void SaveWindowState()
	{
		try
		{
			var position = Position;
			var preference = DesktopPreference.Load().WithWindow(Width, Height, position.X, position.Y) with
			{
				SidebarCollapsed = !_sidebarPreferred,
				SidebarWidth = _workspace.FirstLength.IsAbsolute ? _workspace.FirstLength.Value : AppTheme.SidebarWidth,
				RightPanelWidth = _rightPanelWidth,
			};
			preference.Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Save window state", ex);
			Console.Error.WriteLine($"piweft-desktop: 无法保存窗口状态：{ex.Message}");
		}
	}

	/// <summary>收起时复用现有侧栏与会话，SplitPanel 自动把空间交给主区。</summary>
	/// <summary>从辅助页返回工作区：最近的会话标签，没有会话则关掉当前辅助页回到欢迎页。</summary>
	private void BackToWorkspace()
	{
		var index = _tabEntries.FindLastIndex(entry => entry is SessionTab);
		if (index >= 0)
		{
			_tabControl.SelectedIndex = index;
			UpdateStatus();
		}
		else
		{
			CloseCurrentTab();
		}
	}

	private void ToggleSidebar()
	{
		_sidebarPreferred = !_sidebarPreferred;
		ApplySidebarLayout();
		SaveWindowState();
		// 展开即加载：收着的时候一直不扫摘要。
		if (_sidebarPreferred) _ = EnsureSessionsLoadedAsync();
	}

	// ------------------------------------------------------- 右侧功能面板

	/// <summary>标题栏的 panel 键 / Ctrl+J：收起时展开，展开时收起；面板属于当前会话。</summary>
	private void ToggleRightPanel() => SetRightPanel(_panelHost?.IsVisible != true);

	/// <summary>展开或收起当前会话的右侧面板；指定分段时顺带切过去。</summary>
	private void SetRightPanel(bool open, RightPanelSection? section = null)
	{
		if (CurrentTab() is not { } tab || !_sessionPanels.TryGetValue(tab, out var panel))
		{
			return;
		}

		if (open)
		{
			// 首次展开时才创建视图：默认收起，文件与 Git 都只在需要时读盘。
			panel.Show(section ?? panel.Section);
		}
		else
		{
			// 收起前把当前宽度记下来，下次展开沿用；之后交给 SplitPanel 默认行为。
			CapturePanelWidth();
			panel.Close();
			// 收起键会随面板一起消失，焦点交还主区的输入框。
			FocusActiveEntry();
		}

		SyncPanelLayout();
		SaveWindowState();
	}

	private void ShowGitPanel() => SetRightPanel(open: true, RightPanelSection.Git);

	private void ShowFilesPanel() => SetRightPanel(open: true, RightPanelSection.Files);

	/// <summary>Ctrl+Shift+D / 命令面板：开关当前会话底部的终端面板（终端不再占用右侧面板）。</summary>
	private void ToggleTerminalPanel() => CurrentTab()?.ToggleTerminal();

	/// <summary>
	/// 当前会话的面板决定宿主是否出现：切换会话只切可见性，各会话的分段与内容都保留。
	/// </summary>
	private void SyncPanelLayout()
	{
		if (_panelHost is null || _panelSplit is null)
		{
			return;
		}

		var active = CurrentTab();
		var activePanel = active is not null && _sessionPanels.TryGetValue(active, out var found) ? found : null;
		// 加载中整个工作区只留加载提示，右侧面板也一起收起。
		var open = activePanel?.IsOpen == true && !_sessionLoading.Showing;

		foreach (var (tab, panel) in _sessionPanels)
		{
			panel.View.IsVisible(open && ReferenceEquals(tab, active));
		}

		if (_panelHost.IsVisible != open)
		{
			_panelHost.IsVisible(open);
			if (open)
			{
				ReapplyPanelWidth();
			}

			// 面板开合会改变主标题栏/面板页签条给窗口按钮让位的宽度。
			UpdateChrome();
		}
		else
		{
			UpdateRightPanelButton();
		}
	}

	/// <summary>展开时用上次记录的宽度初始化分栏；之后完全交给 SplitPanel 的默认布局。</summary>
	private void ReapplyPanelWidth()
	{
		if (!_panelHost.IsVisible)
		{
			return;
		}

		_panelSplit.FirstLength = GridLength.Stars(1);
		_panelSplit.SecondLength = GridLength.Pixels(_rightPanelWidth);
	}

	/// <summary>收起面板/关窗时把当前宽度记成偏好，供下次展开使用。</summary>
	private void CapturePanelWidth()
	{
		if (!_panelHost.IsVisible)
		{
			return;
		}

		var width = _panelHost.Bounds.Width;
		if (width >= AppTheme.RightPanelMinWidth - 0.5)
		{
			_rightPanelWidth = Math.Clamp(width, AppTheme.RightPanelMinWidth, AppTheme.RightPanelMaxWidth);
		}
	}

	/// <summary>辅助页（用量统计）不需要会话侧栏；右侧面板不占用侧栏。</summary>
	private void ApplySidebarLayout()
	{
		var entry = _tabControl.SelectedIndex >= 0 && _tabControl.SelectedIndex < _tabEntries.Count
			? _tabEntries[_tabControl.SelectedIndex] : null;
		var sidebarAllowed = entry is null or SessionTab;
		_sidebarColumn.IsVisible(_sidebarPreferred && sidebarAllowed);
		UpdateSidebarChrome();
	}

	private void UpdateSidebarChrome()
	{
		var entry = _tabControl.SelectedIndex >= 0 && _tabControl.SelectedIndex < _tabEntries.Count
			? _tabEntries[_tabControl.SelectedIndex] : null;
		var sidebarAllowed = entry is null or SessionTab;
		var collapsed = !_sidebarColumn.IsVisible;
		// 展开/收起入口：展开时在左段品牌旁，收起后回到主标题栏最左侧。
		_hideSidebarButton.IsVisible(!collapsed && sidebarAllowed);
		_showSidebarButton.IsVisible(collapsed && sidebarAllowed);
		_backButton.IsVisible(!sidebarAllowed);
		UpdateChrome();
	}

	/// <summary>
	/// 切换深色/浅色配色：原位更新主题，并把偏好写盘供下次启动使用。
	/// </summary>
	private void ToggleTheme()
	{
		var preference = DesktopPreference.Load().Cycle();

		try
		{
			preference.Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Save theme preference", ex);
			_pendingNotice = Strings.ThemeChangeFailed(ex.Message);
		}

		// 连续点击只应用最后一次：每次 SetTheme 都会触发整树重排重绘，
		// 高频连发可能让虚拟化行里的文本视图重建到一半又被下一次打断（代码块丢文本）。
		_queuedThemePreference = preference;
		_themeApplyGate ??= CreateThemeApplyGate();
		_themeApplyGate.Stop();
		_themeApplyGate.Start();
	}

	private DispatcherTimer CreateThemeApplyGate()
	{
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120));
		timer.Tick += () =>
		{
			timer.Stop();
			var preference = _queuedThemePreference;
			_queuedThemePreference = null;
			if (preference is null) return;
			ApplyThemePreference(preference);
		};
		return timer;
	}

	private void ApplyThemePreference(DesktopPreference preference)
	{
		var name = preference.Theme switch
		{
			DesktopPreference.LightTheme => Strings.ThemeLight,
			DesktopPreference.SystemTheme => Strings.ThemeSystem,
			_ => Strings.ThemeDark,
		};

		if (Application.IsRunning)
		{
			DesktopTheme.Apply(DesktopTheme.Resolve(preference.Theme, _cwd, SessionPaths.GetAgentDir()));
		}

		// 主题切换的反馈走窗口级 toast；欢迎页横幅只留给启动/打开会话这类持续错误。
		if (_pendingNotice is { } failure) Toasts.Error(this, failure);
		else Toasts.Success(this, Strings.ThemeChanged(name));
		_pendingNotice = null;
	}

	/// <summary>把当前会话的文件路径或会话 id 复制到剪贴板。</summary>
	private void CopySessionInfo(bool path)
	{
		if (CurrentTab() is not { } tab)
		{
			_banner.Text(Strings.SelectSessionFirst);
			return;
		}

		var text = path
			? tab.Session.Session.SessionFile ?? tab.Session.Session.SessionId
			: tab.Session.Session.SessionId;

		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		_banner.Text(clipboard?.TrySetText(text) == true ? Strings.Copied : Strings.ClipboardUnavailable);
	}

	/// <summary>
	/// 把当前会话导出成自包含的 HTML（与 CLI 的 <c>export_html</c> 同一套渲染），
	/// 可选立刻用系统默认程序打开。
	/// </summary>
	private void ExportCurrentSession(bool open)
	{
		if (CurrentTab() is not { } tab)
		{
			_banner.Text(Strings.SelectSessionFirst);
			return;
		}

		try
		{
			var directory = tab.Session.Session.SessionDir;
			if (directory.Length == 0)
			{
				directory = Path.Combine(SessionPaths.GetAgentDir(), "exports");
			}

			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, $"{tab.Session.Session.SessionId}.html");
			File.WriteAllText(path, PiWeft.Cli.SessionHtml.Render(tab.Session));

			_banner.Text(Strings.Exported(path));

			if (open)
			{
				Shell.Open(path);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Export session", ex);
			_banner.Text(Strings.ExportFailed(ex.Message));
		}
	}

	/// <summary>打开（或选中）用量统计标签；每次切回都会重新扫描。</summary>
	private void OpenUsageStatsTab()
	{
		var index = _usageStatsView is null ? -1 : _tabEntries.IndexOf(_usageStatsView);
		if (_usageStatsView is not null && index >= 0)
		{
			_ = _usageStatsView.RefreshAsync();
			_tabControl.SelectedIndex = index;
			return;
		}

		_usageStatsView = new UsageStatsView(
			() => _arguments.SessionDir is { Length: > 0 } dir ? dir : UsageStats.DefaultRoot(),
			(provider, model) => _environment?.Registry.Get(provider, model)?.Cost);
		_tabEntries.Add(_usageStatsView);
		AddTab(header: null!, _usageStatsView.View, _usageStatsView);

		_tabControl.SelectedIndex = _tabEntries.Count - 1;
		UpdateStatus();
	}

	/// <summary>浮层关闭后把焦点还给当前视图：会话给输入框，没有会话时给搜索入口。</summary>
	private void FocusActiveEntry()
	{
		if (CurrentTab() is { } tab)
		{
			tab.FocusComposer();
			return;
		}

		_searchButton.Focus();
	}

	/// <summary>打开设置弹窗（与 PiDeck 一样是模态浮层，不是主区标签）。</summary>
	private void OpenSettingsDialog()
	{
		if (_settingsDialog is null)
		{
			if (_settingsView is null)
			{
				_settingsView = new SettingsView(Snapshot, ReloadConfiguration, ReconnectMcpAsync, ReloadResourcesAsync);
				// 安全门改动要立刻推给所有已打开的会话，否则界面档位与实际执行用的档位会不一致。
				_settingsView.SecurityChanged += ApplySecurityToSessions;
			}
			_settingsDialog = new SettingsDialog(_settingsView);
			_settingsDialog.Closed += () =>
			{
				FocusActiveEntry();
				// Git 提交摘要的默认模型/思考级别可能在设置里刚改过；打开着的面板重读一次。
				foreach (var panel in _sessionPanels.Values)
				{
					panel.Refresh();
				}
			};
		}

		_settingsDialog.Show(this);
	}

	/// <summary>把磁盘上的安全配置推给所有已打开的会话。</summary>
	private void ApplySecurityToSessions()
	{
		var config = PiWeft.CodingAgent.Security.SecurityConfig.Load();
		foreach (var tab in _tabEntries.OfType<SessionTab>().ToArray())
		{
			tab.ApplySecurityConfig(config);
		}
	}

	/// <summary>Reload future-session environments without replacing any running session.</summary>
	private void ReloadConfiguration(ConfigFile file)
	{
		if (file == ConfigFile.Mcp) { _banner.Text(Strings.ConfigMcpRestart); return; }
		try
		{
			var (environment, error) = CliEnvironment.Create(_arguments, _cwd);
			if (environment is null) { _banner.Text(error ?? Strings.ConfigFailed); return; }
			_environment = environment;
			_environments.Clear();
			_environments[Path.GetFullPath(_cwd)] = environment;
			_models = environment.ListAvailableModels(_arguments.ApiKey);
			_banner.Text(Strings.ConfigApplied);
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
		{
			_banner.Text(Strings.ConfigFailed);
		}
	}

	/// <summary>
	/// 重新解析技能、提示词模板与主题，对应 pi 的 <c>/reload</c>：已打开的会话不重建，
	/// 只刷新它们下一次请求使用的系统提示词章节。
	/// </summary>
	private Task ReloadResourcesAsync()
	{
		foreach (var tab in _tabEntries.OfType<SessionTab>())
		{
			try
			{
				tab.Session.ReloadResources();
			}
			catch (ObjectDisposedException)
			{
				// 标签正在关闭：跳过。
			}
		}

		_settingsView?.Refresh();
		_banner.Text(Strings.ResourcesReloaded);
		return Task.CompletedTask;
	}

	/// <summary>命令面板的命令集：宿主操作 + 当前会话的斜杠命令。</summary>
	private IReadOnlyList<PaletteCommand> PaletteCommands()
	{
		var commands = new List<PaletteCommand>
		{
			new(Strings.NewSession, Strings.ShortcutNewSession, NewSessionFromCurrentView),
			new(Strings.SessionSearch, Strings.ShortcutSearchSessions, () => _sessionSearch.Toggle(this)),
			new(Strings.OpenProject, "Ctrl+Shift+O", () => _ = ChooseProjectAsync()),
			new(Strings.ImportTitle, string.Empty, () => _importDialog.Show(this)),
			new(Strings.SelectModel, "Ctrl+M", () => CurrentTab()?.ShowModelPicker()),
			new(Strings.Settings, Strings.ShortcutSettings, OpenSettingsDialog),
			new(Strings.Git, Strings.ShortcutGit, ShowGitPanel),
			new(Strings.FileBrowser, Strings.ShortcutFilesPanel, ShowFilesPanel),
			new(Strings.Terminal, Strings.ShortcutTerminal, ToggleTerminalPanel),
			new(Strings.RightPanel, Strings.ShortcutRightPanel, ToggleRightPanel),
			new(Strings.UsageStatsTitle, string.Empty, OpenUsageStatsTab),
			new(Strings.CopySessionPath, Strings.ShortcutCopyPath, () => CopySessionInfo(path: true)),
			new(Strings.CopySessionId, Strings.ShortcutCopyId, () => CopySessionInfo(path: false)),
			new(Strings.ExportSession, Strings.ShortcutExportSession, () => ExportCurrentSession(open: false)),
			new(Strings.ExportSessionAndOpen, Strings.ShortcutExportAndOpen, () => ExportCurrentSession(open: true)),
			new(Strings.ToggleTheme, Strings.ShortcutTheme, ToggleTheme),
			new(Strings.RefreshSessions, Strings.ShortcutRefresh, () => RefreshSessions(force: true)),
			new(Strings.CloseCurrentTab, Strings.ShortcutCloseTab, CloseCurrentTab),
			new(Strings.ViewBranches, Strings.ShortcutBranches, () =>
			{
				if (CurrentTab() is { } tab)
				{
					tab.ShowBranches();
				}
			}),
			new(Strings.MoveTabLeft, Strings.ShortcutMoveTabLeft, () => MoveCurrentTab(-1)),
			new(Strings.MoveTabRight, Strings.ShortcutMoveTabRight, () => MoveCurrentTab(1)),
			new(Strings.CloseOtherTabs, Strings.ShortcutCloseOtherTabs, CloseOtherTabs),
			new(Strings.CloseAllTabs, Strings.ShortcutCloseAllTabs, CloseAllTabs),
			new(Strings.ViewChat, Strings.ShortcutChat, () =>
			{
				if (CurrentTab() is { } tab)
				{
					tab.ShowChat();
				}
			}),
		};

		foreach (var project in DesktopPreference.Load().RecentProjects.Where(Directory.Exists))
		{
			commands.Add(new PaletteCommand($"{Strings.RecentProjects} · {ShortCwd(project)}", project, () => OpenProject(project)));
		}

		// 会话标签跳转。
		for (var index = 0; index < _tabEntries.Count; index++)
		{
			if (_tabEntries[index] is SessionTab tab)
			{
				var target = index;
				commands.Add(new PaletteCommand(
					tab.Title,
					Strings.ShortcutSwitchTo(target + 1),
					() =>
					{
						_tabControl.SelectedIndex = target;
						UpdateStatus();
					}));
			}
		}

		// 当前会话可以运行的斜杠命令。
		if (CurrentTab() is { } current)
		{
			foreach (var command in current.SlashCommands())
			{
				var name = command;
				commands.Add(new PaletteCommand(
					$"/{name}",
					Strings.ShortcutSlashCommand,
					() => current.Prefill($"/{name} ")));
			}
		}

		return commands;
	}

	/// <summary>关闭当前选中的标签。</summary>
	private void CloseCurrentTab()
	{
		var index = _tabControl.SelectedIndex;
		if (index >= 0 && index < _tabEntries.Count)
		{
			CloseEntry(_tabEntries[index]);
		}
	}

	/// <summary>显示重命名输入框（平时隐藏，保持侧栏整洁）。</summary>
	private void ShowRenameBox()
	{
		if (SelectedSession() is null)
		{
			_banner.Text(Strings.SelectSessionFirst);
			return;
		}

		_nameBox.Text = SelectedSession()?.Name ?? string.Empty;
		_nameBox.IsVisible(true);
		_nameBox.Focus();
	}

	/// <summary>置顶/取消置顶侧栏选中的会话。</summary>
	private void TogglePin()
	{
		var selected = SelectedSession();
		if (selected is null)
		{
			_banner.Text(Strings.SelectSessionFirst);
			return;
		}

		try
		{
			var preference = DesktopPreference.Load().TogglePinned(selected.Id);
			preference.Save();
			RefreshSessions(force: true);
			RestoreSelection(selected.Id);
			_banner.Text(preference.IsPinned(selected.Id) ? Strings.Pinned(selected.Display) : Strings.Unpinned(selected.Display));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_banner.Text(Strings.ThemeChangeFailed(ex.Message));
		}
	}

	/// <summary>标题栏左段的项目铭牌：显示当前会话所属项目，没有会话时显示窗口工作目录。</summary>
	private void UpdateProjectCaption()
	{
		var cwd = CurrentTab()?.Session.Cwd ?? _cwd;
		SetProjectCaption(ShortCwd(cwd), cwd);
	}


	/// <summary>
	/// 侧栏项目是否默认展开：只认「用户上次展开过」与「包含当前选中会话」两种。
	/// 默认折叠——会话多的机器上全展开等于每次启动都把整份清单铺出来。
	/// </summary>
	internal static bool ShouldExpandProject(string? projectCwd, bool containsSelected, IReadOnlyList<string> expandedProjects) =>
		projectCwd is { Length: > 0 } && (containsSelected || expandedProjects.Contains(projectCwd, StringComparer.Ordinal));

	/// <summary>
	/// 该项目节点里是否有当前会话。按路径比对而不是会话 id：外部工具生成的会话
	/// 头部 id 可能与文件名里的 uuid 不一致，用 id 会漏判、把选中项折在看不见的地方。
	/// </summary>
	private static bool ContainsSession(TreeViewNode project, string? sessionPath)
	{
		if (sessionPath is null) return false;
		foreach (var child in project.Children)
		{
			if (child.Tag is SessionListItem item &&
				string.Equals(item.Path, sessionPath, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>用户展开/折叠项目时记一笔并落盘；程序化重建时的同值操作不写文件。</summary>
	private void RememberProjectExpanded(object? item, bool expanded)
	{
		if (item is not TreeViewNode { Tag: string cwd } || cwd.Length == 0)
		{
			return;
		}

		if (expanded ? !_expandedProjects.Add(cwd) : !_expandedProjects.Remove(cwd))
		{
			return;
		}

		try
		{
			(DesktopPreference.Load() with { ExpandedProjects = [.. _expandedProjects] }).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Save expanded projects", ex);
		}
	}

	/// <summary>在列表重建后把选中项恢复到指定会话；「对话」与「项目」两个分段都要找。</summary>
	private void RestoreSelection(string sessionId)
	{
		// 恢复高亮不应再次打开会话或把焦点从正在输入的控件抢走。
		_restoringSelection = true;
		try
		{
			foreach (var chat in _chatNodes)
			{
				if (chat.Tag is SessionListItem item && string.Equals(item.Id, sessionId, StringComparison.Ordinal))
				{
					_chatsTree.SelectedNode = chat;
					return;
				}
			}

			foreach (var project in _sessionNodes)
			{
				foreach (var child in project.Children)
				{
					if (child.Tag is SessionListItem item && string.Equals(item.Id, sessionId, StringComparison.Ordinal))
					{
						_sessionTree.SelectedNode = child;
						return;
					}
				}
			}
		}
		finally
		{
			_restoringSelection = false;
		}
	}

	/// <summary>把当前标签左移/右移一格（拖拽排序的键盘替代）。</summary>
	private void MoveCurrentTab(int delta)
	{
		var index = _tabControl.SelectedIndex;
		if (index >= 0 && index < _tabEntries.Count)
		{
			MoveTab(_tabEntries[index], delta);
		}
	}

	/// <summary>按 _tabEntries 的顺序重建标签栏。</summary>
	private void RebuildTabStrip()
	{
		_tabControl.ClearTabs();

		foreach (var entry in _tabEntries)
		{
			switch (entry)
			{
				case SessionTab tab:
					AddTab(header: null!, tab.View, tab);
					break;

				case UsageStatsView stats:
					AddTab(header: null!, stats.View, stats);
					break;
			}
		}
	}

	/// <summary>关闭除当前标签之外的所有会话标签。</summary>
	private void CloseOtherTabs()
	{
		var keep = CurrentTab();
		foreach (var tab in _tabEntries.OfType<SessionTab>().Where(tab => !ReferenceEquals(tab, keep)).ToList())
		{
			CloseTab(tab);
		}
	}

	/// <summary>关闭所有会话标签（设置面板保留）。</summary>
	private void CloseAllTabs()
	{
		foreach (var tab in _tabEntries.OfType<SessionTab>().ToList())
		{
			CloseTab(tab);
		}
	}

	/// <summary>当前选中的会话标签。</summary>
	private SessionTab? CurrentTab()
	{
		var index = _tabControl.SelectedIndex;
		return index >= 0 && index < _tabEntries.Count ? _tabEntries[index] as SessionTab : null;
	}

	/// <summary>Git 面板的 AI 提交摘要能力：默认跟随会话；模型/思考级别由偏好覆盖。</summary>
	private GitCommitAssistant CreateGitAssistant(CodingAgentSession session, CliEnvironment environment) => new(
		() => _models,
		() => session.Model,
		() => session.ThinkingLevel,
		(model, thinking, diff, cancellationToken) => GitCommitMessage.GenerateAsync(
			environment.Gateway,
			environment.Registry,
			model,
			thinking,
			diff,
			provider => _arguments.ApiKey ?? environment.ProviderApiKeys.GetValueOrDefault(provider),
			cancellationToken),
		() => session.IsRunning,
		OpenGitAssistantSettings);

	/// <summary>从 Git 面板的「更改默认配置」打开设置，并落到 Git 提交摘要分区。</summary>
	private void OpenGitAssistantSettings()
	{
		OpenSettingsDialog();
		if (_settingsView is not null)
		{
			_ = _settingsView.SelectSectionAsync("git");
		}
	}

	/// <summary>设置面板的数据快照。</summary>
	private SettingsSnapshot Snapshot() => new()
	{
		AgentDir = SessionPaths.GetAgentDir(),
		LoadArchived = () => _catalog.ListDeleted(),
		Cwd = CurrentSession()?.Cwd ?? _cwd,
		ModelCount = _models.Count,
		Models = _models,
		Tools = [.. CurrentSession()?.Tools.Select(static tool => tool.Name) ?? []],
		Skills = [.. CurrentSession()?.DiscoveredSkills.Select(static skill => skill.Name) ?? []],
		Resources = CurrentSession()?.Resources ?? ResourceResolver.Resolve(
			CurrentSession()?.Cwd ?? _cwd,
			SessionPaths.GetAgentDir(),
			_environment?.ProjectTrusted ?? true),
		McpServers = [.. _mcp?.Servers.Select(state => new McpServerStatus(state.Name, state.Connected, state.ToolCount, state.Error)) ?? []],
		Session = CurrentSession(),
	};

	/// <summary>辅助标签沿用最近聚焦的会话，打开设置不会丢失模型、工具和用量上下文。</summary>
	private CodingAgentSession? CurrentSession() => CurrentTab()?.Session ?? _lastSessionTab?.Session;

	/// <summary>给某个工作目录准备 MCP 工具：全局连一次，工具按会话所属目录过滤。</summary>
	private IReadOnlyList<AgentTool> McpToolsFor(string cwd) =>
		string.Equals(Path.GetFullPath(cwd), Path.GetFullPath(_cwd), StringComparison.Ordinal) ? _mcpTools : [];

	/// <summary>后台连接 MCP 服务器，并把工具注入所有已打开的会话。</summary>
	private async Task ConnectMcpAsync()
	{
		try
		{
			var mcpWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
			var connection = await McpServers.ConnectConfiguredAsync(
				_cwd,
				onError: message => Dispatch(() => _banner.Text($"MCP：{message}")),
				includeProject: _environment?.ProjectTrusted ?? true).ConfigureAwait(false);

			if (mcpWatch is not null)
			{
				Diagnostics.Record("mcp.connect", mcpWatch, $"servers={connection.Servers.Count}, errors={connection.Errors.Count}");
			}

			var resourceTools = McpResourceTools.Create(() => connection.ResourceServers);

			Dispatch(() =>
			{
				_mcp = connection;
				AppLog.For<MainWindow>().ZLogInformation($"MCP connections ready: servers={connection.Servers.Count}, errors={connection.Errors.Count}");

				// 服务器在运行中添加/移除工具时，同步到已打开的会话。
				connection.ToolsChanged += changed =>
				{
					var tools = ToolSetFor(changed);
					Dispatch(() =>
					{
						_mcpTools = tools;
						foreach (var tab in _tabEntries.OfType<SessionTab>().Where(tab => McpToolsFor(tab.Session.Cwd).Count > 0))
						{
							tab.Session.SetTools([.. WithoutMcp(tab.Session.Tools), .. tools]);
						}

						_settingsView?.Refresh();
					});
				};

				_mcpTools = [.. connection.Tools, .. resourceTools];

				foreach (var tab in _tabEntries.OfType<SessionTab>().Where(tab => McpToolsFor(tab.Session.Cwd).Count > 0))
				{
					tab.Session.SetTools([.. WithoutMcp(tab.Session.Tools), .. _mcpTools]);
				}

				if (connection.Errors.Count == 0 && connection.Servers.Count > 0)
				{
					_banner.Text(Strings.McpConnected(connection.Servers.Count, _mcpTools.Count));
				}

				_settingsView?.Refresh();
			});
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Connect MCP servers", ex);
			Dispatch(() => _banner.Text($"MCP：{ex.Message}"));
		}
	}

	/// <summary>重建 MCP 连接：先释放旧连接，再按当前配置重新连接（供设置面板的手动重连使用）。</summary>
	internal async Task ReconnectMcpAsync()
	{
		var previous = _mcp;
		_mcp = null;
		_mcpTools = [];
		if (previous is not null)
		{
			try
			{
				await previous.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				AppLog.Failure(AppLog.For<MainWindow>(), "Dispose MCP before reconnect", ex);
			}
		}

		await ConnectMcpAsync().ConfigureAwait(false);
	}

	private static IReadOnlyList<AgentTool> ToolSetFor(McpConnection connection) =>
		[.. connection.Tools, .. McpResourceTools.Create(() => connection.ResourceServers)];

	/// <summary>去掉工具列表里来自 MCP 的项，保留内置工具。</summary>
	private static IReadOnlyList<AgentTool> WithoutMcp(IReadOnlyList<AgentTool> tools) =>
		[.. tools.Where(tool => !tool.Name.StartsWith("mcp__", StringComparison.Ordinal) && !McpResourceTools.IsResourceTool(tool.Name))];

	// ------------------------------------------------------------------ 辅助

	/// <summary>会话头部可能来自行为不规范的工具；空目录或坏路径按当前目录处理，至少让历史可读。</summary>
	private static string ResolveCwd(string? cwd)
	{
		if (!string.IsNullOrWhiteSpace(cwd))
		{
			try
			{
				return Path.GetFullPath(cwd);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
			{
				AppLog.For<MainWindow>().ZLogWarning($"Invalid session cwd, falling back to the process directory: cwd={cwd}, error={ex.Message}");
			}
		}

		return Environment.CurrentDirectory;
	}

	/// <summary>按工作目录缓存环境；创建失败时回退到启动时的那套。</summary>
	private CliEnvironment EnvironmentFor(string cwd)
	{
		var key = ResolveCwd(cwd);
		if (_environments.TryGetValue(key, out var cached))
		{
			return cached;
		}

		var (environment, error) = CliEnvironment.Create(_arguments, key);
		if (environment is null)
		{
			if (_environment is null)
			{
				throw new InvalidOperationException(error ?? Strings.NoSession);
			}

			// 另一个项目可能缺配置（例如 models.json 不完整）；用启动环境继续，至少会话可读。
			_banner.Text(Strings.EnvironmentFallback(key, error ?? string.Empty));
			_environments[key] = _environment;
			return _environment;
		}

		_environments[key] = environment;
		return environment;
	}

	/// <summary>把工作目录压缩成便于在列表里识别的短名。</summary>
	private static string ShortCwd(string cwd)
	{
		if (cwd.Length == 0)
		{
			return "?";
		}

		var trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar);
		var name = Path.GetFileName(trimmed);
		return name.Length > 0 ? name : trimmed;
	}

	/// <summary>
	/// 把后台线程的进度汇报转到 UI 线程。<see cref="Progress{T}"/> 依赖同步上下文，
	/// 而 MewUI 不安装它，回调会落在线程池上直接碰 BusyIndicator —— 所以自己调度。
	/// </summary>
	private sealed class UiProgress<T>(Action<T> apply) : IProgress<T>
	{
		public void Report(T value) => Dispatch(() => apply(value));
	}

	/// <summary>把动作调度到 UI 线程；应用尚未运行时直接执行。</summary>
	private static void Dispatch(Action action)
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			action();
			return;
		}

		dispatcher.BeginInvoke(action);
	}

	private static string TimeAgo(DateTimeOffset time)
	{
		var delta = DateTimeOffset.UtcNow - time;
		return delta switch
		{
			{ TotalMinutes: < 1 } => Strings.TimeJustNow,
			{ TotalHours: < 1 } => Strings.TimeMinutes((int)delta.TotalMinutes),
			{ TotalDays: < 1 } => Strings.TimeHours((int)delta.TotalHours),
			{ TotalDays: < 30 } => Strings.TimeDays((int)delta.TotalDays),
			_ => time.ToLocalTime().ToString("yyyy-MM-dd"),
		};
	}
}
