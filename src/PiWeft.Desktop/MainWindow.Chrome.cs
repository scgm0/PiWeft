using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 自定义窗框：PiDeck 式的连续标题栏，左段属于侧栏，右段属于主区。
/// </summary>
/// <remarks>
/// Windows 与 macOS 用 <see cref="Window.ExtendClientAreaTitleBarHeight"/> 把客户区延伸到原生标题栏：
/// 系统继续负责圆角、阴影、缩放与 macOS 的红绿灯；X11 没有等价能力，只能改为无边框窗口，
/// 于是移动、八向缩放与三个窗口按钮全部由应用绘制（<see cref="Window.DragMove"/> /
/// <see cref="Window.DragResize"/>）。标题栏左段与侧栏同色、右段与主区同色，页签条和窗口按钮
/// 都在右段里，内容仍从标题栏下方开始 —— 这样既不新增一条全局工具栏，又比一条横贯全宽的
/// 中性色顶栏更贴近 PiDeck 的窗框层次。
/// </remarks>
public sealed partial class MainWindow
{
	/// <summary>X11 透明窗框的阴影模糊半径；同时是窗口外侧留给阴影的边距。</summary>
	private const double ShadowExtent = 12;

	/// <summary>阴影向下偏移，让窗口在桌面上有轻微悬浮感。</summary>
	private const double ShadowOffset = 2;

	/// <summary>
	/// 窗口级浮层的可见区域：跳过标题栏；X11 还要跳过透明阴影边，
	/// 否则浮层的透明捕获层会盖住标题栏底部或压出圆角窗框。
	/// </summary>
	internal static Thickness OverlayInset => UsesNativeWindowFrame
		? new Thickness(0, AppTheme.TitleBarHeight, 0, 0)
		: new Thickness(
			ShadowExtent,
			(ShadowExtent - ShadowOffset) + AppTheme.TitleBarHeight,
			ShadowExtent,
			ShadowExtent + ShadowOffset);

	/// <summary>标题栏页签文本的最大宽度：长会话名在页签里省略，鼠标悬停看全名。</summary>
	private const double TitleTabMaxWidth = 180;

	private TextBlock _tabCaption = null!;

	/// <summary>标题栏左段：品牌、侧栏折叠键、当前项目铭牌。</summary>
	private Border _sidebarTitleBar = null!;

	/// <summary>标题栏右段：页签条、新建入口、命令入口与窗口按钮。</summary>
	private Border _mainTitleBar = null!;

	private DockPanel _sidebarColumn = null!;

	private StackPanel _titleTabs = null!;

	private Button _showSidebarButton = null!;

	private Button _hideSidebarButton = null!;

	/// <summary>命令入口与窗口按钮之间的发丝线；系统自带窗口按钮时隐藏。</summary>
	private Border _chromeActionsSeparator = null!;

	private StackPanel _titleBarButtons = null!;

	private Button _minimizeButton = null!;

	private Button _maximizeButton = null!;

	private Button _closeButton = null!;

	private GlyphElement _maximizeGlyph = null!;

	/// <summary>X11 的圆角窗框（背景、1px 边界、裁剪与 ShadowDecorator）；原生窗框时为空。</summary>
	private Border? _chromeBorder;

	/// <summary>X11 窗口落影；最大化/全屏时收为 0，窗框填满屏幕。</summary>
	private ShadowDecorator? _shadow;

	/// <summary>页签签名：避免流式状态变化时无谓地重建页签、抢走键盘焦点。</summary>
	private readonly List<object> _titleBarTabEntries = [];

	/// <summary>当前页签控件，只更新样式而不重建，鼠标/键盘焦点在切换标签时不会丢。</summary>
	private readonly List<TitleTabView> _titleTabViews = [];

	private string _titleBarTabTitles = string.Empty;

	private int _titleBarSelectedIndex = -1;

	/// <summary>最近一次写入 DWM 的边框色，避免缩放过程中重复调用系统 API。</summary>
	private Color? _nativeBorderColor;

	/// <summary>
	/// 是否保留系统原生窗框。X11 的扩展标题栏只是「去掉装饰」，无法保留原生按钮，
	/// 所以只有 Win32/macOS 走原生扩展路径。
	/// </summary>
	private static bool UsesNativeWindowFrame =>
#if MEWUI_WIN32 || MEWUI_MACOS
		true;
#else
		false;
#endif

	/// <summary>在创建原生窗口之前决定窗框形态。</summary>
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

