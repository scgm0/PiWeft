using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;
using Aprillz.MewUI.Platform;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Security;
using PiWeft.CodingAgent.Tools;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// 一个会话标签页：消息区 + 输入区 + 该会话的模型/思考级别与用量。
/// 每个标签持有自己的 <see cref="CodingAgentSession"/>，因此多个会话可以并行运行。
public sealed partial class SessionTab : IDisposable
{
	// 会话模型可以是后台预建的（长会话的投影要上百毫秒），默认自己新建一个。
	private readonly ChatViewModel _chat;
	private readonly ConversationTimeline _timeline;
	private readonly DispatcherTimer _renderTimer = new(TimeSpan.FromMilliseconds(40));
	/// 进行态心跳：等待期（没字可长、没事件可收）每 500ms 推一格呼吸点与轮播，
	/// 顺便带退避期的重试倒计时（每秒变化一次，一帧就够了）。不跑回合时停着，不占调度。
	private readonly DispatcherTimer _busyTimer = new(TimeSpan.FromMilliseconds(RespondingStatusPolicy.PulseStepMs));
	private readonly SendOptionsMenu _sendMenu = new();
	private int _renderPending;
	private volatile bool _disposed;
	private bool _sending;
	private Task _pendingSend = Task.CompletedTask;
	private Task _pendingPaste = Task.CompletedTask;
	private CancellationTokenSource? _compactionAbort;
	private readonly Lock _sendAbortGate = new();
	private CancellationTokenSource? _sendAbort;
	private readonly ScrollViewer _messages;
	private readonly ScrollViewer _scroll;
	private readonly ConversationScroll _conversationScroll;
	private readonly TextFocusScrollGuard _textFocus;
	private readonly MultiLineTextBox _composer;
	/// 占位提示。实例一律经 <see cref="BuildComposerHint"/> 创建（换新见 <see cref="ShowComposerHint"/>）：
	/// 每一枚都不能命中，否则提示盖在输入框上会吞掉那一行的点击。
	private TextBlock _composerHint = BuildComposerHint();
	/// 最近一次组合事件。只在框架没有实现 <c>ITextCompositionClient</c> 时兜底：
	/// 事件可能迟到或丢（Update 在 End 之后到达、组合取消不发 End），拿它当权威状态会让占位永久消失。
	private bool _composing;
	private DispatcherTimer? _composerHintTimer;
	private Grid? _composerHintHost;
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
	// 发送/复制按钮可用性的缓存：随投影结果更新，不必每帧整表扫描。
	private int _chromeRowCount = -1;
	private string _chromeFilter = string.Empty;
	private bool _rowsHaveUser;
	private bool _rowsHaveReply;
	private (string Id, string Provider, string Thinking, string? Error) _statusKey;
	private SessionStatisticsDisplay? _statisticsDisplay;
	private PiWeft.CodingAgent.Session.SessionManager? _statisticsManager;
	private long _statisticsRevision = -1;
	private int _statisticsWindow;
	private bool _lastBusy;
	private readonly WrapPanel _attachmentBar = new();
	private readonly Button _scrollToLatest;
	private readonly ShadowDecorator _scrollToLatestFloat;
	private readonly FloatingAnchor _scrollToLatestFloatHost;
	private readonly FloatingTracker _scrollToLatestWatcher;

	private bool _floatingHosted;
	private Window? _floatingWindow;
	private readonly TextBox _searchBox = new TextBox().PrimarySelection();
	private readonly TextBlock _filterStatus = new();
	private readonly TextBlock _extensionLine = new();
	private readonly Dictionary<string, string> _extensionStatuses = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IReadOnlyList<string>> _extensionWidgets = new(StringComparer.Ordinal);
	private string _filter = string.Empty;
	// 开始搜索前读者所在的行：清空过滤后回到原位置，不被重新投影的列表甩到末尾。
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

	private readonly List<(string Path, ImageContent Content)> _attachments = [];

	private string? _pendingBranchEntryId;
	private readonly TextBlock _emptyContext = new();
	private readonly Border _emptyCard;

	private readonly StackPanel _composerColumn;
	/// 输入区（widget 卡 / 空会话提示 / 进行态行 / 输入卡）。问询卡打开时整块收起，
	/// 与 PiDeck 一致：代理在等回答，输入框不该继续抢键盘焦点。
	private readonly StackPanel _inputStack;
	private readonly AskQuestionPanel _askPanel;
	private Border? _askCard;
	private Border _headerRow = null!;
	private readonly WrapPanel _commandBar = new();
	private readonly Border _editNotice;

	private const double ComposerWidgetListMaxHeight = 180;
	private readonly ComposerWidgetCard _todoCard;
	private readonly StackPanel _todoRows = new();
	private readonly ScrollViewer _todoScroll;
	private TodoState? _renderedTodo;
	private TodoPlan? _renderedPlan;

	/// 输入区上方的「排队中」卡：运行中再发的消息在这里逐条列出，可撤回输入框或丢弃。
	/// 队列为空时整卡不占位（与待办卡一致）。
	private readonly ComposerWidgetCard _queueCard;
	private readonly StackPanel _queueRows = new();
	private IReadOnlyList<ChatQueuedMessage>? _renderedQueue;

	private readonly ComposerWidgetCard _extensionCard;
	private readonly StackPanel _extensionBody = new();
	private readonly ReadingColumn _widgetColumn = new();
	private readonly Button _retry;
	private readonly Button _copyReply;
	private readonly Button _modelButton;
	private readonly TextBlock _modelLabel = new();
	private readonly Button _addButton;
	private readonly Button _modeChip;
	private readonly Image _goalModeIcon;
	private readonly Image _planModeIcon;
	private readonly TextBlock _modeChipLabel = new();
	private readonly ComposerAddMenu _composerAddMenu = new();
	private readonly ComposerPicker _composerPicker = new();
	/// 目标模式是输入区自己的开关：会话侧的目标要在第一条带标记的消息里才创建，
	/// 计划模式则直接落在 <see cref="Session"/> 上，两者互斥。
	private bool _goalMode;
	private ComposerMode _renderedMode = ComposerMode.Normal;
	private readonly Button _testButton;
	private bool _testing;
	private readonly ModelPicker _modelPicker;
	private readonly ImagePreview _imagePreview = new();
	private readonly Button _thinkingButton;
	private readonly TextBlock _thinkingLabel = new();
	private readonly ThinkingPicker _thinkingPicker = new();

	/// 输入区上方的会话进行态指示行（PiDeck responding 条）。
	/// 只要这一轮还在跑就一直占位：[图标] [呼吸点] [状态短语 · 事实后缀] | [功能提示]。
	private readonly Border _busyLine;
	private readonly ReadingColumn _busyColumn;
	private readonly Image _busyIcon;
	private readonly TextBlock _busyPulse = new();
	private readonly TextBlock _busyText = new();
	private readonly Border _busyDivider;
	private readonly TextBlock _busyTip = new();
	private RespondingKind? _busyKind;
	private RespondingStatus? _busyStatus;
	private long _turnStarted;

	private const string PendingReplyEntryId = "live-pending-reply";

	private ChatEntry? _pendingRow;

	private readonly Button _terminalButton;

	private readonly Button _securityButton;
	private readonly Image _securityOffIcon;
	private readonly Image _securityAlertIcon;
	private readonly Image _securityCheckIcon;
	private readonly Image _securityCustomIcon;
	private readonly SecurityMenu _securityMenu = new();
	private readonly Border _terminalPane;
	private readonly SplitPanel _terminalSplit;
	private TerminalDock? _terminal;
	private bool _terminalOpen;

	private double _terminalDockHeight = AppTheme.TerminalDockHeight;

	public event Action<SessionTab>? TitleChanged;

	public event Action<SessionTab>? Forked;

	/// 会话文件被原地重写（「编辑重发」的墓碑截断）后触发：会话 id 与文件不变，
	/// 宿主需要丢弃该文件的侧栏摘要缓存，否则会继续用截断前的预览与条数。
	public event Action<SessionTab>? Rewritten;

	public event Action<SessionTab>? StateChanged;

	/// 问询卡开关时触发。不能走 <see cref="StateChanged"/>：那条链路会顺带刷一遍会话列表
	/// （对全部会话文件做 stat），而开关一张卡片只是页签标题上那个小圆点变了。
	public event Action<SessionTab>? AskChanged;

	public event Action<SessionTab>? MessageSent;

