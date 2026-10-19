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
	private IReadOnlyList<SessionListItem> _allSessions = [];
	private IReadOnlyList<Model> _models = [];

	/// <summary>「对话」分段的工作目录：这里开出的会话不属于任何项目。</summary>
	private readonly string _chatWorkspace;

	/// <summary>「对话」分段的节点，重建后用来恢复选中。</summary>
	private IReadOnlyList<TreeViewNode> _chatNodes = [];

	/// <summary>「对话」为空时的提示；列表铺满剩余高度，提示叠在列表上方。</summary>
	private TextBlock _chatsEmpty = null!;

	private SessionSearchPalette _sessionSearch = null!;
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
		_sessionSearch = new SessionSearchPalette(() => _allSessions, OpenSessionFromSearch);
		_sessionSearch.Closed += FocusActiveEntry;
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

		_welcome = new Border()
			.StyleName(AppStyles.Window)
			.Padding(new Thickness(AppTheme.Space6))
			.Child(new StackPanel()
				.Spacing(AppTheme.Space3)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new TextBlock()
						.Text(Strings.WelcomeTitle)
						.FontSize(20)
						.FontWeight(FontWeight.Medium)
						.PrimaryText()
						.HorizontalAlignment(HorizontalAlignment.Center),
					new TextBlock()
						.Text(Strings.WelcomeBody)
						.FontSize(AppTheme.FontSizeCaption)
						.MutedText()
						.TextAlignment(TextAlignment.Center)
						.MaxWidth(420)
						.TextWrapping(TextWrapping.Wrap)
						.HorizontalAlignment(HorizontalAlignment.Center),
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

		var (environment, error) = CliEnvironment.Create(_arguments, _cwd);
		if (environment is null)
		{
			_banner.Text(Strings.StartupFailed(error ?? Strings.NoSession));
			_status.Text(error ?? Strings.NoSession);
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

		RefreshSessions();

		// MCP 服务器在后台连接：连上后把工具注入所有已打开的会话，设置面板同步显示状态。
		_ = ConnectMcpAsync();

		if (_sessions.Count > 0)
		{
			OpenSessionPath(_sessions[0].Path);
		}
		else if (preference.SidebarProjects)
		{
			NewSessionIn(_cwd);
		}
		else
		{
			// 首次启动停在「对话」：在聊天目录里开一个，而不是往当前项目里塞一个空会话。
			NewChatSession();
		}

		if (SmokeTab is { Length: > 0 } smokeTab)
		{
			// 外部工具（xdotool 等）往这个后端注入键盘/指针事件并不可靠，
			// 因此把「打开某个面板」做成启动参数，便于无人值守截图检查。
			if (string.Equals(smokeTab, "git", StringComparison.OrdinalIgnoreCase))
			{
				ShowGitPanel();
			}
			else
			{
				OpenSettingsDialog();
			}
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

	/// <summary>重建侧栏列表。目录快照始终包含全部会话，侧栏再按聊天目录分成「对话」与「项目」两段。</summary>
	private void RefreshSessions()
	{
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

		foreach (var node in nodes)
		{
			_sessionTree.Expand(node);
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

		try
		{
			OpenSessionPath(selected.Path);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Open session", ex);
			_banner.Text(Strings.OpenSessionFailed(ex.Message));
		}
	}

	/// <summary>「搜索会话」弹窗里选中的会话：按路径打开，已打开则切到那个标签。</summary>
	private void OpenSessionFromSearch(SessionListItem item)
	{
		_nameBox.Text = item.Name;

		try
		{
			OpenSessionPath(item.Path);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Open session", ex);
			_banner.Text(Strings.OpenSessionFailed(ex.Message));
		}
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
	private void RenameSelected()
	{
		var path = _renamingPath ?? SelectedSession()?.Path;
		if (path is null)
		{
			_banner.Text(Strings.SelectSessionFirst);
			return;
		}

		try
		{
			_catalog.Rename(path, _nameBox.Text?.Trim());
			_renamingPath = null;
			RefreshSessions();

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
			_banner.Text(Strings.OpenSessionFailed(ex.Message));
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
			RefreshSessions();
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
	private void OpenSessionPath(string path)
	{
		var resolved = Path.GetFullPath(path);
		var existing = _tabEntries.OfType<SessionTab>().FirstOrDefault(tab => tab.Session.Session.SessionFile == resolved);
		if (existing is not null)
		{
			_tabControl.SelectedIndex = _tabEntries.IndexOf(existing);
			existing.FocusComposer();
			return;
		}
		// 外部会话的头部可能没有 cwd；桌面用当前项目兜底，而不是守护进程的启动目录。
		OpenSession(_catalog.Open(resolved, _cwd));
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
		if (path is not null)
		{
			var existing = _tabEntries.OfType<SessionTab>().FirstOrDefault(tab => tab.Session.Session.SessionFile == path);
			if (existing is not null)
			{
				_tabControl.SelectedIndex = _tabEntries.IndexOf(existing);
				existing.FocusComposer();
				return;
			}
		}

		// 会话可能属于别的项目：为该工作目录准备（并缓存）一套环境，工具与会话一样以它的 cwd 为基准。
		var environment = EnvironmentFor(manager.Cwd);
		var extraTools = McpToolsFor(manager.Cwd);
		// 问询卡片需要标签的视图来定位窗口，因此先建宿主、构造会话，再把视图挂回去。
		var uiHost = new DesktopUiHost();
		var session = environment.CreateSession(
			_arguments, manager.Cwd, session: manager, extraTools: extraTools, recordInitialThinkingLevel: false, uiHost: uiHost);

		var tab = AttachTab(session);
		uiHost.Attach(tab);
		tab.UiHost = uiHost;
		AppLog.For<MainWindow>().ZLogInformation($"Session opened: session={session.Session.SessionId}");
		_tabControl.SelectedIndex = _tabEntries.Count - 1;
		UpdateTabHeaders();
		UpdateStatus();
		tab.FocusComposer();
	}

	/// <summary>为已有会话创建一个标签页。</summary>
	private SessionTab AttachTab(CodingAgentSession session)
	{
		// 选择器与会话沿用同一启动环境，避免热重载后列出当前会话尚不能调用的新端点。
		var environment = EnvironmentFor(session.Cwd);
		var tab = new SessionTab(session, environment.ListAvailableModels(_arguments.ApiKey), SessionTab.TitleFor(session),
			() => environment.ListAvailableModels(_arguments.ApiKey));
		tab.TitleChanged += _ => UpdateTabHeaders();
		tab.StateChanged += _ =>
		{
			UpdateTabHeaders();
			if (!tab.IsStreaming)
			{
				RefreshSessions();
			}
		};
		tab.Forked += _ => RefreshSessions();

		_tabEntries.Add(tab);
		AddTab(header: null!, content: tab.View, entry: tab);

		// 这个会话自己的右侧面板：文件 / Git / 终端都以它的工作目录为基准，默认收起。
		// 面板视图挂在共享宿主里，切换会话只切可见性，终端等状态不会丢。
		var panel = new RightPanel(() => session.Cwd);
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

	private void UpdateStatus()
	{
		var index = _tabControl.SelectedIndex;
		var entry = index >= 0 && index < _tabEntries.Count ? _tabEntries[index] : null;
		if (_lastSessionTab is { } previous && !ReferenceEquals(entry, previous)) previous.HideTransientPanels();

		_welcome.IsVisible(entry is null);
		_tabControl.IsVisible(entry is not null);
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
		await Task.Delay(1500).ConfigureAwait(false);

		Dispatch(() =>
		{
			switch (view)
			{
				case "hints":
					PrepareHoverSmoke();
					break;
				case "references":
					// 视觉验收入口：同时展示草稿引用条、历史文件 chip 和技能 chip。
					CurrentTab()?.SetExtensionText("请检查 @\"src/App.cs\" 和 @docs/。");
					Console.WriteLine("reference-ready");
					break;
				case "process":
					CurrentTab()?.PrepareProcessSmoke();
					break;
				case "interactions":
					CurrentTab()?.PrepareInteractionSmoke();
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
				case "git":
					ShowGitPanel();
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

		await Task.Delay(view == "interactions" ? 18000 : 7000).ConfigureAwait(false);
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
				var started = System.Diagnostics.Stopwatch.StartNew();
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
				Application.Shutdown();
			});
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
		var open = activePanel?.IsOpen == true;

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

		var name = preference.Theme switch
		{
			DesktopPreference.LightTheme => Strings.ThemeLight,
			DesktopPreference.SystemTheme => Strings.ThemeSystem,
			_ => Strings.ThemeDark,
		};

		_pendingNotice ??= Strings.ThemeChanged(name);

		if (Application.IsRunning)
		{
			DesktopTheme.Apply(DesktopTheme.Resolve(preference.Theme, _cwd, SessionPaths.GetAgentDir()));
		}
		_banner.Text(_pendingNotice);
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
			_settingsView ??= new SettingsView(Snapshot, ReloadConfiguration, ReconnectMcpAsync, ReloadResourcesAsync);
			_settingsDialog = new SettingsDialog(_settingsView);
			_settingsDialog.Closed += FocusActiveEntry;
		}

		_settingsDialog.Show(this);
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
			new(Strings.RefreshSessions, Strings.ShortcutRefresh, RefreshSessions),
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
			RefreshSessions();
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

	/// <summary>设置面板的数据快照。</summary>
	private SettingsSnapshot Snapshot() => new()
	{
		AgentDir = SessionPaths.GetAgentDir(),
		LoadArchived = () => _catalog.ListDeleted(),
		Cwd = CurrentSession()?.Cwd ?? _cwd,
		ModelCount = _models.Count,
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
			var connection = await McpServers.ConnectConfiguredAsync(
				_cwd,
				onError: message => Dispatch(() => _banner.Text($"MCP：{message}")),
				includeProject: _environment?.ProjectTrusted ?? true).ConfigureAwait(false);

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