	/// <summary>
	/// 窗口模板只负责外框与内容槽；标题栏作为侧栏/主区两列的一部分由 BuildUi 组装。
	/// </summary>
	private UIElement BuildChromeShell()
	{
		var shell = new DockPanel().LastChildFill()
			.Children(new ContentPresenter());

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

	/// <summary>标题栏左段：品牌门牌 + 收起侧栏 + 当前项目。</summary>
	private UIElement BuildSidebarTitleBar()
	{
		// 项目铭牌是标签而不是元数据：用 UI 字体、次要文字色，完整路径留在悬停提示里。
		_tabCaption = new TextBlock()
			.Text(ShortCwd(_cwd))
			.FontSize(AppTheme.FontSizeCaption)
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.MutedText()
			.VerticalAlignment(VerticalAlignment.Center);
		_tabCaption.HoverTip(_cwd);

		// 品牌是「织机」的门牌：2px 白色纬线 + 等宽字，和正文保持同一套材质。
		var thread = new Border().StyleName(AppStyles.TitleBarThread)
			.Width(AppTheme.ThreadWidth)
			.Height(14)
			.VerticalAlignment(VerticalAlignment.Center);

		var brand = new TextBlock()
			.Text(Strings.BrandShort)
			.FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.SemiBold)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center);

		var brandZone = new Border()
			.Child(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(thread, brand));

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

	/// <summary>标题栏右段：页签条 + 新建 + 命令入口 + 窗口按钮。</summary>
	private UIElement BuildMainTitleBar()
	{
		// 侧栏收起后，panel 键回到主标题栏最左侧（PiDeck 的位置），右段不再重复放一个。
		_showSidebarButton = BuildChromeIconButton("panel-left", Strings.ToggleSidebar, ToggleSidebar);
		_showSidebarButton.Margin(new Thickness(6, 0, 4, 0)).IsVisible(false);

		_titleTabs = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center);

		var newTabButton = BuildChromeGlyphButton(
			GlyphKind.Plus,
			Strings.NewSession,
			NewSessionFromCurrentView);
		newTabButton.Margin(new Thickness(4, 0, 0, 0));

		var tabsRow = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(_titleTabs, newTabButton);

		// 页签与按钮之外的空白仍属于标题栏拖动区；页签过多时裁切，避免压到窗口按钮。
		var tabArea = new Grid { ClipToBounds = true }.Children(tabsRow);

		// 右段工具组只保留命令入口；侧栏开关在左段品牌旁 / 收起后的最左侧。
		var palette = AppIcons.Button("ellipsis", Strings.PaletteButton, () => _palette?.Toggle(this), size: AppIcons.Size);
		palette.Width(32).Height(28).Padding(Thickness.Zero)
			.StyleName(AppStyles.TitleBarIconButton)
			.VerticalAlignment(VerticalAlignment.Center);

		var utilities = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(palette);

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

		var actions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.VerticalAlignment(VerticalAlignment.Stretch)
			.Children(utilities, _chromeActionsSeparator, _titleBarButtons);

		_mainTitleBar = new Border()
			.StyleName(AppStyles.TitleBarMain)
			.Height(AppTheme.TitleBarHeight)
			.Padding(new Thickness(AppTheme.Space2, 0, 0, 0))
			.Child(new Grid()
				.Columns("auto,*,auto")
				.Children(
					_showSidebarButton.Column(0),
					tabArea.Column(1),
					actions.Column(2)));

