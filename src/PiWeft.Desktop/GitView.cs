using System.Globalization;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// Git 面板（参考 PiDeck 的 SCM 布局）：紧凑分支/同步栏、提交摘要、暂存/未暂存目录树、
/// 可折叠的差异与更多区。写操作走 <see cref="GitWriter"/>，只读查询走 <see cref="GitStatusReader"/>。
public sealed partial class GitView : IDisposable
{
	private readonly Func<string> _cwd;
	// 会话目录里可能并排多个仓库：面板的 git 操作落在用户选中的那个上。
	private IReadOnlyList<GitRepository> _repos = [];
	private string _repoPath = string.Empty;
	private readonly GitCommitAssistant? _assistant;
	private readonly Action? _openSettings;
	// 确认弹窗做成回调：无窗口的单测里不能弹 MessageBox，但「放弃更改」必须有确认。
	private readonly Func<string, string, Task<bool>> _confirm;
	// 隔离副本上「开个会话」：建会话与刷新侧栏是主窗口的事，面板只发目录。
	private readonly Action<string>? _openSessionIn;
	// 副本目录落在 agent 目录下，与子代理的隔离副本同一处；测试注入临时目录。
	private readonly Func<string> _worktreeRoot;
	// 差异浮层里的「引用到输入框」：把草稿追加到当前会话的 composer。
	private readonly Action<string>? _quoteToComposer;
	private bool _worktreeEditorVisible;
	private bool _focusWorktreeAfterBuild;
	private string _worktreeDraft = string.Empty;
	private string _worktreeBaseDraft = string.Empty;
	private TextBox? _worktreeInput;

	private readonly Grid _host = new();
	private readonly HashSet<string> _collapsedDirectories = new(StringComparer.Ordinal);
	// 分节宿主：交互态变化只换需要变的那一节。整棵面板重建会丢掉光标下的行、滚动位置与悬停态，
	// 点一下文件就「不跟手」。
	private readonly Grid _root = new();
	private readonly DockPanel _changesBody = new DockPanel().LastChildFill();
	private readonly Border _notRepoHost = new();
	private readonly Border _branchBarHost = new();
	private readonly Border _changesHeaderHost = new();
	private readonly Border _failureHost = new();
	private readonly Border _composerHost = new();
	private readonly Border _listHost = new();
	// 每行高度固定（文件 27 / 目录 26 / 分节 28，统一取 28）：固定行高才能让 presenter 只建视口内的行。
	private const double ChangeRowHeight = 28;
	private readonly ItemsControl _changesList = new();
	private readonly StackPanel _changesEmpty = new();
	private readonly Border _graphHost = new();
	private readonly Border _compareHost = new();
	private readonly Border _moreHost = new();
	private readonly PaneSash _graphSash;
	private readonly PaneSash _compareSash;
	private readonly PaneSash _moreSash;
	private GitPaneLayout _panes;
	private GitStatus? _lastStatus;
	private GitChangeStats _stats = GitChangeStats.Empty;
	private bool _sectionsBuilt;

	// 与轮末 chip 同一块浮层：右侧面板不再保留内嵌的 diff 卡片。
	private readonly FileDiffPreview _diffPreview = new();
	private DiffPreviewContent? _lastDiffContent;
	private string _graphRef = string.Empty;
	private int _graphPage = 40;
	private bool _branchesLoaded;
	private bool _branchesDirty = true;
	private bool _branchesLoading;
	private int _branchesVersion;
	private IReadOnlyList<string> _branches = [];
	private bool _graphLoaded;
	private bool _graphDirty = true;
	// 合计（rev-list --count）在翻页之间不会变：只有状态刷新/换筛选后才重新数。
	private bool _graphCountDirty = true;
	private IReadOnlyList<GitCommitEntry> _graphCommits = [];
	private int _graphTotal;
	private string? _graphError;
	// 图这一节搭一次：节头单独宿主，滚动视口一直活着。换掉视口会把滚动位置丢回顶部，
	// 展开提交、点「加载更多」时读者正在看的那一段就跳走了。
	private readonly Border _graphHeaderHost = new();
	private readonly ScrollViewer _graphScroll = new();
	private DockPanel? _graphPane;
	private readonly HashSet<string> _expandedCommits = new(StringComparer.Ordinal);
	private readonly Dictionary<string, GitCommitDetail> _commitDetails = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _commitErrors = new(StringComparer.Ordinal);
	// 一个提交可能有上千个文件：详情文件列表分页，只建前几十行。
	private const int CommitFilePageSize = 50;
	private const int CompareFilePageSize = 50;
	// 新建隔离副本的表单：标题、两行提示、两个输入与按钮，140 的默认区高放不下。
	private const double WorktreeEditorMinBody = 170;
	// 比较节正文的最小高度：扣掉节头、基准/目标、比较按钮与状态行，至少还留得下一行文件。
	private const double CompareMinBody = 120;
	private readonly Dictionary<string, int> _commitFilePages = new(StringComparer.Ordinal);
	private readonly HashSet<string> _commitLoading = new(StringComparer.Ordinal);
	private int _commitDetailVersion;
	private string _compareBase = string.Empty;
	private string _compareTarget = string.Empty;
	private GitBranchComparison? _compareResult;
	private string _compareResultBase = string.Empty;
	private string _compareResultTarget = string.Empty;
	private string? _compareError;
	private int _comparePage = CompareFilePageSize;
	// 审查层与已看标记：比较结果按层过滤，已看是人工进度，换结果时清空。
	private GitReviewLayer _compareLens;
	private readonly HashSet<string> _viewedCompareFiles = new(StringComparer.Ordinal);
	private IReadOnlyList<GitCommitFile> _compareVisibleFiles = [];
	private TextBlock? _compareViewedText;
	// 层按路径分类要切串，比较结果不变时不必每条重画都算一遍。
	private Dictionary<string, GitReviewLayer>? _compareLayers;
	// 仓内 .gitattributes 的分层规则：比较时读一次，设置新结果时一起用。
	private GitReviewRules _reviewRules = GitReviewRules.Empty;
	// 「已看」标记要落盘；测试替换这两处，避免读写用户的 desktop.json。
	internal Func<DesktopPreference> PreferenceLoader { get; set; } = static () => DesktopPreference.Load();

	internal Action<DesktopPreference>? PreferenceSaver { get; set; }

	private string ViewedCompareKey(GitBranchComparison comparison) =>
		$"{Repo()}|{comparison.BaseHash}|{comparison.TargetHash}";

	/// 只把分支间比较的已看进度落盘：工作区目标的分叉点哈希不变，内容却会变，标记会失真。
	private void PersistCompareViewed()
	{
		if (_compareResult is not { } comparison ||
			comparison.Error is not null ||
			IsWorktreeTarget(comparison.TargetHash))
		{
			return;
		}

		try
		{
			var key = ViewedCompareKey(comparison);
			var paths = _viewedCompareFiles.Count == 0 ? [] : _viewedCompareFiles.ToArray();
			var preference = PreferenceLoader().WithGitViewed(key, paths);
			if (PreferenceSaver is { } saver)
			{
				saver(preference);
			}
			else
			{
				preference.Save();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			// 落盘失败不影响当前的审查进度。
			AppLog.Failure(AppLog.For<GitView>(), "Save viewed compare files", ex);
		}
	}
	private bool _compareLoading;
	private int _compareVersion;
	private string _commitDraft = string.Empty;
	private string _branchDraft = string.Empty;
	private string? _lastFailureMessage;
	private string? _lastFailureDetail;
	private bool _branchEditorVisible;
	private bool _busy;
	private string _busyMessage = string.Empty;
	private bool _generating;
	private bool _focusCommitAfterBuild;
	private bool _focusBranchInputAfterBuild;
	private TextBox? _branchInput;
	private CancellationTokenSource? _summaryAbort;
	private bool _disposed;
	private int _refreshVersion;
	private bool _refreshInFlight;
	private bool _refreshQueued;
	private bool _graphLoading;
	private int _graphVersion;
	private MultiLineTextBox? _commitInput;

	// 提交摘要的结构化失败原因（对齐 PiDeck 的 GIT_COMMIT_* 码）：只在摘要链路里展示，
	// 与其它 git 操作的失败展开条分开，免得模型没配好却被引去查仓库。
	private GitSummaryFailure? _summaryFailure;
	private string _summaryFailureText = string.Empty;
	private ProgressBar? _summaryBar;
	private readonly DispatcherTimer _summaryTimer = new(TimeSpan.FromMilliseconds(250));
	// 面板可见时按仓库签名轮询：签名没变就不启动任何 git 子进程。
	// 子代理或在终端里 commit / 切分支 / fetch 后，面板两秒内自己跟上。
	private readonly DispatcherTimer _pollTimer = new(TimeSpan.FromSeconds(2));
	private long? _signature;
	private long? _refSignature;
	// 轮询的签名读取在后台线程：引用很多时枚举 refs 不该压在 UI 线程上。
	private bool _signatureChecking;
	// 「更多」区的两份列表由后台读、按脏位刷新，不在 UI 线程起 git 进程。
	private IReadOnlyList<GitStashEntry> _stashes = [];
	private IReadOnlyList<GitWorktree> _worktrees = [];
	private bool _moreDirty = true;
	private bool _moreLoading;
	private DateTime _summaryStarted;

	/// 面板当前操作的仓库：默认会话目录，选中子仓库后所有读写都以它为准。
	private string Repo() => _repoPath.Length > 0 ? _repoPath : _cwd();

	private static string DefaultWorktreeRoot() => Path.GetFullPath(Path.Combine(SessionPaths.GetAgentDir(), "worktrees"));

	public GitView(
		Func<string> cwd,
		GitCommitAssistant? assistant = null,
		Action? openSettings = null,
		Func<string, string, Task<bool>>? confirm = null,
		Action<string>? openSessionIn = null,
		Func<string>? worktreeRoot = null,
		Action<string>? quoteToComposer = null)
	{
		_cwd = cwd;
		_assistant = assistant;
		_openSettings = openSettings;
		_openSessionIn = openSessionIn;
		_worktreeRoot = worktreeRoot ?? DefaultWorktreeRoot;
		_quoteToComposer = quoteToComposer;

		_confirm = confirm ?? ((message, detail) =>
			MessageBox.ConfirmAsync(message, PromptIconKind.Warning, detail));

		// 三条可拖的分隔条：每条锚定「按下的那一刻、下面那一节多高」，之后按指针的窗口坐标
		// 换算出这一节应有的高度（见 PaneSash 的说明：用增量累加会自我抵消）。
		_graphSash = new PaneSash(() => _panes.Graph, height => SetPaneBody(Pane.Graph, height), SavePanes);
		_compareSash = new PaneSash(() => _panes.Compare, height => SetPaneBody(Pane.Compare, height), SavePanes);
		_moreSash = new PaneSash(() => _panes.More, height => SetPaneBody(Pane.More, height), SavePanes);
		_panes = GitPaneLayout.Decode(DesktopPreference.Load().GitPanes.GetValueOrDefault(Repo()));
		if (_panes.Compare < CompareMinBody)
		{
			// 旧布局存在 60 之类的小高度：比较节的按钮会被节高裁掉，读回来先抬到最小可用高度。
			_panes = _panes.WithCompare(CompareMinBody, _panes.CompareOpen);
		}

		_graphScroll
			.SmoothScrolling()
			// 右侧给滚动条留出浮层宽度。
			.Padding(new Thickness(0, 0, 10, 0))
			.AutoVerticalScroll()
			.NoHorizontalScroll();

		// 进度条向 PiDeck 看齐：按等待上限爬升，不用无限循环的不确定条，让用户知道还剩多久。
		_summaryTimer.Tick += UpdateSummaryProgress;
		_pollTimer.Tick += PollSignature;

		// 骨架只搭一次：DockPanel 的子级顺序即停靠顺序。底部先停靠的在最下面，
		// 所以自下而上是「更多 / 分隔条 / 比较 / 分隔条 / 图 / 分隔条」，「更改」吃掉剩余高度。
		_changesBody.Children(
			_failureHost.DockTop(),
			_composerHost.DockTop(),
			_listHost);

		// 改动可能有上万条（未跟踪的构建产物、大合并）。以前一次把每行都建出来：每行约 20 个控件，
		// 2 万条要 6 秒建树加 8 秒布局，面板打开就冻住。这里交给固定行高 presenter 只建视口内的行。
		_changesList.FixedHeightPresenter(ChangeRowHeight)
			.ItemPadding(Thickness.Zero)
			.BorderThickness(0)
			.Background(AppTheme.Transparent)
			// 右侧留出滚动条的浮层宽度：行尾的暂存按钮与比例条不该压在滚动条下面。
			.Padding(new Thickness(0, 0, 10, 0))
			.SmoothScrolling();
		_changesList.ItemTemplate = new DelegateTemplate<GitChangeRow>(
			static _ => new Border(),
			(host, row, _, _) => BindChangeRow((Border)host, row));
		_changesEmpty
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space3, AppTheme.Space3, AppTheme.Space3))
			.VerticalAlignment(VerticalAlignment.Top)
			.IsHitTestVisible(false)
			.IsVisible(false)
			.Children(
				AppIcons.Icon("circle-check", AppStyles.RoleSuccess, 14),
				new TextBlock().Text(Strings.GitNoPendingChanges).FontSize(AppTheme.FontSizeCaption).MutedText());
		_listHost.Child = new Grid().Children(_changesList, _changesEmpty);
		// 一行一节，吸掉剩余空间的那行由 ApplyPaneHeights 指定（见 AbsorberRow）。
		// 折叠某一节后，剩余高度要还给别的节，否则原地留一块空白。
		_root.Children(
			_branchBarHost.Row(0),
			_notRepoHost.Row(1),
			_changesHeaderHost.Row(2),
			_changesBody.Row(3),
			_graphSash.Row(4),
			_graphHost.Row(5),
			_compareSash.Row(6),
			_compareHost.Row(7),
			_moreSash.Row(8),
			_moreHost.Row(9));
		_notRepoHost.IsVisible = false;
		View = _host;
		// Initial load is asynchronous in a running window: Git status/graph must not run on the
		// UI thread while the panel is opening. Headless tests keep the synchronous path.
		Refresh();
		if (Application.IsRunning)
		{
			// 面板建好就开始盯仓库签名；收起或切到别的分段时 PollSignature 自己跳过。
			_pollTimer.Start();
		}

