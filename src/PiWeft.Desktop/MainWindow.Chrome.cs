using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 自定义窗框：PiDeck 式的连续标题栏，左段属于侧栏，右段属于主区。
/// Windows 与 macOS 用 <see cref="Window.ExtendClientAreaTitleBarHeight"/> 把客户区延伸到原生标题栏：
/// 系统继续负责圆角、阴影、缩放与 macOS 的红绿灯；X11 没有等价能力，只能改为无边框窗口，
/// 于是移动、八向缩放与三个窗口按钮全部由应用绘制（<see cref="Window.DragMove"/> /
/// <see cref="Window.DragResize"/>）。标题栏左段与侧栏同色，右段与主区同色；页签条和窗口按钮
/// 都在右段，内容仍从标题栏下方开始。这样不新增全局工具栏，层次也贴近 PiDeck 的窗框。
public sealed partial class MainWindow
{
	private const double ShadowExtent = 12;

	private const double ShadowOffset = 2;

	/// 窗口级浮层的可见区域：跳过标题栏；X11 正常窗口还要跳过透明阴影边，
	/// 否则透明捕获层会盖住标题栏底部，或压出圆角窗框。最大化/全屏时没有阴影边，
	/// 浮层贴住窗口四边，否则四周留一圈亮边。
	internal static Thickness OverlayInsetFor(WindowState state)
	{
		if (UsesNativeWindowFrame || state is WindowState.Maximized or WindowState.FullScreen)
		{
			return new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		}

		return new Thickness(
			ShadowExtent,
			(ShadowExtent - ShadowOffset) + AppTheme.TitleBarHeight,
			ShadowExtent,
			ShadowExtent + ShadowOffset);
	}

	private const double TitleTabMaxWidth = 180;

	private TextBlock _tabCaption = null!;

	private Border _sidebarTitleBar = null!;

	private Border _mainTitleBar = null!;

	private DockPanel _sidebarColumn = null!;

	private TitleTabStrip _titleTabStrip = null!;

	private Button _showSidebarButton = null!;

	private Button _hideSidebarButton = null!;

	private Button _rightPanelButton = null!;

	private Image _rightPanelGlyph = null!;

	private UIElement _windowActionsOverlay = null!;

	private double _titleBarRightPadding;

	private double _panelHeaderRightInset = AppTheme.Space1;

	private bool? _rightPanelButtonOpen;

	private Border _chromeActionsSeparator = null!;

	private StackPanel _titleBarButtons = null!;

	private Button _minimizeButton = null!;

	private Button _maximizeButton = null!;

	private Button _closeButton = null!;

	private GlyphElement _maximizeGlyph = null!;

	private Border? _chromeBorder;

	private ShadowDecorator? _shadow;

	private readonly List<object> _titleBarTabEntries = [];

	private readonly List<TitleTabView> _titleTabViews = [];

	private int _titleBarSelectedIndex = -1;

	// 选中项按条目而不是索引认：关掉前面的标签会让索引变，选中的人没变。
	private object? _titleBarSelectedEntry;

	private Color? _nativeBorderColor;

	/// 是否保留系统原生窗框。X11 的扩展标题栏只去掉装饰，保不住原生按钮，
	/// 因此只有 Win32/macOS 走原生扩展路径。
	private static bool UsesNativeWindowFrame =>
#if MEWUI_WIN32 || MEWUI_MACOS
		true;
#else
		false;
#endif

	private void ConfigureWindowFrame()
	{
		if (UsesNativeWindowFrame)
		{
			this.ExtendClientAreaTitleBarHeight(AppTheme.TitleBarHeight);
		}
		else
		{
			// X11 没有原生窗框：用 ARGB 窗口 + 应用内圆角/边界/落影还原 PiDeck 的窗口质感；
			// 后端同时会在窗口外侧 12 DIP 内提供透明缩放边与光标。
			this.AllowsTransparency(true);
			this.Borderless(true);
			this.Background = AppTheme.Transparent;
		}
	}

