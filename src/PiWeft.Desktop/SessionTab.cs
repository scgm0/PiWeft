using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Tools;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>
/// 一个会话标签页：消息区 + 输入区 + 该会话的模型/思考级别与用量。
/// 每个标签持有自己的 <see cref="CodingAgentSession"/>，因此多个会话可以并行运行。
/// </summary>
public sealed partial class SessionTab : IDisposable
{
	// 会话模型可以是后台预建的（长会话的投影要上百毫秒），默认自己新建一个。
	private readonly ChatViewModel _chat;
	private readonly ConversationTimeline _timeline;
	private readonly DispatcherTimer _renderTimer = new(TimeSpan.FromMilliseconds(40));
	private int _renderPending;
	private volatile bool _disposed;
	private bool _sending;
	private Task _pendingSend = Task.CompletedTask;
	private Task _pendingPaste = Task.CompletedTask;
	private CancellationTokenSource? _compactionAbort;
	private readonly Lock _sendAbortGate = new();
	private CancellationTokenSource? _sendAbort;
	private readonly ItemsControl _messages;
	private readonly ScrollViewer _scroll;
	private readonly ConversationScroll _conversationScroll;
	private readonly TextFocusScrollGuard _textFocus;
	private readonly MultiLineTextBox _composer;
	private readonly TextBlock _composerHint = new();
	private readonly Button _sendButton;
	private readonly Button _sendMenuButton;
	private readonly Border _sendDivider;
	private readonly TextBlock _sendLabel = new();
	private readonly Image _sendIcon = new();
	private readonly TextBlock _noMatches = new();
	private readonly TextBlock _status = new();
	private readonly ProgressBar _contextBar = new();
	private readonly TextBlock _statisticsLine = new();
	private readonly Border _composerPane;
	private readonly TextBlock _contextLabel = new();
	private readonly SessionStatisticsPanel _statisticsPanel;
	private readonly ProviderUsagePanel _usagePanel;
	private readonly ProviderUsageSummary _usageSummary;
	private bool _returnToStatistics;
	private SessionStatisticsDisplay? _renderedStatistics;
	private SessionStatisticsDisplay? _statisticsDisplay;
	private PiWeft.CodingAgent.Session.SessionManager? _statisticsManager;
	private long _statisticsRevision = -1;
	private int _statisticsWindow;
	private bool _lastBusy;
	private readonly WrapPanel _attachmentBar = new();
	private readonly Button _scrollToLatest;
	private readonly FloatingShadow _scrollToLatestShadow;
	private readonly Grid _scrollToLatestShell;
	private readonly ShellRootWatcher _scrollToLatestWatcher;

	/// <summary>挂窗后按钮外壳升入装饰层时持有的装饰器（见 <see cref="HandleShellRootChanged"/>）。</summary>
	private bool _scrollToLatestHosted;
	private Window? _scrollToLatestWindow;
	private readonly TextBox _searchBox = new TextBox().PrimarySelection();
	private readonly TextBlock _filterStatus = new();
	private readonly TextBlock _extensionLine = new();
	private readonly Dictionary<string, string> _extensionStatuses = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IReadOnlyList<string>> _extensionWidgets = new(StringComparer.Ordinal);
	private string _filter = string.Empty;
	// 开始搜索前读者所在的行：清空过滤后回到原位置，而不是被重新投影的列表甩到末尾。
	private ConversationTimeline.ReadingPoint _filterAnchor;
	private bool _filterWasFollowing;
	private IReadOnlyList<ChatEntry>? _projectedSource;
	private IReadOnlyList<ChatEntry> _projectedRows = [];
	private readonly ExecutionProcessProjector _projector = new();
	private string _projectedFilter = string.Empty;
	private bool _projectedBusy;

	private readonly BranchPanel _branchPanel;
	private readonly Grid _mainArea = new();
	private readonly ConversationOutline _outline = new();

	/// <summary>待发送的图片附件：文件路径与内容块。</summary>
	private readonly List<(string Path, ImageContent Content)> _attachments = [];

	/// <summary>「编辑重发」时记录要回到的条目；发送前先在该处分支。</summary>
	private string? _pendingBranchEntryId;
	private readonly TextBlock _emptyContext = new();
	private readonly Border _emptyCard;

	/// <summary>输入区整列：提示文本 + 输入框 + 状态栏，空会话时一起居中。</summary>
	private readonly StackPanel _composerColumn;
	private Border _headerRow = null!;
	private readonly WrapPanel _commandBar = new();
	private readonly Border _editNotice;

	/// <summary>输入区上方的待办卡：头部显示分段进度，展开后是限高可滚动的完整计划。</summary>
	private const double ComposerWidgetListMaxHeight = 180;
	private readonly ComposerWidgetCard _todoCard;
	private readonly StackPanel _todoRows = new();
	private readonly ScrollViewer _todoScroll;
	private TodoState? _renderedTodo;
	private TodoPlan? _renderedPlan;

	/// <summary>通用扩展 widget 卡：setWidget 的文本行进这里，和待办卡同列同宽。</summary>
	private readonly ComposerWidgetCard _extensionCard;
	private readonly StackPanel _extensionBody = new();
	private readonly ReadingColumn _widgetColumn = new();
	private readonly Button _retry;
	private readonly Button _copyReply;
	private readonly Button _modelButton;
	private readonly TextBlock _modelLabel = new();
	private readonly Button _planButton;
	private readonly TextBlock _planLabel = new();
	private readonly Button _testButton;
	private bool _testing;
	private readonly ModelPicker _modelPicker;
	private readonly ImagePreview _imagePreview = new();
	private readonly Button _thinkingButton;
	private readonly TextBlock _thinkingLabel = new();

	/// <summary>输入区上方的会话进行态指示行（PiDeck responding 条）：正在执行命令…/正在思考…。</summary>
	private readonly Border _busyLine;
	private readonly Image _busyIcon;
	private readonly TextBlock _busyText = new();
	private string? _busyKind;

	/// <summary>输入框左群的终端开关键：控制会话底部的终端面板。</summary>
	private readonly Button _terminalButton;
	private readonly Border _terminalPane;
	private readonly SplitPanel _terminalSplit;
	private TerminalDock? _terminal;
	private bool _terminalOpen;

	/// <summary>底部终端面板的高度：收起时记下来，下次展开沿用。</summary>
	private double _terminalDockHeight = AppTheme.TerminalDockHeight;

	/// <summary>标签标题变化时触发（会话名、运行状态）。</summary>
	public event Action<SessionTab>? TitleChanged;

	/// <summary>会话被分支（新会话文件）后触发，宿主需要刷新侧栏。</summary>
	public event Action<SessionTab>? Forked;

	/// <summary>会话空闲/运行状态变化时触发，用于在标签头显示指示。</summary>
	public event Action<SessionTab>? StateChanged;