	public SessionTab(CodingAgentSession session, IReadOnlyList<Model> models, string title,
		Func<IReadOnlyList<Model>>? availableModels = null, ChatViewModel? chat = null,
		SessionStatisticsDisplay? statistics = null)
	{
		// 后台已经 Attach 过就直接接手，否则 UI 线程要再投影一遍整个会话。
		_chat = chat ?? new ChatViewModel();
		if (statistics is not null)
		{
			// 状态条快照也是后台算好的：首帧不再在 UI 线程重扫整段历史。
			_statisticsDisplay = statistics;
			_statisticsManager = session.Session;
			_statisticsRevision = session.Session.Revision;
			_statisticsWindow = session.Model.ContextWindow;
		}
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
				// BorderSubtle 在深浅主题里都贴底色，轮廓用更亮的 TextFaint。
				button.BorderBrush = palette.TextFaint;
				button.BorderThickness = 1;
			});
		// 圆钮与投影合成一个元素：ShadowDecorator 在自己的画布内画阴影，圆钮是它唯一的子级。
		// 拆成「阴影画布 + 居中子级」是两块独立录制，局部重绘会出现只剩图标或只剩阴影的残缺。
		_scrollToLatestFloat = new ShadowDecorator
		{
			Child = _scrollToLatest,
			CornerRadius = 18,
			BlurRadius = 7,
			OffsetY = 2,
		};
		// 浮层宿主是唯一的位置计算处：每轮 arrange 按锚点的实时 Bounds 摆位，
		// 不再有把边界换算成 margin 存起来的第二处代码，也没有「挂载早于首次布局就拿不到边界」的特例。
		_scrollToLatestFloatHost = new FloatingAnchor
		{
			Anchor = _mainArea,
			// 圆钮下缘越过正文区下边的距离，保持与旧实现一致。
			Overlap = 3,
		}.Children(_scrollToLatestFloat);
		// 正文区几何变化的驱动器：铺满 _mainArea（无背景、不吃命中、DesiredSize 为 0 不影响布局）。
		// 浮层挂在窗口 OverlayLayer 上，Bounds 始终是整个客户区，正文区变化时框架会跳过它的 arrange。
		// 没有这个驱动器，圆钮会停在旧位置，不跟随待办卡展开、搜索条出现或分栏拖动。
		_scrollToLatestWatcher = new FloatingTracker
		{
			Changed = HandleFloatingRootChanged,
			Arranged = () => _scrollToLatestFloatHost.InvalidateArrange(),
			IsHitTestVisible = false,
		};
		ApplyFloatingShadow();
		// 浮层一直参与布局，只用 opacity 隐藏：待办卡展开/收起会改变消息区高度，若用 IsVisible
		// 跳过 arrange，重新出现时图标缓存可能停在旧位置、和按钮底分离。
		// 隐藏只切这一个节点的 opacity，取 0 或 1；按钮没有过渡，浮层不会落进 0<opacity<1 的合成分支。
		_scrollToLatestFloat.Opacity = 0;

		Session = session;
		Title = title;
		_statisticsPanel = new SessionStatisticsPanel(() => _ = CompactAsync());
		_statisticsPanel.Closed += FocusComposer;
		_usageSummary = new ProviderUsageSummary(() => Session.Model, () => Session.Cwd, ConfigureProviderUsage);
		_statisticsPanel.SetUsage(_usageSummary);
		_usagePanel = new ProviderUsagePanel(() => Session.Model, () => Session.Cwd);
		_usagePanel.Saved += _usageSummary.Invalidate;
		_usagePanel.ResultReady += _usageSummary.ApplyResult;
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
		// 展开之后的流式续跟只保持跟底：读者上滚就放手，不每帧拉回末尾。
		_textFocus.ContinueConversationFollowing = () => _conversationScroll!.ContinueFollowing();
		_textFocus.PauseFollowing = () => _conversationScroll!.Browse();
		_conversationScroll = new ConversationScroll(_scroll, visible => SetScrollToLatestVisible(visible && _messages.IsVisible), _timeline);

		// 右缘定位轴：点击刻度跳到那条用户消息，滚动时高亮当前阅读位置。
		_outline.Jump += JumpToOutline;
		_outline.View.MouseWheel += args => SmoothScroll.For(_scroll).RouteWheel(args);
		_scroll.ScrollChanged += OutlineScrolled;
		_mainArea.SizeChanged += _ => UpdateOutlineVisibility();

		_composer = new MultiLineTextBox().PrimarySelection()
			// 文本控件会处理点击（移光标），冒泡不到窗口根：输入区自己记一次。
			.OnMouseDown(_ => Diagnostics.NoteInput())
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
				ShowComposerHint(value.Length == 0);
				RefreshCommandBar();
			})
			// 中文/日文输入法组合期间正文还没落到 Text。只看 Text 长度，占位会一直留在原地，
			// 而组合串也画在同一行（「速度|消息，@ 引用文件…」），两者会叠在一起。
			// 组合期间先让位，结束后再按最终的 Text 决定是否恢复。
			.OnTextCompositionStart(_ => ComposerCompositionStarted())
			.OnTextCompositionUpdate(_ => ComposerCompositionStarted())
			.OnTextCompositionEnd(_ => ComposerCompositionEnded())
			.OnKeyDown(key =>
			{
				// 文件引用是行内原子：方向键跳过、Backspace/Delete 整枚删除。
				if (UserReferenceRichText.TryHandleKey(_composer!, key)) return;

				// Enter 发送，Shift+Enter 换行，与占位符提示一致。
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

		// 默认右键菜单的「粘贴」只看剪贴板有无文本，纯图片时菜单项是灰的。
		// 替换同一个语义命令：菜单、快捷键与工具栏仍走 StandardCommands.Paste，
		// 执行时先试图片，再回退到文本框自己的文本粘贴。
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
			.OnMouseDown(_ => Diagnostics.NoteInput())
			.Primary().Height(AppTheme.ControlHeightMd)
			.CornerRadius(0)
			.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, 0))
			.VerticalAlignment(VerticalAlignment.Center);
		_sendDivider = new Border().Width(1).Height(16).VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, divider) => divider.Background = AppTheme.For(theme).TextOnAccent.WithAlpha(0x40))
			.IsVisible(false);
		_sendMenuButton = new Button()
			.Content(new Grid().Children(new GlyphElement().Kind(GlyphKind.ChevronDown).GlyphSize(3)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)))
			.OnClick(ShowSendMenu)
			.StyleName(AppStyles.AccentSegmentButton)
			.Height(AppTheme.ControlHeightMd).MinWidth(28)
			.CornerRadius(0).Padding(new Thickness(5, 0, 7, 0))
			.VerticalAlignment(VerticalAlignment.Center).IsVisible(false)
			.HoverTip(Strings.SendOptions);
		var sendGroup = new Border().CornerRadius(AppTheme.RadiusMd).ClipToBounds(true)
			.HorizontalAlignment(HorizontalAlignment.Right).VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).Accent)
			.Child(new StackPanel().Orientation(Orientation.Horizontal)
				.VerticalAlignment(VerticalAlignment.Center).Children(_sendButton, _sendDivider, _sendMenuButton));

		_addButton = AppIcons.Button("layout-grid", Strings.ComposerAddTitle, ShowComposerAddMenu);
		_goalModeIcon = AppIcons.Icon("target", AppStyles.RoleSubtle, 12).IsVisible(false);
		_planModeIcon = AppIcons.Icon("list-checks", AppStyles.RoleSubtle, 12).IsVisible(false);
		_modeChipLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText();
		var modeChipClose = AppIcons.Icon("x", AppStyles.RoleSubtle, 11);
		_modeChip = new Button().Ghost()
			.Padding(new Thickness(6, 2, 5, 2))
			.MinHeight(AppTheme.ControlHeightMd)
			.CornerRadius(AppTheme.RadiusSm)
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(4)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new Grid().Width(14).Children(_goalModeIcon, _planModeIcon),
					_modeChipLabel,
					modeChipClose))
			.OnClick(() => SelectMode(ComposerMode.Normal))
			.IsVisible(false);
		_terminalButton = AppIcons.Button("terminal", Strings.Terminal, ToggleTerminal);
		_securityOffIcon = AppIcons.Icon("shield-off", AppStyles.RoleSubtle, 16).IsVisible(false);
		_securityAlertIcon = AppIcons.Icon("shield-alert", AppStyles.RoleSubtle, 16).IsVisible(false);
		_securityCheckIcon = AppIcons.Icon("shield-check", AppStyles.RoleSubtle, 16).IsVisible(false);
		_securityCustomIcon = AppIcons.Icon("shield", AppStyles.RoleSubtle, 16).IsVisible(false);
		_securityButton = new Button().Ghost()
			.Content(new Grid().Width(16).Children(
				_securityOffIcon, _securityAlertIcon, _securityCheckIcon, _securityCustomIcon))
			.OnClick(ShowSecurityMenu);
		_testButton = AppIcons.Button("refresh-cw", Strings.TestConnection, () => _ = TestConnectionAsync());

		_modelPicker = new ModelPicker(availableModels ?? (() => models), () => Session.Model, SelectModel);
		_modelPicker.Closed += FocusComposer;
		_composerPicker.Closed += FocusComposer;
		_modelButton = new Button().Ghost().MaxWidth(248).MinHeight(AppTheme.ControlHeightMd)
			.Content(_modelLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.OnClick(ShowModelPicker);

		// 思考强度与模型名同级：都是安静的 ghost 文本，不带边框/底色；点开才出现强度弹层。
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
			.OnClick(ShowThinkingPicker);

		// 左群是改变下一条消息的动作与模式（加号菜单、当前模式 chip、终端、安全），窄窗口自动换行；
		// 中群是模型与思考强度铭牌（PiDeck 同款：provider/model · max ▾，居中）；
		// 右群是连接测试与发送/中断，发送固定在最右。
		var leftActions = new WrapPanel().Spacing(4).VerticalAlignment(VerticalAlignment.Center)
			.Children(_addButton, _modeChip, _terminalButton, _securityButton);
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

		// 排队条只列尚未投递给模型的消息；高度上限与待办卡同口径，多条时在卡内滚动。
		_queueCard = new ComposerWidgetCard("list-ordered", Strings.QueuedMessagesTitle);
		_queueCard.Body.Children(new ScrollViewer()
			.Content(_queueRows)
			.AutoVerticalScroll()
			.NoHorizontalScroll()
			.Padding(new Thickness(0, 0, AppTheme.Space2, 0))
			.MaxHeight(ComposerWidgetListMaxHeight));

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
			.Children(_queueCard.View, _todoCard.View, _extensionCard.View);

		// 会话进行态指示行：与 composer 卡同宽居中，贴在输入卡上方（PiDeck 同款）。
		// 三段各留固定宽度：呼吸点伸缩、状态短语轮换、提示每七秒换一条；
		// 同时推挤行宽的话，整条线会左右晃。
		_busyIcon = AppIcons.Icon("loader-circle", AppStyles.RoleInfo, 13);
		_busyPulse
			.FontSize(AppTheme.FontSizeMicro)
			.FontFamily(AppTheme.MonoFont)
			// 固定宽度按最宽的一帧（「···」）留：等宽 12px 的三枚点约 22 DIP，
			// 给 20 会裁掉最后一点，再宽只会把状态短语推远。
			.Width(24)
			.VerticalAlignment(VerticalAlignment.Center);
		_busyText
			.FontSize(AppTheme.FontSizeCaption)
			.MinWidth(190)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		_busyDivider = new Border()
			.Width(1)
			.Height(12)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, line) => line.Background = AppTheme.For(theme).BorderSubtle)
			.IsVisible(false);
		_busyTip
			.FontSize(AppTheme.FontSizeMicro)
			// 旁注用「次要」，不用「最弱」：深色主题下 FaintText 在卡外底色上几乎看不见，
			// 而这一行就是要让人看见（提示的是没有可见入口的能力）。
			.SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center)
			.IsVisible(false);
		// 宽度跟输入卡一致，与内容长度无关：提示每七秒换一条，状态短语也在换。
		// 宽度跟内容走的话，整行连行首图标都会左右跳。外层 ReadingColumn 按可用宽度报尺寸，
		// 变长的只有行尾那句，行首钉在输入卡左缘。
		_busyLine = new Border()
			.Child(new DockPanel().LastChildFill().Spacing(6)
				.Children(_busyIcon, _busyPulse, _busyText, _busyDivider, _busyTip));
		_busyColumn = new ReadingColumn
		{
			Spacing = 0,
			Margin = new Thickness(AppTheme.Space4, 0, AppTheme.Space4, 0),
			IsVisible = false,
		};
		_busyColumn.Children(_busyLine);

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
					(_composerHintHost = new Grid()).Children(_composer, _composerHint), toolbar));

		// The window registers with the OS; the session surface owns the target draft.

		_branchPanel = new BranchPanel(() => Session);

		// 主区在「对话」与「分支」两个视图之间切换，两者叠放，只显示其中一个。
		_mainArea.Children(_messages);
		_mainArea.Children(_noMatches.Text(Strings.NoMatches).MutedText()
			.HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center).IsVisible(false));
		_mainArea.Children(_branchPanel.View.IsVisible(false));
		_mainArea.Children(_outline.View);
		// 宿主升入窗口 OverlayLayer 后不再跟随本标签页的视觉根，也不跟随正文区的重新摆位；
		// 所以留一个铺满正文区的驱动器：标签页被切走时摘掉旧浮层（避免多个会话的圆钮叠在一起），
		// 正文区几何变化时要求浮层重排。放在宿主之前，圆钮始终盖在正文之上。
		_mainArea.Children(_scrollToLatestWatcher);
		_mainArea.Children(_scrollToLatestFloatHost);

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

		// 标题行：左状态铭牌（等宽小字），右动作簇，不再是一块独立底色的条。
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

		_inputStack = new StackPanel()
			.Spacing(2)
			.Children(_widgetColumn, _emptyCard.IsVisible(false), _busyColumn, composerPane);

		// 问询卡片与会话同宽同列：真实窗口里该宽由 ReadingPreferences 随阅读宽度再算一次。
		_askPanel = new AskQuestionPanel(OnAskVisibilityChanged);
		_askCard = _askPanel.View
			.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space4, AppTheme.Space2, AppTheme.Space4, 0));

		_composerColumn = new StackPanel()
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Stretch)
			.Children(_extensionLine, _askCard, _inputStack,
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
		_busyTimer.Tick += () =>
		{
			// 等待期没有任何消息事件，这一拍就是唯一的推进：状态条的呼吸点、轮播、
			// 已等待时长与退避倒计时全部跟着它走。整帧 Render 在这一拍里几乎全是缓存命中。
			if (!_disposed) Render();
		};
		_imagePreview.Status = (message, ok) =>
		{
			if (_disposed) return;
			if (ok) Toasts.Success(View, message);
			else NotifyError(message);
		};
		if (!ReferenceEquals(_chat.Session, session)) _chat.Attach(session);

		var pickers = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		RefreshPickers();
		if (pickers is not null)
		{
			Diagnostics.Record("tab.pickers", pickers);
		}

		var first = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		Render();
		if (first is not null)
		{
			Diagnostics.Record("tab.first-render", first, $"rows={_projectedRows.Count}");
		}
	}

	public CodingAgentSession Session { get; }

	internal DesktopUiHost? UiHost { get; set; }

	internal bool TodoCardVisible => _todoCard.View.IsVisible;

	internal AskQuestionPanel AskPanel => _askPanel;

	internal bool AskPending => _askPanel.IsOpen;

	internal bool InputAreaVisible => _inputStack.IsVisible;

	/// 问询卡开关时收起/恢复输入区（PiDeck 同款：代理在等回答，输入框不该抢焦点），
	/// 并通知宿主刷新页签标题，后台会话的提问也能看到。
	private void OnAskVisibilityChanged(bool open)
	{
		if (_disposed)
		{
			return;
		}

		_inputStack.IsVisible(!open);
		AskChanged?.Invoke(this);
	}

	internal bool QueueCardVisible => _queueCard.View.IsVisible;

	internal SendOptionsMenu SendMenuForTests => _sendMenu;

	internal void ShowSendMenuForTests() => ShowSendMenu();

	internal string ComposerText => _composer.Text;

	internal bool ExtensionCardVisible => _extensionCard.View.IsVisible;

	internal ChatViewModel Chat => _chat;

	internal RespondingStatus? BusyStatus => _busyStatus;

	internal bool ChatStreaming => _chat.IsStreaming;

	internal int ComposerReferenceCount => UserReferenceRichText.Count(_composer.Text);

	internal IReadOnlyList<ImageContent> ComposerAttachments => [.. _attachments.Select(static item => item.Content)];

	public string Title { get; private set; }

	public UIElement View { get; }

	public bool IsStreaming => _sending || _chat.IsStreaming;

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

		// 没有名字也没有消息时用通用名，不把 id 摆给用户。
		return Strings.UntitledSession;
	}

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
		// 流式运行中直接释放会留下一轮孤儿运行：agent 继续消耗 token 执行工具，
		// 持久化监听已退订，结果不会落盘。这里先发中止信号兜底；CloseAsync 会等这轮收尾落盘。
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

	/// 关闭标签：先中止运行中的轮次并等它收尾（结果落盘），再释放界面与会话。
	/// 有界等待，收尾失败也继续关闭。
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

	public void DisposeView()
	{
		if (_disposed) return;
		_disposed = true;
		DetachFloatingButton();
		ReadingPreferences.Changed -= ApplyReadingPreferences;
		UiHost?.Hide();
		_askPanel.Close();
		_compactionAbort?.Cancel();
		HideTransientPanels();
		_renderTimer.Dispose();
		_busyTimer.Dispose();
		_composerHintTimer?.Stop();
		_composerHintTimer?.Dispose();
		_composerHintTimer = null;
		// 发送弹窗、思考强度弹层、加号菜单与选择器都是原生/窗口浮层，不随 View 释放；
		// 标签关掉时要关窗并释放 Popup 的原生资源。
		_sendMenu.Dispose();
		_thinkingPicker.Dispose();
		_composerAddMenu.Dispose();
		_composerPicker.Hide();
		_securityMenu.Dispose();
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
		// 进行态行也在这列里：跟着阅读宽度走，否则改完宽度后状态条会和输入卡错位。
		_busyColumn.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6);
		_readingStatisticsBar.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6);
		if (_askCard is not null)
		{
			_askCard.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6);
		}
		_composer.FontSize(AppTheme.FontSizeChat);
		_composerHint.FontSize(AppTheme.FontSizeChat);
		Render(force: true);
	}

	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors((Element)View);
		// 浮层可能挂在窗口 OverlayLayer（不在 View 的视觉树里），换肤要单独刷新，
		// 否则浅色主题下会留下一个深色圆钮。
		_scrollToLatest.Background = AppTheme.CardBackground;
		_scrollToLatest.BorderBrush = AppTheme.TextFaint;
		AppStyles.ApplyTextColors((Element)_scrollToLatestFloatHost);
		ApplyFloatingShadow();
		_status.Foreground(AppStyles.ColorForRole(AppStyles.RoleSubtle));
		AppStyles.ApplyTextColors(_modelPicker.View);
		AppStyles.ApplyTextColors(_statisticsPanel.View);
		AppStyles.ApplyTextColors(_usagePanel.View);
		AppStyles.ApplyTextColors(_branchPanel.View);
		AppStyles.ApplyTextColors(_composerPicker.View);
		AppStyles.ApplyTextColors(_imagePreview.View);
		// 进行态条的图标是直接取色的（没进 AppIcons 的角色跟踪表），换肤后要重新取一次；
		// 清掉 kind 就是让它下一帧按新主题重新上色，而文字那几段已经由角色重上色。
		_busyKind = null;
		_terminal?.ApplyTheme();
	}

	private void Notify(string message) => Toasts.Show(View, message);

	private void NotifyError(string message) => Toasts.Error(View, message);

	public void SendFromShortcut() => Send();

	internal Task PendingSend => _pendingSend;

	public void FocusComposerNow() => _composer.Focus();

	/// 编辑重发带回的图片没有文件路径；附件按路径去重，这里给一个稳定的合成前缀。
	private const string EditImagePrefix = "edit-resend:";

	/// 「编辑重发」：把原文放回输入框，并记下要回到的条目。发送时把该条目及其之后的分支
	/// 原地截断成墓碑（同一份会话文件），修改后的提问接在后面，原历史仍可回看，
	/// 侧栏也不会每次重发多出一条会话。原消息的图片一并带回附件区。
	private void PrepareEdit(string entryId, string text, IReadOnlyList<ChatImage> images)
	{
		if (IsStreaming)
		{
			return;
		}

		_pendingBranchEntryId = entryId;
		_composer.Text = text;
		_attachments.Clear();
		foreach (var image in images)
		{
			AddImage(EditImagePrefix + _attachments.Count, new ImageContent { Data = image.Data, MimeType = image.MimeType });
		}
		RefreshAttachments();
		_composer.Focus();
		_editNotice.IsVisible(true);
	}

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
		// 状态条要回答「已经等了多久」，需要一个属于本回合的起点。AgentStart 比这里晚到，
		// 中间那段（检查点快照、分支切换、展开附件）也用同一个时钟。
		_turnStarted = Stopwatch.GetTimestamp();
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
			// 检查点期间用户也可能按中断：agent 还没开始，要在这里停下，
			// 否则中断按钮看似生效，消息仍会在快照结束后发出。
			cancellationToken.ThrowIfCancellationRequested();

			// 编辑重发：在同一份会话文件里把原提问及之后的分支改成墓碑，再发送修改后的内容。
			// 不创建新会话文件，否则每改一次侧栏就多出一条同名会话。
			if (_pendingBranchEntryId is { Length: > 0 } branchId)
			{
				_pendingBranchEntryId = null;
				Session.RewindForResend(branchId);
				// 旧回答已从投影里移除：先撤掉时间线上的旧行再发新消息。
				// 检查点快照之后的续延可能在线程池上，UI 更新统一走 Dispatch。
				Dispatch(() =>
				{
					_editNotice.IsVisible(false);
					RefreshPickers();
					Render();
					RefreshTitle();
					Rewritten?.Invoke(this);
				});
			}

			// 目标模式的隐藏标记只在这里合一次：带图片与纯文字两条发送路径共用同一份正文。
			var outgoing = text.Trim().Length > 0 || _attachments.Count > 0
				? ComposerPrompt.Compose(text, Mode)
				: text;

			if (_attachments.Count > 0)
			{
				// 展开技能命令/提示词模板，并把图片作为内容块随消息发出。
				var message = Session.PrepareInput(outgoing, [.. _attachments.Select(static item => item.Content)]);

				_attachments.Clear();
				Dispatch(RefreshAttachments);

				await Session.PromptAsync(message, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await _chat.SendAsync(outgoing, cancellationToken).ConfigureAwait(false);
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
				// 这一轮真的发过了：通知宿主记账「上次使用的模型」（PiDeck 也是发送被接受后才记）。
				// 偏好落盘属于宿主，不属于标签：无窗口测试直接驱动标签时不该碰真目录里的 desktop.json。
				MessageSent?.Invoke(this);
				StateChanged?.Invoke(this);
			});
		}
	}

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

	public void ToggleSearchFromSmoke() => ToggleSearch();

	internal void SetFilterFromTests(string value) => _searchBox.Text = value;
	internal ConversationTimeline TimelineFromTests => _timeline;
	internal bool FollowingFromTests => _conversationScroll.Following;

	private void SetScrollToLatestVisible(bool visible)
	{
		var wasHidden = _scrollToLatestFloat.Opacity == 0;
		_scrollToLatestFloat.Opacity = visible ? 1 : 0;
		_scrollToLatest.IsHitTestVisible = visible;
		_scrollToLatest.Focusable = visible;
		// 浮层一直参与 arrange；重新出现时再强制一次测量，清掉极端时序下内容层的旧位置。
		if (visible && wasHidden) _scrollToLatestFloatHost.InvalidateMeasure();
	}

	private void ApplyFloatingShadow() =>
		_scrollToLatestFloat.ShadowColor = Color.FromArgb(AppTheme.IsLightTheme ? (byte)0x22 : (byte)0x96, 0, 0, 0);

	/// 挂窗后把浮层宿主升入窗口 OverlayLayer：浮层与正文是两个独立的绘制根，
	/// 时间线行整块重绘时不会盖掉与自己重叠的按钮像素。
	/// 未挂窗时（单元测试直接排布 <c>tab.View</c>）宿主留在主区，位置算法相同。
	private void HandleFloatingRootChanged(Element? oldRoot, Element? newRoot)
	{
		if (newRoot is Window window)
		{
			if (_floatingHosted)
			{
				if (ReferenceEquals(_floatingWindow, window)) return;
				DetachFloatingButton();
			}

			if (ReferenceEquals(_scrollToLatestFloatHost.Parent, _mainArea)) _mainArea.Remove(_scrollToLatestFloatHost);
			window.OverlayLayer.Add(_scrollToLatestFloatHost);
			_floatingWindow = window;
			_floatingHosted = true;
			// 换根后客户区坐标下的锚点边界要立刻生效，不等下一次尺寸变化。
			_scrollToLatestFloatHost.InvalidateArrange();
		}
		else if (newRoot is null)
		{
			DetachFloatingButton();
		}
	}

	private void DetachFloatingButton()
	{
		if (!_floatingHosted) return;
		_floatingHosted = false;
		_floatingWindow?.OverlayLayer.Remove(_scrollToLatestFloatHost);
		_floatingWindow = null;
		if (_scrollToLatestFloatHost.Parent is null) _mainArea.Children(_scrollToLatestFloatHost);
	}

	/// 浮层宿主：把圆钮摆在 <see cref="Anchor"/> 的水平居中、下缘越过其底边 <see cref="Overlap"/> 的位置。
	/// 每轮 arrange 按锚点实时的 Bounds 计算，正文区宽高怎么变（分栏拖动、侧栏折叠、
	/// 待办卡展开收起、窗口缩放）都不用同步代码；OverlayLayer 只约束客户区矩形，
	/// 锚点要自己读，不能把边界换算成 margin 存起来。
	/// 自身对命中透明：只有圆钮（经 ShadowDecorator 转发给子级）可点，其余区域照常滚正文。
	private sealed class FloatingAnchor : Panel
	{
		internal Element? Anchor;

		internal double Overlap;

		protected override Size MeasureContent(Size availableSize)
		{
			var constraint = double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height)
				? availableSize
				: new Size(10_000, 10_000);
			foreach (Element child in Children)
			{
				child.Measure(constraint);
			}

			// 浮层不给父级添尺寸负担：摆位只看锚点的实时边界。
			return Size.Empty;
		}

		protected override void ArrangeContent(Rect bounds)
		{
			// 锚点还没布局时退回自己的槽位：正文区在浮层之前排版，正常路径拿到的总是实时边界，
			// 但首轮不能因为拿到空矩形就整个不摆（那会让圆钮第一帧不出现）。
			var area = Anchor?.Bounds is { Width: > 0, Height: > 0 } anchorArea ? anchorArea : bounds;
			if (area.Width <= 0 || area.Height <= 0)
			{
				return;
			}

			foreach (Element element in Children)
			{
				if (element is not UIElement child)
				{
					continue;
				}

				var size = child.DesiredSize;
				if (size.Width <= 0 || size.Height <= 0)
				{
					continue;
				}

				// DesiredSize 含阴影留白：按圆钮（去掉底部留白）的下缘定位，才与正文区底边对齐。
				double padBottom = child is ShadowDecorator decorator ? decorator.ShadowPadding.Bottom : 0;
				double top = area.Bottom + Overlap + padBottom - size.Height;
				double left = area.X + (area.Width - size.Width) / 2;
				child.Arrange(new Rect(left, top, size.Width, size.Height));
			}
		}

		/// 宿主铺满正文区，只有圆钮可命中，其余位置穿透，滚轮与悬停照常落到正文。
		protected override UIElement? OnHitTest(Point point)
		{
			if (!IsVisible || !IsHitTestVisible)
			{
				return null;
			}

			for (var index = Children.Count - 1; index >= 0; index--)
			{
				if (Children[index] is UIElement child && child.HitTest(point) is { } hit)
				{
					return hit;
				}
			}

			return null;
		}
	}

	/// 浮层驱动器：跟踪本标签页的视觉根（挂层/摘层），正文区重新摆位时要求浮层重排。
	/// 它自己铺满正文区，因此正文区的任何移动或缩放都会走到它的 ArrangeContent。
	private sealed class FloatingTracker : Grid
	{
		public Action<Element?, Element?>? Changed;
		public Action? Arranged;

		protected override void OnVisualRootChanged(Element? oldRoot, Element? newRoot)
		{
			base.OnVisualRootChanged(oldRoot, newRoot);
			Changed?.Invoke(oldRoot, newRoot);
		}

		protected override void ArrangeContent(Rect bounds)
		{
			base.ArrangeContent(bounds);
			Arranged?.Invoke();
		}
	}

	private void ScrollToLatest() => _conversationScroll.ScrollToLatest();

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

	/// 会话内过滤是显式搜索：进入时取消跟底并回到第一条命中，清空时回到搜索前的行（或末尾）。
	/// 否则虚拟化列表按旧偏移重新投影，读者会落在与搜索无关的位置。
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

	/// 一条聊天记录是否命中当前过滤关键字（正文、思考、工具名，以及执行过程里的步骤）。
	/// 过程组行本身不带正文，不展开成员会让工具输出成为搜索盲区。
	internal static bool Matches(ChatEntry entry, string filter) =>
		filter.Length == 0 ||
		entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
		entry.Thinking.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
		(entry.ToolName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
		entry.ProcessEntries.Any(member => Matches(member, filter));

	public void ShowBranches() => ShowView(showChat: false);

	public void ShowChat() => ShowView(showChat: true);

	public IReadOnlyList<string> SlashCommands() => [.. Commands().Select(static command => command.Name)];

	public void Prefill(string text)
	{
		_composer.Text = text;
		RefreshCommandBar();
		_composer.Focus();
	}

	private void ShowView(bool showChat)
	{
		_conversationScroll.Stop();
		_messages.IsVisible(showChat);
		UpdateOutlineVisibility();
		SetEmptyState(showChat && _chat.Entries().Count == 0);
		_noMatches.IsVisible(showChat && _filter.Length > 0 && _timeline.RowCount == 0);
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

	/// 空会话：提示文本与输入区（含状态栏）作为一个整体居中，落在消息区正中；
	/// 有记录后输入区回到贴底的自适应行。只换网格行，不重建输入框，草稿、附件与焦点都保留。
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
				new Button().Ghost().Content(new TextBlock()
				.Text(path.StartsWith(EditImagePrefix, StringComparison.Ordinal)
					? Strings.EditImageLabel
					: Path.GetFileName(path)).FontSize(AppTheme.FontSizeMicro))
					.HoverTip(Strings.ImagePreviewOpen).OnClick(() => PreviewAttachment(content)),
				AppIcons.Button("x", Strings.RemoveAttachment, () =>
				{
					_attachments.RemoveAll(item => item.Path == captured);
					RefreshAttachments();
				})));
		}

		_attachmentBar.IsVisible(true);
	}

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

	private void CopyText(string text)
	{
		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		if (clipboard?.TrySetText(text) == true) Toasts.Success(View, Strings.Copied);
		else NotifyError(Strings.ClipboardUnavailable);
	}

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

	/// 仅 <c>--ui-smoke thinking</c>：打开思考强度弹层供外部脚本截图。
	internal void ShowThinkingPickerForSmoke()
	{
		ShowThinkingPicker();
		Console.WriteLine($"thinking-picker-open:{_thinkingPicker.IsOpen} level={Session.ThinkingLevel}");
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

	public bool IsTerminalOpen => _terminalOpen;

	public void ToggleTerminal() => SetTerminalOpen(!_terminalOpen);

	/// 展开或收起底部终端。终端在第一次展开时才创建（会真的启动伪终端），之后收起只隐藏，
	/// shell 的历史与工作目录都不丢；展开时把键盘焦点交给它。
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

	private void UpdateTerminalButton() =>
		_terminalButton.StyleName(_terminalOpen ? AppStyles.SelectedButton : AppStyles.GhostButton);

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

	/// 设置页保存安全配置后由宿主推给每个已打开的会话：档位是每次工具调用时读的，
	/// 不推的话界面显示的还是旧档，用户会以为改动没生效。
	public void ApplySecurityConfig(SecurityConfig config)
	{
		if (_disposed)
		{
			return;
		}

		Session.Security = config;
		RefreshPickers();
	}

	private string SecurityKey => Session.Session.SessionFile ?? $"session:{Session.Session.SessionId}";

	/// 仅供 --ui-smoke composer-security：打开安全档位菜单，外部脚本趁机截图。
	internal void PrepareSecurityMenuSmoke() => ShowSecurityMenu();

	/// 档位按钮的提示要说清「现在生效的是哪档、为什么」。总开关关掉时档位标签有欺骗性，
	/// 要写明门没开，不能显示一个看起来像在管的档位名。
	private void UpdateSecurityButton()
	{
		var config = Session.Security;
		var level = config.Resolve(SecurityKey);
		var overridden = config.Enabled &&
			config.SessionLevels.TryGetValue(SecurityKey, out var assigned) &&
			!string.Equals(assigned, config.DefaultLevelId, StringComparison.Ordinal);

		// 盾形图标表达保护强度：停用与关闭档都是划掉盾，严格档是警报盾。
		var icon = config.Enabled ? SecurityMenu.IconFor(level.Level) : "shield-off";
		_securityOffIcon.IsVisible(icon == "shield-off");
		_securityAlertIcon.IsVisible(icon == "shield-alert");
		_securityCheckIcon.IsVisible(icon == "shield-check");
		_securityCustomIcon.IsVisible(icon == "shield");
		_securityButton.Opacity = config.Enabled ? 1 : 0.6;

		_securityButton.HoverTip(!config.Enabled
			? Strings.SecurityDisabledHint
			: overridden
				? Strings.SecurityOverrideHint(level.Name, level.Description)
				: Strings.SecurityFollowHint(level.Name, level.Description));
	}

	/// 按会话切换安全档位。总开关关闭时也打开菜单：顶部的提示行说明为什么不能选，
	/// 比只弹一句 toast 更容易找到恢复路径。
	private void ShowSecurityMenu()
	{
		if (_disposed)
		{
			return;
		}

		var config = SecurityConfig.Load();
		var effective = config.Resolve(SecurityKey);
		var hasOverride = config.Enabled &&
			config.SessionLevels.TryGetValue(SecurityKey, out var assigned) &&
			!string.Equals(assigned, config.DefaultLevelId, StringComparison.Ordinal);

		HoverHints.Cancel(_securityButton);
		_securityMenu.Show(_securityButton, config.Enabled, config.Levels, effective.Level, hasOverride, ApplySecurityLevel);
	}

	private void ApplySecurityLevel(string? levelId)
	{
		var key = SecurityKey;
		try
		{
			var updated = SecurityConfig.Load().WithSessionLevel(key, levelId).Sanitized();
			SecurityConfig.Save(updated);
			Session.Security = updated;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			Toasts.Error(View, Strings.SecuritySaveFailed(exception.Message));
			return;
		}

		RefreshPickers();
	}

	private void ShowThinkingPicker()
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

		// 点击后指针仍在按钮上，先收起悬停提示，避免它压在刚打开的弹层上。
		HoverHints.Cancel(_thinkingButton);
		_thinkingPicker.Show(
			_thinkingButton,
			levels,
			ThinkingLevelResolver.Clamp(Session.Model, Session.ThinkingLevel),
			SelectThinkingLevel);
	}

	private void SelectThinkingLevel(string level)
	{
		if (IsStreaming || _disposed)
		{
			return;
		}

		Session.SetThinkingLevel(level);
		// 每模型默认档位（pi settings.json 的 modelThinkingLevels）：这次点选就是该模型下一次的默认，
		// 新建会话 / 重新打开都用它（PiDeck 的 launchDefaults 同序：每模型默认 > 全局默认）。
		// 与「最近模型」一样，这条写入只在用户真的点过档位时发生。
		try
		{
			PiWeft.CodingAgent.Settings.CodingAgentSettings.SetModelThinkingLevel(Session.Model.Provider, Session.Model.Id, Session.ThinkingLevel);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Save model thinking level", ex);
		}

		RefreshPickers();
		Render();
	}

	/// 输入以 <c>/</c> 开头时，在输入框上方列出匹配的斜杠命令（提示词模板与技能），点击即补全。
	/// 命令数据来自会话发现的结果，与 RPC 的 <c>get_commands</c> 同源。
	/// 创建占位提示：样式与命中设置在这一处配齐。提示叠在输入框上，
	/// 每一枚（构造的或换新的）都要 IsHitTestVisible(false)，点击才能穿到输入框。
	private static TextBlock BuildComposerHint()
	{
		var hint = new TextBlock().FontSize(AppTheme.FontSizeChat).SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.Text(Strings.ComposerPlaceholder)
			.Margin(new Thickness(AppTheme.Space2))
			.IsHitTestVisible(false)
			.VerticalAlignment(VerticalAlignment.Top);
		return hint;
	}

	/// 显隐占位提示。显示即换一枚新实例：
	/// 保留场景把提示节点首次录制的光栅固化在节点上。此后强制重绘、文本重建都换不掉，
	/// 删掉输入内容再重放，留下的仍是那截被裁掉的占位；新实例没有旧录制，完整绘制。
	/// 换新延迟一拍，让「无提示」先被完整录制一帧，避免新旧光栅混在同一脏区。
	private void ShowComposerHint(bool visible)
	{
		if (_disposed) return;

		// 输入法组合期间永不让占位出现：组合串就画在同一行，叠上去就是两层字。
		if (visible && IsComposingNow()) visible = false;

		if (!visible)
		{
			CancelComposerHintSwap();
			_composerHint.IsVisible(false);
			return;
		}

		_composerHint.IsVisible(false);
		// 复用同一个定时器并把 200 ms 重新起算：连续打字时每键一次 Stop/Start，
		// 不每键 new + Dispose 一个 DispatcherTimer，后者每次都要排一次调度器注册。
		var timer = _composerHintTimer;
		if (timer is null)
		{
			timer = _composerHintTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200));
			timer.Tick += () =>
			{
				timer.Stop();
				// 这一拍（200ms）里用户可能又输入了内容、或输入法重新进入组合：
				// 到点时重新求值，不沿用调用瞬间的判断。
				if (_composer.Text.Length > 0 || IsComposingNow()) return;
				SwapComposerHint();
			};
		}
		else
		{
			timer.Stop();
		}

		timer.Start();
	}

	/// 现在是否在组合。以框架的 <see cref="ITextCompositionClient.IsComposing"/> 为准；
	/// 它在组合提交/取消时由框架自己复位；我们记的事件状态只作为兜底。
	/// 只认事件状态时，「组合已结束但 End 没到」会让占位永久消失（中文输入后删空文本即复现）。
	private bool IsComposingNow() =>
		_composer is ITextCompositionClient composition ? composition.IsComposing : _composing;

	internal void ComposerCompositionStarted()
	{
		if (_disposed) return;
		_composing = true;
		CancelComposerHintSwap();
		_composerHint.IsVisible(false);
	}

	/// 组合结束：按提交后的正文决定占位要不要回来（取消组合时正文仍为空）。
	/// 这里显式清掉事件状态，并重新按「文本为空」求值。即使 End 之后又来一次
	/// 迟到的 Update 把状态置回 true，<see cref="IsComposingNow"/> 仍以框架状态为准。
	internal void ComposerCompositionEnded()
	{
		_composing = false;
		if (_disposed) return;
		ShowComposerHint(_composer.Text.Length == 0);
	}

	private void CancelComposerHintSwap() => _composerHintTimer?.Stop();

	internal void SwapComposerHint()
	{
		if (_disposed) return;
		// 手动/自动换新都先把定时器收口：否则复用中的定时器到点会再换一次。
		CancelComposerHintSwap();
		var grid = _composerHintHost;
		if (grid is null || !grid.Children.Any(child => ReferenceEquals(child, _composer))) return;
		var fresh = BuildComposerHint();
		// 换新发生在组合中（或组合刚结束还没拿到正文）时，新实例也不能默认可见。
		if (IsComposingNow() || _composer.Text.Length > 0) fresh.IsVisible(false);
		grid.Remove(_composerHint);
		grid.Add(fresh);
		_composerHint = fresh;
	}

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

	private void RefreshPickers()
	{
		var modelName = Session.Model.Name.Length > 0 ? Session.Model.Name : Session.Model.Id;
		_modelLabel.Text($"{Session.Model.Provider}/{modelName}").TextTrimming(TextTrimming.CharacterEllipsis);
		_modelButton.HoverTip($"{Session.Model.Provider}/{Session.Model.Id}\n{Strings.ModelCapabilities(Session.Model)}");

		UpdateSecurityButton();

		var thinking = Strings.ThinkingLevelName(Session.ThinkingLevel);
		// 铭牌跟 PiDeck 同款拼成一行：provider/model · max ▾；级别用原始短名，中文全名放在提示里。
		_thinkingLabel.Text($"· {Session.ThinkingLevel}");
		_thinkingButton.HoverTip($"{Strings.ThinkingLevelTitle}：{thinking}");
	}

	/// 会话进行态（PiDeck respondingKind 同语义）。判定与措辞都在
	/// <see cref="RespondingStatusPolicy"/>：这一轮还在跑就有一句可说，
	/// 不会出现「完全没提示」的空窗（首轮发送后到首 token 之前就是这一段）。
	/// 本回合的一帧事实。状态条的措辞与尾部占位行都从这一份输入派生，
	/// 两处不会各算一遍（否则每一试就多一次 O(n) 扫描，也容易走偏）；
	/// 占位行是否真的显示要等这份输入出来才能定（见 <see cref="RespondingStatusPolicy.NeedsPendingRow"/>）。
	private RespondingInput TurnState(IReadOnlyList<ChatEntry> rows, bool pendingRowVisible)
	{
		var retry = _chat.Retry;
		var (liveTool, liveBody, liveThinking) = LiveActivity(rows);
		return new RespondingInput(
			TurnElapsedMs(),
			TurnHasOutput(rows),
			pendingRowVisible,
			liveBody,
			liveThinking,
			liveTool,
			Session.IsCompacting,
			retry is { Running: true },
			retry?.Attempt ?? 0,
			retry?.MaxAttempts ?? 0,
			retry is { } scheduled ? RetrySecondsRemaining(scheduled.RetryAtUnixMs) : 0);
	}

	/// 预热期要不要在时间线尾部补一行「在等回复」的占位。判定在
	/// <see cref="RespondingStatusPolicy.NeedsPendingRow"/>：屏幕最后一行还是用户自己那条、
	/// 且没有任何一行在流，才是真的在等首 token。过滤与「表里有没有行」是界面自己的事实。
	private bool PendingRowVisible(in RespondingInput input) =>
		RespondingStatusPolicy.NeedsPendingRow(input) && _filter.Length == 0;

	/// 从最新条目向回找第一个仍在流式的活动。每帧都走，因此只看 O(1) 的长度与标记：
	/// 读 <see cref="ChatEntry.Text"/> 会把整篇流式正文物化一遍。
	private static (bool Tool, bool Body, bool Thinking) LiveActivity(IReadOnlyList<ChatEntry> rows)
	{
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

		return (false, false, false);

		static (bool, bool, bool) KindOf(ChatEntry item) => item.Role switch
		{
			ChatRole.Tool => (true, false, false),
			ChatRole.Assistant when item.TextLength > 0 || item.Images.Count > 0 => (false, true, false),
			_ => (false, false, true),
		};
	}

	/// 本回合有没有已经落下可见的行。屏幕上最后一行还是用户自己那条，就说明请求还没换回
	/// 任何东西。这一段是「预热 + 等首 token」，和「工具收口、下一段还没开始」的空窗不同：
	/// 前者看不见子阶段，可以轮换几句；后者只能说「正在处理」。
	private static bool TurnHasOutput(IReadOnlyList<ChatEntry> rows) =>
		rows.Count > 0 && rows[^1].Role != ChatRole.User;

	private double TurnElapsedMs()
	{
		var started = _turnStarted;
		var agent = _chat.TurnStarted;
		if (agent != 0 && (started == 0 || agent > started)) started = agent;
		return started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;
	}

	/// 右侧功能提示：只在屏幕自己不动的时候占位（推流期眼睛在正上文中，不抢视线），
	/// 每 7 秒换一条；设置里关掉就整段不占位。
	private void ApplyRespondingTip(RespondingStatus status)
	{
		var quiet = status.Kind is RespondingKind.Starting or RespondingKind.Waiting
			or RespondingKind.Executing or RespondingKind.Compacting or RespondingKind.Retrying;
		var tip = quiet ? RespondingTips.Current(status.ElapsedMs, _turnStarted) : null;
		var show = tip is { Length: > 0 };
		_busyDivider.IsVisible(show);
		_busyTip.IsVisible(show);
		if (tip is { Length: > 0 } text && _busyTip.Text != text)
		{
			_busyTip.Text(text);
		}
	}

	/// 预热期在时间线尾部补一行「在等回复」的占位：首轮等待时正文区只有用户自己的气泡，
	/// 看不出请求是否被受理。它不是会话内容：不进投影、不进大纲、不进模型上下文，
	/// 只接在投影结果后面交给行宿主；首 token 一到就自然被真的流式行顶掉。
	private IReadOnlyList<ChatEntry> AppendPending(IReadOnlyList<ChatEntry> entries)
	{
		// 复用同一个实例：ConversationTimeline 按引用判同一行，实例不变就不会每一拍拆了重建，
		// 那一行的心跳也才有意义。
		_pendingRow ??= new ChatEntry
		{
			Role = ChatRole.Assistant,
			EntryId = PendingReplyEntryId,
			Text = string.Empty,
			Timestamp = _turnStarted == 0 ? 0 : DateTimeOffset.Now.ToUnixTimeMilliseconds(),
		};
		return [.. entries, _pendingRow];
	}

	private void Render(bool force = false)
	{
		if (_disposed)
		{
			return;
		}

		// 应用侧重绘请求：帧档用「请求 → 呈现」量真实成本，把按请求重绘前的空闲切掉。
		Diagnostics.NoteFrameRequest();
		// 流式期每 40 ms 合并一次渲染；只把明显慢的帧记进诊断，否则文件会被无害抖动填满。
		var renderWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;

		var all = _chat.Entries();
		var matched = _filter.Length == 0 ? all : [.. all.Where(entry => Matches(entry, _filter))];
		if (!ReferenceEquals(_projectedSource, all) || _projectedFilter != _filter || _projectedBusy != IsStreaming)
		{
			var project = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
			_projectedRows = _projector.Project(matched, IsStreaming);
			if (project is not null)
			{
				Diagnostics.Record("timeline.project", project,
					$"rows={_projectedRows.Count}, mode={(_projector.LastPrefixReused ? "tail" : "full")}, " +
					$"tail={_projector.TailPasses}, full={_projector.FullPasses}");
			}

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

		var statusKey = (Session.Model.Id, Session.Model.Provider, Session.ThinkingLevel, _chat.Error);
		if (statusKey != _statusKey)
		{
			_statusKey = statusKey;
			var status = $"{Session.Model.Provider} / {Session.Model.Id} · {Strings.ThinkingShort(Session.ThinkingLevel)}";
			if (_chat.Error is { Length: > 0 } error)
			{
				status = $"{status}  ·  {Strings.RoleError}: {error}";
			}

			_status.Text(status).HoverTip(status);
		}
		var statsWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
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
		var queued = _chat.QueuedMessages();
		if (!ReferenceEquals(queued, _renderedQueue))
		{
			_renderedQueue = queued;
			RefreshQueueStrip(queued);
		}
		if (statsWatch is not null)
		{
			// 渲染每帧都发生：常态只计数，慢的那几次才落明细，否则诊断自身就是垃圾源。
			Diagnostics.Slow("render.stats", statsWatch, 8, static rows => $"rows={rows}", all.Count);
		}

		var busy = _sending || _chat.IsStreaming;
		// 只要这一轮还在跑，状态条就一直在那儿：以前「没有流式行」直接不显示，
		// 首轮发送后到首 token 之前那段最长的等待反而一片空白。
		// 占位行是否显示变成本帧输入的一部分：它决定谁在报「本轮已 N 秒」。
		var pending = false;
		RespondingStatus? responding = null;
		if (busy)
		{
			var state = TurnState(all, pendingRowVisible: false);
			pending = all.Count > 0 && PendingRowVisible(state);
			responding = RespondingStatusPolicy.Compose(pending ? state with { PendingRowVisible = true } : state);
		}
		_busyStatus = responding;
		var kindChanged = responding?.Kind != _busyKind;
		_busyKind = responding?.Kind;
		_busyColumn.IsVisible(responding is not null);
		if (responding is { } active)
		{
			if (kindChanged)
			{
				_busyIcon.Source(AppIcons.Get(active.Icon, AppStyles.ColorForRole(active.Role)));
				// 文案与呼吸点同色：状态本身就是这一个语义（思考紫、工具蓝、空窗弱色）。
				_busyText.Role(active.Role);
				_busyPulse.Role(active.Role);
			}

			// 倒计时、已等待时长与轮播短语都会变而 Kind 不变：逐段比一下，没变就不碰排版。
			if (_busyPulse.Text != active.Pulse) _busyPulse.Text(active.Pulse);
			var line = active.Text;
			if (_busyText.Text != line) _busyText.Text(line);
			ApplyRespondingTip(active);
		}

		// 心跳只在跑回合的时候开：推流期数据帧自己会把帧打进来，但模型一旦卡顿、工具一旦跑久，
		// 屏幕就不再有事件。没有这一拍，呼吸点会停在半道上，看起来像卡死了。
		if (busy)
		{
			if (!_busyTimer.IsEnabled) _busyTimer.Start();
		}
		else if (_busyTimer.IsEnabled)
		{
			_busyTimer.Stop();
		}

		// 重试收口提示：失败卡/成功回复本身已在时间线，toast 只负责即时告知。
		if (_chat.TakeRetryNotice() is { } notice)
		{
			if (notice.Success) Toasts.Success(View, Strings.RetrySucceeded(notice.Attempt));
			else NotifyError(Strings.RetryFailed(notice.Attempt, notice.MaxAttempts, notice.FinalError));
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
		if (_lastBusy != busy || _sendLabel.Text != (busy ? Strings.Abort : Strings.Send))
		{
			_sendLabel.Text(busy ? Strings.Abort : Strings.Send);
			_sendIcon.Source(AppIcons.Get(busy ? "x" : "arrow-up", AppTheme.TextOnAccent));
			_sendMenuButton.IsVisible(busy);
			_sendDivider.IsVisible(busy);
		}

		_sendMenuButton.IsEnabled = _chat.IsStreaming;
		_modelButton.IsEnabled = !busy;
		_thinkingButton.IsEnabled = !busy;
		var mode = Mode;
		if (mode != _renderedMode)
		{
			_renderedMode = mode;
			UpdateModeChip();
		}

		_modeChip.IsEnabled = !busy;
		_testButton.IsEnabled = !busy && !_testing;
		// 这两个开关以前每帧都整表扫描一遍（流式期就是每 40 ms 扫两千行，还各分配一个枚举器）。
		// 用户行与「有没有正文」只随投影结果变化，按行数与过滤条件缓存即可。
		if (entries.Count != _chromeRowCount || _filter != _chromeFilter)
		{
			_chromeRowCount = entries.Count;
			_chromeFilter = _filter;
			_rowsHaveUser = false;
			_rowsHaveReply = false;
			foreach (var row in entries)
			{
				if (row.Role == ChatRole.User) _rowsHaveUser = true;
				else if (row.Role == ChatRole.Assistant && row.TextLength > 0) _rowsHaveReply = true;
				if (_rowsHaveUser && _rowsHaveReply) break;
			}
		}

		_retry.IsEnabled = !busy && _rowsHaveUser;
		_copyReply.IsEnabled = _rowsHaveReply;

		// 预热占据位：大纲、计数、空会话判定都继续看真投影，这一行只给行宿主看。
		var timelineRows = pending ? AppendPending(entries) : entries;
		var realize = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		_timeline.SetEntries(timelineRows, force, _projector.LastStablePrefix, _projector.LastPrefixReused);
		SyncOutline(entries, _projector.LastStablePrefix);
		if (realize is not null)
		{
			Diagnostics.Slow("timeline.realize", realize, 8, static rows => $"rows={rows}", timelineRows.Count);
		}
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
				foreach (var child in actions.Children)
				{
					if (child is Button action)
					{
						action.IsEnabled = !busy || HoverHints.Text(action) == Strings.CopyUserMessage;
					}
				}
			}
		}

		_conversationScroll.FollowContent();

		if (renderWatch is not null)
		{
			var elapsed = renderWatch.Elapsed.TotalMilliseconds;
			Diagnostics.Slow("ui.render", elapsed, 8,
				static state => $"rows={state.Rows}, visible={state.Visible}",
				(Rows: entries.Count, Visible: _timeline.VisibleRowCount));
		}
	}


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

	/// 重建「排队中」卡：运行中再发的消息还没进会话投影，只能在队列快照里看到，
	/// 这一块就是它们唯一的可见处。撤回 / 丢弃会改队列，下一次 queue_update 再刷新。
	private void RefreshQueueStrip(IReadOnlyList<ChatQueuedMessage> queued)
	{
		if (queued.Count == 0)
		{
			if (_queueRows.Children.Count > 0) _queueRows.Clear();
			_queueCard.Show(false);
			RefreshWidgetColumn();
			return;
		}

		var steer = 0;
		foreach (var item in queued)
		{
			if (item.Steer) steer++;
		}

		_queueCard.Meta = Strings.QueuedMessagesMeta(queued.Count, steer, queued.Count - steer);
		_queueRows.Clear();
		for (var index = 0; index < queued.Count; index++)
		{
			_queueRows.Children(QueuedRow(queued[index], index + 1));
		}

		_queueCard.Show(true);
		RefreshWidgetColumn();
	}

	/// 一条排队消息：序号 + 预览 + 投递时机标签 + 「撤回」「丢弃」。
	/// 与 PiDeck 的排队行一致：悬停给全文，行内只留一行省略号。
	private UIElement QueuedRow(ChatQueuedMessage queued, int index)
	{
		var preview = Strings.QueuedPreview(queued.Text, queued.ImageCount);
		var text = new TextBlock()
			.Text(preview)
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.TextWrapping(TextWrapping.NoWrap)
			.VerticalAlignment(VerticalAlignment.Center)
			.HoverTip(Strings.QueuedRowTip(index, queued.Steer, preview));

		var badge = new TextBlock()
			.Text(queued.Steer ? Strings.QueuedSteer : Strings.QueuedFollowUp)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space2, 0, AppTheme.Space1, 0));

		var retract = AppIcons.Button("pencil", Strings.QueuedRetract, () => RetractQueued(queued))
			.StyleName(AppStyles.GhostButton);
		var discard = AppIcons.Button("x", Strings.QueuedDiscard, () => DiscardQueued(queued))
			.StyleName(AppStyles.GhostButton);

		var row = new DockPanel()
			.LastChildFill()
			.MinHeight(20)
			.Children(discard.DockRight(), retract.DockRight(), badge.DockRight(), text);

		// 序号用等宽小字：多条时能对得上「第几条」，但比图标省宽度。
		return new DockPanel()
			.LastChildFill()
			.MinHeight(24)
			.Children(new TextBlock()
				.Text(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
				.FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeMicro)
				.SubtleText()
				.VerticalAlignment(VerticalAlignment.Center)
				.Width(18)
				.DockLeft(), row);
	}

	/// 撤回：从队列拿掉并把内容放回输入框。与 PiDeck 的 retractQueuedPromptForEdit 同语义：
	/// 撤回的文本排在已有草稿**前面**、空行分隔（用户刚写的那半句不会被吞掉），
	/// 图片前插到附件列表，光标落在撤回文本末尾。
	internal void RetractQueued(ChatQueuedMessage queued)
	{
		if (_disposed) return;
		if (!_chat.RetractQueued(queued))
		{
			Notify(Strings.QueuedGone);
			return;
		}

		var draft = _composer.Text;
		var restored = string.IsNullOrWhiteSpace(draft)
			? queued.Text
			: string.IsNullOrWhiteSpace(queued.Text) ? draft : $"{queued.Text.TrimEnd()}\n\n{draft}";
		_composer.Text = restored;

		if (queued.Message is UserMessage { Content.Blocks: { } blocks })
		{
			InsertAttachmentsFirst([.. blocks.OfType<ImageContent>().Select(static image => new ImageContent { Data = image.Data, MimeType = image.MimeType })]);
		}

		Notify(Strings.QueuedRetracted);
		FocusComposer();
		// 光标停在撤回内容之后：接着写的那一句紧跟着被撤回的消息，不跑到末尾。
		// 放在 Focus 之后：聚焦本身可能把光标规范化到末尾。
		_composer.CaretPosition = Math.Min(restored.Length, queued.Text.TrimEnd().Length);
	}

	/// 把图片插到附件列表最前面（与 PiDeck 的 `[...prompt.images, ...current]` 一致）。
	/// 仍走输入区的容量上限：撤回不是绕过 10 张 / 20 MiB 限制的入口。
	private void InsertAttachmentsFirst(IReadOnlyList<ImageContent> images)
	{
		if (images.Count == 0) return;
		var accepted = new List<(string Path, ImageContent Content)>(images.Count);
		var count = _attachments.Count;
		var total = _attachments.Sum(static item => item.Content.Data.Length * 3L / 4);
		foreach (var image in images)
		{
			var bytes = image.Data.Length * 3L / 4;
			if (count >= ComposerDrop.MaxImages || total + bytes > ComposerDrop.MaxTotalBytes)
			{
				NotifyError(Strings.AttachmentLimit);
				break;
			}

			accepted.Add(($"queue-retract:{Guid.NewGuid():N}", image));
			count++;
			total += bytes;
		}

		if (accepted.Count == 0) return;
		_attachments.InsertRange(0, accepted);
		RefreshAttachments();
	}

	internal void DiscardQueued(ChatQueuedMessage queued)
	{
		if (_disposed) return;
		if (!_chat.RetractQueued(queued)) Notify(Strings.QueuedGone);
	}

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

	private void RefreshWidgetColumn()
	{
		// IsVisible 的同值写入也会触发失效：待办/队列/扩展三处都会调这里，先比较再写。
		var visible = _todoCard.View.IsVisible || _extensionCard.View.IsVisible || _queueCard.View.IsVisible;
		if (_widgetColumn.IsVisible != visible) _widgetColumn.IsVisible(visible);
	}

	/// 轨迹页的数据源：当前分支的会话条目（已落盘事实）+ 聊天投影里还没落盘的流式尾行。
	/// 不另开读取通道：会话文件与聊天投影同属这个标签，切换面板不需要等待 IO。
	internal TrajectorySource TrajectorySnapshot() => new(
		Session.Session.GetBranch(),
		_chat.Entries(),
		IsStreaming || Session.IsCompacting,
		_chat.ThinkingTimings,
		_chat.ThinkingTimingsRevision);

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

	public void SetAvailableModels(Func<IReadOnlyList<Model>> availableModels) => _modelPicker.SetSource(availableModels);
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
		// 切标签时弹层跟着走：淡出只会把旧菜单多留在窗口上，直接收。
		_composerAddMenu.CloseInstantly();
		_composerPicker.Hide();
		_securityMenu.CloseInstantly();
		_usagePanel.Hide();
	}

	private async Task CompactAsync()
	{
		if (IsStreaming || Session.IsCompacting || _disposed) return;
		var cancellation = new CancellationTokenSource();
		_compactionAbort = cancellation;
		_sending = true;
		_turnStarted = Stopwatch.GetTimestamp();
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
		// 等待占位行按身份认，不按 Role：它看上去是助手侧的第一行，但不是助手回复。
		if (entry.EntryId == PendingReplyEntryId) return new PendingReplyView(TurnElapsedMs);
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

	/// 用户消息：右侧内嵌气泡，与助手回复（左侧正文）一眼分开；技能上下文收成 chip，
	/// 文件引用在正文里由投影层显示成行内 chip。正文保持可选中，时间与操作在气泡下方，图片缩略图在气泡上方。
	/// 外层 StackPanel 挂 actions（Tag 约定）。
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
					AppIcons.Button("pencil", Strings.EditAndResend,
						() => PrepareEdit(entryId, entry.Text, entry.Images), size: 14)
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

	/// 回到这条用户消息之前重开一条分支：会话文件会换成新文件（旧文件保留在历史里），
	/// 之后输入的内容都落在新分支上。
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

	internal static UIElement ImageView(ChatImage image, Action<ImageSource>? preview = null) => ChatImages.Create(image, preview);

	private void OpenMessageLink(string url) => Shell.OpenMessageLink(url, Session.Cwd, message => NotifyError(message));

	private void PreviewImage(ImageSource source) => _imagePreview.Show(View, source);

	private void PreviewAttachment(ImageContent image)
	{
		try { PreviewImage(ChatImages.Source(new ChatImage(image.Data, image.MimeType))); }
		catch (FormatException) { NotifyError(Strings.BrokenImage); }
	}

	/// 运行中发送键右侧的菜单（PiDeck 的 Split Send 下拉同款）：每项是「标题 + 生效时机」
	/// 两行。投递方式是发送前最难猜的部分，「— steer」这种尾缀说明不了它什么时候生效，
	/// 所以协议名与时机写在第二行。
	private void ShowSendMenu()
	{
		if (_disposed) return;
		if (!_chat.IsStreaming) return;

		HoverHints.Cancel(_sendMenuButton);
		_sendMenu.Show(
			_sendMenuButton,
			[
				new SendOptionsMenu.Item("arrow-up", Strings.SteerCurrentTurn, Strings.SteerCurrentTurnHint, () => QueueDraft(steer: true)),
				new SendOptionsMenu.Item("list-ordered", Strings.QueueNextTurn, Strings.QueueNextTurnHint, () => QueueDraft(steer: false)),
				// 中断是破坏性动作：图标转红，并用分隔线与两种投递方式分组。
				new SendOptionsMenu.Item("x", Strings.Abort, Strings.AbortHint, Send, AppStyles.RoleError, SeparatorBefore: true),
			]);
	}

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
			var outgoing = ComposerPrompt.Compose(text, Mode);
			var queued = steer ? _chat.Steer(outgoing, images) : _chat.FollowUp(outgoing, images);
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

	private bool CanPasteFromClipboard()
	{
		if (_disposed || _composer.IsReadOnly) return false;
		if (ClipboardImages.Service?.HasImage() == true) return true;
		var clipboard = _composer.ClipboardService ??
			(Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null);
		return clipboard?.HasText() == true;
	}

	private bool PasteImageFromClipboard()
	{
		if (_disposed || ClipboardImages.Service is not { } service || !service.HasImage()) return false;
		_pendingPaste = PasteImageAsync(service);
		return true;
	}

	internal Task PendingPaste => _pendingPaste;

	internal ImagePreview ImagePreview => _imagePreview;

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

	private static int RetrySecondsRemaining(long retryAtUnixMs)
	{
		if (retryAtUnixMs <= 0) return 0;
		var remaining = retryAtUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining / 1000.0);
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
