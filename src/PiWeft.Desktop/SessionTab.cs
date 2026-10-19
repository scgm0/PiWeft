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
	private readonly ChatViewModel _chat = new();
	private readonly ConversationTimeline _timeline;
	private readonly DispatcherTimer _renderTimer = new(TimeSpan.FromMilliseconds(40));
	private int _renderPending;
	private volatile bool _disposed;
	private bool _sending;
	private Task _pendingSend = Task.CompletedTask;
	private Task _pendingPaste = Task.CompletedTask;
	private CancellationTokenSource? _compactionAbort;
	private readonly ItemsControl _messages;
	private readonly ScrollViewer _scroll;
	private readonly ConversationScroll _conversationScroll;
	private readonly TextFocusScrollGuard _textFocus;
	private readonly MultiLineTextBox _composer;
	private readonly TextBlock _composerHint = new();
	private readonly Button _sendButton;
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
	private readonly TextBox _searchBox = new TextBox().PrimarySelection();
	private readonly TextBlock _filterStatus = new();
	private string _filter = string.Empty;
	private IReadOnlyList<ChatEntry>? _projectedSource;
	private IReadOnlyList<ChatEntry> _projectedRows = [];
	private readonly ExecutionProcessProjector _projector = new();
	private string _projectedFilter = string.Empty;
	private bool _projectedBusy;

	private readonly BranchPanel _branchPanel;
	private readonly Grid _mainArea = new();

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

	/// <summary>输入区上方的待办条：只在计划存在时显示，最多展示固定行数。</summary>
	private const int TodoStripMaxItems = 6;
	private readonly Border _todoStrip;
	private readonly StackPanel _todoRows = new();
	private TodoState? _renderedTodo;
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
		Func<IReadOnlyList<Model>>? availableModels = null)
	{
		_scrollToLatest = AppIcons.Button("arrow-down", Strings.ScrollToLatest, ScrollToLatest);
		_scrollToLatest.IsVisible(false);

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
		_conversationScroll = new ConversationScroll(_scroll, visible => _scrollToLatest.IsVisible(visible));

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
			});

		_commandBar.Spacing(AppTheme.Space2).Margin(new Thickness(0, 0, 0, 4)).IsVisible(false);

		_sendIcon.Width(14).Height(14).VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, image) => image.Source = AppIcons.Get(_lastBusy ? "x" : "arrow-up", AppTheme.For(theme).TextOnAccent));
		_sendButton = new Button()
			.Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
				_sendIcon, _sendLabel.Text(Strings.Send).FontSize(AppTheme.FontSizeCaption).OnAccentText()))
			.OnClick(Send)
			.Primary().Height(AppTheme.ControlHeightMd)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, 0))
			.VerticalAlignment(VerticalAlignment.Center);

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

		// 左群是会改变下一条消息的开关与插入动作（计划、附件、命令、终端），窄窗口自动换行；
		// 右群是这次请求的目标与代价（连接测试、模型、思考强度），发送/中断永远贴在最右。
		var leftActions = new WrapPanel().Spacing(4)
			.Children(_planButton, attach, commands, _terminalButton);
		var rightActions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			// 思考强度与发送键之间留一段间距：发送是主操按钮，不该贴着文本按钮。
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0))
			.Children(_testButton, _modelButton, _thinkingButton);
		var toolbar = new DockPanel().LastChildFill()
			.Children(_sendButton.DockRight(), rightActions.DockRight(), leftActions);

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

		_todoRows.Spacing(2);
		_todoStrip = new Border()
			.StyleName(AppStyles.Panel)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.IsVisible(false)
			.Child(_todoRows);

		var composerPane = _composerPane = new Border()
			.StyleName(AppStyles.Composer)
			.CornerRadius(AppTheme.RadiusLg)
			.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space3, AppTheme.Space3, AppTheme.Space2))
			.Margin(new Thickness(AppTheme.Space4, AppTheme.Space2, AppTheme.Space4, AppTheme.Space3))
			.Child(new StackPanel()
				.Spacing(AppTheme.Space2)
				.Children(_todoStrip, _editNotice, _commandBar, _attachmentBar,
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
			.OnTextChanged(_ =>
			{
				_filter = _searchBox.Text?.Trim() ?? string.Empty;
				Render();
			})
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
				.Children(viewSwitch.DockRight(), searchToggle.DockRight(), _scrollToLatest.DockRight(),
					_retry.DockRight(), _copyReply.DockRight(), _filterStatus.DockRight(), _status));

		var contextButton = new Button().Content(_contextLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.HoverTip(Strings.SessionStatistics).OnClick(ShowStatistics).Ghost();
		var statisticsBar = new ReadingColumn().Spacing(4).MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
			.HorizontalAlignment(HorizontalAlignment.Center).Children(
				_contextBar,
				new DockPanel().LastChildFill().Children(contextButton.DockRight(),
					_statisticsLine.FontSize(AppTheme.FontSizeMicro).SubtleText().FontFamily(AppTheme.MonoFont)));

		// 输入区整列（提示文本 + 输入框 + 状态栏）：空会话时落在填充行并居中，有消息后回到贴底的自适应行。
		_composerColumn = new StackPanel()
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Stretch)
			.Children(_emptyCard.IsVisible(false), composerPane,
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
		_imagePreview.Status = message =>
		{
			if (!_disposed) _status.Text(message);
		};
		_chat.Attach(session);

		RefreshPickers();
		Render();
	}

	public CodingAgentSession Session { get; }

	/// <summary>本标签的宿主交互（ask_question）；随标签释放一起关闭。</summary>
	internal DesktopUiHost? UiHost { get; set; }

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
		ReadingPreferences.Changed -= ApplyReadingPreferences;
		UiHost?.Hide();
		_compactionAbort?.Cancel();
		HideTransientPanels();
		_renderTimer.Dispose();
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
			_chat.Abort();
			return;
		}

		if (_loadingAttachments > 0)
		{
			_status.Text(Strings.AttachmentLoading);
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
		_readingStatisticsBar.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6);
		_composer.FontSize(AppTheme.FontSizeChat);
		_composerHint.FontSize(AppTheme.FontSizeChat);
		Render(force: true);
	}

	/// <summary>原位换色，保留 Markdown、选区、工具折叠与阅读位置。</summary>
	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors((Element)View);
		_status.Foreground(AppStyles.ColorForRole(AppStyles.RoleSubtle));
		AppStyles.ApplyTextColors(_modelPicker.View);
		AppStyles.ApplyTextColors(_statisticsPanel.View);
		AppStyles.ApplyTextColors(_usagePanel.View);
		AppStyles.ApplyTextColors(_branchPanel.View);
		AppStyles.ApplyTextColors(_imagePreview.View);
		_terminal?.ApplyTheme();
	}


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
			_status.Text(Strings.AttachmentLoading);
			return;
		}
		if (_attachments.Count > 0 && !Session.Model.SupportsImages)
		{
			_status.Text(Strings.ImageModelRequired);
			_composer.Text = text;
			return;
		}

		_sending = true;
		string? failure = null;
		Render();
		StateChanged?.Invoke(this);

		try
		{
			// 发送前自动打一个检查点（每仓库每分钟最多一次，且工作区有改动才打），
			// 让模型的改动可以回滚；快照走后台，避免阻塞界面。
			await Task.Run(() => Checkpoints.AutoSnapshot(Session.Cwd, TimeSpan.FromMinutes(1))).ConfigureAwait(false);

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

				await Session.PromptAsync(message).ConfigureAwait(false);
			}
			else
			{
				await _chat.SendAsync(text).ConfigureAwait(false);
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
					_status.Text(Strings.SendFailed(failure));
				}

				RefreshTitle();
				StateChanged?.Invoke(this);
			});
		}
	}

	/// <summary>展开/收起会话内搜索框（供无人值守的界面演练调用）。</summary>
	public void ToggleSearchFromSmoke() => ToggleSearch();

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

		_searchBox.Text = string.Empty;
		_filter = string.Empty;
		Render();
	}

	/// <summary>一条聊天记录是否命中当前过滤关键字（正文或工具名）。</summary>
	private static bool Matches(ChatEntry entry, string filter) =>
		filter.Length == 0 ||
		entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
		entry.Thinking.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
		(entry.ToolName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

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
		SetEmptyState(showChat && _chat.Entries().Count == 0);
		_noMatches.IsVisible(showChat && _filter.Length > 0 && _messages.ItemsSource.Count == 0);
		_branchPanel.View.IsVisible(!showChat);

		if (showChat)
		{
			Render();
		}
		else
		{
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
			_status.Text(Strings.AttachFailed(ex.Message));
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
			_status.Text(Strings.NothingToCopy);
			return;
		}

		CopyText(text);
	}

	/// <summary>复制一段文本，并在状态行给出反馈。</summary>
	private void CopyText(string text)
	{
		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		_status.Text(clipboard?.TrySetText(text) == true ? Strings.Copied : Strings.ClipboardUnavailable);
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

	/// <summary>测试当前模型的连通性，结果写到状态行。</summary>
	private async Task TestConnectionAsync()
	{
		if (_testing || _sending || _chat.IsStreaming || _disposed)
		{
			return;
		}

		_testing = true;
		Dispatch(() => _status.Text(Strings.TestingConnection));
		try
		{
			var result = await Session.TestConnectionAsync().ConfigureAwait(false);
			Dispatch(() => _status.Text(result.Ok
				? Strings.ConnectionOk(Session.Model.Provider, Session.Model.Id, result.LatencyMs)
				: Strings.ConnectionFailed(result.Error ?? "unknown")));
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
		_modelLabel.Text(Session.Model.Id);
		_modelButton.HoverTip($"{Session.Model.Provider}/{Session.Model.Id}\n{Strings.ModelCapabilities(Session.Model)}");

		var thinking = Strings.ThinkingLevelName(Session.ThinkingLevel);
		_thinkingLabel.Text(thinking);
		_thinkingButton.HoverTip($"{Strings.ThinkingLevelTitle}：{thinking}");
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
		if (Session.IsCompacting) status += $" · {Strings.Compacting}";
		else if (IsStreaming) status += $" · {Strings.Streaming}";
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
		_modelButton.IsEnabled = !busy;
		_thinkingButton.IsEnabled = !busy;
		_planLabel.Text(Session.PlanModeEnabled ? Strings.PlanModeOn : Strings.PlanMode);
		_planButton.IsEnabled = !busy;
		_testButton.IsEnabled = !busy && !_testing;
		_retry.IsEnabled = !busy && all.Any(static row => row.Role == ChatRole.User);
		_copyReply.IsEnabled = all.Any(static row => row.Role == ChatRole.Assistant && row.Text.Length > 0);

		_timeline.SetEntries(entries, force, _projector.LastStablePrefix, _projector.LastPrefixReused);
		SetEmptyState(all.Count == 0 && _messages.IsVisible);
		_noMatches.IsVisible(all.Count > 0 && entries.Count == 0 && _messages.IsVisible);
		if (all.Count == 0)
		{
			_conversationScroll.Stop();
		}

		foreach (var bubble in _timeline.VisibleRows.OfType<Border>())
		{
			if (bubble.Tag is StackPanel actions)
			{
				foreach (var action in actions.Children.OfType<Button>().Skip(1))
				{
					action.IsEnabled = !busy;
				}
			}
		}

		_conversationScroll.FollowContent();
	}


	/// <summary>重建待办条；只在计划对象变化时调用，平时零成本。</summary>
	private void RefreshTodoStrip(TodoState state)
	{
		var plan = state.ActivePlan;
		if (plan is null || plan.Items.Count == 0)
		{
			if (_todoStrip.IsVisible)
			{
				_todoRows.Clear();
				_todoStrip.IsVisible(false);
			}

			return;
		}

		var completed = 0;
		foreach (var item in plan.Items)
		{
			if (item.Status == "completed")
			{
				completed++;
			}
		}

		_todoRows.Clear();
		_todoRows.Children(new TextBlock()
			.Text(Strings.TodoPlanProgress(completed, plan.Items.Count))
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText());

		var visible = Math.Min(plan.Items.Count, TodoStripMaxItems);
		for (var index = 0; index < visible; index++)
		{
			var item = plan.Items[index];
			_todoRows.Children(new TextBlock()
				.Text($"{TodoMarker(item.Status)} #{item.Id} {item.Text.Replace('\n', ' ')}")
				.FontSize(AppTheme.FontSizeMicro)
				.MutedText()
				.TextTrimming(TextTrimming.CharacterEllipsis));
		}

		if (plan.Items.Count > visible)
		{
			_todoRows.Children(new TextBlock()
				.Text(Strings.TodoPlanMore(plan.Items.Count - visible))
				.FontSize(AppTheme.FontSizeMicro)
				.SubtleText());
		}

		_todoStrip.IsVisible(true);
	}

	private static string TodoMarker(string status) => status switch
	{
		"completed" => "☑",
		"in_progress" => "◐",
		_ => "☐",
	};

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
				if (failure is not null) _status.Text(Strings.SendFailed(failure));
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
	/// 用户消息：整块靠右排，与助手回复（左侧正文）一眼分开；元信息行在色块上方（角色铭牌 + 悬停操作）。
	/// 色块不再带左缘「纬线」——那种强调形态留给错误行。外层 Border 挂 actions（Tag 约定）。
	/// </summary>
	private UIElement Bubble(ChatEntry entry)
	{
		var body = MessageText.Create(entry.Text);
		_textFocus.Track(body);

		var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2)
			.Children(AppIcons.Button("copy", Strings.CopyUserMessage, () => CopyText(entry.Text), size: 14));

		var rows = new StackPanel().Spacing(AppTheme.Space2).Children(body);
		foreach (var image in entry.Images)
		{
			rows.Children(ImageView(image, PreviewImage));
		}

		if (entry.EntryId is { Length: > 0 } entryId)
		{
			actions.Children(
				AppIcons.Button("pencil", Strings.EditAndResend, () => PrepareEdit(entryId, entry.Text), size: 14)
					.IsEnabled(!IsStreaming),
				AppIcons.Button("git-branch", Strings.BranchHere, () => BranchAt(entryId), size: 14)
					.IsEnabled(!IsStreaming));
		}

		var card = new Border()
			.StyleName(AppStyles.UserMessage)
			.BorderThickness(0)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.MaxWidth(AppTheme.MessageMaxWidth)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Child(rows);

		card.Tag = actions;

		return new StackPanel()
			.Spacing(AppTheme.Space1)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Children(
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space1)
					.HorizontalAlignment(HorizontalAlignment.Right)
					.Children(actions, Header(string.Empty, entry, Strings.RoleUser)),
				card);
	}

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
			_status.Text(Strings.Branched(Session.Session.SessionFile ?? string.Empty));
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Fork session", ex);
			_status.Text(Strings.BranchFailed(ex.Message));
		}
	}

	/// <summary>消息缩略图与预览复用同一个图片源。</summary>
	internal static UIElement ImageView(ChatImage image, Action<ImageSource>? preview = null) => ChatImages.Create(image, preview);

	private void OpenMessageLink(string url) => Shell.OpenMessageLink(url, Session.Cwd, message => _status.Text(message));

	private void PreviewImage(ImageSource source) => _imagePreview.Show(View, source);

	private void PreviewAttachment(ImageContent image)
	{
		try { PreviewImage(ChatImages.Source(new ChatImage(image.Data, image.MimeType))); }
		catch (FormatException) { _status.Text(Strings.BrokenImage); }
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
			Dispatch(() => { if (!_disposed) _status.Text(Strings.ClipboardImageUnavailable); });
			return;
		}

		if (_disposed) return;
		if (attachments.Count == 0)
		{
			_status.Text(Strings.ClipboardImageUnavailable);
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