		AttachTitleBarDrag(_mainTitleBar);
		SyncTitleBarTabs(force: true);
		return _mainTitleBar;
	}

	/// <summary>标题栏里的小图标按钮：28×28 的悬停块，不撑满整行。</summary>
	private Button BuildChromeIconButton(string icon, string tip, Action onClick)
	{
		var button = AppIcons.Button(icon, tip, onClick, size: AppIcons.Size);
		button.Width(28).Height(28).Padding(Thickness.Zero)
			.StyleName(AppStyles.TitleBarIconButton)
			.VerticalAlignment(VerticalAlignment.Center);
		return button;
	}

	/// <summary>标题栏里的形状按钮（+）：与窗口按钮同一套 Glyph 绘制，跟随前景色。</summary>
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

	/// <summary>
	/// 标题栏页签的签名检查：只有标签集合、标题或选中项变化时才重建，流式状态刷新不会打断焦点。
	/// </summary>
	private void SyncTitleBarTabs(bool force = false)
	{
		if (_titleTabs is null)
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

		var titles = string.Join('\u001f', _tabEntries.Select(EntryTitle));
		var titlesChanged = titles != _titleBarTabTitles;
		var selectionChanged = selected != _titleBarSelectedIndex;
		if (!force && sameEntries && !titlesChanged && !selectionChanged)
		{
			return;
		}

		// 只有选中项变化：原位更新样式，不重建控件，鼠标/键盘焦点留在页签上。
		if (!force && sameEntries && !titlesChanged)
		{
			_titleBarSelectedIndex = selected;
			UpdateTitleBarTabSelection(selected);
			return;
		}

		_titleBarTabEntries.Clear();
		_titleBarTabEntries.AddRange(_tabEntries);
		_titleBarTabTitles = titles;
		_titleBarSelectedIndex = selected;
		RebuildTitleBarTabs(selected);
	}

	/// <summary>按 _tabEntries 重建页签控件；只在标签集合或标题变化时调用。</summary>
	private void RebuildTitleBarTabs(int selected)
	{
		_titleTabs.Clear();
		_titleTabViews.Clear();

		for (var index = 0; index < _tabEntries.Count; index++)
		{
			var view = BuildTitleTab(_tabEntries[index], index == selected);
			_titleTabViews.Add(view);
			_titleTabs.Add(view.Button);
		}
	}

	/// <summary>活动态 / 非活动态切换：只改样式与关闭键可见性。</summary>
	private void UpdateTitleBarTabSelection(int selected)
	{
		for (var index = 0; index < _titleTabViews.Count; index++)
		{
			var view = _titleTabViews[index];
			var active = index == selected;
			view.Button.StyleName(active ? AppStyles.TitleBarTabActive : AppStyles.TitleBarTab);
			view.Close.IsVisible(active);
		}
	}

	/// <summary>标题栏页签：活动页签带底与关闭键，非活动页签悬停才浮起。</summary>
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

		var close = BuildTitleTabClose(entry);
		close.IsVisible(active);

		var row = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(4)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(label, close);

		var tab = new Button()
			.Content(row)
			.Height(28)
			.Padding(new Thickness(10, 0, 8, 0))
			.StyleName(active ? AppStyles.TitleBarTabActive : AppStyles.TitleBarTab)
			.OnClick(() => SelectEntry(entry))
			.HoverTip(title);

		// 右键沿用命令面板里的同一组标签动作（关闭/保留/右移…）。
		tab.OnMouseDown(e =>
		{
			if (e.Button == MouseButton.Right)
			{
				TabMenu(tab, entry).Show(tab);
				e.Handled = true;
			}
		});

		return new TitleTabView(tab, close);
	}

	/// <summary>页签控件与它的关闭键：活动态切换时一起更新。</summary>
	private sealed record TitleTabView(Button Button, Button Close);

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
			_tabControl.SelectedIndex = index;
		}

		UpdateStatus();
	}

	/// <summary>页签上显示的名字：会话用会话标题，辅助页用短名。</summary>
	private static string EntryTitle(object entry) => entry switch
	{
		SessionTab tab => tab.Title,
		SettingsView => Strings.Settings,
		GitView => Strings.SidebarGit,
		FileBrowserView => Strings.FileBrowser,
		TerminalDock => Strings.Terminal,
		UsageStatsView => Strings.UsageStatsTitle,
		_ => string.Empty,
	};

	/// <summary>
	/// 标题栏的空白处拖动窗口，双击最大化/还原。macOS 的原生拖动会进入 AppKit 的事件循环，
	/// 第二次按下时不能再启动，否则双击事件收不到 —— 因此只让单击启动拖动（与 MewUI 示例一致）。
	/// </summary>
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

	/// <summary>
	/// 按平台能力刷新窗框：原生有按钮（macOS）就隐藏自绘按钮并让出红绿灯的位置；
	/// 否则显示三个自绘按钮，并让它们贴住右缘。
	/// </summary>
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

		// macOS 的红绿灯固定在窗口左上角：侧栏可见时由左段让位，收起后由右段让位。
		_sidebarTitleBar.Padding(new Thickness(
			AppTheme.Space3 + inset.Left,
			0,
			AppTheme.Space2,
			0));
		_mainTitleBar.Padding(new Thickness(
			AppTheme.Space2 + (sidebarVisible ? 0 : inset.Left),
			0,
			inset.Right + (nativeButtons ? AppTheme.Space3 : 0),
			0));

		_minimizeButton.IsVisible(CanMinimize);
		_maximizeButton.IsVisible(CanMaximize);
		_closeButton.IsVisible(CanClose);

		var maximized = WindowState == WindowState.Maximized;
		_maximizeGlyph.Kind(maximized ? GlyphKind.WindowRestore : GlyphKind.WindowMaximize);
		HoverHints.Set(_maximizeButton, maximized ? Strings.RestoreWindow : Strings.MaximizeWindow);

		UpdateTransparentFrame(WindowState == WindowState.Normal);
		SyncNativeBorderColor();
	}

	/// <summary>
	/// X11 透明窗框：普通态有 10 DIP 圆角、1px 边界与 12 DIP 落影；
	/// 最大化/全屏时全部归零，圆角窗框铺满屏幕。原生窗框平台不做任何事。
	/// </summary>
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

	/// <summary>Win11 的原生边框跟着配色走，窗口与桌面之间才没有一圈突兀的浅色线。</summary>
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