		LoadRepositories();
		_compareLens = LoadCompareLens();
	}

	public UIElement View { get; }

	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors(View);
		_diffPreview.ApplyTheme();
	}

	/// 面板收起或换会话时一并收掉浮层（浮层挂在窗口上，不属于面板的可视树）。
	public void HideOverlays() => _diffPreview.Hide();

	public void Refresh()
	{
		if (_disposed)
		{
			return;
		}

		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			Rebuild();
			return;
		}

		if (_refreshInFlight)
		{
			// 已有一轮在跑：等它回来再补一轮。直接再开一组 git 进程只会让两轮互相作废。
			_refreshQueued = true;
			return;
		}

		StartRefresh(dispatcher);
	}

	/// 自动刷新（轮询发现仓库变了、窗口回到前台、回合结束）：正在写提交信息时不动列表，
	/// 否则重建提交框会把焦点与已经输入的半句一起吞掉。返回是否真的重读了一次。
	internal bool RefreshAuto()
	{
		if (_disposed || _commitInput?.IsFocused == true)
		{
			return false;
		}

		Refresh();
		return true;
	}

	/// 比较仓库签名；变了就重读状态。返回是否真的刷新了。
	private bool RefreshIfChanged()
	{
		if (_disposed)
		{
			return false;
		}

		var snapshot = GitSignature.Read(Repo());
		if (snapshot is not { } signature)
		{
			// 仓库被删/移走：之前有签名就强制刷一次，让面板回到「不是仓库」。
			return _signature is not null && RefreshAuto();
		}

		if (_signature == signature.Full)
		{
			return false;
		}

		// 引用变了才重读提交图与分支列表：暂存一次只动 index，不该为它多跑一遍 git log。
		var refsChanged = _refSignature != signature.Refs;
		_graphDirty |= refsChanged;
		_branchesDirty |= refsChanged;

		// 签名在读到结果之后才记：`git status` 自己会重写 index（stat 缓存刷新），
		// 先记再读会把那次重写当成「又变了」，两秒一拍地白跑。
		return RefreshAuto();
	}

	/// 两秒一拍：面板看不见、正在跑写操作或已有刷新在途时都不动。
	private void PollSignature()
	{
		if (_disposed || !Application.IsRunning)
		{
			_pollTimer.Stop();
			return;
		}

		if (_busy || _generating || _refreshInFlight || _signatureChecking || !IsPanelVisible())
		{
			return;
		}

		if (Application.Current.Dispatcher is not { } dispatcher)
		{
			return;
		}

		_signatureChecking = true;
		_ = CheckSignatureAsync(Repo(), dispatcher);
	}

	/// 签名在后台读：大仓库的 refs 有几百上千个文件，枚举不能压在两秒一拍的 UI 线程上。
	private async Task CheckSignatureAsync(string repo, IDispatcher dispatcher)
	{
		GitSignatureValue? signature = null;
		try
		{
			signature = await Task.Run(() => GitSignature.Read(repo)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<GitView>(), "Read Git signature", ex);
		}

		void Complete()
		{
			_signatureChecking = false;
			if (_disposed)
			{
				return;
			}

			if (signature is not { } current)
			{
				if (_signature is not null)
				{
					_signature = null;
					_refSignature = null;
					RefreshAuto();
				}

				return;
			}

			if (_signature == current.Full)
			{
				return;
			}

			var refsChanged = _refSignature != current.Refs;
			_graphDirty |= refsChanged;
			_branchesDirty |= refsChanged;
			RefreshAuto();
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	/// 面板可能在窗口里被收起（右侧面板关掉）、被切到别的分段，或者整窗隐藏到托盘：
	/// 这三种情况都不该继续 stat/poll，收起时不花任何代价。
	private bool IsPanelVisible()
	{
		for (Element? element = View; element is not null; element = element.Parent)
		{
			if (element is FrameworkElement { IsVisible: false })
			{
				return false;
			}
		}

		return View.FindVisualRoot() is Window window && window.IsVisible && window.WindowState != WindowState.Minimized;
	}

	/// 测试入口：跳过计时器与可见性，直接跑一次签名比较。
	internal bool RefreshIfSignatureChangedForTests() => RefreshIfChanged();

	private void StartRefresh(IDispatcher dispatcher)
	{
		_refreshInFlight = true;
		_refreshQueued = false;
		var version = ++_refreshVersion;
		// 状态刷新也让在途的图加载作废：它对应上一份仓库快照。
		_graphVersion++;
		_graphLoading = false;
		_graphCountDirty = true;
		var cwd = Repo();
		_ = RefreshAsync(cwd, version, dispatcher);
	}

	private async Task RefreshAsync(string cwd, int version, IDispatcher dispatcher)
	{
		GitStatus status;
		GitChangeStats stats;
		GitSignatureValue? signature;
		try
		{
			// 行数统计和状态一起在后台读：面板打开前要多等两次 git diff，但都在 UI 线程外。
			// 签名同批算完：ApplyStatus 之后不用再在 UI 线程上 stat 一遍引用目录。
			(status, stats, signature) = await Task.Run(() =>
			{
				var read = GitStatusReader.Read(cwd);
				return (read,
					read.IsRepository ? ReadStats(cwd, read) : GitChangeStats.Empty,
					GitSignature.Read(cwd));
			}).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			status = new GitStatus { IsRepository = false, Error = Strings.GitUnavailable };
			stats = GitChangeStats.Empty;
			signature = null;
		}
		catch (Exception ex)
		{
			// 后台读抛了预期外的异常也要收尾：否则在途标记永远不清，面板不再刷新。
			AppLog.Failure(AppLog.For<GitView>(), "Refresh Git panel", ex);
			status = new GitStatus { IsRepository = false, Error = Strings.GitUnavailable };
			stats = GitChangeStats.Empty;
			signature = null;
		}

		void Complete()
		{
			_refreshInFlight = false;
			if (_disposed)
			{
				return;
			}

			if (version == _refreshVersion)
			{
				_signature = signature?.Full;
				_refSignature = signature?.Refs;
				ApplyStatus(status, stats);
			}

			if (_refreshQueued)
			{
				_refreshQueued = false;
				StartRefresh(dispatcher);
			}
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	/// 只读有内容的一侧：没有暂存改动就不跑 --cached，只有未跟踪文件就不跑工作区 diff。
	private static GitChangeStats ReadStats(string cwd, GitStatus status) =>
		GitDiffStatsReader.Read(
			cwd,
			status.Changes.Any(static change => change.HasStaged),
			status.Changes.Any(static change => change.WorkTreeStatus is not (" " or "?")));

	private void Rebuild()
	{
		var status = GitStatusReader.Read(Repo());
		var stats = status.IsRepository ? ReadStats(Repo(), status) : GitChangeStats.Empty;
		// 签名在状态之后读：`git status` 可能自己重写 index，先读会把那次重写当变化。
		var signature = status.IsRepository ? GitSignature.Read(Repo()) : null;
		_signature = signature?.Full;
		_refSignature = signature?.Refs;
		ApplyStatus(status, stats);
	}

	private void ApplyStatus(GitStatus status, GitChangeStats stats)
	{
		// 「先去暂存」这类内联提示在暂存之后就该消失，否则一直钉在提交框下面。
		if (_summaryFailure == GitSummaryFailure.EmptyDiff &&
			status.Changes.Any(static change => change.HasStaged && !change.IsConflict))
		{
			_summaryFailure = null;
			_summaryFailureText = string.Empty;
		}

		_lastStatus = status;
		_stats = stats;
		try
		{
			RebuildSections();
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
		{
			// 单个控件构建失败不该让整个面板空白；保留可见错误并写日志，用户能复制给维护者。
			AppLog.Failure(AppLog.For<GitView>(), "Build Git panel", ex);
			Swap(_changesHeaderHost, new TextBlock().Text(ex.Message)
				.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap)
				.Margin(new Thickness(AppTheme.Space2)));
		}
	}

	private void RebuildSections()
	{
		if (_lastStatus is not { } status)
		{
			// 换仓库的窗口期：旧行已经撤下、新状态还没回来，留一块明确的加载态。
			ShowRepositoryLoading();
			return;
		}

		if (!_sectionsBuilt)
		{
			_host.Children(_root);
			_sectionsBuilt = true;
		}

		_notRepoHost.IsVisible = !status.IsRepository;
		_graphHost.IsVisible = status.IsRepository;
		_graphSash.IsVisible = status.IsRepository;
		_compareHost.IsVisible = status.IsRepository;
		_compareSash.IsVisible = status.IsRepository;
		_moreHost.IsVisible = status.IsRepository;
		_moreSash.IsVisible = status.IsRepository;
		_changesHeaderHost.IsVisible = status.IsRepository;
		_changesBody.IsVisible = status.IsRepository;
		_branchBarHost.IsVisible = status.IsRepository;

		if (!status.IsRepository)
		{
			Swap(_notRepoHost, BuildNotRepository(status));
			return;
		}

		Swap(_branchBarHost, BuildBranchBar(status));
		RebuildChanges();
		RebuildBottomPanes(status);
		ApplyPaneHeights();

		// 提交框/AI 摘要与新建分支编辑器会重建控件，落位后把焦点要回来（AI 生成的草稿要能直接改）。
		if (_commitInput is not null)
		{
			_commitInput.IsVisible = true;
			if (_focusCommitAfterBuild)
			{
				_focusCommitAfterBuild = false;
				_commitInput.Focus();
			}
		}

		if (_branchInput is not null && _focusBranchInputAfterBuild)
		{
			_focusBranchInputAfterBuild = false;
			_branchInput.Focus();
		}
	}

	/// 换仓库的窗口期：撤下所有跟旧仓库有关的节，只留一句加载说明。
	/// 旧行留在界面上更危险：它们的动作闭包会在切换后落到新仓库上。
	private void ShowRepositoryLoading()
	{
		if (!_sectionsBuilt)
		{
			_host.Children(_root);
			_sectionsBuilt = true;
		}

		_notRepoHost.IsVisible = true;
		_graphHost.IsVisible = false;
		_graphSash.IsVisible = false;
		_compareHost.IsVisible = false;
		_compareSash.IsVisible = false;
		_moreHost.IsVisible = false;
		_moreSash.IsVisible = false;
		_changesHeaderHost.IsVisible = false;
		_changesBody.IsVisible = false;
		_branchBarHost.IsVisible = false;
		Swap(_notRepoHost, new StackPanel()
			.Spacing(AppTheme.Space2)
			.Padding(new Thickness(AppTheme.Space4))
			.Children(
				new TextBlock().Text(Strings.GitLoading)
					.FontSize(AppTheme.FontSizeControl).FontWeight(FontWeight.SemiBold).PrimaryText(),
				new TextBlock().Text(Strings.GitLoadingHint)
					.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap)));
	}

	private void RebuildChanges()
	{
		if (_lastStatus is not { } status)
		{
			return;
		}
		// 提交框与 diff 面板都留在各自的宿主里：换一节不影响另一节的焦点与滚动位置。
		Swap(_changesHeaderHost, BuildChangesHeader(status));
		Swap(_failureHost, BuildFailureStrip());
		RebuildComposer(status);
		RebuildList();
		ApplyPaneHeights();
	}

	private void RebuildComposer(GitStatus status) => Swap(_composerHost, BuildCommitComposer(status));

	/// 重建更改行表。列表控件本身不换：换成新宿主会把滚动位置、已建的行与悬停态一起丢掉。
	private void RebuildList()
	{
		if (_lastStatus is not { } status)
		{
			return;
		}

		var rows = GitChangeList.Build(status, _collapsedDirectories, _stats);
		_changesEmpty.IsVisible = rows.Count == 0;
		_changesList.IsVisible = rows.Count > 0;
		_changesList.ItemsSource = ItemsView.Create(
			rows,
			// 行文本只用于量列表自然宽度。给了真姓名，超长文件名会把 extent 撞出视口宽度，
			// 列表底部就多一条横向滚动条；行里的 TextBlock 自己会截断成省略号。
			static _ => string.Empty,
			static row => row.Key);
	}

	private void RebuildGraph()
	{
		var branches = _panes.GraphOpen || _panes.CompareOpen ? CurrentBranches() : [];
		LoadGraph(branches);
		if (_graphPane is null)
		{
			_graphPane = new DockPanel().LastChildFill().Children(_graphHeaderHost.DockTop(), _graphScroll);
			Swap(_graphHost, _graphPane);
		}

		Swap(_graphHeaderHost, BuildGraphHeader(branches));
		_graphScroll.IsVisible = _panes.GraphOpen;
		if (_panes.GraphOpen)
		{
			SetGraphContent(BuildGraphContent());
		}

		ApplyPaneHeights();
	}

	/// 只换图的内容：视口不换，滚动位置与滚动动画都留在原地。
	private void SetGraphContent(UIElement content)
	{
		if (_graphScroll.Content is { } previous)
		{
			_graphScroll.Content = null;
			UiLifetime.Release(previous);
		}

		_graphScroll.Content = content;
	}

	private void RebuildCompare()
	{
		if (_lastStatus is not { } status)
		{
			return;
		}

		var branches = _panes.GraphOpen || _panes.CompareOpen ? CurrentBranches() : [];
		Swap(_compareHost, BuildComparePane(status, branches));
		ApplyPaneHeights();
	}

	/// 「更多」区的列表（stash / 隔离副本）在后台读：面板每次刷新都在 UI 线程起两个
	/// git 进程会卡住滚动与输入，和 status/分支/图各自下线程池的做法不一致。
	private void LoadMore()
	{
		if (_moreLoading)
		{
			return;
		}

		_moreDirty = false;
		var repo = Repo();
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			_stashes = ReadStashes(repo);
			_worktrees = ReadWorktrees(repo);
			return;
		}

		_moreLoading = true;
		_ = LoadMoreAsync(repo, dispatcher);
	}

	private async Task LoadMoreAsync(string repo, IDispatcher dispatcher)
	{
		IReadOnlyList<GitStashEntry> stashes;
		IReadOnlyList<GitWorktree> worktrees;
		try
		{
			(stashes, worktrees) = await Task.Run(() => (ReadStashes(repo), ReadWorktrees(repo))).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<GitView>(), "Read Git more pane", ex);
			stashes = [];
			worktrees = [];
		}

		void Complete()
		{
			_moreLoading = false;
			if (_disposed)
			{
				return;
			}

			_stashes = stashes;
			_worktrees = worktrees;
			RebuildMore();
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	private static IReadOnlyList<GitStashEntry> ReadStashes(string repo)
	{
		try
		{
			return GitWriter.Stashes(repo);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<GitView>(), "Read Git stashes", ex);
			return [];
		}
	}

	private static IReadOnlyList<GitWorktree> ReadWorktrees(string repo)
	{
		try
		{
			return GitWriter.Worktrees(repo);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<GitView>(), "Read Git worktrees", ex);
			return [];
		}
	}

	private void RebuildMore()
	{
		// 面板刚建好、首轮异步状态还没回来时也可能走到这里（展开「更多」或截图脚本）：
		// 没有快照就先要一份，别拿 null 去建整段。
		if (_lastStatus is not { } status)
		{
			Refresh();
			return;
		}

		if (_panes.MoreOpen && _moreDirty)
		{
			LoadMore();
		}

		Swap(_moreHost, BuildMorePane(status));
		ApplyPaneHeights();
		// 编辑器重建后把焦点要回来：点「新建隔离副本」之后应该直接能打字。
		if (_focusWorktreeAfterBuild && _worktreeInput is not null)
		{
			_focusWorktreeAfterBuild = false;
			_worktreeInput.Focus();
		}
	}

	private void RebuildBottomPanes(GitStatus status)
	{
		RebuildGraph();
		RebuildCompare();
		RebuildMore();
	}

	private static void Swap(Border host, UIElement? content)
	{
		if (host.Child is { } previous && !ReferenceEquals(previous, content))
		{
			host.Child = null;
			UiLifetime.Release(previous);
		}

		host.Child = content;
		host.IsVisible = content is not null;
		host.InvalidateMeasure();
	}


	private IReadOnlyList<string> CurrentBranches()
	{
		if (_branchesLoaded && !_branchesDirty)
		{
			return _branches;
		}

		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			_branches = ReadBranches(Repo());
			_branchesLoaded = true;
			_branchesDirty = false;
			return _branches;
		}

		// Even `git branch` can be slow on a huge ref set. Keep the cached list visible and
		// fetch it in the background; the first graph render already shows the status.
		if (!_branchesLoading)
		{
			_branchesLoading = true;
			var version = ++_branchesVersion;
			var cwd = Repo();
			_ = LoadBranchesAsync(cwd, version, dispatcher);
		}

		return _branches;
	}

	private async Task LoadBranchesAsync(string cwd, int version, IDispatcher dispatcher)
	{
		var branches = await Task.Run(() => ReadBranches(cwd)).ConfigureAwait(false);

		void Complete()
		{
			if (_disposed || version != _branchesVersion)
			{
				return;
			}

			_branchesLoading = false;
			_branches = branches;
			_branchesLoaded = true;
			_branchesDirty = false;
			RebuildGraph();
			RebuildCompare();
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	/// 读分支列表：异常不能漏出 fire-and-forget 任务，否则 _branchesLoading 永远为 true，
	/// 筛选与比较节再也拿不到分支。
	private static IReadOnlyList<string> ReadBranches(string cwd)
	{
		try
		{
			return GitWriter.Branches(cwd);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<GitView>(), "Read Git branches", ex);
			return [];
		}
	}

	private UIElement BuildBranchBar(GitStatus status)
	{
		var bar = Separator(
			new Border()
				.Height(36)
				.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space1, 0))
				.Child(BuildBranchBarContent(status)),
			new Thickness(0, 0, 0, 1));

		if (!_branchEditorVisible)
		{
			return bar;
		}

		return new StackPanel().Spacing(0).Children(bar, BuildNewBranchEditor());
	}

	private UIElement BuildBranchBarContent(GitStatus status)
	{
		var right = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(1)
			.VerticalAlignment(VerticalAlignment.Center);

		if (_busy)
		{
			right.Children(new TextBlock()
				.Text(_busyMessage.Length > 0 ? _busyMessage : Strings.GitBusy)
				.FontSize(AppTheme.FontSizeMicro)
				.TextWrapping(TextWrapping.NoWrap)
				.Margin(new Thickness(0, 0, AppTheme.Space1, 0))
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Info));
		}

		var refresh = ToolbarButton("refresh-cw", Strings.RefreshGit, () =>
		{
			_branchesDirty = true;
			_graphDirty = true;
			// 手动刷新顺带重扫仓库列表：在子目录里新建仓库、删掉子仓库后不用重开面板。
			LoadRepositories();
			Refresh();
		});
		refresh.IsEnabled = !_busy && !_generating;
		right.Children(refresh);

		if (status.IsRepository)
		{
			var fetch = ToolbarButton("download", Strings.GitFetch, () => _ = RunRepoGitAsync(GitWriter.Fetch, Strings.GitFetchBusy));
			fetch.IsEnabled = status.HasRemotes && !_busy && !_generating;
			right.Children(fetch);

			var pulling = _busy && _busyMessage == Strings.GitPullBusy;
			var pushing = _busy && _busyMessage == Strings.GitPushBusy;
			var pullTooltip = status.Behind > 0 ? Strings.GitPullBehind(status.Behind) : Strings.GitPull;
			var pull = ToolbarCountButton("arrow-down", pullTooltip,
				() => _ = RunRepoGitAsync(GitWriter.Pull, Strings.GitPullBusy), status.Behind, pulling);
			pull.IsEnabled = status.HasRemotes && status.HasUpstream && !status.Detached && !_busy && !_generating;
			right.Children(pull);

			var pushTooltip = status.Ahead > 0 ? Strings.GitPushAhead(status.Ahead) : Strings.GitPush;
			var push = ToolbarCountButton("arrow-up", pushTooltip,
				() => _ = RunRepoGitAsync(GitWriter.Push, Strings.GitPushBusy), status.Ahead, pushing);
			push.IsEnabled = status.HasRemotes && !status.Detached && !_busy && !_generating;
			right.Children(push);
		}

		var branch = status.Branch.Length > 0 ? status.Branch : "HEAD";
		var branchContent = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(5)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				AppIcons.Icon("git-branch", AppStyles.RoleSubtle, 13),
				new TextBlock().Text(branch).FontSize(AppTheme.FontSizeCaption).PrimaryText()
					.MaxWidth(146).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis),
				ThemedGlyph(GlyphKind.ChevronDown, AppStyles.RoleFaint, 3))
			// 纯显示内容不参与命中：见 BuildFileRow 里的同一条说明。
			.IsHitTestVisible(false);

		Button? branchButton = null;
		branchButton = new Button()
			.Ghost()
			.Height(AppTheme.ControlHeightSm)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Content(branchContent);
		// DockPanel 的填充位会把按钮拉成整行宽（301 DIP）：那样悬停底色与命中区会铺满整条空白，
		// 分支键该按内容收口，剩余空白留给右侧的同步图标。
		branchButton.HorizontalAlignment(HorizontalAlignment.Left);
		branchButton.IsEnabled = status.IsRepository && !_busy && !_generating;
		branchButton.HoverTip(Strings.GitBranchMenu);
		// 闭包需要按钮本身作为菜单锚点；先建按钮再挂事件。
		branchButton.OnClick(() => ShowBranchMenu(branchButton!, status));

		if (_repos.Count > 1)
		{
			// 只有真的扫到多个仓库才占位置：单仓库会话的分支栏保持原样。
			var left = new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(2)
				.Children(RepositoryChip(), branchButton);
			return new DockPanel().LastChildFill().Children(right.DockRight(), left);
		}

		return new DockPanel().LastChildFill().Children(right.DockRight(), branchButton);
	}

	/// 仓库 chip：当前仓库名 + 下拉；切换后所有节都按新仓库重读。
	private Button RepositoryChip()
	{
		var current = _repos.FirstOrDefault(repository =>
			string.Equals(repository.Path, Repo(), StringComparison.OrdinalIgnoreCase));
		var label = current is { IsRoot: false }
			? current.Label
			: _repos.FirstOrDefault(static repository => repository.IsRoot)?.Label ?? Path.GetFileName(Repo());
		var chip = new Button()
			.Ghost()
			.Height(AppTheme.ControlHeightSm)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.HorizontalAlignment(HorizontalAlignment.Left)
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(4)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					AppIcons.Icon("folder-open", AppStyles.RoleSubtle, 13),
					new TextBlock().Text(label)
						.FontSize(AppTheme.FontSizeMicro)
						.MutedText()
						.MaxWidth(64)
						.TextWrapping(TextWrapping.NoWrap)
						.TextTrimming(TextTrimming.CharacterEllipsis),
					ThemedGlyph(GlyphKind.ChevronDown, AppStyles.RoleFaint, 3)));
		chip.OnClick(() => ShowRepositoryMenu(chip, current?.Path ?? _repoPath));
		chip.IsEnabled = !_busy && !_generating;
		chip.HoverTip(Strings.GitRepositoryMenu);
		chip.Tag = Strings.GitRepositoryMenu;
		return chip;
	}

	private void ShowRepositoryMenu(Button owner, string selectedPath)
	{
		var checkIcon = AppIcons.CheckTemplate();

		var menu = new ContextMenu();
		for (var index = 0; index < _repos.Count; index++)
		{
			var repository = _repos[index];
			var command = new Command($"pi.git.repo.{index}", repository.IsRoot ? $"{repository.Label}（根）" : repository.Label);
			owner.Commands.Register(command, () => SelectRepository(repository.Path));
			menu.Items.Add(new MenuItem(command)
			{
				Icon = string.Equals(repository.Path, selectedPath, StringComparison.OrdinalIgnoreCase) ? checkIcon : null,
			});
		}

		menu.Placement = MenuPlacement.Below;
		menu.MaxMenuHeight = 320;
		menu.Show(owner);
	}

	/// 切换仓库：存下旧仓库的面板布局，清掉全部按仓库缓存的结果，再按新仓库重读。
	private void SelectRepository(string path)
	{
		if (string.Equals(Repo(), path, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		SavePanes();
		_repoPath = path;
		ResetRepositoryState();
		_panes = GitPaneLayout.Decode(DesktopPreference.Load().GitPanes.GetValueOrDefault(Repo()));
		_compareLens = LoadCompareLens();
		Refresh();
	}

	private void ResetRepositoryState()
	{
		// 在途的状态读取属于上一个仓库：版本号推进后不会被应用。
		_refreshVersion++;
		_branchesVersion++;
		_branchesLoading = false;
		_branchesLoaded = false;
		_branchesDirty = true;
		_branches = [];
		_graphVersion++;
		_graphLoading = false;
		_graphLoaded = false;
		_graphDirty = true;
		_graphCountDirty = true;
		_graphCommits = [];
		_graphTotal = 0;
		ResetCommitDetails();
		_compareVersion++;
		_compareLoading = false;
		_compareResult = null;
		_compareError = null;
		_compareVisibleFiles = [];
		_compareLayers = null;
		_reviewRules = GitReviewRules.Empty;
		_compareLens = GitReviewLayer.All;
		_viewedCompareFiles.Clear();
		// 折叠状态与副本表单属于上一个仓库：换仓库后路径都不一样了。
		_collapsedDirectories.Clear();
		_worktreeEditorVisible = false;
		_worktreeDraft = string.Empty;
		_worktreeBaseDraft = string.Empty;
		_lastStatus = null;
		_lastFailureMessage = null;
		_lastFailureDetail = null;
		_summaryFailure = null;
		_summaryFailureText = string.Empty;
		_signature = null;
		_refSignature = null;
		_moreDirty = true;
		_moreLoading = false;
		_stashes = [];
		_worktrees = [];
	}

	private void LoadRepositories()
	{
		var cwd = _cwd();
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			ApplyRepositories(GitRepositories.Scan(cwd));
			return;
		}

		_ = LoadRepositoriesAsync(cwd, dispatcher);
	}

	private async Task LoadRepositoriesAsync(string cwd, IDispatcher dispatcher)
	{
		IReadOnlyList<GitRepository> repositories;
		try
		{
			repositories = await Task.Run(() => GitRepositories.Scan(cwd)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			// 扫描只是便利：失败就当只有会话目录一个仓库，面板照常可用。
			AppLog.Failure(AppLog.For<GitView>(), "Scan Git repositories", ex);
			repositories = [];
		}

		void Complete()
		{
			if (!_disposed)
			{
				ApplyRepositories(repositories);
			}
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	private void ApplyRepositories(IReadOnlyList<GitRepository> repositories)
	{
		if (SameRepositories(repositories))
		{
			return;
		}

		var selectedGone = _repoPath.Length > 0 &&
			!repositories.Any(repository => string.Equals(repository.Path, _repoPath, StringComparison.OrdinalIgnoreCase));
		_repos = repositories;
		if (selectedGone)
		{
			_repoPath = string.Empty;
			ResetRepositoryState();
		}

		// 首轮状态还没回来且已有一轮刷新在途：那轮 ApplyStatus 会重画分支栏，
		// 不为一枚按钮再排第二轮 git status。只有确实没有在途刷新时才自己拉一次。
		if (_lastStatus is null)
		{
			if (!_refreshInFlight)
			{
				Refresh();
			}

			return;
		}

		if (selectedGone)
		{
			Refresh();
			return;
		}

		RebuildSections();
	}

	private bool SameRepositories(IReadOnlyList<GitRepository> repositories)
	{
		if (repositories.Count != _repos.Count)
		{
			return false;
		}

		for (var index = 0; index < repositories.Count; index++)
		{
			if (!string.Equals(repositories[index].Path, _repos[index].Path, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return true;
	}

	private UIElement BuildNewBranchEditor()
	{
		TextBox? input = null;
		input = new TextBox().PrimarySelection()
			.Text(_branchDraft)
			.Placeholder(Strings.GitBranchPlaceholder)
			.Height(AppTheme.ControlHeightSm)
			.FontSize(AppTheme.FontSizeCaption)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.StyleName(AppStyles.TextInputLine)
			.OnTextChanged(value => _branchDraft = value)
			.OnKeyDown(key =>
			{
				if (key.Key == Key.Enter)
				{
					key.Handled = true;
					CreateBranch(input?.Text);
				}
				else if (key.Key == Key.Escape)
				{
					key.Handled = true;
					_branchEditorVisible = false;
					Refresh();
				}
			});
		_branchInput = input;

		var create = new Button()
			.Content(CenteredLabel(Strings.GitNewBranch).PrimaryText())
			.Height(AppTheme.ControlHeightSm)
			.OnClick(() => CreateBranch(input?.Text));

		return new Border()
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space2, AppTheme.Space2))
			.Child(new DockPanel().LastChildFill().Children(
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space1)
					.Children(create, CancelButton()).DockRight(),
				input.Margin(new Thickness(0, 0, AppTheme.Space1, 0))));
	}

	private void ShowBranchMenu(Button owner, GitStatus status)
	{
		var branches = GitWriter.Branches(Repo()).ToList();
		if (branches.Count == 0 && status.Branch.Length > 0)
		{
			branches.Add(status.Branch);
		}

		var checkIcon = AppIcons.CheckTemplate();

		var menu = new ContextMenu();
		for (var index = 0; index < branches.Count; index++)
		{
			var name = branches[index];
			var command = new Command($"pi.git.branch.{index}", name);
			owner.Commands.Register(command, () =>
			{
				if (string.Equals(name, status.Branch, StringComparison.Ordinal))
				{
					return;
				}

				_ = RunRepoGitAsync(repo => GitWriter.SwitchBranch(repo, name), Strings.GitBusy);
			});
			menu.Items.Add(new MenuItem(command)
			{
				Icon = string.Equals(name, status.Branch, StringComparison.Ordinal) ? checkIcon : null,
			});
		}

		if (branches.Count > 0)
		{
			menu.Items.Add(MenuSeparator.Instance);
		}

		var create = new Command("pi.git.branch.new", Strings.GitNewBranchMenu);
		owner.Commands.Register(create, () =>
		{
			_branchEditorVisible = true;
			_focusBranchInputAfterBuild = true;
			Refresh();
		});
		menu.Items.Add(new MenuItem(create));
		menu.Placement = MenuPlacement.Below;
		menu.MaxMenuHeight = 320;
		menu.Show(owner);
	}

	private void CreateBranch(string? name)
	{
		_ = RunRepoGitAsync(
			repo => GitWriter.CreateBranch(repo, name?.Trim() ?? string.Empty),
			Strings.GitBusy,
			onSuccess: () =>
			{
				_branchDraft = string.Empty;
				_branchEditorVisible = false;
			});
	}


	private UIElement BuildChangesHeader(GitStatus status)
	{
		var headerContent = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				ThemedGlyph(_panes.ChangesOpen ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3),
				new TextBlock().Text(Strings.GitChanges).FontSize(AppTheme.FontSizeCaption)
					.FontWeight(FontWeight.SemiBold).PrimaryText());
		if (status.Changes.Count > 0)
		{
			headerContent.Children(new TextBlock().Text(status.Changes.Count.ToString())
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		headerContent.IsHitTestVisible = false;

		var headerButton = new Button()
			.Ghost()
			.Height(28)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space2, 0))
			.OnClick(() =>
			{
				_panes = _panes.WithChanges(!_panes.ChangesOpen);
				SavePanes();
				RebuildChanges();
				RebuildBottomPanes(_lastStatus!);
			})
			.Content(headerContent);

		return Separator(
			new Border()
				.Height(30)
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Child(headerButton),
			new Thickness(0, 0, 0, 1));
	}

	private UIElement? BuildFailureStrip()
	{
		if (_lastFailureMessage is null && _lastFailureDetail is null)
		{
			return null;
		}

		var body = new StackPanel().Spacing(4)
			.Children(new TextBlock()
				.Text(_lastFailureMessage is { Length: > 0 } message ? message : Strings.GitFailed)
				.FontSize(AppTheme.FontSizeMicro).TextWrapping(TextWrapping.Wrap)
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Error));

		if (_lastFailureDetail is { Length: > 0 } detail)
		{
			body.Children(new TextBlock()
				.Text(detail)
				.FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeMicro)
				.FaintText()
				.TextWrapping(TextWrapping.Wrap));
		}

		return Separator(
			new Border()
				.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1))
				.Child(new Expander()
					.Header(new TextBlock().Text(Strings.GitLastFailure).FontSize(AppTheme.FontSizeMicro).MutedText())
					.IsExpanded(false)
					.Content(body)),
			new Thickness(0, 0, 0, 1));
	}

	private UIElement BuildCommitComposer(GitStatus status)
	{
		var staged = status.Changes.Count(static change => change.HasStaged && !change.IsConflict);
		var canCommit = staged > 0 && !_busy && !_generating;
		var branch = status.Branch.Length > 0 ? status.Branch : "HEAD";

		_commitInput = new MultiLineTextBox().PrimarySelection()
			.Text(_commitDraft)
			.Placeholder(Strings.GitCommitPlaceholderTo(branch))
			.Height(58)
			.FontSize(AppTheme.FontSizeCaption)
			.Padding(new Thickness(AppTheme.Space2, 6, AppTheme.Space2, 6))
			.StyleName(AppStyles.TextInput)
			.OnTextChanged(value => _commitDraft = value)
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Enter || !key.ControlKey)
				{
					return;
				}

				key.Handled = true;
				_ = CommitDraftAsync(_commitInput?.Text);
			});

		var actions = new Grid().Columns("auto,*").Spacing(AppTheme.Space2);
		actions.Children(BuildAssistantButton(staged).Column(0));

		var commit = new Button()
			.Primary()
			.Height(AppTheme.ControlHeightMd)
			.Content(CenteredLabel(Strings.GitCommit))
			.IsEnabled(canCommit)
			.OnClick(() => _ = CommitDraftAsync(_commitInput?.Text))
			.HoverTip(canCommit ? Strings.GitCommit : Strings.GitNothingStaged);
		actions.Children(commit.Column(1));

		var body = new StackPanel().Spacing(AppTheme.Space1).Children(_commitInput, actions);
		if (staged == 0 && status.Changes.Count > 0 && !_busy && !_generating)
		{
			// 没有暂存但有改动：给一个一键入口，但必须确认——「全部」会把没看过的文件也带进去。
			body.Children(SmallTextButton(Strings.GitStageAllAndCommit, () => _ = StageAllAndCommitAsync()));
		}

		if (staged == 0 && !_busy && !_generating)
		{
			body.Children(new TextBlock()
				.Text(Strings.GitNothingStaged)
				.FontSize(AppTheme.FontSizeMicro)
				.FaintText()
				.TextWrapping(TextWrapping.Wrap));
		}
		else if (staged > 0 && !_generating && _assistant?.IsBusy() == true)
		{
			body.Children(new TextBlock()
				.Text(Strings.GitSummaryBusy)
				.FontSize(AppTheme.FontSizeMicro)
				.FaintText()
				.TextWrapping(TextWrapping.Wrap));
		}

		if (_generating)
		{
			_summaryBar = new ProgressBar()
				.Minimum(0)
				.Maximum(100)
				.Height(3)
				.Margin(new Thickness(0, AppTheme.Space1, 0, 0));
			body.Children(_summaryBar);
			UpdateSummaryProgress();
		}
		else
		{
			_summaryBar = null;
		}

		if (_summaryFailure is { } summaryFailure)
		{
			var row = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center);
			row.Children(new TextBlock()
				.Text(_summaryFailureText.Length > 0 ? _summaryFailureText : SummaryFailureMessage(summaryFailure))
				.FontSize(AppTheme.FontSizeMicro)
				.TextWrapping(TextWrapping.Wrap)
				.WithTheme((theme, text) => text.Foreground = summaryFailure is GitSummaryFailure.ModelMissing or GitSummaryFailure.Failed
					? AppTheme.For(theme).Error
					: AppTheme.For(theme).TextMuted));
			if (summaryFailure is GitSummaryFailure.ModelMissing or GitSummaryFailure.Failed && _openSettings is not null)
			{
				row.Children(SmallTextButton(Strings.GitSummaryGoSettings, _openSettings));
			}

			body.Children(row);
		}

		return Separator(
			new Border()
				.Padding(new Thickness(AppTheme.Space2, AppTheme.Space2, AppTheme.Space2, AppTheme.Space2))
				.Child(body),
			new Thickness(0, 0, 0, 1));
	}

	private UIElement BuildAssistantButton(int staged)
	{
		var busy = _assistant?.IsBusy() == true;
		var hasModel = HasSummaryModel();
		var enabled = _assistant is not null && hasModel && staged > 0 && !_busy && !_generating && !busy;
		var tooltip = _assistant is null
			? Strings.GitSummary
			: !hasModel
				? Strings.GitSummaryModelMissing
				: _generating
					? Strings.GitGeneratingCommitSummary
					: busy
						? Strings.GitSummaryBusy
						: staged == 0
							? Strings.GitStageBeforeGenerate
							: ModelTooltip();

		var icon = AppIcons.Icon("sparkles", AppStyles.RoleSubtle, 15);
		var button = new Button()
			.Ghost()
			.Width(AppTheme.ControlHeightMd)
			.Height(AppTheme.ControlHeightMd)
			.Padding(0)
			.CornerRadius(AppTheme.RadiusSm)
			.BorderThickness(1)
			.WithTheme((theme, button) => button.BorderBrush = AppTheme.For(theme).BorderSubtle)
			.Content(icon.HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center))
			.IsEnabled(enabled)
			.OnClick(() => _ = GenerateCommitSummaryAsync())
			.HoverTip(tooltip);
		button.Tag = Strings.GitGenerateCommitSummary;
		return button;
	}

	private string ModelTooltip()
	{
		var resolved = ResolveAssistant();
		return $"{Strings.GitGenerateCommitSummary}（{resolved.Model.Provider}/{resolved.Model.Id} · {Strings.ThinkingLevelName(resolved.Thinking)}）";
	}

	private async Task CommitDraftAsync(string? message)
	{
		if (_busy || _generating || _disposed)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(message))
		{
			Toasts.Error(View, Strings.GitCommitMessageRequired);
			return;
		}

		var repo = Repo();
		var status = GitStatusReader.Read(repo);
		if (!status.Changes.Any(static change => change.HasStaged && !change.IsConflict))
		{
			Toasts.Show(View, Strings.GitNothingStaged);
			return;
		}

		var draft = message.Trim();
		await RunGitAsync(() => GitWriter.Commit(repo, draft), Strings.GitCommitBusy, onSuccess: () => _commitDraft = string.Empty);
	}

	/// 把一行折进列表容器。Bind 只在新实现一行或列表代际变化时调用；先比对身份，
	/// 同一行的内容不重弄（重复求值来自 ItemsView 每次重建都换实例）。
	private void BindChangeRow(Border host, GitChangeRow row)
	{
		if (host.Tag is GitChangeRow previous && ReferenceEquals(previous, row) && host.Child is not null)
		{
			return;
		}

		host.Tag = row;
		if (host.Child is { } stale)
		{
			host.Child = null;
			UiLifetime.Release(stale);
		}

		host.Child = row.Kind switch
		{
			GitChangeRowKind.Section => BuildSectionRow(row),
			GitChangeRowKind.Directory => BuildDirectoryRow(row),
			_ => BuildFileRow(row),
		};
	}

	private UIElement BuildSectionRow(GitChangeRow row)
	{
		var header = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				new TextBlock().Text(row.Title).FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.SemiBold)
					.WithTheme((theme, text) => text.Foreground = row.Conflict
						? AppTheme.For(theme).Error
						: AppTheme.For(theme).TextPrimary),
				new TextBlock().Text(row.Changes.Count.ToString()).FontSize(AppTheme.FontSizeMicro).FaintText());
		header.IsHitTestVisible = false;

		var host = new DockPanel().LastChildFill().Margin(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space2, 0));
		if (row.AllowActions)
		{
			var changes = row.Changes;
			var paths = changes.Select(static change => change.Path).ToArray();
			if (row.Conflict)
			{
				// 冲突文件的「暂存」在 git 里就是标记已解决：解决完才能提交。
				var resolve = SmallTextButton(
					Strings.GitMarkAllResolved,
					() => _ = RunRepoGitAsync(repo => GitWriter.Stage(repo, paths), Strings.GitBusy));
				resolve.IsEnabled = !_busy && !_generating;
				host.Children(resolve.DockRight());
			}
			else
			{
				var actions = new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(2)
					.VerticalAlignment(VerticalAlignment.Center);
				var action = SmallTextButton(
					row.Staged ? Strings.GitUnstageAll : Strings.GitStageAll,
					() => _ = RunRepoGitAsync(
						repo => row.Staged
							? GitWriter.Unstage(repo, paths)
							: GitWriter.Stage(repo, paths),
						Strings.GitBusy));
				action.IsEnabled = !_busy && !_generating;
				actions.Children(action);
				if (!row.Staged)
				{
					// 放弃整节等于丢掉这一节的未暂存改动；未跟踪文件也一并清掉。
					// 放在暂存键右侧：破坏性动作离读的位置最远。
					var discardAll = RowIconButton("rotate-ccw", Strings.GitDiscardAll,
						() => _ = DiscardAsync(changes, Strings.GitDiscardConfirm(changes.Count)));
					discardAll.IsEnabled = !_busy && !_generating;
					actions.Children(discardAll);
				}

				host.Children(actions.DockRight());
			}
		}

		return host.Children(header);
	}

	private UIElement BuildDirectoryRow(GitChangeRow row)
	{
		var tree = row.Tree!;
		var path = tree.Path;
		var collapsed = _collapsedDirectories.Contains(path);
		var directoryContent = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(5)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				ThemedGlyph(collapsed ? GlyphKind.ChevronRight : GlyphKind.ChevronDown, AppStyles.RoleFaint, 2.5),
				AppIcons.Icon("folder-open", AppStyles.RoleSubtle, 13),
				new TextBlock().Text(tree.Label).FontSize(AppTheme.FontSizeCaption).MutedText()
					.MaxWidth(Math.Max(80, 220 - tree.Depth * 10))
					.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis),
				new TextBlock().Text(tree.Changes.Count.ToString()).FontSize(AppTheme.FontSizeMicro).FaintText());
		FlatContent(directoryContent);

		var directoryButton = new Button()
			.Ghost()
			.Height(26)
			.Padding(new Thickness(AppTheme.Space2 + tree.Depth * 10, 0, AppTheme.Space1, 0))
			.Content(directoryContent)
			.OnClick(() =>
			{
				if (!_collapsedDirectories.Remove(path))
				{
					_collapsedDirectories.Add(path);
				}

				// 折叠只影响改动树本身。
				RebuildList();
			});

		var action = row.Conflict
			? ResolveButton(Strings.GitMarkResolved, [.. tree.Changes.Select(static change => change.Path)])
			: GlyphButton(
				row.Staged ? GlyphKind.Minus : GlyphKind.Plus,
				row.Staged ? Strings.GitUnstageDirectory : Strings.GitStageDirectory,
				() =>
				{
					var paths = tree.Changes.Select(static change => change.Path).ToArray();
					// 目录可能上千个文件：git add 放线程池，别卡住面板。
					_ = RunRepoGitAsync(
						repo => row.Staged ? GitWriter.Unstage(repo, paths) : GitWriter.Stage(repo, paths),
						Strings.GitBusy);
				});
		action.IsEnabled = !_busy && !_generating;
		action.IsVisible = row.AllowActions;

		var actions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(1)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(action);
		if (row.AllowActions && !row.Staged && !row.Conflict)
		{
			var discard = RowIconButton("rotate-ccw", Strings.GitDiscardDirectory,
				() => _ = DiscardAsync(tree.Changes, Strings.GitDiscardConfirmDirectory(tree.Label)));
			discard.IsEnabled = !_busy && !_generating;
			actions.Children(discard);
		}

		return new DockPanel().LastChildFill().Children(actions.DockRight(), directoryButton);
	}

	private UIElement BuildFileRow(GitChangeRow row)
	{
		var tree = row.Tree!;
		var change = tree.File!;
		var letter = row.Conflict ? "U" : change.StatusLetter(row.Staged);
		var letterRole = LetterRole(letter);
		// 行尾停靠统计，文件名占剩余宽度并自己截断：面板窄到 360 时也不会把行撑出可视区。
		var rowContent = new DockPanel().LastChildFill();
		if (BuildDiffStat(row.Stat, AppTheme.FontSizeCaption) is { } stat)
		{
			rowContent.Children(stat.Margin(new Thickness(8, 0, 2, 0)).DockRight());
		}

		rowContent.Children(
			new TextBlock().Text(letter)
				.FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeMicro)
				.Width(14)
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, text) => text.Foreground = RoleColor(AppTheme.For(theme), letterRole))
				.DockLeft(),
			new TextBlock().Text(change.FileName)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText()
				.MaxWidth(Math.Max(80, 240 - tree.Depth * 10))
				.TextWrapping(TextWrapping.NoWrap)
				.TextTrimming(TextTrimming.CharacterEllipsis)
				.VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(6, 0, 0, 0)));
		FlatContent(rowContent);

		var rowButton = new Button()
			.Ghost()
			.Height(27)
			.Padding(new Thickness(AppTheme.Space2 + tree.Depth * 10, 0, AppTheme.Space1, 0))
			.Content(rowContent)
			.OnClick(() => SelectDiff(change, row.Staged))
			.HoverTip(change.Path + "\n" + change.Label);
		rowButton.Tag = change;

		var action = row.Conflict
			? ResolveButton(Strings.GitMarkResolved, [change.Path])
			: GlyphButton(
				row.Staged ? GlyphKind.Minus : GlyphKind.Plus,
				row.Staged ? Strings.GitUnstage : Strings.GitStage,
				() => _ = RunRepoGitAsync(
					repo => row.Staged
						? GitWriter.Unstage(repo, [change.Path])
						: GitWriter.Stage(repo, [change.Path]),
					Strings.GitBusy));
		action.IsEnabled = !_busy && !_generating;
		action.IsVisible = row.AllowActions;
		action.Tag = change;

		var actions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(1)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(action);
		if (row.AllowActions && !row.Staged && !row.Conflict)
		{
			var untracked = change.IndexStatus == "?" && change.WorkTreeStatus == "?";
			var discard = RowIconButton(
				untracked ? "trash" : "rotate-ccw",
				untracked ? Strings.GitDiscardUntracked : Strings.GitDiscard,
				() => _ = DiscardAsync([change], Strings.GitDiscardConfirmNamed(change.FileName)));
			discard.IsEnabled = !_busy && !_generating;
			discard.Tag = change;
			actions.Children(discard);
		}

		return new Border()
			.CornerRadius(AppTheme.RadiusXs)
			.Child(new DockPanel().LastChildFill().Children(actions.DockRight(), rowButton));
	}

	/// 行尾的增删行数与五格比例条；没有数字（未跟踪、二进制、纯改名）就不占位置。
	/// 更改列表、提交详情与分支比较共用同一套排版，字号跟各自行里的文件名一致。
	private static FrameworkElement? BuildDiffStat(GitDiffStat? stat, double fontSize)
	{
		if (stat is not { } counted || counted.Total <= 0)
		{
			return null;
		}

		var numbers = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(4)
			.VerticalAlignment(VerticalAlignment.Center);
		if (counted.Added > 0)
		{
			numbers.Children(new TextBlock().Text(Strings.GitAddedLines(counted.Added))
				.FontFamily(AppTheme.MonoFont).FontSize(fontSize)
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Success));
		}

		if (counted.Removed > 0)
		{
			numbers.Children(new TextBlock().Text(Strings.GitRemovedLines(counted.Removed))
				.FontFamily(AppTheme.MonoFont).FontSize(fontSize)
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Error));
		}

		var blocks = new DiffStatBlocks();
		blocks.Set(counted.Added, counted.Removed);
		return new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(numbers, blocks);
	}

	/// 点一行改动：读这一份差异并交给浮层，面板本身不再内嵌 diff 卡片。
	private void SelectDiff(GitChange change, bool stagedSide)
	{
		var untracked = change.IndexStatus == "?" && change.WorkTreeStatus == "?";
		ShowDiff(GitDiffRequest.WorkingTree(change.Path, stagedSide, untracked));
	}

	/// 放弃未暂存改动：先确认，再打检查点，最后 restore / clean。
	/// 检查点建不出来就中止：这一步不可撤销，没有可恢复的快照宁可什么都不做。
	/// 已暂存的那一份不在放弃范围内（先取消暂存），部分暂存的文件只回退工作区这一侧。
	private async Task DiscardAsync(IReadOnlyList<GitChange> changes, string message)
	{
		if (_disposed || _busy || _generating)
		{
			return;
		}

		// 确认框可能开着很久：把它之后的检查点与 restore/clean 钉在点击这一刻的仓库上，
		// 否则中途切仓库会让快照落在 A、删除落在 B。
		var repo = Repo();
		var tracked = new List<string>();
		var untracked = new List<string>();
		foreach (var change in changes)
		{
			if (change.IndexStatus == "?" && change.WorkTreeStatus == "?")
			{
				untracked.Add(change.Path);
			}
			else if (change.WorkTreeStatus is not " ")
			{
				tracked.Add(change.Path);
			}
		}

		if (tracked.Count == 0 && untracked.Count == 0)
		{
			Toasts.Show(View, Strings.GitDiscardEmpty);
			return;
		}

		await ConfirmRunAsync(
			message,
			Strings.GitDiscardConfirmDetail,
			() =>
			{
				if (Checkpoints.Create(repo, "before-discard") is null)
				{
					return GitResult.Failure(Strings.GitDiscardCheckpointFailed);
				}

				Checkpoints.Prune(repo);
				return GitWriter.Discard(repo, tracked, untracked);
			},
			Strings.GitDiscardBusy).ConfigureAwait(true);
	}

	/// 全部暂存并提交：面板里唯一会带走「没勾选文件」的动作，所以先确认。
	/// 有冲突时直接拒绝：add 会把带冲突标记的文件标成已解决。
	private async Task StageAllAndCommitAsync()
	{
		if (_disposed || _busy || _generating)
		{
			return;
		}

		var message = _commitInput?.Text ?? _commitDraft;
		if (string.IsNullOrWhiteSpace(message))
		{
			Toasts.Error(View, Strings.GitCommitMessageRequired);
			return;
		}

		var repo = Repo();
		var status = GitStatusReader.Read(repo);
		if (status.IsRepository && status.Changes.Any(static change => change.IsConflict))
		{
			Toasts.Error(View, Strings.GitConflict);
			return;
		}

		var paths = status.Changes.Select(static change => change.Path).ToArray();
		if (paths.Length == 0)
		{
			Toasts.Show(View, Strings.GitNothingToCommit);
			return;
		}

		var draft = message.Trim();
		await ConfirmRunAsync(
			Strings.GitStageAllAndCommitConfirm(paths.Length),
			Strings.GitStageAllAndCommitDetail,
			() =>
			{
				var staged = GitWriter.Stage(repo, paths);
				return staged.Ok ? GitWriter.Commit(repo, draft) : staged;
			},
			Strings.GitCommitBusy,
			onSuccess: () => _commitDraft = string.Empty).ConfigureAwait(true);
	}

	/// 冲突行的动作：`git add` 就是标记已解决，用对勾比加号更像「收尾」。
	private Button ResolveButton(string tooltip, IReadOnlyList<string> paths) =>
		RowIconButton("circle-check", tooltip,
			() => _ = RunRepoGitAsync(repo => GitWriter.Stage(repo, paths), Strings.GitBusy));

	/// 行尾的小图标按钮：表格式行高 27，26 的按钮会把行撑高，取 22 留出悬停底色。
	private static Button RowIconButton(string icon, string tooltip, Action action)
	{
		var button = AppIcons.Button(icon, tooltip, action, size: 13);
		button.Width(22).Height(22).Padding(Thickness.Zero);
		return button;
	}

	private void CopyText(string text, string successMessage)
	{
		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		if (clipboard?.TrySetText(text) == true)
		{
			Toasts.Success(View, successMessage);
		}
		else
		{
			Toasts.Error(View, Strings.ClipboardUnavailable);
		}
	}

	/// 读一份差异并打开浮层。工作区/暂存来自 git status，提交与分支比较来自历史读取；
	/// 读取失败或没有差异时浮层显示说明而不是空卡片。
	private void ShowDiff(GitDiffRequest request)
	{
		var path = request.Path;
		string diff;
		string? error = null;
		switch (request.Kind)
		{
			case GitDiffKind.Staged:
				diff = GitStatusReader.StagedDiff(Repo(), path);
				break;
			case GitDiffKind.Commit:
			{
				var content = GitHistoryReader.CommitFileDiff(
					Repo(), request.CommitHash ?? string.Empty, request.BaseHash, path, request.OriginalPath);
				diff = content.Text;
				error = content.Error;
				break;
			}
			case GitDiffKind.Compare:
			{
				var content = GitHistoryReader.CompareFileDiff(
					Repo(), request.BaseHash ?? string.Empty, request.TargetHash ?? string.Empty,
					path, request.OriginalPath);
				diff = content.Text;
				error = content.Error;
				break;
			}
			case GitDiffKind.WorkingTreeCompare:
			{
				if (request.Untracked)
				{
					// 未跟踪文件不在 git diff 里：与改动列表共用合成补丁。
					diff = GitStatusReader.Diff(Repo(), path, untracked: true);
				}
				else
				{
					var content = GitHistoryReader.WorkingTreeFileDiff(Repo(), request.BaseHash ?? string.Empty, path);
					diff = content.Text;
					error = content.Error;
				}

				break;
			}
			default:
				diff = GitStatusReader.Diff(Repo(), path, request.Untracked);
				break;
		}

		var (added, removed) = DiffStats.Count(diff);
		var full = Path.IsPathRooted(path) ? path : Path.Combine(Repo(), path);
		var fileName = Path.GetFileName(path);
		var preview = new DiffPreviewContent(
			fileName.Length > 0 ? fileName : path,
			request.Title,
			full,
			added,
			removed,
			diff,
			error is not null ? Strings.GitDiffFailedReason(error) : diff.Length == 0 ? Strings.GitNoDiff : null,
			IsError: error is not null,
			// 提交/分支比较是历史内容，打开工作区文件会看错版本；工作区范围看的就是当前文件。
			CanOpenFile: request.Kind is GitDiffKind.WorkingTree or GitDiffKind.Staged or GitDiffKind.WorkingTreeCompare);
		_lastDiffContent = preview;
		_diffPreview.Show(View, preview, OpenInEditor, _quoteToComposer);
	}

	/// 最近一次交给浮层的差异内容；无窗口的测试用它核对读出的补丁与来源标记。
	internal DiffPreviewContent? LastDiffContentForTests => _lastDiffContent;

	/// 改动行总数（含分节标题行）与已实例化的行数：用例靠它确认大改动集不会全量建控件。
	internal int ChangeRowCountForTests => _changesList.ItemsSource.Count;

	internal bool IsChangeRowRealizedForTests(int index) => _changesList.TryGetContainer(index, out _);

	internal void ScrollChangesForTests(double offset) => _changesList.SetScrollOffsets(0, offset);

	internal int RealizedChangeRowsForTests
	{
		get
		{
			var realized = 0;
			for (var index = 0; index < ChangeRowCountForTests; index++)
			{
				if (_changesList.TryGetContainer(index, out _))
				{
					realized++;
				}
			}

			return realized;
		}
	}

	private UIElement BuildMorePane(GitStatus status)
	{
		var headerButton = new Button()
			.Ghost()
			.Height(28)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space2, 0))
			.OnClick(() =>
			{
				_panes = _panes.WithMore(_panes.More, !_panes.MoreOpen);
				ApplyPaneHeights();
				SavePanes();
				RebuildMore();
			})
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					ThemedGlyph(_panes.MoreOpen ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3),
					new TextBlock().Text(Strings.GitMore).FontSize(AppTheme.FontSizeMicro)
						.FontWeight(FontWeight.SemiBold).MutedText()));

		var header = Separator(
			new Border()
				.Height(30)
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Child(headerButton),
			new Thickness(0, 1, 0, 0));

		if (!_panes.MoreOpen)
		{
			return header;
		}

		var content = new StackPanel().Spacing(AppTheme.Space2)
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space2, AppTheme.Space2));

		if (_worktreeEditorVisible)
		{
			// 新建副本时「更多」只留这一块：默认 140 高的区里塞着 stash 与摘要设置时，
			// 输入框和按钮会落到视口外，用户得先滚一遍才能填表。
			content.Children(BuildWorktreeEditor());
		}
		else
		{
			content.Children(BuildAssistantSettingsRow());
			content.Children(BuildStashSection());
			content.Children(BuildWorktreeSection());
			if (!status.HasRemotes)
			{
				content.Children(new TextBlock().Text(Strings.GitNoRemote)
					.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap));
			}
		}

		var scroll = new ScrollViewer()
			.SmoothScrolling()
			.MaxHeight(_panes.More)
			.Content(content)
			.AutoVerticalScroll()
			.NoHorizontalScroll();
		return new StackPanel().Spacing(0).Children(header, scroll);
	}

	private UIElement BuildAssistantSettingsRow()
	{
		var body = new StackPanel().Spacing(3);
		if (_assistant is null)
		{
			body.Children(new TextBlock().Text(Strings.GitSummary).FontSize(AppTheme.FontSizeMicro).MutedText());
			body.Children(new TextBlock().Text(Strings.GitSummaryNoModel).FontSize(AppTheme.FontSizeMicro).FaintText());
			return body;
		}

		var resolved = ResolveAssistant(out var preference);
		body.Children(new TextBlock().Text(Strings.GitSummary).FontSize(AppTheme.FontSizeMicro).MutedText());
		body.Children(new TextBlock()
			.Text(Strings.GitSummaryCurrent($"{resolved.Model.Provider}/{resolved.Model.Id}", Strings.ThinkingLevelName(resolved.Thinking)))
			.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap));
		body.Children(new TextBlock()
			.Text(Strings.GitSummaryPromptState(preference.GitCommitPrompt is { Length: > 0 } ? Strings.GitSummaryPromptCustom : Strings.GitSummaryPromptDefault))
			.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap));
		if (_openSettings is not null)
		{
			body.Children(SmallTextButton(Strings.GitSummarySettingsOpen, _openSettings));
		}

		return body;
	}

	private UIElement BuildStashSection()
	{
		var section = new StackPanel().Spacing(2);
		var push = ToolbarButton("package", Strings.GitStash, () => _ = RunRepoGitAsync(GitWriter.StashPush, Strings.GitBusy));
		push.IsEnabled = !_busy && !_generating;
		section.Children(new DockPanel().LastChildFill().Children(push.DockRight(),
			new TextBlock().Text(Strings.GitStashes).FontSize(AppTheme.FontSizeMicro)
				.FontWeight(FontWeight.SemiBold).MutedText()));

		var stashes = _stashes;
		if (stashes.Count == 0)
		{
			section.Children(new TextBlock()
				.Text(_moreLoading ? Strings.GitMoreLoading : Strings.GitStashEmpty)
				.FontSize(AppTheme.FontSizeMicro).FaintText());
			return section;
		}

		foreach (var stash in stashes)
		{
			var captured = stash;
			var apply = ToolbarButton("download", Strings.GitStashApply, () => _ = RunRepoGitAsync(repo => GitWriter.StashApply(repo, captured.Reference), Strings.GitBusy));
			var pop = ToolbarButton("rotate-ccw", Strings.GitStashPop, () => _ = RunRepoGitAsync(repo => GitWriter.StashPop(repo, captured.Reference), Strings.GitBusy));
			var drop = ToolbarButton("trash", Strings.GitStashResolved, () => _ = RunRepoGitAsync(repo => GitWriter.StashDrop(repo, captured.Reference), Strings.GitBusy));
			apply.IsEnabled = !_busy && !_generating;
			pop.IsEnabled = !_busy && !_generating;
			drop.IsEnabled = !_busy && !_generating;
			var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(1).Children(apply, pop, drop);
			section.Children(new DockPanel().LastChildFill().Children(actions.DockRight(),
				new TextBlock()
					.Text(captured.Description.Length > 0 ? captured.Description : captured.Reference)
					.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).MutedText()
					.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)));
		}

		return section;
	}

	/// 隔离副本：子代理的 worktree、或者用户自己开给另一个人/另一个任务的目录。
	/// 只许打开目录或删副本；分支保留，避免面板里一键删掉别人还在用的分支。
	private UIElement BuildWorktreeSection()
	{
		var section = new StackPanel().Spacing(2);
		var add = ToolbarButton("plus", Strings.GitWorktreeNew, () =>
		{
			// 编辑器在「更多」区里，先保证这一节展开并且放得下表单。
			_panes = _panes.WithMore(Math.Max(_panes.More, WorktreeEditorMinBody), open: true);
			_worktreeEditorVisible = true;
			_focusWorktreeAfterBuild = true;
			RebuildMore();
		});
		add.IsEnabled = !_busy && !_generating;
		section.Children(new DockPanel().LastChildFill().Children(add.DockRight(),
			new TextBlock().Text(Strings.GitWorktrees).FontSize(AppTheme.FontSizeMicro)
				.FontWeight(FontWeight.SemiBold).MutedText()));

		var linked = _worktrees.Where(static worktree => !worktree.IsMain).ToArray();
		if (linked.Length == 0)
		{
			section.Children(new TextBlock()
				.Text(_moreLoading ? Strings.GitMoreLoading : Strings.GitWorktreeNone)
				.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap));
			return section;
		}

		foreach (var worktree in linked)
		{
			var entry = worktree;
			var missing = !Directory.Exists(entry.Path);
			var label = entry.BranchName.Length > 0 ? entry.BranchName : Strings.GitWorktreeMain;
			if (missing)
			{
				label += "（" + Strings.GitWorktreeMissing + "）";
			}

			var open = ToolbarButton("folder-open", Strings.GitWorktreeOpen, () => Shell.Open(entry.Path));
			var remove = ToolbarButton("trash", Strings.GitWorktreeRemove, () => _ = RemoveWorktreeAsync(entry));
			remove.IsEnabled = !_busy && !_generating;
			var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(1);
			if (missing)
			{
				// 目录没了：打开/开会话都没意义，只留清掉 git 里的残留登记。
				actions.Children(ToolbarButton(
					"trash",
					Strings.GitWorktreePrune,
					() => _ = RunRepoGitAsync(GitWriter.WorktreePrune, Strings.GitBusy)));
			}
			else
			{
				if (_openSessionIn is { } openSessionIn)
				{
					// 隔离副本最常见的用途就是在里面另起一轮工作：省掉「先复制路径再开会话」。
					actions.Children(ToolbarButton("message-square", Strings.GitWorktreeSession, () => openSessionIn(entry.Path)));
				}

				actions.Children(open, remove);
			}

			var body = new StackPanel().Spacing(0).Children(
				new TextBlock().Text(label)
					.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).MutedText()
					.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
					.HoverTip(entry.Path),
				new TextBlock().Text(entry.Path)
					.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).FaintText()
					.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
					.HoverTip(entry.Path));

			section.Children(new DockPanel().LastChildFill()
				.Margin(new Thickness(0, 2, 0, 0))
				.Children(actions.DockRight(), body));
		}

		return section;
	}

	/// 新建副本：分支名由用户输入，基线默认 HEAD。两个输入框都内嵌在「更多」区，
	/// 建好后再点行尾的「在新会话中打开」把它交给一轮新工作。
	private UIElement BuildWorktreeEditor()
	{
		TextBox? name = null;
		TextBox? baseline = null;
		name = new TextBox().PrimarySelection()
			.Text(_worktreeDraft)
			.Placeholder(Strings.GitWorktreeNamePlaceholder)
			.Height(AppTheme.ControlHeightSm)
			.FontSize(AppTheme.FontSizeCaption)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.StyleName(AppStyles.TextInputLine)
			.OnTextChanged(value => _worktreeDraft = value)
			.OnKeyDown(key => WorktreeEditorKey(key, name, baseline));
		baseline = new TextBox().PrimarySelection()
			.Text(_worktreeBaseDraft)
			.Placeholder(Strings.GitWorktreeBasePlaceholder)
			.Height(AppTheme.ControlHeightSm)
			.FontSize(AppTheme.FontSizeCaption)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.StyleName(AppStyles.TextInputLine)
			.OnTextChanged(value => _worktreeBaseDraft = value)
			.OnKeyDown(key => WorktreeEditorKey(key, name, baseline));
		_worktreeInput = name;

		var create = new Button()
			.Content(CenteredLabel(Strings.GitWorktreeCreate).PrimaryText())
			.Height(AppTheme.ControlHeightSm)
			.OnClick(() => CreateWorktree(name?.Text, baseline?.Text));
		var cancel = new Button()
			.Ghost()
			.Height(AppTheme.ControlHeightSm)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Content(new TextBlock().Text(Strings.Cancel).FontSize(AppTheme.FontSizeMicro).MutedText())
			.OnClick(() =>
			{
				_worktreeEditorVisible = false;
				RebuildMore();
			});

		// 基线输入与两个按钮同一行：面板宽 360 时也要能整块看见，不留第三行。
		var actions = new Grid().Columns("*,auto,auto").Spacing(AppTheme.Space1)
			.Children(baseline.Column(0), create.Column(1), cancel.Column(2));

		return new Border()
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, AppTheme.Space1))
			.Child(new StackPanel().Spacing(AppTheme.Space1).Children(
				new TextBlock().Text(Strings.GitWorktreeNew)
					.FontSize(AppTheme.FontSizeMicro).FontWeight(FontWeight.SemiBold).MutedText(),
				new TextBlock().Text(Strings.GitWorktreeNewHint)
					.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap),
				name,
				actions));
	}

	private void WorktreeEditorKey(KeyEventArgs key, TextBox? name, TextBox? baseline)
	{
		if (key.Key == Key.Enter)
		{
			key.Handled = true;
			CreateWorktree(name?.Text, baseline?.Text);
		}
		else if (key.Key == Key.Escape)
		{
			key.Handled = true;
			_worktreeEditorVisible = false;
			RebuildMore();
		}
	}

	private void CreateWorktree(string? name, string? baseReference)
	{
		if (_busy || _generating || _disposed)
		{
			return;
		}

		var branch = name?.Trim() ?? string.Empty;
		if (branch.Length == 0)
		{
			ApplyResult(GitResult.Failure(Strings.GitBranchNameInvalid));
			return;
		}

		var reference = string.IsNullOrWhiteSpace(baseReference) ? "HEAD" : baseReference.Trim();
		var repo = Repo();
		_ = RunGitAsync(
			() =>
			{
				var baseline = GitWriter.WorktreeBase(repo, reference);
				if (!baseline.Ok)
				{
					return GitResult.Failure(Strings.GitWorktreeBaseMissing(reference), baseline.BaseCommit);
				}

				var root = _worktreeRoot();
				Directory.CreateDirectory(root);
				var path = Path.Combine(
					root,
					$"{SafeWorktreeName(Path.GetFileName(baseline.Repository))}-" +
					$"{SafeWorktreeName(branch)}-{Guid.NewGuid().ToString("N")[..4]}");
				return GitWriter.WorktreeAdd(baseline.Repository, path, branch, baseline.BaseCommit);
			},
			Strings.GitWorktreeCreateBusy,
			onSuccess: () =>
			{
				_worktreeDraft = string.Empty;
				_worktreeBaseDraft = string.Empty;
				_worktreeEditorVisible = false;
			});
	}

	/// 目录名只留字母数字与三个分隔符：分支名允许 `feature/x`、`v1.2` 这类形态。
	private static string SafeWorktreeName(string value)
	{
		var builder = new System.Text.StringBuilder(value.Length);
		foreach (var character in value)
		{
			builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-');
		}

		var trimmed = builder.ToString().Trim('-', '.');
		return trimmed.Length == 0 ? "copy" : trimmed.Length <= 40 ? trimmed : trimmed[..40];
	}

	private async Task RemoveWorktreeAsync(GitWorktree worktree)
	{
		if (_disposed || _busy || _generating)
		{
			return;
		}

		var repository = Repo();
		var path = worktree.Path;
		await ConfirmRunAsync(
			Strings.GitWorktreeRemoveConfirm(worktree.BranchName, path),
			Strings.GitWorktreeRemoveDetail,
			() => GitWriter.WorktreeRemoveOnly(repository, path),
			Strings.GitBusy,
			// 登记表只用于清理入口：副本自己删掉后不能再被当成孤儿重复处理。
			onSuccess: () => WorktreeRegistry.Forget(path)).ConfigureAwait(true);
	}


	private UIElement BuildNotRepository(GitStatus status)
	{
		var unavailable = string.Equals(status.Error, Strings.GitUnavailable, StringComparison.Ordinal);
		var timedOut = string.Equals(status.Error, Strings.GitTimeout, StringComparison.Ordinal);
		var nonRepository = status.Error is not { Length: > 0 } error ||
			error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
		var canInit = !unavailable && !timedOut && nonRepository;

		var body = new StackPanel().Spacing(AppTheme.Space2)
			.Padding(new Thickness(AppTheme.Space4, AppTheme.Space4, AppTheme.Space4, AppTheme.Space4))
			.Children(
				AppIcons.Icon("git-branch", AppStyles.RoleSubtle, 22),
				new TextBlock().Text(unavailable ? Strings.GitUnavailable : Strings.GitNotRepository)
					.FontSize(AppTheme.FontSizeControl).FontWeight(FontWeight.SemiBold).PrimaryText(),
				new TextBlock().Text(canInit ? Strings.GitNotRepositoryHint(Repo()) : Strings.GitNotRepository)
					.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap));

		if (canInit)
		{
			// 主操作按内容宽，不拉满面板：空态里的按钮是下一步，不是一块底色。
			body.Children(new Button()
				.Primary()
				.Height(AppTheme.ControlHeightMd)
				.HorizontalAlignment(HorizontalAlignment.Left)
				.Content(new TextBlock().Text(Strings.GitInit).FontSize(AppTheme.FontSizeCaption))
				.OnClick(() => _ = RunRepoGitAsync(GitWriter.Init, Strings.GitBusy)));
		}

		// 「不是 Git 仓库」的原始 stderr 只是同一句话的 git 版，铺在空态里只会稀释下一步动作；
		// 命令不可用/超时标题已说明，其它真实失败才把原文留下来供排查。
		if (!unavailable && !timedOut && !nonRepository && status.Error is { Length: > 0 } detail)
		{
			body.Children(new TextBlock().Text(detail)
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro)
				.FaintText().TextWrapping(TextWrapping.Wrap));
		}

		return new ScrollViewer().SmoothScrolling().Content(body).AutoVerticalScroll().NoHorizontalScroll();
	}

	private void ApplyResult(GitResult result)
	{
		var message = result.Message.Length > 0 ? result.Message : Strings.GitDone;
		if (result.Ok)
		{
			Toasts.Success(View, message);
			_lastFailureMessage = null;
			_lastFailureDetail = null;
			_branchesDirty = true;
			_graphDirty = true;
			_moreDirty = true;
		}
		else
		{
			Toasts.Error(View, message);
			_lastFailureMessage = message;
			_lastFailureDetail = result.Detail;
		}

		Refresh();
	}

	/// 确认后执行一次写操作：守卫、确认框、忙碌文案与收尾只写一遍。
	/// 破坏性动作（放弃、删副本、提交级操作）都从这里过，避免各抄一份 25 行。
	private async Task ConfirmRunAsync(
		string message,
		string detail,
		Func<GitResult> action,
		string busyMessage,
		Action? onSuccess = null)
	{
		if (_disposed || _busy || _generating)
		{
			return;
		}

		if (!await _confirm(message, detail).ConfigureAwait(true))
		{
			return;
		}

		await RunGitAsync(action, busyMessage, onSuccess).ConfigureAwait(true);
	}

	/// 把动作钉在点击那一刻的仓库上：确认框、后台线程与切换仓库都不改变它的目标。
	/// 返回的 Task 与 <see cref="RunGitAsync"/> 语义一致。
	private Task RunRepoGitAsync(Func<string, GitResult> action, string busyMessage, Action? onSuccess = null)
	{
		var repo = Repo();
		return RunGitAsync(() => action(repo), busyMessage, onSuccess);
	}

	private async Task RunGitAsync(Func<GitResult> action, string busyMessage, Action? onSuccess = null)
	{
		if (_busy || _generating || _disposed)
		{
			return;
		}

		// 无窗口的布局测试没有调度器：同步执行，按钮点击后状态必须立即可见。
		if (_host.FindVisualRoot() is not Window)
		{
			GitResult direct;
			try
			{
				direct = action();
			}
			catch (Exception ex)
			{
				direct = GitResult.Failure(ex.Message);
			}

			if (direct.Ok)
			{
				onSuccess?.Invoke();
			}

			ApplyResult(direct);
			return;
		}

		_busy = true;
		_busyMessage = busyMessage;
		// 忙状态只用上一次快照重画（按钮禁用态与忙碌文案）：此刻再读一遍 status + numstat
		// 白开一组 git 进程，完成后 ApplyResult 里的 Refresh 才是要结果的那一次。
		if (_lastStatus is not null)
		{
			RebuildChanges();
		}
		else
		{
			Refresh();
		}

		GitResult result;
		try
		{
			result = await Task.Run(action).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			result = GitResult.Failure(ex.Message);
		}

		void Complete()
		{
			if (_disposed)
			{
				return;
			}

			_busy = false;
			_busyMessage = string.Empty;
			if (result.Ok)
			{
				onSuccess?.Invoke();
			}

			ApplyResult(result);
		}

		if (Application.IsRunning && Application.Current.Dispatcher is { } dispatcher)
		{
			dispatcher.BeginInvoke(Complete);
		}
		else
		{
			Complete();
		}
	}

	private async Task GenerateCommitSummaryAsync()
	{
		if (_assistant is not { } assistant || _busy || _generating || _disposed)
		{
			return;
		}

		if (assistant.IsBusy())
		{
			SetSummaryFailure(GitSummaryFailure.Busy, Strings.GitSummaryBusy);
			return;
		}

		// 模型没配好与「仓库/暂存区」无关：先判这一条，否则用户在空暂存区看到的是「先去暂存」，
		// 暂存完再撞一次「模型不可用」（PiDeck 用 GIT_COMMIT_MODEL_REQUIRED 单列这个场景）。
		if (!HasSummaryModel())
		{
			SetSummaryFailure(GitSummaryFailure.ModelMissing, Strings.GitSummaryModelMissing);
			return;
		}

		var status = GitStatusReader.Read(Repo());
		if (!status.Changes.Any(static change => change.HasStaged && !change.IsConflict))
		{
			SetSummaryFailure(GitSummaryFailure.EmptyDiff, Strings.GitStageBeforeGenerate);
			return;
		}

		var diff = GitStatusReader.StagedDiffAll(Repo(), GitCommitMessage.MaxDiffCharacters);
		if (!diff.Ok)
		{
			// 读 diff 失败（cwd 失效 / 仓库损坏 / git 超时）不能当成「空暂存区」：
			// 前者要去查仓库，后者要去暂存，两种文案的后续动作完全不同。
			SetSummaryFailure(GitSummaryFailure.DiffFailed, Strings.GitSummaryDiffFailed, diff.Error);
			return;
		}

		if (diff.Text.Trim().Length == 0)
		{
			SetSummaryFailure(GitSummaryFailure.EmptyDiff, Strings.GitStageBeforeGenerate);
			return;
		}

		var resolved = ResolveAssistant();
		var abort = new CancellationTokenSource(TimeSpan.FromSeconds(GitCommitMessage.GenerationTimeoutSeconds));
		_summaryAbort = abort;
		_generating = true;
		_summaryFailure = null;
		_summaryStarted = DateTime.UtcNow;
		_summaryTimer.Start();
		Refresh();

		string? generated = null;
		GitSummaryFailure? failureReason = null;
		string? failure = null;
		try
		{
			// 无窗口测试直接走生成委托；真实窗口把它丢到线程池，保持 UI 可交互。
			var generation = _host.FindVisualRoot() is Window
				? Task.Run(() => assistant.Generate(resolved.Model, resolved.Thinking, diff.Text, abort.Token), abort.Token)
				: assistant.Generate(resolved.Model, resolved.Thinking, diff.Text, abort.Token);
			generated = await generation.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			failureReason = GitSummaryFailure.Timeout;
			failure = Strings.GitSummaryTimeout;
		}
		catch (GitSummaryException ex)
		{
			failureReason = ex.Failure;
			failure = ex.Message;
			if (ex.Detail is { Length: > 0 } detail)
			{
				_lastFailureDetail = detail;
			}
		}
		catch (Exception ex)
		{
			failureReason = GitSummaryFailure.Failed;
			failure = ex.Message;
		}
		finally
		{
			abort.Dispose();
			if (ReferenceEquals(_summaryAbort, abort))
			{
				_summaryAbort = null;
			}
		}

		void Complete()
		{
			if (_disposed)
			{
				return;
			}

			_generating = false;
			_summaryTimer.Stop();
			if (failure is null && generated is { Length: > 0 } message)
			{
				_commitDraft = message;
				_focusCommitAfterBuild = true;
				_summaryFailure = null;
				_summaryFailureText = string.Empty;
				_lastFailureMessage = null;
				_lastFailureDetail = null;
				Toasts.Success(View, Strings.GitSummaryGenerated);
			}
			else
			{
				// 失败原因按类型落到面板內联提示；原始报错进展开条，排查时能看到完整文本。
				_summaryFailure = failureReason ?? GitSummaryFailure.Failed;
				_lastFailureMessage = Strings.GitSummaryFailed;
				_lastFailureDetail = failure ?? Strings.GitSummaryEmpty;
				Toasts.Error(View, failure is { Length: > 0 } ? Strings.GitSummaryFailedReason(failure) : Strings.GitSummaryEmpty);
			}

			Refresh();
		}

		if (Application.IsRunning && Application.Current.Dispatcher is { } dispatcher)
		{
			dispatcher.BeginInvoke(Complete);
		}
		else
		{
			Complete();
		}
	}

	internal void GenerateSummaryForSmoke() => _ = GenerateCommitSummaryAsync();

	internal void ReportSummaryForSmoke() =>
		Console.WriteLine(
			"PiWeft: git summary draft=" +
			(_commitDraft.Length > 0 ? _commitDraft.Replace("\n", " \\n") : "(空)") +
			" failure=" + (_summaryFailure?.ToString() ?? "(无)") +
			" detail=" + (_lastFailureDetail ?? string.Empty).Replace("\n", " \\n"));

	private (Model Model, string Thinking) ResolveAssistant(out DesktopPreference preference)
	{
		var assistant = _assistant ?? throw new InvalidOperationException(Strings.GitSummaryNoModel);
		var loaded = DesktopPreference.Load();
		preference = loaded;
		var current = assistant.CurrentModel();
		var models = assistant.Models();
		var model = current;
		if (preference.GitCommitModel is { Length: > 0 } fixedModel)
		{
			model = models.FirstOrDefault(candidate =>
				string.Equals($"{candidate.Provider}/{candidate.Id}", fixedModel, StringComparison.Ordinal)) ?? current;
		}

		var thinking = loaded.GitCommitThinking is { Length: > 0 } fixedThinking
			? fixedThinking
			: assistant.CurrentThinking();
		return (model, thinking);
	}

	private (Model Model, string Thinking) ResolveAssistant() => ResolveAssistant(out _);

	private bool HasSummaryModel() =>
		_assistant?.Models().Any(static model => model.Type == ModelType.Chat) == true;

	private void SetSummaryFailure(GitSummaryFailure reason, string message, string? detail = null)
	{
		_summaryFailure = reason;
		_summaryFailureText = message;
		switch (reason)
		{
			case GitSummaryFailure.DiffFailed:
			case GitSummaryFailure.Failed:
				_lastFailureMessage = Strings.GitSummaryFailed;
				_lastFailureDetail = detail is { Length: > 0 } ? detail : message;
				Toasts.Error(View, Strings.GitSummaryFailedReason(_lastFailureDetail));
				break;
			case GitSummaryFailure.ModelMissing:
				Toasts.Error(View, message);
				break;
			default:
				Toasts.Show(View, message);
				break;
		}

		Refresh();
	}

	private static string SummaryFailureMessage(GitSummaryFailure failure) => failure switch
	{
		GitSummaryFailure.EmptyDiff => Strings.GitStageBeforeGenerate,
		GitSummaryFailure.DiffFailed => Strings.GitSummaryDiffFailed,
		GitSummaryFailure.ModelMissing => Strings.GitSummaryModelMissing,
		GitSummaryFailure.Busy => Strings.GitSummaryBusy,
		GitSummaryFailure.Timeout => Strings.GitSummaryTimeout,
		_ => Strings.GitSummaryFailed,
	};

	private void UpdateSummaryProgress()
	{
		if (_summaryBar is not { } bar)
		{
			return;
		}

		if (!_generating)
		{
			bar.Value = 0;
			return;
		}

		var seconds = (DateTime.UtcNow - _summaryStarted).TotalSeconds;
		bar.Value = Math.Clamp(seconds / GitCommitMessage.GenerationTimeoutSeconds * 100d, 0, 97);
	}

	private void OpenInEditor(string path)
	{
		var full = Path.IsPathRooted(path) ? path : Path.Combine(Repo(), path);
		if (!File.Exists(full))
		{
			Toasts.Error(View, Strings.FileMissing(full));
			return;
		}

		if (Shell.OpenInEditor(full))
		{
			Toasts.Success(View, Strings.OpenedInEditor(full));
		}
		else
		{
			Toasts.Error(View, Strings.OpenFailed(full));
		}
	}


	private static Border Separator(Border border, Thickness thickness) =>
		border.BorderThickness(thickness)
			.WithTheme((theme, element) => element.BorderBrush = AppTheme.For(theme).BorderSubtle);

	private static TextBlock Label(string text) =>
		new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.SemiBold).PrimaryText();

	private static Button ToolbarButton(string icon, string tooltip, Action action)
	{
		var button = AppIcons.Button(icon, tooltip, action, size: 14);
		button.Width(26).Height(26).Padding(Thickness.Zero);
		return button;
	}

	/// 带差异计数的同步按钮（对齐 PiDeck 的 push/pull 徽标）：计数是「这一键现在能推出去/拉进来多少提交」，
	/// 只在该方向真有差异时出现；这一键正在执行时先收起，免得数字停在旧值上。
	/// 布局照 PiDeck：图标保持居中，计数做成压在按钮右上角的小圆牌。箭头图标的右上是空角，
	/// 圆牌落在那里不遮笔画，也不必把图标挤歪；把图标挪到左下（第一版的做法）会让整行看着歪。
	private static Button ToolbarCountButton(string icon, string tooltip, Action action, int count, bool running)
	{
		if (count <= 0 || running)
		{
			return ToolbarButton(icon, tooltip, action);
		}

		var label = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
		// AppIcons.Icon 只设了纵向居中；横向默认 Stretch，给了固定宽度就贴到格子左边，
		// 于是「居中的图标 + 右上角圆牌」会变成「左边的图标 + 右边的圆牌」，看着就是歪的。
		var glyph = AppIcons.Icon(icon, AppStyles.RoleSubtle, 14)
			.HorizontalAlignment(HorizontalAlignment.Center);
		var content = new Grid().Children(
			glyph,
			new Border()
				.Width(13)
				.Height(13)
				.CornerRadius(7)
				.HorizontalAlignment(HorizontalAlignment.Right)
				.VerticalAlignment(VerticalAlignment.Top)
				.IsHitTestVisible(false)
				.WithTheme((theme, border) => border.Background = AppTheme.For(theme).Info)
				.Child(new TextBlock()
					.Text(label)
					.FontFamily(AppTheme.MonoFont)
					.FontSize(9)
					.FontWeight(FontWeight.SemiBold)
					.HorizontalAlignment(HorizontalAlignment.Center)
					.VerticalAlignment(VerticalAlignment.Center)
					.OnAccentText()));

		return new Button()
			.Ghost()
			.Width(28)
			.Height(26)
			.Padding(Thickness.Zero)
			.Content(content)
			.OnClick(action)
			.HoverTip(tooltip);
	}

	private enum Pane
	{
		Graph,
		Compare,
		More,
	}

	/// 把某一节的正文高度设成拖动算出来的绝对值。上限留给主区：更改区至少要能看见提交框与
	/// 几行改动，所以三节加起来不能把上面挤没。
	private void SetPaneBody(Pane pane, double body)
	{
		var clamped = Math.Max(FloorBody(pane), GitPaneLayout.ClampBody(body, MaxPaneBody()));
		_panes = pane switch
		{
			Pane.Graph => _panes.WithGraph(clamped, _panes.GraphOpen),
			Pane.Compare => _panes.WithCompare(clamped, _panes.CompareOpen),
			_ => _panes.WithMore(clamped, _panes.MoreOpen),
		};

		ApplyPaneHeights();
	}

	private static double FloorBody(Pane pane) => pane == Pane.Compare ? CompareMinBody : GitPaneLayout.MinBody;

	private double MaxPaneBody()
	{
		var available = _root.Bounds.Height;
		if (available <= 0)
		{
			return 420;
		}

		// 40 的分支栏 + 节头 + 提交框与几行改动：更改区至少留 240 DIP。
		return Math.Max(GitPaneLayout.MinBody, (available - 40 - GitPaneLayout.HeaderHeight) * 0.6);
	}

	/// 把三节的高度、分隔条可见性与「谁来吸掉剩余高度」写成当前布局。
	/// 折叠的那一节只留节头，并且它的分隔条一并收起（没有可改的对象就不留一条能按到的线）。
	private void ApplyPaneHeights()
	{
		var absorber = AbsorberRow();
		var repo = _lastStatus?.IsRepository == true;

		void Apply(Border host, PaneSash sash, int row, double body, bool open)
		{
			var absorbs = row == absorber;
			host.Height = absorbs || !open ? double.NaN : GitPaneLayout.ExpandedHeight(body);
			host.ClipToBounds = open && !absorbs;
			sash.IsVisible = open && repo;
		}

		// 窗口很矮时固定的节加起来可能超过可用高度：更改区按自己的行裁剪，
		// 否则提交框会画到下面「图」的节头上，两层文字叠在一起。
		_changesBody.ClipToBounds = true;
		_changesBody.IsVisible = _panes.ChangesOpen && repo;
		_changesBody.Height = _panes.ChangesOpen ? double.NaN : 0;
		Apply(_graphHost, _graphSash, GraphRow, _panes.Graph, _panes.GraphOpen);
		Apply(_compareHost, _compareSash, CompareRow, Math.Max(FloorBody(Pane.Compare), _panes.Compare), _panes.CompareOpen);
		Apply(_moreHost, _moreSash, MoreRow, _panes.More, _panes.MoreOpen);

		_root.Rows(
			"auto," +                       // 0 分支栏
			"auto," +                       // 1 非仓库提示
			"auto," +                       // 2 更改节头
			(ChangesRow == absorber ? "*" : "auto") + "," +
			"auto," +                       // 4 分隔条
			(GraphRow == absorber ? "*" : "auto") + "," +
			"auto," +                       // 6 分隔条
			(CompareRow == absorber ? "*" : "auto") + "," +
			"auto," +                       // 8 分隔条
			(MoreRow == absorber ? "*" : "auto"));
		_root.InvalidateMeasure();
	}

	private const int ChangesRow = 3;
	private const int GraphRow = 5;
	private const int CompareRow = 7;
	private const int MoreRow = 9;

	/// 吸掉剩余高度的那一节：按 更改 → 图 → 比较 → 更多 取第一个展开着的。
	/// 更改收起来时把它让给图，面板才不会在中间留一块空白。
	private int AbsorberRow()
	{
		var repo = _lastStatus?.IsRepository == true;
		if (!repo)
		{
			return ChangesRow;
		}

		if (_panes.ChangesOpen)
		{
			return ChangesRow;
		}

		if (_panes.GraphOpen)
		{
			return GraphRow;
		}

		return _panes.CompareOpen ? CompareRow : MoreRow;
	}

	private void SavePanes()
	{
		try
		{
			DesktopPreference.Load().WithGitPanes(Repo(), _panes.Encode()).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 布局记住失败不影响使用，下一次拖动再试。
		}
	}

	/// 让按钮的显示内容不参与命中测试。不关掉时，鼠标在一行里走过图标/文字/计数会让命中叶子不断换，
	/// 每换一次都要重算悬停链并重绘一帧；快速扫过列表时输入队列就是这样堆起来的（高亮「不跟手」）。
	/// 按钮本体仍然命中（整行一次），行尾的动作按钮是兄弟节点，不受影响。
	private static void FlatContent(Panel content) => content.IsHitTestVisible = false;

	/// 主按钮文字：面板里所有 Primary 按钮的标签都居中，不贴着按钮左缘。
	/// 字色不写死：主按钮样式在可用 / 禁用两态自己换 Foreground，标签继承它才不会在禁用时变成
	/// 深色字压深色底（截图里那种「已经消失」的按钮）。
	private static TextBlock CenteredLabel(string text) =>
		new TextBlock()
			.Text(text)
			.FontSize(AppTheme.FontSizeCaption)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center);

	private static Button SmallTextButton(string text, Action action) =>
		new Button()
			.Ghost()
			.Height(22)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Content(new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro).SubtleText())
			.OnClick(action)
			.HoverTip(text);

	private Button CancelButton() =>
		new Button()
			.Ghost()
			.Height(AppTheme.ControlHeightSm)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Content(new TextBlock().Text(Strings.Cancel).FontSize(AppTheme.FontSizeMicro).MutedText())
			.OnClick(() =>
			{
				_branchEditorVisible = false;
				Refresh();
			});

	private static Button GlyphButton(GlyphKind kind, string tooltip, Action action)
	{
		var glyph = ThemedGlyph(kind, AppStyles.RoleSubtle, 3);
		// 与 RowIconButton 同尺寸：同一行里两颗动作键差 2 DIP 会显得一高一低。
		return new Button()
			.Ghost()
			.Width(22)
			.Height(22)
			.Padding(0)
			.Content(glyph)
			.OnClick(action)
			.HoverTip(tooltip);
	}

	private static GlyphElement ThemedGlyph(GlyphKind kind, string role, double size) =>
		new GlyphElement()
			.Kind(kind)
			.GlyphSize(size)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, glyph) => glyph.Foreground = RoleColor(AppTheme.For(theme), role));

	private static Color RoleColor(AppPalette palette, string role) => role switch
	{
		AppStyles.RolePrimary => palette.TextPrimary,
		AppStyles.RoleMuted => palette.TextMuted,
		AppStyles.RoleSubtle => palette.TextSubtle,
		AppStyles.RoleFaint => palette.TextFaint,
		AppStyles.RoleAccent => palette.Accent,
		AppStyles.RoleInfo => palette.Info,
		AppStyles.RoleSuccess => palette.Success,
		AppStyles.RoleError => palette.Error,
		_ => palette.TextMuted,
	};

	private static string GitDiffStat(int added, int removed) => Strings.GitDiffStat(added, removed);

	internal void SetGraphHeightForTests(double body)
	{
		SetPaneBody(Pane.Graph, body);
		SavePanes();
	}

	/// 仅供 --ui-smoke git-compare-lens：切到某一审查层，截图核对 .gitattributes 覆盖与筛选结果。
	internal void SetCompareLensForSmoke(GitReviewLayer layer) => SelectCompareLens(layer);

	/// 仅供 --ui-smoke git-commit：展开图里第一条提交，截图核对复制/操作菜单的入口。
	internal void ExpandFirstCommitForSmoke() => ExpandFirstCommitForTests();

	/// 展开图里第一条提交：用例靠它核对大提交的文件列表是否分页。
	internal void ExpandFirstCommitForTests()
	{
		if (_graphCommits.Count > 0)
		{
			ToggleCommit(_graphCommits[0]);
		}
	}

	/// 展开/收起第 <paramref name="index"/> 条提交：用例靠它核对重排后图的滚动位置是否保留。
	internal void ToggleCommitForTests(int index)
	{
		if ((uint)index < (uint)_graphCommits.Count)
		{
			ToggleCommit(_graphCommits[index]);
		}
	}

	internal void ScrollGraphForTests(double offset) => _graphScroll.SetScrollOffsets(0, offset);

	internal double GraphScrollOffsetForTests => _graphScroll.VerticalOffset;

	/// 仅供 --ui-smoke git-worktree：展开「更多」并打开新建副本编辑器，带一份草稿。
	internal void OpenWorktreeEditorForSmoke()
	{
		_panes = _panes.WithMore(_panes.More, open: true);
		_worktreeEditorVisible = true;
		_worktreeDraft = "review/team-copy";
		_worktreeBaseDraft = "HEAD";
		RebuildMore();
	}

	/// 展开「更多」区：stash 与隔离副本的渲染用例从这里进。
	internal void OpenMorePaneForTests()
	{
		_panes = _panes.WithMore(_panes.More, open: true);
		RebuildMore();
		ApplyPaneHeights();
	}

	/// 把比较节设成给定正文高度并展开：用例核对最小高度下限与内容裁剪。
	internal void SetCompareHeightForTests(double body)
	{
		_panes = _panes.WithCompare(body, open: true);
		SetPaneBody(Pane.Compare, body);
		RebuildCompare();
	}

	internal double ComparePaneHeightForTests => _compareHost.Height;

	/// 测试用：扫描结果、当前仓库与切仓库（绕过下拉事件）。
	internal IReadOnlyList<GitRepository> RepositoriesForTests => _repos;

	/// 面板当前操作的仓库；右侧「检查点」分节跟随它，才能找回子仓库里的快照。
	internal string CurrentRepositoryPath => Repo();

	internal string CurrentRepositoryForTests => Repo();

	internal string CurrentBranchForTests => _lastStatus?.Branch ?? string.Empty;

	internal void SelectRepositoryForTests(string path) => SelectRepository(path);

	/// 测试用：直接切审查层（绕过下拉事件）。
	internal void SetCompareLensForTests(GitReviewLayer layer) => SelectCompareLens(layer);

	internal int CompareVisibleCountForTests => _compareVisibleFiles.Count;

	/// 直接跑一次分支比较：绕过下拉选择，用例只关心结果行的分页。
	internal void CompareBranchesForTests(string baseRef, string targetRef)
	{
		_panes = _panes.WithCompare(_panes.Compare, true);
		_compareBase = baseRef;
		_compareTarget = targetRef;
		RunCompare();
	}

	/// 直接跑一次「工作区 vs 分叉点」：绕过下拉与异步加载，用例只关心结果行。
	internal void CompareWorkingTreeForTests(string baseRef)
	{
		_panes = _panes.WithCompare(_panes.Compare, true);
		_compareBase = baseRef;
		_compareTarget = GitHistoryReader.WorktreeTarget;
		RunCompare();
	}

	/// 仅供 --ui-smoke git-compare：展开比较节并跑一次工作区范围，供截图核对下拉与结果行。
	internal void OpenWorktreeCompareForSmoke(string baseRef)
	{
		_panes = _panes.WithCompare(Math.Max(_panes.Compare, 260), true);
		_compareBase = baseRef;
		_compareTarget = GitHistoryReader.WorktreeTarget;
		RebuildCompare();
		RunCompare();
	}

	internal void ReportGraphGeometryForSmoke()
	{
		var lanes = new List<Rect>();

		void Walk(Element element)
		{
			if (element is CommitGraphLanes)
			{
				lanes.Add(element.Bounds);
			}

			if (element is IVisualTreeHost host)
			{
				host.VisitChildren(child => { Walk(child); return true; });
			}
		}

		Walk(View);
		Console.WriteLine(
			$"git-sashes graph={_graphSash.Bounds} compare={_compareSash.Bounds} more={_moreSash.Bounds} " +
			$"panes={_panes.Encode()}");
		Console.WriteLine(
			$"git-graph root={((FrameworkElement)View).Bounds} commits={_graphCommits.Count}/{_graphTotal} " +
			$"branches={CurrentBranches().Count} error={_graphError ?? "-"} lanes=[{string.Join(" ", lanes)}]");
	}

	public void SelectFirstChangeForSmoke()
	{
		var status = GitStatusReader.Read(Repo());
		if (status.Changes.FirstOrDefault() is { } first)
		{
			var untracked = first.IndexStatus == "?" && first.WorkTreeStatus == "?";
			ShowDiff(GitDiffRequest.WorkingTree(first.Path, first.HasStaged, untracked));
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			_summaryAbort?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// 生成任务已经结束并释放了 CTS。
		}

		_summaryAbort?.Dispose();
		_summaryAbort = null;
		_summaryTimer.Stop();
		_summaryTimer.Dispose();
		_pollTimer.Stop();
		_pollTimer.Dispose();
	}
}
