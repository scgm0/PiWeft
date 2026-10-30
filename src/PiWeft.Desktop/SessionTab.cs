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
	/// <summary>
	/// 进行态心跳：等待期（没字可长、没事件可收）每 500ms 推一格呼吸点与轮播，
	/// 顺便带退避期的重试倒计时（每秒变化一次，一帧就够了）。不跑回合时停着，不占调度。
	/// </summary>
	private readonly DispatcherTimer _busyTimer = new(TimeSpan.FromMilliseconds(RespondingStatusPolicy.PulseStepMs));
	/// <summary>发送键右侧的投递方式菜单（自绘两行项，贴发送键上方展开）。</summary>
	private readonly SendOptionsMenu _sendMenu = new();
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
	/// <summary>
	/// 占位提示。实例一律经 <see cref="BuildComposerHint"/> 创建（显示换新见 <see cref="ShowComposerHint"/>）：
	/// 构造与换新的每一枚都必须不可命中，否则提示盖在输入框上会吞掉那一行的点击。
	/// </summary>
	private TextBlock _composerHint = BuildComposerHint();
	/// <summary>
	/// 最近一次组合事件的记录。只在框架没有实现 <c>ITextCompositionClient</c> 时作为兜底：
	/// 事件可能迟到或丢（Update 在 End 之后到达、组合被取消不发 End），
	/// 拿它当权威状态会让占位永久消失。
	/// </summary>
	private bool _composing;
	/// <summary>占位换新已被调度时挂在这里；组合开始要能把它取消，否则新实例会默认可见地盖到组合串上。</summary>
	private DispatcherTimer? _composerHintTimer;
	/// <summary>占位提示的宿主格（构造时与输入框同格），避免每次换新都全树搜索。</summary>
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

	/// <summary>挂窗后浮层宿主升入窗口 OverlayLayer（见 <see cref="HandleFloatingRootChanged"/>）。</summary>
	private bool _floatingHosted;
	private Window? _floatingWindow;
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
	/// <summary>输入区（widget 卡 / 空会话提示 / 进行态行 / 输入卡）。问询卡打开时整块收起，
	/// 与 PiDeck 一致：代理在等回答，输入框不该继续抢键盘焦点。</summary>
	private readonly StackPanel _inputStack;
	/// <summary>会话内联问询卡（ask_question）：钉在对话区与输入区之间，不再用遮罩弹窗。</summary>
	private readonly AskQuestionPanel _askPanel;
	private Border? _askCard;
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

	/// <summary>
	/// 输入区上方的「排队中」卡：运行中再发的消息在这里逐条列出，可撤回输入框或丢弃。
	/// 队列为空时整卡不占位（与待办卡一致）。
	/// </summary>
	private readonly ComposerWidgetCard _queueCard;
	private readonly StackPanel _queueRows = new();
	private IReadOnlyList<ChatQueuedMessage>? _renderedQueue;

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

	/// <summary>
	/// 输入区上方的会话进行态指示行（PiDeck responding 条）。
	/// 只要这一轮还在跑就一直占位：[图标] [呼吸点] [状态短语 · 事实后缀] | [功能提示]。
	/// </summary>
	private readonly Border _busyLine;
	/// <summary>状态行的列宿主：只在「跑回合」时占位（隐藏时整列不参与布局）。</summary>
	private readonly ReadingColumn _busyColumn;
	private readonly Image _busyIcon;
	private readonly TextBlock _busyPulse = new();
	private readonly TextBlock _busyText = new();
	private readonly Border _busyDivider;
	private readonly TextBlock _busyTip = new();
	private RespondingKind? _busyKind;
	/// <summary>最近一帧算出的进行态；心跳只拿它重新取轮播帧，不必重扫整段历史。</summary>
	private RespondingStatus? _busyStatus;
	/// <summary>本回合起点（发送与压缩自己记的单调时间戳）；0 表示没跑过。</summary>
	private long _turnStarted;

	/// <summary>时间线里等待占位行的身份：不属于会话条目，只在预热期拼在投影末尾。</summary>
	private const string PendingReplyEntryId = "live-pending-reply";

	/// <summary>占位行的共享实例：每一拍新建会让时间线误以为换了一行。</summary>
	private ChatEntry? _pendingRow;

	/// <summary>输入框左群的终端开关键：控制会话底部的终端面板。</summary>
	private readonly Button _terminalButton;

	/// <summary>本会话的安全档位入口（按会话覆盖，与 PiDeck 的分布一致）。</summary>
	private readonly Button _securityButton;
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

	/// <summary>
	/// 问询卡开关时触发。不能走 <see cref="StateChanged"/>：那条链路会顺带刷一遍会话列表
	/// （对全部会话文件做 stat），而开关一张卡片只是页签标题上那个小圆点变了。
	/// </summary>
	public event Action<SessionTab>? AskChanged;

	/// <summary>一条消息真的走完了一轮（成功、失败或中断都算发过了）；宿主用它记录「上次使用的模型」。</summary>
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
				// BorderSubtle 在深浅主题里都贴底色；用更亮的 TextFaint 让轮廓真正可见。
				button.BorderBrush = palette.TextFaint;
				button.BorderThickness = 1;
			});
		// 圆钮与它的投影合成一个元素：阴影由框架的 ShadowDecorator 画在自己的画布内，
		// 圆钮是其唯一子级、由装饰器排布。旧的「阴影画布 + 居中子级」是两块互相独立的录制，
		// 局部重绘里会出现只剩图标或只剩阴影的分层残缺。
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
		// 浮层挂在窗口 OverlayLayer 上，自己的 Bounds 始终是整个客户区，正文区变化时框架会跳过它的
		// arrange —— 没有这个驱动器，圆钮就停在旧位置、不跟随待办卡展开/搜索条出现/分栏拖动。
		_scrollToLatestWatcher = new FloatingTracker
		{
			Changed = HandleFloatingRootChanged,
			Arranged = () => _scrollToLatestFloatHost.InvalidateArrange(),
			IsHitTestVisible = false,
		};
		ApplyFloatingShadow();
		// 浮层一直参与布局，只用 opacity 隐藏：待办卡展开/收起会改变消息区高度，若用 IsVisible
		// 跳过 arrange，重新出现时图标缓存可能停在旧位置、和按钮底分离。
		// 隐藏只切这一个节点的 opacity，且只有 0 与 1 两个值（本按钮没有 opacity 过渡），
		// 因此不会把浮层带进 0<opacity<1 的合成分支。
		_scrollToLatestFloat.Opacity = 0;

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
		// 展开之后的流式续跟只能「保持」跟底：读者一上滚就得放手，不能每帧把会话拉回末尾。
		_textFocus.ContinueConversationFollowing = () => _conversationScroll!.ContinueFollowing();
		_textFocus.PauseFollowing = () => _conversationScroll!.Browse();
		_conversationScroll = new ConversationScroll(_scroll, visible => SetScrollToLatestVisible(visible && _messages.IsVisible));

		// 右缘定位轴：点击刻度跳到那条用户消息，滚动时高亮当前阅读位置。
		_outline.Jump += JumpToOutline;
		_outline.View.MouseWheel += args => SmoothScroll.For(_scroll).RouteWheel(args);
		_scroll.ScrollChanged += OutlineScrolled;
		_mainArea.SizeChanged += _ => UpdateOutlineVisibility();

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
				ShowComposerHint(value.Length == 0);
				RefreshCommandBar();
			})
			// 中文/日文输入法组合期间正文还没落到 Text：只看 Text 长度会让占位一直留在原地，
			// 而 IME 的组合串也画在同一行，两者叠在一起（「速度|消息，@ 引用文件…」）。
			// 组合期间先让位，组合结束再按最终的 Text 决定恢复。
			.OnTextCompositionStart(_ => ComposerCompositionStarted())
			.OnTextCompositionUpdate(_ => ComposerCompositionStarted())
			.OnTextCompositionEnd(_ => ComposerCompositionEnded())
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
		_securityButton = AppIcons.Button("shield", Strings.SecurityTitle, ShowSecurityMenu);
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
			.Children(_planButton, attach, reference, commands, _terminalButton, _securityButton);
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
		// 三段都预留固定宽度：呼吸点在长与缩、状态短语在轮换、提示每七秒换一条，
		// 三者同时把行宽推来推去的话，等待中的眼睛就会看到整条线左右晃。
		_busyIcon = AppIcons.Icon("loader-circle", AppStyles.RoleInfo, 13);
		_busyPulse
			.FontSize(AppTheme.FontSizeMicro)
			.FontFamily(AppTheme.MonoFont)
			// 固定宽度按最宽的一帧（「···」）留够：等宽 12px 的三枚点约 22 DIP，
			// 给 20 会被裁掉最后一点；再宽就只是白白把状态短语推远。
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
			// 旁注用「次要」而不是「最弱」：深色主题下 FaintText 在卡外底色上几乎看不见，
			// 而这一行的全部意义就是被人看见（提示的是没有可见入口的能力）。
			.SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center)
			.IsVisible(false);
		// 宽度跟输入卡一致、且与内容长度无关：提示每七秒换一条、状态短语也在换，
		// 若宽度跟内容走（Center + 内容宽），整行包括行首图标会跟着左右跳，等待中的眼睛
		// 特别容易注意到这个。外面这层 ReadingColumn 按可用宽度报尺寸（与扩展卡同一套），
		// 于是变长的是行尾那一句，行首永远钉在输入卡的左缘。
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

	/// <summary>本标签的宿主交互（ask_question）；随标签释放一起关闭。</summary>
	internal DesktopUiHost? UiHost { get; set; }

	/// <summary>待办卡是否占据输入区上方的位置（诊断与契约测试用）。</summary>
	internal bool TodoCardVisible => _todoCard.View.IsVisible;

	/// <summary>会话内联问询卡（ask_question）：宿主 <see cref="DesktopUiHost"/> 直接用它展示。 </summary>
	internal AskQuestionPanel AskPanel => _askPanel;

	/// <summary>当前是否有阻塞式问询卡在等回答（页签标题会带标记，后台页签也能看出来）。</summary>
	internal bool AskPending => _askPanel.IsOpen;

	/// <summary>输入区是否可见（问询卡打开时整块收起；诊断与契约测试用）。</summary>
	internal bool InputAreaVisible => _inputStack.IsVisible;

	/// <summary>
	/// 问询卡开关时收起/恢复输入区（PiDeck 同款：代理在等回答，输入框不能继续抢焦点），
	/// 并通知宿主刷新页签标题，让后台会话的提问也能被看到。
	/// </summary>
	private void OnAskVisibilityChanged(bool open)
	{
		if (_disposed)
		{
			return;
		}

		_inputStack.IsVisible(!open);
		AskChanged?.Invoke(this);
	}

	/// <summary>「排队中」卡是否占位（队列为空时整卡不占位）。</summary>
	internal bool QueueCardVisible => _queueCard.View.IsVisible;

	/// <summary>发送键右侧的投递方式菜单（诊断与契约测试用）。</summary>
	internal SendOptionsMenu SendMenuForTests => _sendMenu;

	/// <summary>无窗口测试里直接打开投递方式菜单（真实入口是发送键的 caret）。</summary>
	internal void ShowSendMenuForTests() => ShowSendMenu();

	/// <summary>输入框当前内容（测试与快捷发送共用同一条读路径）。</summary>
	internal string ComposerText => _composer.Text;

	/// <summary>扩展 widget 卡是否占据输入区上方的位置（诊断与契约测试用）。</summary>
	internal bool ExtensionCardVisible => _extensionCard.View.IsVisible;

	/// <summary>标签页使用的聊天记录模型；宿主可以在后台预建后交接过来。</summary>
	internal ChatViewModel Chat => _chat;

	/// <summary>当前进行态（诊断与单测读取；空闲为 null）。就是状态条那一行现在说的话。</summary>
	internal RespondingStatus? BusyStatus => _busyStatus;

	/// <summary>代理事件层是否已进入流式状态（诊断与契约测试用）。</summary>
	internal bool ChatStreaming => _chat.IsStreaming;

	/// <summary>草稿里已识别的文件引用数（诊断与契约测试用）。</summary>
	internal int ComposerReferenceCount => UserReferenceRichText.Count(_composer.Text);

	/// <summary>输入区当前附件（诊断与契约测试用）。</summary>
	internal IReadOnlyList<ImageContent> ComposerAttachments => [.. _attachments.Select(static item => item.Content)];

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
		// 发送弹窗是原生浮层，不随 View 释放；标签关掉时要关窗并释放 Popup 的原生资源。
		_sendMenu.Dispose();
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
		// 进行态行也在这一列里：它必须跟着阅读宽度走，否则改完宽度后状态条会跟输入卡错位。
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

	/// <summary>原位换色，保留 Markdown、选区、工具折叠与阅读位置。</summary>
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
		AppStyles.ApplyTextColors(_imagePreview.View);
		// 进行态条的图标是直接取色的（没进 AppIcons 的角色跟踪表），换肤后要重新取一次；
		// 清掉 kind 就是让它下一帧按新主题重新上色，而文字那几段已经由角色重上色。
		_busyKind = null;
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

	/// 编辑重发带回的图片没有文件路径；附件按路径去重，这里给一个稳定的合成前缀。
	private const string EditImagePrefix = "edit-resend:";

	/// <summary>
	/// 「编辑重发」：把原文放回输入框，并记下要回到的条目。发送时先在该处开分支，
	/// 于是修改后的提问落在新分支上，原历史仍可回看。原消息的图片一并带回附件区，
	/// 重发才会带上同样的内容块。
	/// </summary>
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
		// 状态条要回答「已经等了多久」，得有一个属于本回合的起点；AgentStart 比这里晚到，
		// 中间那一段（检查点快照、分支切换、展开附件）也用同一个时钟。
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
				// 这一轮真的发过了：通知宿主记账「上次使用的模型」（PiDeck 也是发送被接受后才记）。
				// 偏好落盘属于宿主，不属于标签：无窗口测试直接驱动标签时不该碰真目录里的 desktop.json。
				MessageSent?.Invoke(this);
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
		var wasHidden = _scrollToLatestFloat.Opacity == 0;
		_scrollToLatestFloat.Opacity = visible ? 1 : 0;
		_scrollToLatest.IsHitTestVisible = visible;
		_scrollToLatest.Focusable = visible;
		// 浮层一直参与 arrange；重新出现时再强制一次测量，清掉极端时序下内容层的旧位置。
		if (visible && wasHidden) _scrollToLatestFloatHost.InvalidateMeasure();
	}

	/// <summary>阴影按当前配色取深浅两档：浅色主题下重阴影会糊成一圈灰边。浮层收不到窗口的换肤通知，所以显式刷。</summary>
	private void ApplyFloatingShadow() =>
		_scrollToLatestFloat.ShadowColor = Color.FromArgb(AppTheme.IsLightTheme ? (byte)0x22 : (byte)0x96, 0, 0, 0);

	/// <summary>
	/// 挂到窗口后把浮层宿主升入窗口的 OverlayLayer：浮层与正文是两个独立的绘制根，
	/// 时间线行在流式/回收中被整块重绘时不会再把与自己重叠的按钮像素盖掉
	/// （保留场景对正文脏区的重绘不保证回放树序在它之上的兄弟内容）。
	/// 未挂窗（单元测试直接排布 <c>tab.View</c>）时宿主留在主区里，位置算法完全相同。
	/// </summary>
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

	/// <summary>
	/// 浮层宿主：把圆钮摆在 <see cref="Anchor"/> 的水平居中、下缘越过其底边 <see cref="Overlap"/> 的位置。
	/// 摆位在每轮 arrange 里按锚点的实时 Bounds 计算 —— 正文区宽/高怎么变（分栏拖动、侧栏折叠、
	/// 待办卡展开收起、窗口缩放）都不需要同步代码；OverlayLayer 只给客户区矩形这一个约束，
	/// 所以锚点必须自己读，而不是把边界换算成 margin 存起来。
	/// 自身对命中透明：只有圆钮（经由 ShadowDecorator 转发给子级）可点，其余区域照常滚正文。
	/// </summary>
	private sealed class FloatingAnchor : Panel
	{
		internal Element? Anchor;

		/// <summary>圆钮下缘越过锚点区下边的距离（DIP）。</summary>
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

		/// <summary>
		/// 宿主铺满正文区，但只有圆钮可命中：其余位置一律穿透，滚轮与悬停照常落到正文，
		/// 不会像 Panel 的默认实现那样把自己当作命中结果。
		/// </summary>
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

	/// <summary>
	/// 浮层驱动器：两件事——跟踪本标签页的视觉根（挂层/摘层），以及在正文区被重新摆位时要求浮层重排。
	/// 它自己铺满正文区，因此正文区的任何移动或缩放都会走到它的 ArrangeContent。
	/// </summary>
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

	/// <summary>
	/// 设置页保存安全配置后由宿主推给每个已打开的会话：档位是每次工具调用时读的，
	/// 不推的话界面显示的还是旧档，用户会以为改动没生效。
	/// </summary>
	public void ApplySecurityConfig(SecurityConfig config)
	{
		if (_disposed)
		{
			return;
		}

		Session.Security = config;
		RefreshPickers();
	}

	/// <summary>本会话安全档位的持久化键：新建会话还没有文件时用会话 id 兜底。</summary>
	private string SecurityKey => Session.Session.SessionFile ?? $"session:{Session.Session.SessionId}";

	/// <summary>
	/// 档位按钮的提示直接说清「现在生效的是哪档、为什么」——总开关关掉时档位标签会有欺骗性，
	/// 这时必须写明门没开，而不是显示一个看起来像在管的档位名。
	/// </summary>
	private void UpdateSecurityButton()
	{
		var config = Session.Security;
		var level = config.Resolve(SecurityKey);
		var overridden = config.Enabled &&
			config.SessionLevels.TryGetValue(SecurityKey, out var assigned) &&
			!string.Equals(assigned, config.DefaultLevelId, StringComparison.Ordinal);

		_securityButton.HoverTip(!config.Enabled
			? Strings.SecurityDisabledHint
			: overridden
				? Strings.SecurityOverrideHint(level.Name, level.Description)
				: Strings.SecurityFollowHint(level.Name, level.Description));
	}

	/// <summary>
	/// 按会话切换安全档位。列表里给「跟随默认」而不是直接列档位：
	/// 清除覆盖与选了一个恰好等于默认的档位在配置上是两件事，混在一起会留下永远清不掉的脏键。
	/// </summary>
	private void ShowSecurityMenu()
	{
		if (_disposed)
		{
			return;
		}

		var config = SecurityConfig.Load();
		if (!config.Enabled)
		{
			Toasts.Show(View, Strings.SecurityDisabledHint);
			return;
		}

		// 与思考强度菜单一致：MewUI 的 MenuItem 没有 IsCheckable，当前项用图标列的勾表达。
		HoverHints.Cancel(_securityButton);

		var current = config.SessionLevels.TryGetValue(SecurityKey, out var existing) ? existing : null;
		var checkIcon = new IconTemplate(size => new GlyphElement()
			.Kind(GlyphKind.CheckMark)
			.GlyphSize(4)
			.Foreground(AppTheme.Accent)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center));

		var menu = new ContextMenu();

		var follow = new Command("pi.security.follow", Strings.SecurityFollowDefault);
		_securityButton.Commands.Register(follow, () => ApplySecurityLevel(null));
		menu.Items.Add(new MenuItem(follow) { Icon = current is null ? checkIcon : null });

		foreach (var level in config.Levels)
		{
			var captured = level;
			var command = new Command($"pi.security.{captured.Level}", Strings.SecurityLevelOption(captured.Name, captured.Builtin));
			_securityButton.Commands.Register(command, () => ApplySecurityLevel(captured.Level));
			menu.Items.Add(new MenuItem(command)
			{
				Icon = string.Equals(current, captured.Level, StringComparison.Ordinal) ? checkIcon : null,
			});
		}

		menu.Show(_securityButton);
	}

	/// <summary>写回全局设置并就地替换本会话的生效配置：换档不该重建会话，也不该要求重启。</summary>
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

	/// <summary>
	/// 输入以 <c>/</c> 开头时，在输入框上方列出匹配的斜杠命令（提示词模板与技能），点击即补全。
	/// 命令数据来自会话发现的结果，与 RPC 的 <c>get_commands</c> 同源。
	/// </summary>
	/// <summary>
	/// 创建占位提示：样式与命中设置在这一处配齐 —— 提示叠在输入框上，
	/// 任何一枚（构造的或换新的）都必须 IsHitTestVisible(false)，点击才能穿到输入框。
	/// </summary>
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

	/// <summary>
	/// 显隐占位提示。显示即换一枚新实例：
	/// 保留场景把提示节点首次录制的光栅固化在节点上 —— 此后强制重绘、文本重建都换不掉，
	/// 输入内容再删除后重放的仍是那截被裁掉的占位；新实例没有旧录制，完整绘制。
	/// 换新延迟一拍，让「无提示」先被完整录制一帧，避免新旧光栅混在同一脏区。
	/// </summary>
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
		// 而不是每键 new + Dispose 一个 DispatcherTimer（后者每次都排一次调度器注册）。
		var timer = _composerHintTimer;
		if (timer is null)
		{
			timer = _composerHintTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200));
			timer.Tick += () =>
			{
				timer.Stop();
				// 这一拍（200ms）里用户可能又输入了内容、或输入法重新进入组合：
				// 到点时重新求值，而不是沿用调用瞬间的判断。
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

	/// <summary>
	/// 现在是否真的在组合。以框架的 <see cref="ITextCompositionClient.IsComposing"/> 为准——
	/// 它在组合提交/取消时由框架自己复位；我们记的事件状态只作为兜底。
	/// 只认事件状态时，「组合已结束但 End 没到」会让占位永久消失（中文输入后删空文本即复现）。
	/// </summary>
	private bool IsComposingNow() =>
		_composer is ITextCompositionClient composition ? composition.IsComposing : _composing;

	/// <summary>输入法组合开始（IME 预编辑）：立刻让位，并取消挂起的换新。</summary>
	internal void ComposerCompositionStarted()
	{
		if (_disposed) return;
		_composing = true;
		CancelComposerHintSwap();
		_composerHint.IsVisible(false);
	}

	/// <summary>
	/// 组合结束：按提交后的正文决定占位要不要回来（取消组合时正文仍为空）。
	/// 这里显式清掉事件状态，并重新按「文本为空」求值——即使 End 之后又来一次
	/// 迟到的 Update 把状态置回 true，<see cref="IsComposingNow"/> 仍以框架状态为准。
	/// </summary>
	internal void ComposerCompositionEnded()
	{
		_composing = false;
		if (_disposed) return;
		ShowComposerHint(_composer.Text.Length == 0);
	}

	/// <summary>取消已挂起的占位换新：只停表，实例保留给下一次显示复用。</summary>
	private void CancelComposerHintSwap() => _composerHintTimer?.Stop();

	/// <summary>把提示换成一枚全新实例（延迟一拍由 ShowComposerHint 调度）。测试直接驱动这一拍。</summary>
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

		UpdateSecurityButton();

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
	/// 会话进行态（PiDeck respondingKind 同语义）。判定与措辞都在
	/// <see cref="RespondingStatusPolicy"/>：只要这一轮还在跑就永远有一句可说，
	/// 不再出现「完全没提示」的空窗（首轮发送后到首 token 之前就是这一段）。
	/// </summary>
	private RespondingStatus ComposeStatus(IReadOnlyList<ChatEntry> rows, bool pendingRowVisible)
	{
		var retry = _chat.Retry;
		var (liveTool, liveBody, liveThinking) = LiveActivity(rows);
		return RespondingStatusPolicy.Compose(new RespondingInput(
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
			retry is { } scheduled ? RetrySecondsRemaining(scheduled.RetryAtUnixMs) : 0));
	}

	/// <summary>
	/// 预热期要不要在时间线尾部补一行「在等回复」的占位。条件是「屏幕上最后一行还是用户
	/// 自己那条」且这一轮真的在等首 token —— 重试与压缩有自己的状态条文案，再挂一行
	/// 「正在等第一句话」就是自相矛盾。
	/// </summary>
	private bool PendingRowVisible(IReadOnlyList<ChatEntry> rows) =>
		!TurnHasOutput(rows) && _filter.Length == 0 && rows.Count > 0 &&
		_chat.Retry is not { Running: true } && !Session.IsCompacting;

	/// <summary>
	/// 从最新条目向回找第一个仍在流式的活动。每帧都走，因此只看 O(1) 的长度与标记：
	/// 读 <see cref="ChatEntry.Text"/> 会把整篇流式正文物化一遍。
	/// </summary>
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

	/// <summary>
	/// 本回合有没有已经落下可见的行。屏幕上最后一行还是用户自己那条，就说明请求还没换回
	/// 任何东西——这一段是「预热 + 等首 token」，和「工具收口、下一段还没开始」的空窗不是一回事：
	/// 前者看不见子阶段，可以给几句真话轮着说；后者只能说「正在处理」。
	/// </summary>
	private static bool TurnHasOutput(IReadOnlyList<ChatEntry> rows) =>
		rows.Count > 0 && rows[^1].Role != ChatRole.User;

	/// <summary>本回合已用时长：取「发送/压缩」与「AgentStart」里更晚的那个起点。</summary>
	private double TurnElapsedMs()
	{
		var started = _turnStarted;
		var agent = _chat.TurnStarted;
		if (agent != 0 && (started == 0 || agent > started)) started = agent;
		return started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;
	}

	/// <summary>
	/// 右侧功能提示：只在屏幕自己不动的时候占位（推流期眼睛在正上文中，不抢视线），
	/// 每 7 秒换一条；设置里关掉就整段不占位。
	/// </summary>
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

	/// <summary>
	/// 预热期在时间线尾部补一行「在等回复」的占位：首轮等待时正文区只有用户自己的气泡，
	/// 看不出请求是否被受理。它不是会话内容：不进投影、不进大纲、不进模型上下文，
	/// 只接在投影结果后面交给行宿主；首 token 一到就自然被真的流式行顶掉。
	/// </summary>
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

	/// <summary>保留未变化的消息控件和折叠状态，只重绘变化后的尾部。</summary>
	private void Render(bool force = false)
	{
		if (_disposed)
		{
			return;
		}

		// 流式期每 40 ms 合并一次渲染；只把真正贵的帧记进诊断，否则文件会被无害的抖动填满。
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
			// 渲染每帧都发生：常态只计数，真正慢的那几次才落明细（否则诊断自身就是垃圾源）。
			Diagnostics.Slow("render.stats", statsWatch, 8, $"rows={all.Count}");
		}

		var busy = _sending || _chat.IsStreaming;
		// 只要这一轮还在跑，状态条就一直在那儿：以前「没有流式行」直接不显示，
		// 首轮发送后到首 token 之前那段最长的等待反而一片空白。
		var pending = busy && PendingRowVisible(all);
		var responding = busy ? ComposeStatus(all, pending) : (RespondingStatus?)null;
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
		// 屏幕就不再有事件——没有这一拍，呼吸点会停在半道上，看起来就像卡死了。
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
		_planLabel.Text(Session.PlanModeEnabled ? Strings.PlanModeOn : Strings.PlanMode);
		_planButton.IsEnabled = !busy;
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
			Diagnostics.Slow("timeline.realize", realize, 8, $"rows={timelineRows.Count}");
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
				foreach (var action in actions.Children.OfType<Button>())
				{
					action.IsEnabled = !busy || HoverHints.Text(action) == Strings.CopyUserMessage;
				}
			}
		}

		_conversationScroll.FollowContent();

		if (renderWatch is not null)
		{
			var elapsed = renderWatch.Elapsed.TotalMilliseconds;
			Diagnostics.Slow("ui.render", elapsed, 8, $"rows={entries.Count}, visible={_timeline.VisibleRowCount}");
		}
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

	/// <summary>
	/// 重建「排队中」卡：运行中再发的消息还没进会话投影，只能在队列快照里看到，
	/// 这一块就是它们唯一的可见处。撤回 / 丢弃会改队列，下一次 queue_update 再刷新。
	/// </summary>
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

	/// <summary>
	/// 一条排队消息：序号 + 预览 + 投递时机标签 + 「撤回」「丢弃」。
	/// 与 PiDeck 的排队行一致——悬停给全文，行内只留一行省略号。
	/// </summary>
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

	/// <summary>
	/// 撤回：从队列拿掉并把内容放回输入框。与 PiDeck 的 retractQueuedPromptForEdit 同语义：
	/// 撤回的文本排在已有草稿**前面**、空行分隔（用户刚写的那半句不会被吞掉），
	/// 图片前插到附件列表，光标落在撤回文本末尾。
	/// </summary>
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
		// 光标停在撤回内容之后：接着写的那一句应该紧跟着被撤回的消息，而不是跑到末尾。
		// 放在 Focus 之后：聚焦本身可能把光标规范化到末尾。
		_composer.CaretPosition = Math.Min(restored.Length, queued.Text.TrimEnd().Length);
	}

	/// <summary>
	/// 把图片插到附件列表最前面（与 PiDeck 的 `[...prompt.images, ...current]` 一致）。
	/// 仍走输入区的容量上限：撤回不是绕过 10 张 / 20 MiB 限制的入口。
	/// </summary>
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

	/// <summary>丢弃：不回填，直接从队列拿掉。</summary>
	internal void DiscardQueued(ChatQueuedMessage queued)
	{
		if (_disposed) return;
		if (!_chat.RetractQueued(queued)) Notify(Strings.QueuedGone);
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

	private void RefreshWidgetColumn()
	{
		// IsVisible 的同值写入也会触发失效：待办/队列/扩展三处都会调这里，先比较再写。
		var visible = _todoCard.View.IsVisible || _extensionCard.View.IsVisible || _queueCard.View.IsVisible;
		if (_widgetColumn.IsVisible != visible) _widgetColumn.IsVisible(visible);
	}

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

	/// <summary>
	/// 运行中发送键右侧的菜单（PiDeck 的 Split Send 下拉同款）：每项是「标题 + 生效时机」
	/// 两行。投递方式是发送前最难猜的部分——「— steer」这种尾缀说明不了它什么时候生效，
	/// 所以协议名与时机写在第二行。
	/// </summary>
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

	/// <summary>退避倒计时的剩余整秒（向上取整，永不为负）；无计划时刻时为 0。</summary>
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