	/// 窗口模板只负责外框、内容槽与右上角的窗口按钮浮层；标题栏属于侧栏/主区/面板各列，
	/// 由 BuildUi 组装。
	private UIElement BuildChromeShell()
	{
		// 窗口按钮浮在内容之上：右侧面板展开时它们落在面板页签条的右端，
		// 收起时落在主标题栏右端，与 PiDeck 一样属于窗框，不属于某一列。
		var shell = new Grid()
			.Children(new ContentPresenter(), _windowActionsOverlay);

		UIElement root;
		if (UsesNativeWindowFrame)
		{
			root = new Border().StyleName(AppStyles.Window).Child(shell);
		}
		else
		{
			// 圆角 Border 裁剪标题栏/侧栏/主区；ShadowDecorator 在外侧留出透明阴影边距。
			_chromeBorder = new Border().StyleName(AppStyles.WindowFrame).Child(shell);
			_shadow = new ShadowDecorator()
			{
				BlurRadius = ShadowExtent,
				OffsetY = ShadowOffset,
				CornerRadius = AppTheme.RadiusLg,
				Child = _chromeBorder,
			}.WithTheme((theme, shadow) => shadow.ShadowColor = theme.IsDark
				? new Color(0xB4, 0x00, 0x00, 0x00)
				: new Color(0x3C, 0x00, 0x00, 0x00));
			root = _shadow;
		}

		// 原生能力要等后端挂载后才可查询，因此统一在加载、尺寸与状态变化时刷新一次。
		this.Loaded += UpdateChrome;
		this.ClientSizeChanged += _ => UpdateChrome();
		this.WindowStateChanged += _ => UpdateChrome();
		UpdateChrome();
		SyncTitleBarTabs(force: true);

		return root;
	}