	public SessionTab(CodingAgentSession session, IReadOnlyList<Model> models, string title,
		Func<IReadOnlyList<Model>>? availableModels = null, ChatViewModel? chat = null)
	{
		// 后台已经 Attach 过就直接接手，否则 UI 线程要再投影一遍整个会话。
		_chat = chat ?? new ChatViewModel();
		_scrollToLatest = new Button()
			.Content(new Grid().Children(AppIcons.Icon("arrow-down", AppStyles.RolePrimary, 16)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)))
			.OnClick(ScrollToLatest)
			.Ghost().Width(36).Height(36).MinWidth(36).MinHeight(36)
			.Padding(0).CornerRadius(18)
			.HoverTip(Strings.ScrollToLatest)
			.Focusable(false).IsHitTestVisible(false)
			.WithTheme((theme, button) =>
			{
				var palette = AppTheme.For(theme);
				button.Background = palette.CardBackground;
				// BorderSubtle 在深浅主题里都贴底色；用更亮的 TextFaint 让轮廓真正可见。
				button.BorderBrush = palette.TextFaint;
				button.BorderThickness = 1;
			});
		// 阴影画布要比圆钮大一圈：光晕（模糊 7 + 下移 2）画在自己的 bounds 里才不会出现硬边黑带。
		const double shadowInset = 10;
		_scrollToLatestShadow = new FloatingShadow().IsHitTestVisible(false);
		var shell = new FloatingShell();
		_scrollToLatestWatcher = new ShellRootWatcher
		{
			Changed = HandleShellRootChanged, IsHitTestVisible = false,
			Width = 0, Height = 0,
			HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
		};
		_scrollToLatestShell = shell.Width(36 + shadowInset * 2).Height(36 + shadowInset * 2)
			.HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Bottom)
			// 圆钮相对窗口底边的位置保持原样（原来的 12 = 2 + 10）。
			.Margin(new Thickness(0, 0, 0, Math.Max(0, AppTheme.Space3 - shadowInset)))
			.Children(_scrollToLatestShadow,
				_scrollToLatest.HorizontalAlignment(HorizontalAlignment.Center)
					.VerticalAlignment(VerticalAlignment.Center));
		// 永远参与布局，只用 opacity 隐藏：待办卡展开/收起会改变消息区高度，
		// 若用 IsVisible 跳过 arrange，重新出现时图标缓存可能停在旧位置，和按钮底分离。
		_scrollToLatest.Opacity = 0;
		_scrollToLatestShadow.Opacity = 0;

		Session = session;
		Title = title;
		_statisticsPanel = new SessionStatisticsPanel(() => _ = CompactAsync());
		_statisticsPanel.Closed += FocusComposer;
		_usageSummary = new ProviderUsageSummary(() => Session.Model, () => Session.Cwd, ConfigureProviderUsage);
		_statisticsPanel.SetUsage(_usageSummary);
		_usagePanel = new ProviderUsagePanel(() => Session.Model, () => Session.Cwd);
		_usagePanel.Saved += _usageSummary.Invalidate;
		_usagePanel.Closed += () =>
		{
			if (_returnToStatistics && !_disposed)
			{
				_returnToStatistics = false;
				ShowStatistics();
			}
			else FocusComposer();
		};


		// 空会话提示：与输入区同属一列，居中时正好落在输入框上方，只留一段固定间距。
		_emptyCard = new Border()
			.Background(AppTheme.Transparent)
			.MaxWidth(560)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space6, 0, AppTheme.Space6, AppTheme.Space4))
			.Child(new StackPanel()
				.Spacing(AppTheme.Space2)
				.Children(
					new TextBlock().Text(Strings.WelcomeTitle).FontSize(24).FontWeight(FontWeight.Medium).PrimaryText()
						.HorizontalAlignment(HorizontalAlignment.Center)));

		_timeline = new ConversationTimeline(Row);
		_messages = _timeline.View;
		_scroll = _timeline.Scroll;

		// Restore focus offsets before follow-state observes the scroll notification.
		_textFocus = new TextFocusScrollGuard(_scroll, anchor => _conversationScroll!.PreserveAnchor(anchor));
		// 折叠开关改变行高：会话视图自己收敛一次，不能等用户滚动才发现内容没了。
		_textFocus.LayoutInvalidated = fold => _timeline.RefreshLayout(fold);
		// 展开正在流式的思考时，外层会话重新贴住内容末尾（嵌套的组体则由它自己的视口跟随）。
		_textFocus.FollowConversation = () => _conversationScroll!.ScrollToLatest();
		_textFocus.PauseFollowing = () => _conversationScroll!.Browse();
		_conversationScroll = new ConversationScroll(_scroll, visible => SetScrollToLatestVisible(visible && _messages.IsVisible));

		// 右缘定位轴：点击刻度跳到那条用户消息，滚动时高亮当前阅读位置。
		_outline.Jump += JumpToOutline;
		_outline.View.MouseWheel += args => SmoothScroll.For(_scroll).RouteWheel(args);
		_scroll.ScrollChanged += OutlineScrolled;
		_mainArea.SizeChanged += _ =>
		{
			UpdateOutlineVisibility();
			UpdateFloatingShellMargins();
		};

		_composer = new MultiLineTextBox().PrimarySelection()
			.FontSize(AppTheme.FontSizeChat)
			.Placeholder(string.Empty)
			.Height(AppTheme.ComposerHeight)
			.CornerRadius(AppTheme.RadiusMd)
			.StyleName(AppStyles.TextInput)
			.BorderThickness(0)
			.Background(AppTheme.Transparent)
			.Padding(new Thickness(AppTheme.Space2))
			.OnTextChanged(value =>
			{
				_composerHint.IsVisible(value.Length == 0);
				RefreshCommandBar();
			})
			.OnKeyDown(key =>
			{
				// 文件引用是行内原子：方向键跳过、Backspace/Delete 整枚删除。
				if (UserReferenceRichText.TryHandleKey(_composer!, key)) return;

				// Enter 发送，Shift+Enter 换行 —— 与占位符里的提示一致。
				if (key.Key == Key.Enter && !key.ShiftKey)
				{
					key.Handled = true;
					Send();
					return;
				}

				// 命令分发在按键冒泡之后，这里能先看到 Ctrl/Cmd+V；剪贴板里是图片时就当附件，
				// 否则交给框架做原有文本粘贴。
				if (key.Key == Key.V && key.PrimaryKey && !key.AltKey && PasteImageFromClipboard()) key.Handled = true;
			})
			.OnTextInput(input => UserReferenceRichText.TryHandleTextInput(_composer!, input));

		// 草稿里的文件引用和 /skill:名称 都用行内 chip 显示；原始 @"..." / /skill:... 仍留在文档里。
		UserReferenceRichText.Attach(_composer, editable: true, cwd: Session.Cwd, skillLocation: SkillLocation);

		// 默认右键菜单的「粘贴」只判断剪贴板有无文本，纯图片剪贴板时菜单项是灰的。
		// 替换同一个语义命令：菜单、快捷键与工具栏仍走 StandardCommands.Paste，
		// 但执行时先尝试图片，再回退到文本框自己的文本粘贴。
		_composer.Commands.Unregister(StandardCommands.Paste);
		_composer.Commands.Register(StandardCommands.Paste, this,
			static tab => tab.PasteFromClipboard(),
			static tab => tab.CanPasteFromClipboard());

		// 剪切选区时先把部分覆盖的引用扩成整枚 chip，菜单与快捷键共用这一条命令。
		_composer.Commands.Unregister(StandardCommands.Cut);
		_composer.Commands.Register(StandardCommands.Cut, this,
			static tab => tab.CutFromComposer(),
			static tab => !tab._composer.IsReadOnly && tab._composer.SelectionLength > 0);

		_commandBar.Spacing(AppTheme.Space2).Margin(new Thickness(0, 0, 0, 4)).IsVisible(false);

		_sendIcon.Width(14).Height(14).VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, image) => image.Source = AppIcons.Get(_lastBusy ? "x" : "arrow-up", AppTheme.For(theme).TextOnAccent));
		_sendButton = new Button()
			.Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
				_sendIcon, _sendLabel.Text(Strings.Send).FontSize(AppTheme.FontSizeCaption).OnAccentText()))
			.OnClick(Send)
			.Primary().Height(AppTheme.ControlHeightMd)
			.CornerRadius(0)
			.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, 0))
			.VerticalAlignment(VerticalAlignment.Center);
		_sendDivider = new Border().Width(1).Height(16).VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, divider) => divider.Background = AppTheme.For(theme).TextOnAccent.WithAlpha(0x40))
			.IsVisible(false);
		_sendMenuButton = new Button()
			.Content(new Grid().Children(new GlyphElement().Kind(GlyphKind.ChevronDown).GlyphSize(3)
				.WithTheme((theme, glyph) => glyph.Foreground = AppTheme.For(theme).TextOnAccent)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)))
			.OnClick(ShowSendMenu)
			.Primary().Height(AppTheme.ControlHeightMd).MinWidth(28)
			.CornerRadius(0).Padding(new Thickness(5, 0, 7, 0))
			.VerticalAlignment(VerticalAlignment.Center).IsVisible(false)
			.HoverTip(Strings.SendOptions);
		var sendGroup = new Border().CornerRadius(AppTheme.RadiusMd).ClipToBounds(true)
			.HorizontalAlignment(HorizontalAlignment.Right).VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).Accent)
			.Child(new StackPanel().Orientation(Orientation.Horizontal)
				.VerticalAlignment(VerticalAlignment.Center).Children(_sendButton, _sendDivider, _sendMenuButton));

		var attach = AppIcons.Button("image-plus", Strings.AttachFiles, () => _ = AttachImageAsync());
		var commands = AppIcons.Button("square-slash", Strings.Commands, () => _composer.Text = "/");

		_planLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText();
		_planButton = new Button().Ghost().Content(_planLabel).HoverTip(Strings.PlanModeHint)
			.OnClick(TogglePlanMode);
		_terminalButton = AppIcons.Button("terminal", Strings.Terminal, ToggleTerminal);
		_testButton = AppIcons.Button("refresh-cw", Strings.TestConnection, () => _ = TestConnectionAsync());

		_modelPicker = new ModelPicker(availableModels ?? (() => models), () => Session.Model, SelectModel);
		_modelPicker.Closed += FocusComposer;
		_modelButton = new Button().Ghost().MaxWidth(248).MinHeight(AppTheme.ControlHeightMd)
			.Content(_modelLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.OnClick(ShowModelPicker);

		// 思考强度与模型名同级：都是安静的 ghost 文本，不带边框/底色；点开才出现单选菜单。
		_thinkingLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText();
		var thinkingChevron = new GlyphElement()
			.Kind(GlyphKind.ChevronDown)
			.GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center);
		_thinkingButton = new Button().Ghost().MinHeight(AppTheme.ControlHeightMd)
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(_thinkingLabel, thinkingChevron))
			.OnClick(ShowThinkingMenu);

		// 左群是会改变下一条消息的开关与插入动作（计划、附件、引用、命令、终端），窄窗口自动换行；
		// 中群是模型与思考强度铭牌（PiDeck 同款：provider/model · max ▾，居中）；
		// 右群是连接测试与发送/中断，发送永远贴在最右。
		var reference = new Button().Ghost().MinWidth(28).Padding(new Thickness(4, 2, 4, 2))
			.Content(new TextBlock().Text("@").FontSize(AppTheme.FontSizeChat).MutedText()
				.VerticalAlignment(VerticalAlignment.Center))
			.OnClick(InsertReference)
			.HoverTip(Strings.ComposerReference);
		var leftActions = new WrapPanel().Spacing(4).VerticalAlignment(VerticalAlignment.Center)
			.Children(_planButton, attach, reference, commands, _terminalButton);
		var centerActions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(_modelButton, _thinkingButton);
		var rightActions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Children(_testButton, sendGroup);
		var toolbar = new Grid().Columns("1*,Auto,1*")
			.Children(leftActions.Column(0), centerActions.Column(1), rightActions.Column(2));

		// 上下文占用条：接近窗口上限时一眼可见。
		_contextBar.Maximum(100).Height(3).Margin(new Thickness(0, 2, 0, 2));

		_editNotice = new Border().StyleName(AppStyles.Panel).IsVisible(false);
		_editNotice.Padding(new Thickness(AppTheme.Space2))
			.Child(new DockPanel().LastChildFill().Children(
				AppIcons.Button("x", Strings.CancelEdit, () =>
				{
					_pendingBranchEntryId = null;
					_editNotice.IsVisible(false);
				}).DockRight(),
				new TextBlock().Text(Strings.EditPending).FontSize(AppTheme.FontSizeCaption)
					.MutedText().TextWrapping(TextWrapping.Wrap)));

		_todoRows.Spacing(6);
		_todoScroll = new ScrollViewer()
			.Content(_todoRows)
			.AutoVerticalScroll()
			.NoHorizontalScroll()
			.Padding(new Thickness(0, 0, AppTheme.Space2, 0))
			.MaxHeight(ComposerWidgetListMaxHeight);
		_todoCard = new ComposerWidgetCard("list-checks", Strings.TodoPlanTitle);
		_todoCard.Body.Children(_todoScroll);

		_extensionBody.Spacing(AppTheme.Space2);
		_extensionCard = new ComposerWidgetCard("package", Strings.ExtensionWidgetsTitle);
		_extensionCard.Body.Children(new ScrollViewer()
			.Content(_extensionBody)
			.AutoVerticalScroll()
			.NoHorizontalScroll()
			.Padding(new Thickness(0, 0, AppTheme.Space2, 0))
			.MaxHeight(ComposerWidgetListMaxHeight));

		// 扩展卡与输入卡同宽同列：ReadingColumn 按阅读宽度封顶并撑满可用宽度，
		// 卡片在自己的列里拉伸，窗口缩放与阅读宽度设置都自动跟随。
		_widgetColumn.Spacing(AppTheme.Space2)
			.Margin(new Thickness(AppTheme.Space4, 0, AppTheme.Space4, 0))
			.IsVisible(false)
			.Children(_todoCard.View, _extensionCard.View);

		// 会话进行态指示行：与 composer 卡同宽居中，贴在输入卡上方（PiDeck 同款）。
		_busyIcon = AppIcons.Icon("terminal", AppStyles.RoleInfo, 13);
		_busyText.FontSize(AppTheme.FontSizeCaption).MutedText().VerticalAlignment(VerticalAlignment.Center);
		_busyLine = new Border()
			.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space4, 0, AppTheme.Space4, 0))
			.IsVisible(false)
			.Child(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6)
				.Children(_busyIcon, _busyText));

		var composerPane = _composerPane = new Border()
			.StyleName(AppStyles.Composer)
			.CornerRadius(AppTheme.RadiusLg)
			.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space3, AppTheme.Space3, AppTheme.Space2))
			.Margin(new Thickness(AppTheme.Space4, AppTheme.Space2, AppTheme.Space4, AppTheme.Space3))
			.Child(new StackPanel()
				.Spacing(AppTheme.Space2)
				.Children(_editNotice, _commandBar, _attachmentBar,
					new Grid().Children(_composer, _composerHint.Text(Strings.ComposerPlaceholder)
						.FontSize(AppTheme.FontSizeChat).SubtleText().TextTrimming(TextTrimming.CharacterEllipsis)
						.Margin(new Thickness(AppTheme.Space2)).IsHitTestVisible(false)
						.VerticalAlignment(VerticalAlignment.Top)), toolbar));

		// The window registers with the OS; the session surface owns the target draft.

		_branchPanel = new BranchPanel(() => Session);

		// 主区在「对话」与「分支」两个视图之间切换，两者叠放，只显示其中一个。
		_mainArea.Children(_messages);
		_mainArea.Children(_noMatches.Text(Strings.NoMatches).MutedText()
			.HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center).IsVisible(false));
		_mainArea.Children(_branchPanel.View.IsVisible(false));
		_mainArea.Children(_outline.View);
		_mainArea.Children(_scrollToLatestShell);
		// 外壳升到窗口 OverlayLayer 后不再跟随本标签页的视觉根；单独留一个零尺寸观察者，
		// 标签页被切走时才能把旧外壳摘掉，避免多个会话的「移动到最新」按钮叠在一起。
		_mainArea.Children(_scrollToLatestWatcher);

		var chatView = AppIcons.Button("message-square", Strings.ViewChat, () => ShowView(showChat: true));
		var branchView = AppIcons.Button("git-branch", Strings.ViewBranches, () => ShowView(showChat: false));
		var searchToggle = AppIcons.Button("search", Strings.SearchInSession, ToggleSearch);
		_copyReply = AppIcons.Button("copy", Strings.CopyLastReply, CopyLastReply);
		_retry = AppIcons.Button("rotate-ccw", Strings.Retry, () => _ = RetryAsync());

		// 会话内搜索：默认收起，只留一个图标；展开后在消息区上方过滤。
		_searchBox
			.Placeholder(Strings.SearchInSession)
			.Width(240)
			.IsVisible(false)
			.OnTextChanged(_ => ApplyFilter(_searchBox.Text?.Trim() ?? string.Empty))
			.OnKeyDown(key =>
			{
				if (key.Key == Key.Escape)
				{
					key.Handled = true;
					ToggleSearch();
				}
			});

		_filterStatus.FontSize(AppTheme.FontSizeMicro).SubtleText().IsVisible(false);

		var viewSwitch = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.Children(chatView, branchView);

		_status.FontSize(AppTheme.FontSizeMicro).SubtleText().FontFamily(AppTheme.MonoFont);

		// 标题行：左状态铭牌（等宽小字），右动作簇；不再是一块独立底色的条。
		// 会话头行（状态铭牌 + 视图切换/搜索/重试）：空会话时整行隐藏，出现第一条消息后再显示。
		_headerRow = new Border()
			.Background(AppTheme.Transparent)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.Child(new DockPanel()
				.LastChildFill()
				.Children(viewSwitch.DockRight(), searchToggle.DockRight(),
					_retry.DockRight(), _copyReply.DockRight(), _filterStatus.DockRight(), _status));

		var contextButton = new Button().Content(_contextLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.HoverTip(Strings.SessionStatistics).OnClick(ShowStatistics).Ghost();
		var statisticsBar = new ReadingColumn().Spacing(4).MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
			.HorizontalAlignment(HorizontalAlignment.Center).Children(
				_contextBar,
				new DockPanel().LastChildFill().Children(contextButton.DockRight(),
					_statisticsLine.FontSize(AppTheme.FontSizeMicro).SubtleText().FontFamily(AppTheme.MonoFont)));

		// 输入区整列（提示文本 + 输入框 + 状态栏）：空会话时落在填充行并居中，有消息后回到贴底的自适应行。
		_extensionLine.FontSize(AppTheme.FontSizeMicro).SubtleText().FontFamily(AppTheme.MonoFont)
			.HorizontalAlignment(HorizontalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis)
			.Margin(new Thickness(AppTheme.Space4, 0, AppTheme.Space4, 0)).IsVisible(false);

		_composerColumn = new StackPanel()
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Stretch)
			.Children(_extensionLine, _widgetColumn, _emptyCard.IsVisible(false), _busyLine, composerPane,
				statisticsBar.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space2)));

		var layout = new Grid()
			.Rows("Auto,Auto,*,Auto")
			.Children(
				_headerRow.Row(0),
				_searchBox.HorizontalAlignment(HorizontalAlignment.Left)
					.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space2)).Row(1),
				_mainArea.Row(2),
				_composerColumn.Row(3));

		// 底部终端：属于会话、默认收起，第一次展开才拉起伪终端；收起只隐藏，shell 的历史与工作目录都留着。
		// 与对话之间是一条分隔条，拖拽即可调整终端高度（高度按会话记着，下次展开沿用）。
		_terminalPane = new Border()
			.StyleName(AppStyles.TerminalDock)
			.IsVisible(false);
		_terminalSplit = new SplitPanel().Vertical().SplitterThickness(4)
			.MinFirst(240)
			.MinSecond(AppTheme.TerminalDockMinHeight)
			.MaxSecond(AppTheme.TerminalDockMaxHeight)
			.FirstLength(GridLength.Stars(1))
			.SecondLength(GridLength.Pixels(_terminalDockHeight))
			.First(layout).Second(_terminalPane);

		View = _terminalSplit.AllowDrop().OnDragEnter(OnFilesDragOver).OnDragOver(OnFilesDragOver).OnDrop(OnFilesDropped)
			.OnLayoutSizeChanged(args => _composer.Height = args.NewSize.Width < 640 || args.NewSize.Height < 560 ? 56 : AppTheme.ComposerHeight);

		_readingStatisticsBar = statisticsBar;
		ReadingPreferences.Changed += ApplyReadingPreferences;
		_chat.Changed += OnChatChanged;
		_renderTimer.Tick += () =>
		{
			_renderTimer.Stop();
			Interlocked.Exchange(ref _renderPending, 0);
			if (!_disposed)
			{
				Render();
			}
		};
		_imagePreview.Status = (message, ok) =>
		{
			if (_disposed) return;
			if (ok) Toasts.Success(View, message);
			else NotifyError(message);
		};
		if (!ReferenceEquals(_chat.Session, session)) _chat.Attach(session);

		RefreshPickers();
		Render();
	}

	public CodingAgentSession Session { get; }

	/// <summary>本标签的宿主交互（ask_question）；随标签释放一起关闭。</summary>
	internal DesktopUiHost? UiHost { get; set; }

	/// <summary>待办卡是否占据输入区上方的位置（诊断与契约测试用）。</summary>
	internal bool TodoCardVisible => _todoCard.View.IsVisible;

	/// <summary>扩展 widget 卡是否占据输入区上方的位置（诊断与契约测试用）。</summary>
	internal bool ExtensionCardVisible => _extensionCard.View.IsVisible;

	/// <summary>代理事件层是否已进入流式状态（诊断与契约测试用）。</summary>
	/// <summary>标签页使用的聊天记录模型；宿主可以在后台预建后交接过来。</summary>
	internal ChatViewModel Chat => _chat;

	internal bool ChatStreaming => _chat.IsStreaming;

	/// <summary>草稿里已识别的文件引用数（诊断与契约测试用）。</summary>
	internal int ComposerReferenceCount => UserReferenceRichText.Count(_composer.Text);

	public string Title { get; private set; }

	/// <summary>标签页内容。</summary>
	public UIElement View { get; }

	public bool IsStreaming => _sending || _chat.IsStreaming;

	/// <summary>会话显示名：会话名 > 第一条用户消息 > 短 id。</summary>
	public static string TitleFor(CodingAgentSession session)
	{
		if (session.SessionName is { Length: > 0 } name)
		{
			return name;
		}

		var first = session.Session.FirstUserMessageText();
		if (first is { Length: > 0 })
		{
			var flat = first.Replace('\n', ' ').Trim();
			return flat.Length <= 24 ? flat : string.Concat(flat.AsSpan(0, 24), "…");
		}

		// 没有名字也没有消息时用通用名，而不是把 id 摆给用户看。
		return Strings.UntitledSession;
	}

	/// <summary>扩展 <c>setStatus</c>：状态条目显示在输入区上方；text 为 null 时清除。</summary>
	public void SetExtensionStatus(string key, string? text)
	{
		if (_disposed)
		{
			return;
		}

		if (text is null)
		{
			_extensionStatuses.Remove(key);
		}
		else
		{
			_extensionStatuses[key] = text;
		}

		RefreshExtensionLine();
	}

	/// <summary>扩展 <c>setWidget</c>：把 widget 的文本行放进输入区上方的扩展卡。</summary>
	public void SetExtensionWidget(string key, IReadOnlyList<string>? lines)
	{
		if (_disposed)
		{
			return;
		}

		if (lines is null || lines.Count == 0)
		{
			_extensionWidgets.Remove(key);
		}
		else
		{
			_extensionWidgets[key] = lines;
		}

		RefreshExtensionWidgets();
	}

	/// <summary>扩展 <c>setTitle</c>：更新标签页标题。</summary>
	public void SetExtensionTitle(string title)
	{
		if (_disposed)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(title) || string.Equals(Title, title, StringComparison.Ordinal))
		{
			return;
		}

		Title = title;
		TitleChanged?.Invoke(this);
	}

	/// <summary>扩展 <c>set_editor_text</c>：替换输入框内容并把焦点交回编辑器。</summary>
	public void SetExtensionText(string text)
	{
		if (_disposed)
		{
			return;
		}

		_composer.Text = text;
		FocusComposer();
	}

	private void RefreshExtensionLine()
	{
		// widget 有自己的卡；这一行只保留 setStatus 的短状态，避免和卡片内容重复。
		var parts = _extensionStatuses.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
			.Select(static pair => pair.Value)
			.Where(static part => !string.IsNullOrWhiteSpace(part))
			.ToArray();
		_extensionLine.Text(string.Join("  ·  ", parts));
		_extensionLine.IsVisible(parts.Length > 0);
	}

	public void RefreshTitle()
	{
		var title = TitleFor(Session);
		if (title != Title)
		{
			Title = title;
			TitleChanged?.Invoke(this);
		}
	}

	public void FocusComposer()
	{
		if (!_disposed)
		{
			_composer.Focus();
		}
	}

	public void Dispose()
	{
		// 流式运行中直接释放会留下一轮孤儿运行：agent 继续烧 token 执行工具，
		// 而持久化监听已退订、这一轮的结果不会再落盘。这里先发中止信号（安全网）；
		// 用户主动关闭走 CloseAsync，会等这轮收尾落盘。
		try
		{
			CancelPendingSend();
			Session.Abort();
		}
		catch (ObjectDisposedException)
		{
			// 会话已经释放。
		}

		DisposeView();
		Session.Dispose();
	}

	/// <summary>
	/// 关闭标签：先中止运行中的轮次并等它收尾（结果落盘），再释放界面与会话。
	/// 有界等待，收尾失败也继续关闭。
	/// </summary>
	public async Task CloseAsync()
	{
		try
		{
			if (IsStreaming)
			{
				CancelPendingSend();
				Session.Abort();
				await _pendingSend.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
			}
		}
		catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or ObjectDisposedException)
		{
			// 收尾失败也继续关闭。
		}

		Dispose();
	}

	/// <summary>释放界面订阅和待执行的刷新，不释放会话。</summary>
	public void DisposeView()
	{
		if (_disposed) return;
		_disposed = true;
		DetachFloatingShell();
		ReadingPreferences.Changed -= ApplyReadingPreferences;
		UiHost?.Hide();
		_compactionAbort?.Cancel();
		HideTransientPanels();
		_renderTimer.Dispose();
		_scroll.ScrollChanged -= OutlineScrolled;
		_outline.Dispose();
		_conversationScroll.Dispose();
		_textFocus.Dispose();
		_timeline.Dispose();
		_terminal?.Dispose();
		_chat.Changed -= OnChatChanged;
		_chat.Dispose();
		UiLifetime.Release(View);
		UiLifetime.Release(_statisticsPanel.View);
		UiLifetime.Release(_usagePanel.View);
		GC.SuppressFinalize(this);
	}

	/// <summary>把同一帧内的增量合并，避免每个 token 都触发布局与绘制。</summary>
	private void OnChatChanged()
	{
		if (_disposed || Interlocked.Exchange(ref _renderPending, 1) != 0)
		{
			return;
		}

		Dispatch(() =>
		{
			if (!_disposed)
			{
				_renderTimer.Start();
			}
		});
	}

	/// <summary>发送输入框中的指令，运行结束后刷新界面。</summary>
	private void Send()
	{
		if (_sending || _chat.IsStreaming)
		{
			_compactionAbort?.Cancel();
			CancelPendingSend();
			_chat.Abort();
			return;
		}

		if (_loadingAttachments > 0)
		{
			Notify(Strings.AttachmentLoading);
			return;
		}
		var text = _composer.Text;
		if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0)
		{
			return;
		}

		_composer.Text = string.Empty;
		_pendingSend = SendAsync(text);
	}

	private readonly StackPanel _readingStatisticsBar;

	private void ApplyReadingPreferences()
	{
		if (_disposed) return;
		_timeline.ApplyReadingWidth();
		_composerPane.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6);
		_widgetColumn.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6);
		_readingStatisticsBar.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6);
		_composer.FontSize(AppTheme.FontSizeChat);
		_composerHint.FontSize(AppTheme.FontSizeChat);
		Render(force: true);
	}

	/// <summary>原位换色，保留 Markdown、选区、工具折叠与阅读位置。</summary>
	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors((Element)View);
		// 「移动到最新」有时挂在 AdornerLayer（不在 View 的视觉树里），换肤要单独刷新，
		// 否则浅色主题下会留下一个深色圆钮。
		_scrollToLatest.Background = AppTheme.CardBackground;
		_scrollToLatest.BorderBrush = AppTheme.TextFaint;
		AppStyles.ApplyTextColors((Element)_scrollToLatestShell);
		_scrollToLatestShadow.InvalidateVisual();
		_status.Foreground(AppStyles.ColorForRole(AppStyles.RoleSubtle));
		AppStyles.ApplyTextColors(_modelPicker.View);
		AppStyles.ApplyTextColors(_statisticsPanel.View);
		AppStyles.ApplyTextColors(_usagePanel.View);
		AppStyles.ApplyTextColors(_branchPanel.View);
		AppStyles.ApplyTextColors(_imagePreview.View);
		_terminal?.ApplyTheme();
	}

	/// <summary>临时消息统一走窗口级 toast；头部铭牌只留给模型与思考级别。</summary>
	private void Notify(string message) => Toasts.Show(View, message);

	/// <summary>失败类提示：红色图标，停留时间按文案长度。</summary>
	private void NotifyError(string message) => Toasts.Error(View, message);

	/// <summary>宿主快捷键：发送当前输入框内容；正在运行则中断。</summary>
	public void SendFromShortcut() => Send();

	internal Task PendingSend => _pendingSend;

	/// <summary>焦点移入输入框。</summary>
	public void FocusComposerNow() => _composer.Focus();

	/// <summary>
	/// 「编辑重发」：把原文放回输入框，并记下要回到的条目。发送时先在该处开分支，
	/// 于是修改后的提问落在新分支上，原历史仍可回看。
	/// </summary>
	private void PrepareEdit(string entryId, string text)
	{
		if (IsStreaming)
		{
			return;
		}

		_pendingBranchEntryId = entryId;
		_composer.Text = text;
		_composer.Focus();
		_editNotice.IsVisible(true);
	}

	/// <summary>由外部（快捷发送、命令面板）触发发送。带附件时以内容块形式发送。</summary>
	public async Task SendAsync(string text)
	{
		if (_sending || _chat.IsStreaming || _disposed)
		{
			return;
		}
		if (_loadingAttachments > 0)
		{
			Notify(Strings.AttachmentLoading);
			return;
		}
		if (_attachments.Count > 0 && !Session.Model.SupportsImages)
		{
			NotifyError(Strings.ImageModelRequired);
			_composer.Text = text;
			return;
		}

		_sending = true;
		var sendAbort = new CancellationTokenSource();
		lock (_sendAbortGate)
		{
			_sendAbort = sendAbort;
		}

		var cancellationToken = sendAbort.Token;
		string? failure = null;
		Render();
		StateChanged?.Invoke(this);

		try
		{
			// 发送前自动打一个检查点（每仓库每分钟最多一次，且工作区有改动才打），
			// 让模型的改动可以回滚；快照走后台，避免阻塞界面。
			await Task.Run(() => Checkpoints.AutoSnapshot(Session.Cwd, TimeSpan.FromMinutes(1)), cancellationToken).ConfigureAwait(false);
			// 检查点期间用户也可能按下中断：此时 agent 还没开始，必须在这里就停下，
			// 否则中断按钮看似生效，消息仍会在快照结束后发出去。
			cancellationToken.ThrowIfCancellationRequested();

			// 编辑重发：先回到原提问之前（新会话文件），再发送修改后的内容。
			if (_pendingBranchEntryId is { Length: > 0 } branchId)
			{
				_pendingBranchEntryId = null;
				Dispatch(() => _editNotice.IsVisible(false));
				Session.ForkAt(branchId);
				RefreshPickers();
				Forked?.Invoke(this);
			}

			if (_attachments.Count > 0)
			{
				// 展开技能命令/提示词模板，并把图片作为内容块随消息发出。
				var message = Session.PrepareInput(text, [.. _attachments.Select(static item => item.Content)]);

				_attachments.Clear();
				Dispatch(RefreshAttachments);

				await Session.PromptAsync(message, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await _chat.SendAsync(text, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Send with attachments or fork", ex);
			failure = ex.Message;
		}
		finally
		{
			lock (_sendAbortGate)
			{
				if (ReferenceEquals(_sendAbort, sendAbort))
				{
					_sendAbort = null;
				}
			}

			sendAbort.Dispose();
			Dispatch(() =>
			{
				_sending = false;
				if (_disposed)
				{
					return;
				}

				Render();
				if (failure is not null)
				{
					NotifyError(Strings.SendFailed(failure));
				}

				RefreshTitle();
				StateChanged?.Invoke(this);
			});
		}
	}

	/// <summary>取消发送前的准备工作（检查点、分支切换等），并让正在进行的运行收到取消信号。</summary>
	private void CancelPendingSend()
	{
		CancellationTokenSource? cancellation;
		lock (_sendAbortGate)
		{
			cancellation = _sendAbort;
		}

		if (cancellation is null)
		{
			return;
		}

		try
		{
			cancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// 发送刚好结束并释放了 CTS。
		}
	}

	/// <summary>展开/收起会话内搜索框（供无人值守的界面演练调用）。</summary>
	public void ToggleSearchFromSmoke() => ToggleSearch();

	/// <summary>测试入口：走与输入框相同的过滤路径。</summary>
	internal void SetFilterFromTests(string value) => _searchBox.Text = value;
	internal ConversationTimeline TimelineFromTests => _timeline;
	internal bool FollowingFromTests => _conversationScroll.Following;

	/// <summary>只切 opacity，保留悬浮按钮的布局位置，避免隐藏期间布局变化后图标与底分离。</summary>
	private void SetScrollToLatestVisible(bool visible)
	{
		var wasHidden = _scrollToLatest.Opacity == 0;
		_scrollToLatest.Opacity = visible ? 1 : 0;
		_scrollToLatestShadow.Opacity = visible ? 1 : 0;
		_scrollToLatest.IsHitTestVisible = visible;
		_scrollToLatest.Focusable = visible;
		// 按钮一直参与 arrange；重新出现时再强制一次测量，清掉极端时序下内容层的旧位置。
		if (visible && wasHidden) _scrollToLatestShell.InvalidateMeasure();
	}

	/// <summary>
	/// 挂到窗口后把按钮外壳升入装饰层：装饰层与正文是两个独立的绘制根，
	/// 时间线行在流式/回收中被整块重绘时不会再把与自己重叠的按钮像素盖掉
	/// （保留场景对正文脏区的重绘不保证回放树序在它之上的兄弟内容）。
	/// 装饰器按 <c>_mainArea</c> 的 bounds 排布，位置行为与原先作为其子级完全一致；
	/// 未挂窗（单元测试直接排布 <c>tab.View</c>）时外壳留在主区里，行为不变。
	/// </summary>
	/// <summary>
	/// 浮层按整个窗口客户区排布：把外壳的边距对齐到消息区列，
	/// 按钮才始终居中在正文上方、贴住输入区上沿（而不是窗口底边）。
	/// </summary>
	private void UpdateFloatingShellMargins()
	{
		if (!_scrollToLatestHosted) return;
		var area = _mainArea.Bounds;
		// 挂层可能发生在首次布局前：此时还没有有效边界，等布局收敛后再对齐。
		if (area.Width <= 0 || area.Height <= 0) return;
		// Bounds 是窗口客户区坐标，浮层也按客户区排布：直接用主区边界做边距，
		// 不能再减 View 的偏移（侧栏在 View 外面，减了会把按钮推到整窗居中）。
		var left = Math.Max(0, area.Left);
		var right = Math.Max(0, View.Bounds.Right - area.Right);
		var bottom = Math.Max(0, View.Bounds.Bottom - area.Bottom) + 2;
		var margin = new Thickness(left, 0, right, bottom);
		if (_scrollToLatestShell.Margin == margin) return;
		_scrollToLatestShell.Margin(margin);
	}

	private void HandleShellRootChanged(Element? oldRoot, Element? newRoot)
	{
		// 挂到窗口级 OverlayLayer 而不是 Adorner：浮层在每帧都会重排重绘、且绘制在正文之上，
		// 不会像装饰层那样在保留合成的局部重绘里丢掉背景与阴影（偶发“只剩图标”）。
		if (newRoot is Window window)
		{
			if (_scrollToLatestHosted)
			{
				if (ReferenceEquals(_scrollToLatestWindow, window)) return;
				DetachFloatingShell();
			}
			if (ReferenceEquals(_scrollToLatestShell.Parent, _mainArea)) _mainArea.Remove(_scrollToLatestShell);
			window.OverlayLayer.Add(_scrollToLatestShell);
			_scrollToLatestWindow = window;
			_scrollToLatestHosted = true;
			UpdateFloatingShellMargins();
		}
		else if (newRoot is null)
		{
			DetachFloatingShell();
		}
	}

	private void DetachFloatingShell()
	{
		if (!_scrollToLatestHosted) return;
		_scrollToLatestHosted = false;
		_scrollToLatestWindow?.OverlayLayer.Remove(_scrollToLatestShell);
		_scrollToLatestWindow = null;
		if (_scrollToLatestShell.Parent is null) _mainArea.Children(_scrollToLatestShell);
	}

	/// <summary>按钮外壳：外圈是阴影画布，自身不参与命中，只有圆钮可点。</summary>
	private sealed class FloatingShell : Grid
	{
		protected override UIElement? OnHitTest(Point point)
		{
			UIElement? hit = null;
			for (var index = 0; index < Children.Count && hit is null; index++)
			{
				if (Children[index] is UIElement { IsVisible: true, IsHitTestVisible: true } child)
				{
					hit = child.HitTest(point);
				}
			}
			return hit;
		}
	}

	/// <summary>零尺寸观察者：只跟踪所在标签页的视觉根，不参与布局与命中。</summary>
	private sealed class ShellRootWatcher : Grid
	{
		public Action<Element?, Element?>? Changed;

		protected override void OnVisualRootChanged(Element? oldRoot, Element? newRoot)
		{
			base.OnVisualRootChanged(oldRoot, newRoot);
			Changed?.Invoke(oldRoot, newRoot);
		}
	}

	/// <summary>回到最新一条消息。</summary>
	private void ScrollToLatest() => _conversationScroll.ScrollToLatest();

	/// <summary>展开/收起会话内搜索框。</summary>
	private void ToggleSearch()
	{
		var show = !_searchBox.IsVisible;
		_searchBox.IsVisible(show);

		if (show)
		{
			_searchBox.Focus();
			return;
		}

		// 输入框的文本变化会走 ApplyFilter，清空后恢复到搜索前的阅读位置。
		if (_searchBox.Text is { Length: > 0 }) _searchBox.Text = string.Empty;
		else ApplyFilter(string.Empty);
	}

	/// <summary>
	/// 会话内过滤是显式搜索：进入时取消跟底并回到第一条命中，清空时回到搜索前的行（或末尾）。
	/// 否则虚拟化列表按旧偏移重新投影，读者会落在与搜索无关的位置。
	/// </summary>
	private void ApplyFilter(string next)
	{
		if (string.Equals(next, _filter, StringComparison.Ordinal)) return;
		var entering = _filter.Length == 0 && next.Length > 0;
		var leaving = _filter.Length > 0 && next.Length == 0;
		if (entering)
		{
			_filterAnchor = _timeline.CurrentReadingAnchor();
			_filterWasFollowing = _conversationScroll.Following;
		}

		_filter = next;
		if (next.Length > 0)
		{
			_conversationScroll.FollowSuspended = true;
			_conversationScroll.Browse();
			Render();
			// 过滤后的第一行才是搜索结果的开头；不沿用旧偏移，也不追随原会话末尾。
			_timeline.JumpToRow(0);
			return;
		}

		_conversationScroll.FollowSuspended = false;
		Render();
		if (!leaving) return;
		if (_filterWasFollowing)
		{
			_conversationScroll.ScrollToLatest();
		}
		else if (_timeline.IndexOfEntryId(_filterAnchor.EntryId) is var index && index >= 0)
		{
			_conversationScroll.Browse();
			_timeline.JumpToRow(index, _filterAnchor.WithinRow);
		}
		_filterAnchor = default;
	}

	/// <summary>
	/// 一条聊天记录是否命中当前过滤关键字（正文、思考、工具名，以及执行过程里的步骤）。
	/// 过程组行本身不带正文，不展开成员会让工具输出成为搜索盲区。
	/// </summary>
	internal static bool Matches(ChatEntry entry, string filter) =>
		filter.Length == 0 ||
		entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
		entry.Thinking.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
		(entry.ToolName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
		entry.ProcessEntries.Any(member => Matches(member, filter));

	/// <summary>切到分支树视图。</summary>
	public void ShowBranches() => ShowView(showChat: false);

	/// <summary>切回对话视图。</summary>
	public void ShowChat() => ShowView(showChat: true);

	/// <summary>当前会话可用的斜杠命令名。</summary>
	public IReadOnlyList<string> SlashCommands() => [.. Commands().Select(static command => command.Name)];

	/// <summary>把文本填进输入框并把焦点给它（命令面板用）。</summary>
	public void Prefill(string text)
	{
		_composer.Text = text;
		RefreshCommandBar();
		_composer.Focus();
	}

	/// <summary>在对话视图与分支树之间切换。</summary>
	private void ShowView(bool showChat)
	{
		_conversationScroll.Stop();
		_messages.IsVisible(showChat);
		UpdateOutlineVisibility();
		SetEmptyState(showChat && _chat.Entries().Count == 0);
		_noMatches.IsVisible(showChat && _filter.Length > 0 && _messages.ItemsSource.Count == 0);
		_branchPanel.View.IsVisible(!showChat);

		if (showChat)
		{
			SetScrollToLatestVisible(!_conversationScroll.Following);
			Render();
		}
		else
		{
			SetScrollToLatestVisible(false);
			_branchPanel.Refresh();
		}
	}

	/// <summary>
	/// 空会话：提示文本与输入区（含状态栏）作为一个整体居中，落在消息区正中；
	/// 有记录后输入区回到贴底的自适应行。只换网格行，不重建输入框 —— 草稿、附件与焦点都保留。
	/// </summary>
	private void SetEmptyState(bool empty)
	{
		_emptyCard.IsVisible(empty);
		var row = empty ? 2 : 3;
		if (Grid.GetRow(_composerColumn) != row)
		{
			// 换行只发生在有/无记录之间：整棵树重排一次，行高与对齐立刻生效。
			Grid.SetRow(_composerColumn, row);
			View.InvalidateMeasure();
		}

		_composerColumn.VerticalAlignment(empty ? VerticalAlignment.Center : VerticalAlignment.Stretch);
	}

	/// <summary>选择图片并附加到下一次发送。图片以 Base64 随消息发给模型（`prompt` 的 images 字段同源）。</summary>
	private async Task AttachImageAsync()
	{
		string[]? files;
		try
		{
			files = await FileDialog.OpenFilesAsync(new OpenFileDialogOptions
			{
				Title = Strings.AttachFiles,
				Filters = [new FileFilter(Strings.AllFiles, "*.*")],
				Multiselect = true,
			});
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Choose image attachment", ex);
			NotifyError(Strings.AttachFailed(ex.Message));
			return;
		}

		if (files is null)
		{
			return;
		}

		await AddFilesAsync(files);
	}

	/// <summary>刷新附件条：每个附件一个可移除的标签。</summary>
	private void RefreshAttachments()
	{
		_attachmentBar.Clear();

		if (_attachments.Count == 0)
		{
			_attachmentBar.IsVisible(false);
			return;
		}

		foreach (var (path, content) in _attachments)
		{
			var captured = path;
			_attachmentBar.Children(new StackPanel().Orientation(Orientation.Horizontal).Spacing(2).Children(
				new Button().Ghost().Content(new TextBlock().Text(Path.GetFileName(path)).FontSize(AppTheme.FontSizeMicro))
					.HoverTip(Strings.ImagePreviewOpen).OnClick(() => PreviewAttachment(content)),
				AppIcons.Button("x", Strings.RemoveAttachment, () =>
				{
					_attachments.RemoveAll(item => item.Path == captured);
					RefreshAttachments();
				})));
		}

		_attachmentBar.IsVisible(true);
	}

	/// <summary>把最后一条助手回复复制到剪贴板。</summary>
	private void CopyLastReply()
	{
		var text = _chat.LastAssistantText();
		if (string.IsNullOrEmpty(text))
		{
			Notify(Strings.NothingToCopy);
			return;
		}

		CopyText(text);
	}

	/// <summary>复制一段文本，并用 toast 告知结果。</summary>
	private void CopyText(string text)
	{
		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		if (clipboard?.TrySetText(text) == true) Toasts.Success(View, Strings.Copied);
		else NotifyError(Strings.ClipboardUnavailable);
	}

	/// <summary>重新发送最后一条用户消息（会话历史保留，等同于再问一次）。</summary>
	private async Task RetryAsync()
	{
		var text = _chat.LastUserText();
		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		await SendAsync(text).ConfigureAwait(false);
	}

	public void ShowModelPicker()
	{
		if (!IsStreaming && !_disposed)
		{
			_modelPicker.Show(View);
		}
	}

	private void SelectModel(Model model)
	{
		if (IsStreaming || _disposed)
		{
			return;
		}

		Session.SetModel(model);
		Session.SetThinkingLevel(ThinkingLevelResolver.Clamp(model, Session.ThinkingLevel));
		try
		{
			DesktopPreference.Load().RememberModel(model.Provider, model.Id).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Save recent model", ex);
		}

		RefreshPickers();
		Render();
	}

	/// <summary>测试当前模型的连通性，结果用 toast 提示。</summary>
	private async Task TestConnectionAsync()
	{
		if (_testing || _sending || _chat.IsStreaming || _disposed)
		{
			return;
		}

		_testing = true;
		Dispatch(() => Notify(Strings.TestingConnection));
		try
		{
			var result = await Session.TestConnectionAsync().ConfigureAwait(false);
			Dispatch(() =>
			{
				if (result.Ok)
					Toasts.Success(View, Strings.ConnectionOk(Session.Model.Provider, Session.Model.Id, result.LatencyMs));
				else
					NotifyError(Strings.ConnectionFailed(result.Error ?? "unknown"));
			});
		}
		finally
		{
			_testing = false;
			Dispatch(() => Render());
		}
	}

	/// <summary>切换计划模式（只读探索）。运行期间不可切换。</summary>
	private void TogglePlanMode()
	{
		if (IsStreaming || _disposed)
		{
			return;
		}

		Session.SetPlanMode(!Session.PlanModeEnabled);
		Render();
	}

	/// <summary>会话底部的终端面板是否展开。</summary>
	public bool IsTerminalOpen => _terminalOpen;

	/// <summary>输入框的终端键 / Ctrl+Shift+D：展开时收起，收起时展开。</summary>
	public void ToggleTerminal() => SetTerminalOpen(!_terminalOpen);

	/// <summary>
	/// 展开或收起底部终端。终端在第一次展开时才创建（会真的启动伪终端），之后收起只隐藏，
	/// shell 的历史与工作目录都不丢；展开时把键盘焦点交给它。
	/// </summary>
	public void SetTerminalOpen(bool open)
	{
		if (_disposed || _terminalOpen == open)
		{
			return;
		}

		if (!open)
		{
			// 收起前记下当前高度，下次展开沿用。
			if (_terminalPane.Bounds.Height >= AppTheme.TerminalDockMinHeight - 0.5)
			{
				_terminalDockHeight = Math.Clamp(_terminalPane.Bounds.Height,
					AppTheme.TerminalDockMinHeight, AppTheme.TerminalDockMaxHeight);
			}

			_terminalOpen = false;
			_terminalPane.IsVisible(false);
			UpdateTerminalButton();
			FocusComposer();
			return;
		}

		if (_terminal is null)
		{
			_terminal = new TerminalDock(() => Session.Cwd, compact: true, onClose: () => SetTerminalOpen(false));
			_terminalPane.Child(_terminal.View);
		}

		_terminalOpen = true;
		_terminalPane.IsVisible(true);
		_terminalSplit.SecondLength = GridLength.Pixels(_terminalDockHeight);
		UpdateTerminalButton();
		FocusTerminalSoon();
	}

	/// <summary>终端键在面板展开时常驻浮起，一眼看出终端是从这里打开的。</summary>
	private void UpdateTerminalButton() =>
		_terminalButton.StyleName(_terminalOpen ? AppStyles.SelectedButton : AppStyles.GhostButton);

	/// <summary>展开后再交焦点：面板这一帧还没布局完，终端输入层此时还收不到键盘。</summary>
	private void FocusTerminalSoon()
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null)
		{
			_terminal?.FocusTerminal();
			return;
		}

		dispatcher.BeginInvoke(() =>
		{
			if (!_disposed)
			{
				_terminal?.FocusTerminal();
			}
		});
	}

	/// <summary>思考强度菜单：与模型选择一样按当前模型支持项生成，当前项打勾。</summary>
	private void ShowThinkingMenu()
	{
		if (IsStreaming || _disposed)
		{
			return;
		}

		var levels = ThinkingLevelResolver.GetSupported(Session.Model);
		if (levels.Count == 0)
		{
			return;
		}

		// 点击后指针仍在按钮上，先收起悬停提示，避免它压在刚打开的菜单上。
		HoverHints.Cancel(_thinkingButton);

		// 当前级别在菜单图标列打勾；MewUI 0.22.1 的 MenuItem 还没有 IsCheckable，
		// 用 IconTemplate 表达同一层信息。
		var checkIcon = new IconTemplate(size => new GlyphElement()
			.Kind(GlyphKind.CheckMark)
			.GlyphSize(4)
			.Foreground(AppTheme.Accent)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center));

		var menu = new ContextMenu();
		foreach (var level in levels)
		{
			var command = new Command($"pi.thinking.{level}", Strings.ThinkingLevelName(level));
			_thinkingButton.Commands.Register(command, () => SelectThinkingLevel(level));
			menu.Items.Add(new MenuItem(command)
			{
				Icon = string.Equals(level, Session.ThinkingLevel, StringComparison.Ordinal) ? checkIcon : null,
			});
		}

		menu.Show(_thinkingButton);
	}

	private void SelectThinkingLevel(string level)
	{
		if (IsStreaming || _disposed)
		{
			return;
		}

		Session.SetThinkingLevel(level);
		RefreshPickers();
		Render();
	}

	/// <summary>
	/// 输入以 <c>/</c> 开头时，在输入框上方列出匹配的斜杠命令（提示词模板与技能），点击即补全。
	/// 命令数据来自会话发现的结果，与 RPC 的 <c>get_commands</c> 同源。
	/// </summary>
	private void RefreshCommandBar()
	{
		var text = _composer.Text;
		var slash = text.StartsWith('/') ? text[1..] : null;

		// 只在该输入第一个词时提示；出现空格说明已经在写参数了。
		if (slash is null || slash.Contains(' ') || slash.Contains('\n'))
		{
			_commandBar.IsVisible(false);
			return;
		}

		var matches = Commands()
			.Where(command => command.Name.StartsWith(slash, StringComparison.OrdinalIgnoreCase))
			.Take(8)
			.ToList();

		if (matches.Count == 0)
		{
			_commandBar.IsVisible(false);
			return;
		}

		_commandBar.Clear();
		foreach (var (name, description) in matches)
		{
			var label = description.Length > 0 ? $"/{name} — {description}" : $"/{name}";
			_commandBar.Children(new Button()
				.Content(new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro))
				.OnClick(() =>
				{
					_composer.Text = $"/{name} ";
					_composer.Focus();
				}));
		}

		_commandBar.IsVisible(true);
	}

	/// <summary>可用的斜杠命令：提示词模板 + 技能。</summary>
	private IEnumerable<(string Name, string Description)> Commands()
	{
		foreach (var template in Session.PromptTemplates)
		{
			yield return (template.Name, string.Empty);
		}

		foreach (var skill in Session.DiscoveredSkills)
		{
			yield return ($"skill:{skill.Name}", skill.Description);
		}
	}

	/// <summary>同步当前模型与思考强度铭牌。</summary>
	private void RefreshPickers()
	{
		var modelName = Session.Model.Name.Length > 0 ? Session.Model.Name : Session.Model.Id;
		_modelLabel.Text($"{Session.Model.Provider}/{modelName}").TextTrimming(TextTrimming.CharacterEllipsis);
		_modelButton.HoverTip($"{Session.Model.Provider}/{Session.Model.Id}\n{Strings.ModelCapabilities(Session.Model)}");

		var thinking = Strings.ThinkingLevelName(Session.ThinkingLevel);
		// 铭牌跟 PiDeck 同款拼成一行：provider/model · max ▾；级别用原始短名，中文全名放在提示里。
		_thinkingLabel.Text($"· {Session.ThinkingLevel}");
		_thinkingButton.HoverTip($"{Strings.ThinkingLevelTitle}：{thinking}");
	}

	/// <summary>在光标处插入 @，开始一个文件引用。</summary>
	private void InsertReference()
	{
		if (_disposed) return;
		_composer.Focus();
		_composer.EnterText(_composer.CaretPosition, 0, "@");
	}

	/// <summary>
	/// 会话进行态（PiDeck respondingKind 同语义）：压缩 → 执行工具 → 推流正文 → 思考。
	/// 从最新条目向回找第一个仍在流式的活动，空闲返回 null。
	/// </summary>
	private (string Kind, string Icon, string Role, string Text)? BusyIndicator(IReadOnlyList<ChatEntry> rows)
	{
		if (Session.IsCompacting)
		{
			return ("compacting", "refresh-cw", AppStyles.RoleSubtle, Strings.Compacting);
		}

		for (var index = rows.Count - 1; index >= 0; index--)
		{
			var row = rows[index];
			if (row.Role == ChatRole.Process)
			{
				var members = row.ProcessEntries;
				for (var member = members.Count - 1; member >= 0; member--)
				{
					var item = members[member];
					if (item.IsStreaming || item.IsThinking) return KindOf(item);
				}

				continue;
			}

			if (row.IsStreaming) return KindOf(row);
		}

		return null;

		static (string, string, string, string) KindOf(ChatEntry item) => item.Role switch
		{
			ChatRole.Tool => ("executing", "terminal", AppStyles.RoleInfo, Strings.BusyExecuting),
			ChatRole.Assistant when item.Text.Length > 0 || item.Images.Count > 0 =>
				("responding", "message-square", AppStyles.RoleInfo, Strings.Streaming),
			_ => ("thinking", "sparkles", AppStyles.RoleThinking, Strings.ThinkingActive),
		};
	}

	/// <summary>保留未变化的消息控件和折叠状态，只重绘变化后的尾部。</summary>
	private void Render(bool force = false)
	{
		if (_disposed)
		{
			return;
		}

		var all = _chat.Entries();
		var matched = _filter.Length == 0 ? all : [.. all.Where(entry => Matches(entry, _filter))];
		if (!ReferenceEquals(_projectedSource, all) || _projectedFilter != _filter || _projectedBusy != IsStreaming)
		{
			_projectedRows = _projector.Project(matched, IsStreaming);
			_projectedSource = all;
			_projectedFilter = _filter;
			_projectedBusy = IsStreaming;
		}
		var entries = _projectedRows;

		if (_filter.Length > 0)
		{
			_filterStatus.Text(Strings.FilterMatches(matched.Count, all.Count));
			_filterStatus.IsVisible(true);
		}
		else
		{
			_filterStatus.IsVisible(false);
		}

		_headerRow.IsVisible(entries.Count > 0);

		var status = $"{Session.Model.Provider} / {Session.Model.Id} · {Strings.ThinkingShort(Session.ThinkingLevel)}";
		if (_chat.Error is { Length: > 0 } error)
		{
			status = $"{status}  ·  {Strings.RoleError}: {error}";
		}

		_status.Text(status).HoverTip(status);
		var display = StatisticsDisplay();
		if (!ReferenceEquals(_renderedStatistics, display))
		{
			var summary = display.Summary();
			_statisticsLine.Text(summary).HoverTip(summary);
			_contextLabel.Text(display.ContextLabel);
			_contextBar.Value(Math.Clamp(display.ContextPercent ?? 0, 0, 100));
			_renderedStatistics = display;
		}
		var todo = Session.CurrentTodo();
		if (!ReferenceEquals(todo, _renderedTodo))
		{
			_renderedTodo = todo;
			RefreshTodoStrip(todo);
		}
		var busy = _sending || _chat.IsStreaming;
		var indicator = BusyIndicator(all);
		var kindChanged = indicator?.Kind != _busyKind;
		_busyKind = indicator?.Kind;
		_busyLine.IsVisible(busy && indicator is not null);
		if (indicator is { } active && kindChanged)
		{
			_busyIcon.Source(AppIcons.Get(active.Icon, AppStyles.ColorForRole(active.Role)));
			_busyText.Text(active.Text);
		}
		if (_statisticsPanel.IsOpen)
		{
			_statisticsPanel.Update(display, busy);
			_usageSummary.Show();
		}
		if (_lastBusy != busy)
		{
			_lastBusy = busy;
			StateChanged?.Invoke(this);
		}
		_sendLabel.Text(busy ? Strings.Abort : Strings.Send);
		_sendIcon.Source(AppIcons.Get(busy ? "x" : "arrow-up", AppTheme.TextOnAccent));
		_sendMenuButton.IsVisible(busy);
		_sendMenuButton.IsEnabled = _chat.IsStreaming;
		_sendDivider.IsVisible(busy);
		_modelButton.IsEnabled = !busy;
		_thinkingButton.IsEnabled = !busy;
		_planLabel.Text(Session.PlanModeEnabled ? Strings.PlanModeOn : Strings.PlanMode);
		_planButton.IsEnabled = !busy;
		_testButton.IsEnabled = !busy && !_testing;
		_retry.IsEnabled = !busy && all.Any(static row => row.Role == ChatRole.User);
		_copyReply.IsEnabled = all.Any(static row => row.Role == ChatRole.Assistant && row.Text.Length > 0);

		_timeline.SetEntries(entries, force, _projector.LastStablePrefix, _projector.LastPrefixReused);
		SyncOutline(entries);
		SetEmptyState(all.Count == 0 && _messages.IsVisible);
		_noMatches.IsVisible(all.Count > 0 && entries.Count == 0 && _messages.IsVisible);
		if (all.Count == 0)
		{
			_conversationScroll.Stop();
		}

		foreach (var row in _timeline.VisibleRows)
		{
			if (row is FrameworkElement { Tag: StackPanel actions })
			{
				foreach (var action in actions.Children.OfType<Button>())
				{
					action.IsEnabled = !busy || HoverHints.Text(action) == Strings.CopyUserMessage;
				}
			}
		}

		_conversationScroll.FollowContent();
	}


	/// <summary>重建待办卡；只在计划对象变化时调用，平时零成本。</summary>
	private void RefreshTodoStrip(TodoState state)
	{
		var plan = state.ActivePlan;
		if (plan is null || plan.Items.Count == 0)
		{
			if (_renderedPlan is not null)
			{
				_renderedPlan = null;
				_todoRows.Clear();
			}
			_todoCard.Show(false);
			RefreshWidgetColumn();
			return;
		}

		var completed = 0;
		var inProgress = 0;
		foreach (var item in plan.Items)
		{
			if (item.Status == "completed") completed++;
			else if (item.Status == "in_progress") inProgress++;
		}

		_todoCard.Meta = Strings.TodoPlanProgress(completed, inProgress, plan.Items.Count - completed - inProgress);
		// 计划没变（例如只是重新渲染）时保留已建的行，避免每 40ms 抖动一次。
		if (!ReferenceEquals(_renderedPlan, plan))
		{
			_renderedPlan = plan;
			_todoRows.Clear();
			foreach (var item in plan.Items)
			{
				_todoRows.Children(TodoRow(item));
			}
		}

		_todoCard.Show(true);
		RefreshWidgetColumn();
	}

	/// <summary>一条待办：状态字形 + 可截断的正文，整行悬停显示完整文本。</summary>
	private static UIElement TodoRow(TodoItem item)
	{
		var text = new TextBlock()
			.Text(item.Text.Replace('\n', ' '))
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.TextWrapping(TextWrapping.NoWrap)
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space2 + 2, 0, 0, 0))
			.HoverTip(item.Text);

		if (item.Status == "completed") text.SubtleText();

		return new DockPanel()
			.LastChildFill()
			.MinHeight(20)
			.Children(new TodoStatusGlyph(item.Status).DockLeft(), text);
	}

	/// <summary>扩展 widget 卡：每个 key 一段，先用等宽小字标出 key，再逐行显示文本。</summary>
	private void RefreshExtensionWidgets()
	{
		_extensionBody.Clear();
		if (_extensionWidgets.Count == 0)
		{
			_extensionCard.Show(false);
			RefreshWidgetColumn();
			return;
		}

		foreach (var (key, lines) in _extensionWidgets.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var section = new StackPanel().Spacing(4);
			section.Children(new TextBlock()
				.Text(ExtensionKeyLabel(key))
				.FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeMicro)
				.SubtleText());
			foreach (var line in lines)
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				section.Children(new TextBlock()
					.Text(line)
					.FontSize(AppTheme.FontSizeCaption)
					.MutedText()
					.TextWrapping(TextWrapping.Wrap));
			}
			_extensionBody.Children(section);
		}

		_extensionCard.Meta = Strings.ExtensionWidgetsCount(_extensionWidgets.Count);
		_extensionCard.Show(true);
		RefreshWidgetColumn();
	}

	private static string ExtensionKeyLabel(string key)
	{
		foreach (var prefix in new[] { "pi-deck-", "piweft-", "pi-" })
		{
			if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return key[prefix.Length..].Replace('-', ' ').Replace('_', ' ');
			}
		}

		return key.Replace('-', ' ').Replace('_', ' ');
	}

	private void RefreshWidgetColumn() => _widgetColumn.IsVisible(_todoCard.View.IsVisible || _extensionCard.View.IsVisible);

	private SessionStatisticsDisplay StatisticsDisplay()
	{
		var manager = Session.Session;
		var window = Session.Model.ContextWindow;
		if (_statisticsDisplay is null || !ReferenceEquals(_statisticsManager, manager) ||
			_statisticsRevision != manager.Revision || _statisticsWindow != window)
		{
			_statisticsRevision = manager.Revision;
			_statisticsDisplay = SessionStatisticsDisplay.Create(Session);
			_statisticsManager = manager;
			_statisticsWindow = window;
		}
		return _statisticsDisplay;
	}

	public void ShowStatistics() => _statisticsPanel.Show(View, StatisticsDisplay(), IsStreaming || Session.IsCompacting);
	public void ShowStatisticsDetails()
	{
		ShowStatistics();
		_statisticsPanel.ShowDetails();
	}
	public void HideStatistics() => _statisticsPanel.Hide();
	public void ShowProviderUsage() => _usagePanel.Show(View);
	private void ConfigureProviderUsage()
	{
		_statisticsPanel.Hide();
		_usagePanel.Show(View);
		_returnToStatistics = true;
	}
	internal void HideTransientPanels()
	{
		_outline.HidePreview();
		_returnToStatistics = false;
		_imagePreview.Hide();
		_statisticsPanel.Hide();
		_modelPicker.Hide();
		_usagePanel.Hide();
	}

	private async Task CompactAsync()
	{
		if (IsStreaming || Session.IsCompacting || _disposed) return;
		var cancellation = new CancellationTokenSource();
		_compactionAbort = cancellation;
		_sending = true;
		string? failure = null;
		Render();
		try
		{
			await Session.CompactAsync(cancellationToken: cancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Compact context", ex);
			failure = ex.Message;
		}
		finally
		{
			Dispatch(() =>
			{
				_compactionAbort = null;
				cancellation.Dispose();
				_sending = false;
				if (_disposed) return;
				Render();
				if (failure is not null) NotifyError(Strings.SendFailed(failure));
			});
		}
	}

	private UIElement Row(ChatEntry entry)
	{
		var row = entry.Role switch
		{
			ChatRole.User => Bubble(entry),
			ChatRole.Process => new ExecutionProcessView(entry, Row, _textFocus).View,
			ChatRole.Tool => new ToolMessageView(entry, _textFocus, PreviewImage).View,
			ChatRole.Error => Card(entry, Strings.RoleError),
			_ => new AssistantMessageView(entry, CopyText, _textFocus, PreviewImage, OpenMessageLink).View,
		};
		_textFocus.TrackTree(row);
		return row;
	}

	/// <summary>
	/// 用户消息：右侧内嵌气泡，与助手回复（左侧正文）一眼分开；技能上下文收成 chip，
	/// 文件引用在正文里由投影层显示成行内 chip。正文保持可选中，时间与操作在气泡下方，图片缩略图在气泡上方。
	/// 外层 StackPanel 挂 actions（Tag 约定）。
	/// </summary>
	private UIElement Bubble(ChatEntry entry)
	{
		var (skills, text) = UserMessageText.Parse(entry.Text);
		var (collapsedText, collapsible) = UserMessageText.Collapse(text);
		var expanded = false;

		var bubble = new StackPanel().Spacing(AppTheme.Space2);
		if (skills.Count > 0)
		{
			var chips = new WrapPanel().Spacing(AppTheme.Space1).HorizontalAlignment(HorizontalAlignment.Left);
			foreach (var skill in skills) chips.Children(SkillChip(skill));
			bubble.Children(chips);
		}

		MultiLineTextBox? body = null;
		if (text.Length > 0)
		{
			body = MessageText.Create(collapsible ? collapsedText : text);
			UserReferenceRichText.Attach(body, editable: false,
				onOpen: path => Shell.OpenMessageLink(path, Session.Cwd, message => NotifyError(message)),
				cwd: Session.Cwd, skillLocation: SkillLocation);
			_textFocus.Track(body);
			bubble.Children(body);
		}

		if (body is not null && collapsible)
		{
			var expandLabel = new TextBlock().Text(Strings.MessageExpand)
				.FontSize(AppTheme.FontSizeMicro).MutedText();
			bubble.Children(new Button().Ghost()
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.MinHeight(22)
				.HorizontalAlignment(HorizontalAlignment.Right)
				.Content(expandLabel)
				.OnClick(() =>
				{
					expanded = !expanded;
					MessageText.Update(body, expanded ? text : collapsedText);
					expandLabel.Text(expanded ? Strings.MessageCollapse : Strings.MessageExpand);
				}));
		}

		var card = new Border()
			.StyleName(AppStyles.UserMessage)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.MaxWidth(AppTheme.MessageMaxWidth)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Child(bubble);
		card.IsVisible(text.Length > 0 || skills.Count > 0);

		var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(0);
		if (entry.Text.Length > 0)
		{
			actions.Children(AppIcons.Button("copy", Strings.CopyUserMessage, () => CopyText(entry.Text), size: 14));
		}
		if (entry.EntryId is { Length: > 0 } entryId)
		{
			if (entry.Text.Length > 0)
			{
				actions.Children(
					AppIcons.Button("pencil", Strings.EditAndResend, () => PrepareEdit(entryId, entry.Text), size: 14)
						.IsEnabled(!IsStreaming));
			}
			actions.Children(
				AppIcons.Button("git-branch", Strings.BranchHere, () => BranchAt(entryId), size: 14)
					.IsEnabled(!IsStreaming));
		}

		var time = Timestamp(entry);
		var footer = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Margin(new Thickness(0, 0, AppTheme.Space1, 0))
			.Children(actions);
		if (time.Length > 0)
		{
			footer.Children(new TextBlock().Text(time).FontSize(AppTheme.FontSizeMicro)
				.FontFamily(AppTheme.MonoFont).SubtleText());
		}
		footer.IsVisible(time.Length > 0 || actions.Children.Count > 0);

		var images = new WrapPanel().Spacing(AppTheme.Space2).HorizontalAlignment(HorizontalAlignment.Right);
		foreach (var image in entry.Images)
		{
			images.Children(ChatImages.CreateThumbnail(image, PreviewImage));
		}
		images.IsVisible(entry.Images.Count > 0);

		var row = new StackPanel()
			.Spacing(AppTheme.Space1)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Children(images, card, footer);
		// 忙碌时由 Render 统一切换复制之外的动作；Tag 保持与旧布局一致。
		row.Tag = actions;
		return row;
	}

	/// <summary>技能上下文 chip：蓝色信息色 + sparkles，tooltip 显示技能文件位置。</summary>
	/// <summary>技能 chip 的悬浮提示用：技能名 → SKILL.md 的绝对路径。</summary>
	private string? SkillLocation(string name)
	{
		foreach (var skill in Session.DiscoveredSkills)
		{
			if (string.Equals(skill.Name, name, StringComparison.Ordinal)) return skill.FilePath;
		}
		return null;
	}

	private static UIElement SkillChip(UserSkillChip skill)
	{
		return new Border()
			.BorderThickness(0)
			.CornerRadius(AppTheme.RadiusXs)
			.Padding(new Thickness(6, 2, 7, 2))
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).Thinking.WithAlpha(0x1f))
			.HoverTip(skill.Location.Length > 0 ? skill.Location : skill.Name)
			.Child(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(4)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					AppIcons.Icon("sparkles", AppStyles.RoleThinking, 12),
					new TextBlock().Text(skill.Name).FontSize(AppTheme.FontSizeMicro)
						.ThinkingText().VerticalAlignment(VerticalAlignment.Center)));
	}

	private static string Timestamp(ChatEntry entry) => entry.Timestamp > 0
		? DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToLocalTime().ToString("HH:mm")
		: string.Empty;

	/// <summary>
	/// 回到这条用户消息之前重开一条分支：会话文件会换成新文件（旧文件保留在历史里），
	/// 之后输入的内容都落在新分支上。
	/// </summary>
	private void BranchAt(string entryId)
	{
		if (IsStreaming)
		{
			return;
		}

		try
		{
			Session.ForkAt(entryId);
			RefreshPickers();
			Render();
			RefreshTitle();
			Forked?.Invoke(this);
			Toasts.Success(View, Strings.Branched(Session.Session.SessionFile ?? string.Empty));
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Fork session", ex);
			NotifyError(Strings.BranchFailed(ex.Message));
		}
	}

	/// <summary>消息缩略图与预览复用同一个图片源。</summary>
	internal static UIElement ImageView(ChatImage image, Action<ImageSource>? preview = null) => ChatImages.Create(image, preview);

	private void OpenMessageLink(string url) => Shell.OpenMessageLink(url, Session.Cwd, message => NotifyError(message));

	private void PreviewImage(ImageSource source) => _imagePreview.Show(View, source);

	private void PreviewAttachment(ImageContent image)
	{
		try { PreviewImage(ChatImages.Source(new ChatImage(image.Data, image.MimeType))); }
		catch (FormatException) { NotifyError(Strings.BrokenImage); }
	}

	/// <summary>右键菜单「粘贴」的共享实现：图片优先，纯文本回退到文本框的默认粘贴。</summary>
	/// <summary>运行中发送键右侧的菜单：引导当前回合、排到下一轮或中断。</summary>
	private void ShowSendMenu()
	{
		if (_disposed) return;
		if (!_chat.IsStreaming) return;

		HoverHints.Cancel(_sendMenuButton);
		var menu = new ContextMenu();
		void Add(string label, Action action)
		{
			var command = new Command($"pi.send.{menu.Items.Count}", label);
			_sendMenuButton.Commands.Register(command, action);
			menu.Items.Add(new MenuItem(command));
		}

		Add(Strings.SteerCurrentTurn, () => QueueDraft(steer: true));
		Add(Strings.QueueNextTurn, () => QueueDraft(steer: false));
		menu.AddSeparator();
		Add(Strings.Abort, Send);
		menu.Show(_sendMenuButton);
	}

	/// <summary>把当前草稿排入正在运行的代理；保留输入控件，只清空草稿。</summary>
	internal void QueueDraft(bool steer)
	{
		if (_disposed || !_chat.IsStreaming) return;
		if (_loadingAttachments > 0)
		{
			Notify(Strings.AttachmentLoading);
			return;
		}
		var text = _composer.Text;
		if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0) return;
		if (_attachments.Count > 0 && !Session.Model.SupportsImages)
		{
			NotifyError(Strings.ImageModelRequired);
			return;
		}

		try
		{
			var images = _attachments.Select(static item => item.Content).ToArray();
			var queued = steer ? _chat.Steer(text, images) : _chat.FollowUp(text, images);
			if (!queued) return;
			_composer.Text = string.Empty;
			_attachments.Clear();
			RefreshAttachments();
			Notify(steer ? Strings.SteerQueued : Strings.FollowUpQueued);
			FocusComposer();
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Queue running message", ex);
			NotifyError(Strings.SendFailed(ex.Message));
		}
	}

	private void PasteFromClipboard()
	{
		if (_disposed) return;
		if (PasteImageFromClipboard()) return;
		UserReferenceRichText.NormalizeForEdit(_composer);
		_composer.Paste();
	}

	private void CutFromComposer()
	{
		if (_disposed) return;
		UserReferenceRichText.NormalizeForEdit(_composer);
		_composer.Cut();
	}

	/// <summary>粘贴菜单项的可用性：剪贴板有图片或文本时都允许执行。</summary>
	private bool CanPasteFromClipboard()
	{
		if (_disposed || _composer.IsReadOnly) return false;
		if (ClipboardImages.Service?.HasImage() == true) return true;
		var clipboard = _composer.ClipboardService ??
			(Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null);
		return clipboard?.HasText() == true;
	}

	/// <summary>剪贴板里是图片时把它附加到输入框；返回是否接管了这次按键。</summary>
	private bool PasteImageFromClipboard()
	{
		if (_disposed || ClipboardImages.Service is not { } service || !service.HasImage()) return false;
		_pendingPaste = PasteImageAsync(service);
		return true;
	}

	/// <summary>最近一次剪贴板图片粘贴；测试与交互诊断用它等待后台读取结束。</summary>
	internal Task PendingPaste => _pendingPaste;

	/// <summary>窗口级图片预览浮层；浮层不在会话可视树里，测试与交互诊断经这里取其中的控件。</summary>
	internal ImagePreview ImagePreview => _imagePreview;

	/// <summary>读取剪贴板图片并逐个附加；读取失败时说明原因，避免按键被吞掉后没有任何反馈。</summary>
	private async Task PasteImageAsync(IClipboardImages service)
	{
		IReadOnlyList<ClipboardAttachment> attachments;
		try
		{
			attachments = await service.ReadAsync().ConfigureAwait(true);
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Paste clipboard image", ex);
			Dispatch(() => { if (!_disposed) NotifyError(Strings.ClipboardImageUnavailable); });
			return;
		}

		if (_disposed) return;
		if (attachments.Count == 0)
		{
			NotifyError(Strings.ClipboardImageUnavailable);
			return;
		}

		foreach (var attachment in attachments) AddImage(attachment.Name, attachment.Content);
		FocusComposer();
	}

	/// <summary>带标题与时间的通用卡片，用于错误等系统类消息。</summary>
	private UIElement Card(ChatEntry entry, string label)
	{
		var text = MessageText.Create(entry.Text, fontSize: AppTheme.FontSizeCaption)
			.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).Error);
		text.Tag = null;
		_textFocus.Track(text);
		var content = new StackPanel()
			.Spacing(4)
			.Children(Header(string.Empty, entry, label), text);

		// 错误同样走「纬线」形态：左缘红线 + 内嵌底，不再包整圈边框。
		var thread = new Border()
			.Width(AppTheme.ThreadWidth)
			.CornerRadius(AppTheme.RadiusXs)
			.VerticalAlignment(VerticalAlignment.Stretch)
			.WithTheme((theme, element) => element.Background = AppTheme.For(theme).Error);

		return new DockPanel().LastChildFill()
			.Children(thread.DockLeft(), new Border()
				.WithTheme((theme, element) => element.Background = AppTheme.For(theme).CardBackgroundAlt)
				.CornerRadius(AppTheme.RadiusSm)
				.Padding(new Thickness(AppTheme.Space3))
				.Margin(new Thickness(AppTheme.Space2, 0, 0, 0))
				.MaxWidth(AppTheme.MessageMaxWidth)
				.Child(content));
	}

	/// <summary>角色行：角色名 + 时间。</summary>
	private static UIElement Header(string prefix, ChatEntry entry, string label)
	{
		var name = prefix.Length > 0 ? $"{prefix}{label}" : label;
		var time = entry.Timestamp > 0
			? DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToLocalTime().ToString("HH:mm")
			: string.Empty;

		// 角色铭牌：等宽小字，工坊铭牌而非标题。
		return new TextBlock()
			.Text(time.Length > 0 ? $"{name}  ·  {time}" : name)
			.FontSize(AppTheme.FontSizeMicro)
			.FontFamily(AppTheme.MonoFont)
			.SubtleText();
	}

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
}