	private UIElement BuildSidebarTitleBar()
	{
		// 项目铭牌是标签，不当元数据：用 UI 字体、次要文字色，完整路径留在悬停提示里。
		_tabCaption = new TextBlock()
			.Text(ShortCwd(_cwd))
			.FontSize(AppTheme.FontSizeCaption)
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.MutedText()
			.VerticalAlignment(VerticalAlignment.Center);
		_tabCaption.HoverTip(_cwd);

		// 品牌是「织机」的门牌：应用图标 + 加粗字，和正文保持同一套材质。
		var mark = new Image()
			.Source(AppIcons.Brand())
			.Width(16)
			.Height(16)
			.VerticalAlignment(VerticalAlignment.Center);

		var brand = new TextBlock()
			.Text(Strings.BrandShort)
			.FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.Bold)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center);

		var brandZone = new Border()
			.Child(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(mark, brand));

		_hideSidebarButton = BuildChromeIconButton("panel-left", Strings.ToggleSidebar, ToggleSidebar);
		_hideSidebarButton.Margin(new Thickness(10, 0, 10, 0));

		var captionZone = new Border().Child(_tabCaption);

		_sidebarTitleBar = new Border()
			.StyleName(AppStyles.TitleBarSidebar)
			.Height(AppTheme.TitleBarHeight)
			.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space2, 0))
			.Child(new Grid()
				.Columns("auto,auto,*")
				.Children(
					brandZone.Column(0),
					_hideSidebarButton.Column(1),
					captionZone.Column(2)));

		// 整条标题栏都是拖动区；页签、按钮等可交互子元素会先吃掉自己的按下事件。
		AttachTitleBarDrag(_sidebarTitleBar);
		return _sidebarTitleBar;
	}

	private UIElement BuildMainTitleBar()
	{
		// 侧栏收起后，panel 键回到主标题栏最左侧（PiDeck 的位置），右段不再重复放一个。
		_showSidebarButton = BuildChromeIconButton("panel-left", Strings.ToggleSidebar, ToggleSidebar);
		_showSidebarButton.Margin(new Thickness(6, 0, 4, 0)).IsVisible(false);

		_titleTabStrip = new TitleTabStrip();

		var newTabButton = BuildChromeGlyphButton(
			GlyphKind.Plus,
			Strings.NewSession,
			NewSessionFromCurrentView);
		newTabButton.Margin(new Thickness(4, 0, 0, 0)).VerticalAlignment(VerticalAlignment.Center);

		// 页签条超出宽度时横向滚动；加号固定在右端，页签再多也够得到。
		// 页签与按钮之外的空白仍属于标题栏拖动区（ScrollViewer 自己不拦左键）。
		var tabArea = new Grid { ClipToBounds = true }
			.Columns("*,Auto")
			.Children(_titleTabStrip.View.Column(0), newTabButton.Column(1));

		// 右段工具组：唤醒键 + 命令入口。窗口按钮在窗框顶层（BuildWindowActionsOverlay），
		// 面板展开时它们落在面板页签右端，收起时落在主标题栏右端。
		_rightPanelButton = BuildRightPanelButton();
		var palette = AppIcons.Button("ellipsis", Strings.PaletteButton, () => _palette?.Toggle(this), size: AppIcons.Size);
		palette.Width(32).Height(28).Padding(Thickness.Zero)
			.StyleName(AppStyles.TitleBarIconButton)
			.VerticalAlignment(VerticalAlignment.Center);

		// 唤醒键放在最右：面板展开时主标题栏止于面板左缘，它就贴在分界上。
		var utilities = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(palette, _rightPanelButton);

		var titleRow = new Grid()
			.Columns("auto,*,auto")
			.Children(
				_showSidebarButton.Column(0),
				tabArea.Column(1),
				utilities.Column(2));

		_mainTitleBar = new Border()
			.StyleName(AppStyles.TitleBarMain)
			.Height(AppTheme.TitleBarHeight)
			.Padding(new Thickness(AppTheme.Space2, 0, 0, 0))
			.Child(titleRow);

		AttachTitleBarDrag(_mainTitleBar);
		SyncTitleBarTabs(force: true);
		return _mainTitleBar;
	}

	private Button BuildChromeIconButton(string icon, string tip, Action onClick)
	{
		var button = AppIcons.Button(icon, tip, onClick, size: AppIcons.Size);
		button.Width(28).Height(28).Padding(Thickness.Zero)
			.StyleName(AppStyles.TitleBarIconButton)
			.VerticalAlignment(VerticalAlignment.Center);
		return button;
	}

	/// 右侧面板的唤醒键：图标自己持有，打开态换主文字色 + 常驻浮起，
	/// 提示文字随开关变成「打开 / 关闭右侧边栏」并带上当前快捷键。
	private Button BuildRightPanelButton()
	{
		_rightPanelGlyph = new Image()
			.Source(AppIcons.Get("panel-right", AppTheme.TextMuted))
			.Width(AppIcons.Size)
			.Height(AppIcons.Size)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center);

		return new Button()
			.Content(_rightPanelGlyph)
			.Width(28)
			.Height(28)
			.Padding(Thickness.Zero)
			.StyleName(AppStyles.TitleBarIconButton)
			.VerticalAlignment(VerticalAlignment.Center)
			.OnClick(ToggleRightPanel)
			.IsVisible(false);
	}

	/// 窗口按钮浮层：贴在窗口右上角、盖在内容之上。右侧面板展开时它落在面板页签条右端
	/// （页签右侧预留同样宽度），收起时落在主标题栏右端；位置与面板开关无关，不需要重排。
	private UIElement BuildWindowActionsOverlay()
	{
		_chromeActionsSeparator = new Border()
			.StyleName(AppStyles.TitleBarSeparator)
			.Width(1)
			.Height(16)
			.Margin(new Thickness(6, 0, 6, 0))
			.VerticalAlignment(VerticalAlignment.Center);

		_minimizeButton = BuildWindowButton(GlyphKind.WindowMinimize, Strings.MinimizeWindow, Minimize);
		_maximizeGlyph = new GlyphElement().Kind(GlyphKind.WindowMaximize).GlyphSize(4);
		_maximizeButton = BuildWindowButton(_maximizeGlyph, Strings.MaximizeWindow, ToggleMaximize);
		_closeButton = BuildWindowButton(GlyphKind.Cross, Strings.CloseWindow, Close, isClose: true);

		_titleBarButtons = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.VerticalAlignment(VerticalAlignment.Stretch)
			.Children(_minimizeButton, _maximizeButton, _closeButton);

		return new StackPanel()
			.Orientation(Orientation.Horizontal)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.VerticalAlignment(VerticalAlignment.Top)
			.Height(AppTheme.TitleBarHeight)
			.Children(_chromeActionsSeparator, _titleBarButtons);
	}

	/// 同步唤醒键：没有会话时不占位；展开时呈强调弱底。位置由布局决定；
	/// 它是主标题栏右端的最后一项，面板展开时主标题栏变窄，它自然停在面板左缘。
	private void UpdateRightPanelButton()
	{
		if (_rightPanelButton is null || _rightPanelGlyph is null || _tabControl is null)
		{
			return;
		}

		var open = _panelHost?.IsVisible == true;
		_rightPanelButton.IsVisible(CurrentTab() is not null);

		if (_rightPanelButtonOpen != open)
		{
			_rightPanelButtonOpen = open;
			_rightPanelButton.StyleName(open ? AppStyles.TitleBarIconButtonActive : AppStyles.TitleBarIconButton);
			_rightPanelGlyph.Source(AppIcons.Get("panel-right", open ? AppTheme.TextPrimary : AppTheme.TextMuted));
		}

		var action = open ? Strings.CloseRightPanel : Strings.OpenRightPanel;
		HoverHints.Set(_rightPanelButton, _shortcuts.Get(ShortcutActions.RightPanel) is { } binding
			? $"{action} · {binding}"
			: action);
	}

	private Button BuildChromeGlyphButton(GlyphKind kind, string tip, Action onClick)
	{
		var glyph = new GlyphElement()
			.Kind(kind)
			.GlyphSize(4)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center);

		return new Button()
			.Content(glyph)
			.Width(28)
			.Height(28)
			.Padding(Thickness.Zero)
			.StyleName(AppStyles.TitleBarIconButton)
			.VerticalAlignment(VerticalAlignment.Center)
			.OnClick(onClick)
			.HoverTip(tip);
	}

	private Button BuildWindowButton(GlyphKind kind, string tip, Action onClick, bool isClose = false)
		=> BuildWindowButton(
			new GlyphElement().Kind(kind).GlyphSize(4),
			tip,
			onClick,
			isClose);

	private Button BuildWindowButton(GlyphElement glyph, string tip, Action onClick, bool isClose = false)
	{
		glyph.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center);

		return new Button()
			.Content(glyph)
			.Width(AppTheme.TitleBarButtonWidth)
			.Height(AppTheme.TitleBarHeight)
			.Padding(Thickness.Zero)
			.StyleName(isClose ? AppStyles.TitleBarCloseButton : AppStyles.TitleBarButton)
			.OnClick(onClick)
			.HoverTip(tip);
	}

	/// 标题栏页签的签名检查：条目变化才重建，标题与选中项在原控件上更新，
	/// 流式状态刷新不会换掉按钮，也不会丢焦点和悬停提示。
	private void SyncTitleBarTabs(bool force = false)
	{
		if (_titleTabStrip is null)
		{
			return;
		}

		var selected = _tabControl?.SelectedIndex ?? -1;
		var sameEntries = _titleBarTabEntries.Count == _tabEntries.Count;
		if (sameEntries)
		{
			for (var index = 0; index < _tabEntries.Count; index++)
			{
				if (!ReferenceEquals(_titleBarTabEntries[index], _tabEntries[index]))
				{
					sameEntries = false;
					break;
				}
			}
		}

		var selectionChanged = selected != _titleBarSelectedIndex;
		var selectedEntry = selected >= 0 && selected < _tabEntries.Count ? _tabEntries[selected] : null;
		var entryChanged = !ReferenceEquals(selectedEntry, _titleBarSelectedEntry);

		if (!force && sameEntries)
		{
			// 标题变化（子代理等待圆点、会话标题）只改文字：不重建按钮，宽度变化交给布局。
			var selectedTitleChanged = UpdateTitleBarTabTitles(selected);
			if (selectionChanged)
			{
				_titleBarSelectedIndex = selected;
				_titleBarSelectedEntry = selectedEntry;
				UpdateTitleBarTabSelection(selected);
			}
			else if (selectedTitleChanged && selected >= 0 && selected < _titleTabViews.Count)
			{
				// 当前页签变宽（标题从「新会话」变成任务名）可能把行尾关闭键挤出视口。
				_titleTabStrip.Reveal(_titleTabViews[selected].Button);
			}

			return;
		}

		// 条目换了（新开/关闭/重排）：整条重建，不能把读者手动滚到的位置抢回去。
		// 只有选中项换了一个条目才把当前页签滚进视口；关掉别的标签只让索引变化，
		// 读者看到的位置保持不动。
		var reveal = force || entryChanged;
		_titleBarTabEntries.Clear();
		_titleBarTabEntries.AddRange(_tabEntries);
		_titleBarSelectedIndex = selected;
		_titleBarSelectedEntry = selectedEntry;
		RebuildTitleBarTabs(selected, reveal || _titleTabStrip.HasPendingReveal);
	}

	/// 标题与状态各写各的：状态（目标轮次、子代理数）与标题无关，靠标题变化的顺风车会让
	/// 「目标 1/32」停在旧值。返回值表示当前页签的标题是否变了宽度。
	private bool UpdateTitleBarTabTitles(int selected)
	{
		var selectedChanged = false;
		for (var index = 0; index < _titleTabViews.Count; index++)
		{
			var view = _titleTabViews[index];
			var entry = _tabEntries[index];
			var title = EntryTitle(entry);
			var titleChanged = !string.Equals(view.Label.Text, title, StringComparison.Ordinal);
			if (titleChanged)
			{
				view.Label.Text(title);
			}

			if (!titleChanged && !UpdateTitleBarTabStatus(view, EntryStatus(entry)))
			{
				continue;
			}

			HoverHints.Set(view.Button, EntryTooltip(entry));
			selectedChanged |= titleChanged && index == selected;
		}

		return selectedChanged;
	}

	/// 状态点与状态字：文案与可见性都没变就不碰控件。
	/// 状态字唯一决定角色，所以颜色跟着文字一起换，不必单独比较。
	private static bool UpdateTitleBarTabStatus(TitleTabView view, TitleTabStatus? status)
	{
		var hasStatus = status is not null;
		var changed = false;
		if (view.StatusDot.IsVisible != hasStatus)
		{
			view.StatusDot.IsVisible(hasStatus);
			changed = true;
		}

		if (view.StatusText.IsVisible != hasStatus)
		{
			view.StatusText.IsVisible(hasStatus);
			changed = true;
		}

		if (status is not { } active)
		{
			return changed;
		}

		if (!string.Equals(view.StatusText.Text, active.Text, StringComparison.Ordinal))
		{
			view.StatusText.Text(active.Text);
			changed = true;
		}

		if (changed)
		{
			view.StatusDot.Background(AppStyles.ColorForRole(active.Role));
			view.StatusText.Role(active.Role);
		}

		return changed;
	}

	private void RebuildTitleBarTabs(int selected, bool reveal)
	{
		_titleTabStrip.Clear();
		_titleTabViews.Clear();

		for (var index = 0; index < _tabEntries.Count; index++)
		{
			var view = BuildTitleTab(_tabEntries[index], index == selected);
			_titleTabViews.Add(view);
			_titleTabStrip.Add(view.Button);
		}

		if (reveal && selected >= 0 && selected < _titleTabViews.Count)
		{
			_titleTabStrip.Reveal(_titleTabViews[selected].Button);
		}
	}

	private void UpdateTitleBarTabSelection(int selected)
	{
		for (var index = 0; index < _titleTabViews.Count; index++)
		{
			var view = _titleTabViews[index];
			var active = index == selected;
			view.Button.StyleName(active ? AppStyles.TitleBarTabActive : AppStyles.TitleBarTab);
			view.Close.IsVisible(active);
		}

		// 快捷键 Ctrl+1…9 可能选中被滚出视口的页签；切完把它滚回来。
		if (selected >= 0 && selected < _titleTabViews.Count)
		{
			_titleTabStrip.Reveal(_titleTabViews[selected].Button);
		}
	}

	private TitleTabView BuildTitleTab(object entry, bool active)
	{
		var title = EntryTitle(entry);
		var label = new TextBlock()
			.Text(title)
			.FontSize(AppTheme.FontSizeCaption)
			.MaxWidth(TitleTabMaxWidth)
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);

		var status = EntryStatus(entry);
		var dot = new Border()
			.Width(6)
			.Height(6)
			.CornerRadius(3)
			.VerticalAlignment(VerticalAlignment.Center);
		dot.IsVisible(status is not null);
		if (status is { } initialStatus)
		{
			dot.Background(AppStyles.ColorForRole(initialStatus.Role));
		}

		var statusText = new TextBlock()
			.FontSize(AppTheme.FontSizeMicro)
			.Role(status?.Role ?? AppStyles.RoleSubtle)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		statusText.Text(status?.Text ?? string.Empty);
		statusText.IsVisible(status is not null);

		var close = BuildTitleTabClose(entry);
		close.IsVisible(active);

		var row = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(4)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(dot, label, statusText, close);

		var tab = new Button()
			.Content(row)
			.Height(28)
			.Padding(new Thickness(10, 0, 8, 0))
			.StyleName(active ? AppStyles.TitleBarTabActive : AppStyles.TitleBarTab)
			.OnClick(() => SelectEntry(entry))
			.HoverTip(EntryTooltip(entry));

		// 右键沿用命令面板里的同一组标签动作（关闭/保留/右移…）。
		tab.OnMouseDown(e =>
		{
			Diagnostics.NoteInput();
			if (e.Button == MouseButton.Right)
			{
				TabMenu(tab, entry).Show(tab);
				e.Handled = true;
			}
		});

		return new TitleTabView(tab, close, label, dot, statusText);
	}

	// 五个引用的小视图元组：每次重建按值放进列表，不再为每个页签多建一个对象。
	private readonly record struct TitleTabView(
		Button Button,
		Button Close,
		TextBlock Label,
		Border StatusDot,
		TextBlock StatusText);

	private Button BuildTitleTabClose(object entry)
	{
		var glyph = new GlyphElement()
			.Kind(GlyphKind.Cross)
			.GlyphSize(3.5)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center);

		return new Button()
			.Content(glyph)
			.Width(18)
			.Height(18)
			.Padding(Thickness.Zero)
			.StyleName(AppStyles.TitleBarTabClose)
			.OnClick(() => CloseEntry(entry))
			.HoverTip(Strings.CloseCurrentTab);
	}

	private void SelectEntry(object entry)
	{
		var index = _tabEntries.IndexOf(entry);
		if (index < 0)
		{
			return;
		}

		if (_tabControl.SelectedIndex != index)
		{
			// TabControl 的 SelectionChanged 已经跑过一遍 UpdateStatus，这里再调会把页签条扫第二遍。
			_tabControl.SelectedIndex = index;
			return;
		}

		UpdateStatus();
	}

	/// 页签悬停：标题之外再说明 ◐ 是哪一种运行，避免计划与目标共用一个标记后猜。
	internal static string EntryTooltip(object entry) => entry switch
	{
		SessionTab tab when tab.AskPending => $"{tab.Title}\n{Strings.TabWaitingAnswer}",
		SessionTab tab when tab.HasAwaitingSubagent => $"{tab.Title}\n{Strings.TabSubagentAwaiting}",
		SessionTab tab when tab.Session.Goal.Running =>
			$"{tab.Title}\n{Strings.TabGoalRunning(tab.Session.Goal.RoundsStarted, tab.Session.Goal.MaxRounds)}",
		SessionTab tab when tab.Session.PlanExecutionEnabled => $"{tab.Title}\n{Strings.TabPlanRunning}",
		SessionTab tab when tab.RunningSubagentCount > 0 =>
			$"{tab.Title}\n{Strings.TabSubagentsRunning(tab.RunningSubagentCount)}",
		SessionTab tab when tab.IsStreaming => $"{tab.Title}\n{Strings.TabRunning}",
		SessionTab tab => tab.Title,
		_ => EntryTitle(entry),
	};

	/// 页签上的状态：等待回答优先，其次目标、计划、子代理、正在跑。
	/// 返回 null 时不画状态点与状态字。
	internal static TitleTabStatus? EntryStatus(object entry) => entry switch
	{
		SessionTab tab when tab.AskPending || tab.HasAwaitingSubagent =>
			new TitleTabStatus(AppStyles.RoleWarning, Strings.TabWaitingAnswerShort),
		SessionTab tab when tab.Session.Goal.Running =>
			new TitleTabStatus(AppStyles.RoleInfo,
				Strings.TabGoalShort(tab.Session.Goal.RoundsStarted, tab.Session.Goal.MaxRounds)),
		SessionTab tab when tab.Session.PlanExecutionEnabled =>
			new TitleTabStatus(AppStyles.RoleInfo, Strings.TabPlanShort),
		SessionTab tab when tab.RunningSubagentCount > 0 =>
			new TitleTabStatus(AppStyles.RoleInfo, Strings.TabSubagentsShort(tab.RunningSubagentCount)),
		SessionTab tab when tab.IsStreaming =>
			new TitleTabStatus(AppStyles.RoleInfo, Strings.TabRunning),
		_ => null,
	};

	internal readonly record struct TitleTabStatus(string Role, string Text);

	internal static string EntryTitle(object entry) => entry switch
	{
		// 状态不再拼进标题：页签用状态点 + 状态字，标题保持可搜索、可读。
		SessionTab tab => tab.Title,
		_ => string.Empty,
	};

	/// 标题栏的空白处拖动窗口，双击最大化/还原。macOS 的原生拖动会进入 AppKit 的事件循环，
	/// 第二次按下时不能再启动，否则双击事件收不到，因此只让单击启动拖动（与 MewUI 示例一致）。
	private void AttachTitleBarDrag(UIElement zone)
	{
		zone.OnMouseDown(e =>
		{
			if (e.Button == MouseButton.Left && e.ClickCount == 1)
			{
				DragMove();
				e.Handled = true;
			}
		});

		zone.OnMouseDoubleClick(e =>
		{
			if (e.Button != MouseButton.Left)
			{
				return;
			}

			ToggleMaximize();
			e.Handled = true;
		});
	}

	private void ToggleMaximize()
	{
		if (!CanMaximize)
		{
			return;
		}

		if (WindowState == WindowState.Maximized)
		{
			Restore();
		}
		else
		{
			Maximize();
		}
	}

	/// 按平台能力刷新窗框：原生有按钮（macOS）就隐藏自绘按钮并让出红绿灯的位置；
	/// 否则显示三个自绘按钮，并让它们贴住右缘。
	private void UpdateChrome()
	{
		if (_mainTitleBar is null || _sidebarTitleBar is null)
		{
			return;
		}

		var nativeButtons = HasNativeChromeButtons;
		var inset = NativeChromeButtonInset;
		var sidebarVisible = _sidebarColumn?.IsVisible == true;

		_titleBarButtons.IsVisible(!nativeButtons);
		_chromeActionsSeparator.IsVisible(!nativeButtons);
		_minimizeButton.IsVisible(CanMinimize);
		_maximizeButton.IsVisible(CanMaximize);
		_closeButton.IsVisible(CanClose);

		// 右上角窗口按钮浮层的宽度：自绘 = 分隔条 + 可见按钮；原生 = 系统 inset（macOS 只留右缘空白）。
		var buttonCount = (CanMinimize ? 1 : 0) + (CanMaximize ? 1 : 0) + (CanClose ? 1 : 0);
		var overlayWidth = nativeButtons
			? inset.Right + AppTheme.Space3
			: 6 + 1 + 6 + (AppTheme.TitleBarButtonWidth * buttonCount);
		var panelOpen = _panelHost?.IsVisible == true;

		// macOS 的红绿灯固定在窗口左上角：侧栏可见时由左段让位，收起后由右段让位。
		_sidebarTitleBar.Padding(new Thickness(
			AppTheme.Space3 + inset.Left,
			0,
			AppTheme.Space2,
			0));

		// 面板收起时主标题栏铺到窗口右缘，要给浮层让位；展开时止于面板左缘，
		// 只留 4px 让唤醒键避开分隔条向主区伸出的命中区。
		_titleBarRightPadding = panelOpen ? AppTheme.Space1 : overlayWidth;
		_mainTitleBar.Padding(new Thickness(
			AppTheme.Space2 + (sidebarVisible ? 0 : inset.Left),
			0,
			_titleBarRightPadding,
			0));

		// 面板页签条右侧同样给浮层让位（浮层盖在面板列右上角）。
		_panelHeaderRightInset = overlayWidth + AppTheme.Space1;
		foreach (var panel in _sessionPanels.Values)
		{
			panel.SetHeaderRightInset(_panelHeaderRightInset);
		}

		var maximized = WindowState == WindowState.Maximized;
		_maximizeGlyph.Kind(maximized ? GlyphKind.WindowRestore : GlyphKind.WindowMaximize);
		HoverHints.Set(_maximizeButton, maximized ? Strings.RestoreWindow : Strings.MaximizeWindow);

		UpdateTransparentFrame(WindowState == WindowState.Normal);
		SyncNativeBorderColor();

		UpdateRightPanelButton();
	}

	/// X11 透明窗框：普通态有 10 DIP 圆角、1px 边界与 12 DIP 落影；
	/// 最大化/全屏时全部归零，圆角窗框铺满屏幕。原生窗框平台不做任何事。
	private void UpdateTransparentFrame(bool normal)
	{
		if (_chromeBorder is null || _shadow is null)
		{
			return;
		}

		_chromeBorder.CornerRadius = normal ? AppTheme.RadiusLg : 0;
		_chromeBorder.BorderThickness = normal ? 1 : 0;
		_shadow.BlurRadius = normal ? ShadowExtent : 0;
		_shadow.OffsetY = normal ? ShadowOffset : 0;
		_shadow.CornerRadius = normal ? AppTheme.RadiusLg : 0;
	}

	private void SyncNativeBorderColor()
	{
		if (Handle == 0 || !ChromeCapabilities.HasFlag(WindowChromeCapabilities.NativeBorderColor))
		{
			return;
		}

		var color = AppTheme.Border;
		if (_nativeBorderColor == color)
		{
			return;
		}

		_nativeBorderColor = color;
		SetWindowBorderColor(color);
	}
}
