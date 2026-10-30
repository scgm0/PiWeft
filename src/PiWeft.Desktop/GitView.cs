using System.Globalization;
using System.Net.Http;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>
/// Git 面板（参考 PiDeck 的 SCM 布局）：紧凑分支/同步栏、提交摘要、暂存/未暂存目录树、
/// 可折叠的差异与更多区。写操作走 <see cref="GitWriter"/>，只读查询走 <see cref="GitStatusReader"/>。
/// </summary>
public sealed partial class GitView : IDisposable
{
	private readonly Func<string> _cwd;
	private readonly GitCommitAssistant? _assistant;
	private readonly Action? _openSettings;

	private readonly Grid _host = new();
	private readonly HashSet<string> _collapsedDirectories = new(StringComparer.Ordinal);
	// 分节宿主：交互态变化只换需要变的那一节。整棵面板重建会把光标下的行、滚动位置与悬停态
	// 一起丢掉 —— 点一下文件就「不跟手」，就是这个原因。
	private readonly Grid _root = new();
	private readonly DockPanel _changesBody = new DockPanel().LastChildFill();
	private readonly Border _notRepoHost = new();
	private readonly Border _branchBarHost = new();
	private readonly Border _changesHeaderHost = new();
	private readonly Border _failureHost = new();
	private readonly Border _composerHost = new();
	private readonly Border _listHost = new();
	private readonly Border _diffHost = new();
	private readonly Border _graphHost = new();
	private readonly Border _compareHost = new();
	private readonly Border _moreHost = new();
	private readonly PaneSash _graphSash;
	private readonly PaneSash _compareSash;
	private readonly PaneSash _moreSash;
	private GitPaneLayout _panes;
	private readonly Dictionary<string, Border> _rowFrames = new(StringComparer.Ordinal);
	private GitStatus? _lastStatus;
	private bool _sectionsBuilt;

	private GitDiffRequest? _diffRequest;
	private string _graphRef = string.Empty;
	private int _graphPage = 40;
	private bool _branchesLoaded;
	private bool _branchesDirty = true;
	private IReadOnlyList<string> _branches = [];
	private bool _graphLoaded;
	private bool _graphDirty = true;
	private IReadOnlyList<GitCommitEntry> _graphCommits = [];
	private int _graphTotal;
	private string? _graphError;
	private readonly HashSet<string> _expandedCommits = new(StringComparer.Ordinal);
	private readonly Dictionary<string, GitCommitDetail> _commitDetails = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _commitErrors = new(StringComparer.Ordinal);
	private string _compareBase = string.Empty;
	private string _compareTarget = string.Empty;
	private GitBranchComparison? _compareResult;
	private string _compareResultBase = string.Empty;
	private string _compareResultTarget = string.Empty;
	private string? _compareError;
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
	private MultiLineTextBox? _commitInput;

	// 提交摘要的结构化失败原因（对齐 PiDeck 的 GIT_COMMIT_* 码）：只在摘要链路里展示，
	// 与其它 git 操作的失败展开条分开，免得模型没配好却被引去查仓库。
	private GitSummaryFailure? _summaryFailure;
	private string _summaryFailureText = string.Empty;
	private ProgressBar? _summaryBar;
	private readonly DispatcherTimer _summaryTimer = new(TimeSpan.FromMilliseconds(250));
	private DateTime _summaryStarted;

	public GitView(Func<string> cwd, GitCommitAssistant? assistant = null, Action? openSettings = null)
	{
		_cwd = cwd;
		_assistant = assistant;
		_openSettings = openSettings;

		// 三条可拖的分隔条：每条锚定「按下的那一刻、下面那一节多高」，之后按指针的窗口坐标
		// 换算出这一节应有的高度（见 PaneSash 的说明：用增量累加会自我抵消）。
		_graphSash = new PaneSash(() => _panes.Graph, height => SetPaneBody(Pane.Graph, height), SavePanes);
		_compareSash = new PaneSash(() => _panes.Compare, height => SetPaneBody(Pane.Compare, height), SavePanes);
		_moreSash = new PaneSash(() => _panes.More, height => SetPaneBody(Pane.More, height), SavePanes);
		_panes = GitPaneLayout.Decode(DesktopPreference.Load().GitPanes.GetValueOrDefault(_cwd()));

		// 进度条向 PiDeck 看齐：按等待上限爬升（而不是无限循环的不确定条），让用户知道还剩多久。
		_summaryTimer.Tick += UpdateSummaryProgress;

		// 骨架只搭一次：DockPanel 的子级顺序即停靠顺序。底部先停靠的在最下面，
		// 所以自下而上是「更多 / 分隔条 / 比较 / 分隔条 / 图 / 分隔条」，「更改」吃掉剩余高度。
		_changesBody.Children(
			_failureHost.DockTop(),
			_composerHost.DockTop(),
			_diffHost.DockBottom(),
			_listHost);
		// 一行一节；吸掉剩余空间的那行由 ApplyPaneHeights 指定（见 AbsorberRow）。
		// 折叠某一节后剩余高度要给还别的节，否则原地留一块空白。
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
		Rebuild();
	}

	public UIElement View { get; }

	/// <summary>主题切换后刷新角色文本颜色；图形在各自 WithTheme 里跟随。</summary>
	public void ApplyTheme() => AppStyles.ApplyTextColors(View);

	/// <summary>重新读取 Git 状态并重建面板；草稿、折叠状态与阅读位置保留。</summary>
	public void Refresh()
	{
		if (_disposed)
		{
			return;
		}

		Rebuild();
	}

	private void Rebuild()
	{
		var status = GitStatusReader.Read(_cwd());
		if (_diffRequest is { Kind: GitDiffKind.WorkingTree or GitDiffKind.Staged } request &&
			!status.Changes.Any(change => string.Equals(change.Path, request.Path, StringComparison.Ordinal)))
		{
			_diffRequest = null;
		}

		_lastStatus = status;
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

	/// <summary>用缓存的 status 重填所有分节；交互态变化只走下面的单项 Rebuild*。</summary>
	private void RebuildSections()
	{
		var status = _lastStatus ??= GitStatusReader.Read(_cwd());
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

	/// <summary>「更改」区整体重填（节头 / 错误条 / 提交框 / 改动树 / diff）。</summary>
	private void RebuildChanges()
	{
		var status = _lastStatus!;
		// 提交框与 diff 面板都留在各自的宿主里：换一节不影响另一节的焦点与滚动位置。
		Swap(_changesHeaderHost, BuildChangesHeader(status));
		Swap(_failureHost, BuildFailureStrip());
		RebuildComposer(status);
		RebuildList();
		RebuildDiff();
		ApplyPaneHeights();
	}

	/// <summary>只重填提交框（暂存数变化会改占位符与按钮可用性）。</summary>
	private void RebuildComposer(GitStatus status) => Swap(_composerHost, BuildCommitComposer(status));

	private void RebuildList()
	{
		_rowFrames.Clear();
		Swap(_listHost, BuildChangeList(_lastStatus!));
	}

	private void RebuildDiff()
	{
		Swap(_diffHost, BuildDiffPane());
		ApplyRowSelection();
	}

	private void RebuildGraph()
	{
		var branches = _panes.GraphOpen || _panes.CompareOpen ? CurrentBranches() : [];
		Swap(_graphHost, BuildGraphPane(branches));
		ApplyPaneHeights();
	}

	private void RebuildCompare()
	{
		var branches = _panes.GraphOpen || _panes.CompareOpen ? CurrentBranches() : [];
		Swap(_compareHost, BuildComparePane(_lastStatus!, branches));
		ApplyPaneHeights();
	}

	private void RebuildMore()
	{
		Swap(_moreHost, BuildMorePane(_lastStatus!));
		ApplyPaneHeights();
	}

	private void RebuildBottomPanes(GitStatus status)
	{
		RebuildGraph();
		RebuildCompare();
		RebuildMore();
	}

	/// <summary>选中行的底色就地切换：不重建改动树，光标下的行与滚动位置都保持原样。</summary>
	private void ApplyRowSelection()
	{
		foreach (var (key, frame) in _rowFrames)
		{
			var (path, staged) = SplitRowKey(key);
			frame.Background = _diffRequest?.MatchesWorkingTree(path, staged) == true
				? AppTheme.CardBackgroundAlt
				: AppTheme.Transparent;
		}
	}

	private static string RowKey(string path, bool staged) => (staged ? "s" : "w") + path;

	private static (string Path, bool Staged) SplitRowKey(string key) => (key[1..], key[0] == 's');

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

	// ----------------------------------------------------------------- 分支栏

	private IReadOnlyList<string> CurrentBranches()
	{
		if (!_branchesLoaded || _branchesDirty)
		{
			_branches = GitWriter.Branches(_cwd());
			_branchesLoaded = true;
			_branchesDirty = false;
		}

		return _branches;
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
			Refresh();
		});
		refresh.IsEnabled = !_busy && !_generating;
		right.Children(refresh);

		if (status.IsRepository)
		{
			var fetch = ToolbarButton("download", Strings.GitFetch, () => _ = RunGitAsync(() => GitWriter.Fetch(_cwd()), Strings.GitFetchBusy));
			fetch.IsEnabled = status.HasRemotes && !_busy && !_generating;
			right.Children(fetch);

			var pulling = _busy && _busyMessage == Strings.GitPullBusy;
			var pushing = _busy && _busyMessage == Strings.GitPushBusy;
			var pullTooltip = status.Behind > 0 ? Strings.GitPullBehind(status.Behind) : Strings.GitPull;
			var pull = ToolbarCountButton("arrow-down", pullTooltip,
				() => _ = RunGitAsync(() => GitWriter.Pull(_cwd()), Strings.GitPullBusy), status.Behind, pulling);
			pull.IsEnabled = status.HasRemotes && status.HasUpstream && !status.Detached && !_busy && !_generating;
			right.Children(pull);

			var pushTooltip = status.Ahead > 0 ? Strings.GitPushAhead(status.Ahead) : Strings.GitPush;
			var push = ToolbarCountButton("arrow-up", pushTooltip,
				() => _ = RunGitAsync(() => GitWriter.Push(_cwd()), Strings.GitPushBusy), status.Ahead, pushing);
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

		return new DockPanel().LastChildFill().Children(right.DockRight(), branchButton);
	}

	private UIElement BuildNewBranchEditor()
	{
		TextBox? input = null;
		input = new TextBox()
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
		var branches = GitWriter.Branches(_cwd()).ToList();
		if (branches.Count == 0 && status.Branch.Length > 0)
		{
			branches.Add(status.Branch);
		}

		var checkIcon = new IconTemplate(size => new GlyphElement()
			.Kind(GlyphKind.CheckMark)
			.GlyphSize(4)
			.Foreground(AppTheme.Accent)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center));

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

				_ = RunGitAsync(() => GitWriter.SwitchBranch(_cwd(), name), Strings.GitBusy);
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
		var result = GitWriter.CreateBranch(_cwd(), name?.Trim() ?? string.Empty);
		if (result.Ok)
		{
			_branchDraft = string.Empty;
			_branchEditorVisible = false;
		}

		ApplyResult(result);
	}

	// ----------------------------------------------------------------- 更改区

	private UIElement BuildChangesPane(GitStatus status)
	{
		var pane = new DockPanel().LastChildFill();
		pane.Children(BuildChangesHeader(status).DockTop());
		if (!_panes.ChangesOpen)
		{
			return pane;
		}

		if (BuildFailureStrip() is { } failure)
		{
			pane.Children(failure.DockTop());
		}
		pane.Children(BuildCommitComposer(status).DockTop());
		if (BuildDiffPane() is { } diff)
		{
			pane.Children(diff.DockBottom());
		}

		pane.Children(BuildChangeList(status));
		return pane;
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

		_commitInput = new MultiLineTextBox()
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

		var status = GitStatusReader.Read(_cwd());
		if (!status.Changes.Any(static change => change.HasStaged && !change.IsConflict))
		{
			Toasts.Show(View, Strings.GitNothingStaged);
			return;
		}

		var draft = message.Trim();
		await RunGitAsync(() => GitWriter.Commit(_cwd(), draft), Strings.GitCommitBusy, onSuccess: () => _commitDraft = string.Empty);
	}

	private UIElement BuildChangeList(GitStatus status)
	{
		var list = new StackPanel().Spacing(2);
		var conflicts = status.Changes.Where(static change => change.IsConflict).ToList();
		var staged = status.Changes.Where(static change => change.HasStaged && !change.IsConflict).ToList();
		var unstaged = status.Changes.Where(static change => change.HasWorkTree && !change.IsConflict).ToList();

		if (conflicts.Count == 0 && staged.Count == 0 && unstaged.Count == 0)
		{
			list.Children(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center)
				.Padding(new Thickness(AppTheme.Space3, AppTheme.Space3, AppTheme.Space3, AppTheme.Space3))
				.Children(
					AppIcons.Icon("circle-check", AppStyles.RoleSuccess, 14),
					new TextBlock().Text(Strings.GitNoPendingChanges).FontSize(AppTheme.FontSizeCaption).MutedText()));
		}
		else
		{
			if (conflicts.Count > 0)
			{
				list.Children(BuildChangeSection(Strings.GitMergeChanges, conflicts, stagedSide: false, bulkLabel: null, bulk: null, conflict: true, allowActions: false));
			}

			if (staged.Count > 0)
			{
				list.Children(BuildChangeSection(
					Strings.GitStaged,
					staged,
					stagedSide: true,
					bulkLabel: Strings.GitUnstageAll,
					bulk: () => ApplyResult(GitWriter.Unstage(_cwd(), [.. staged.Select(static change => change.Path)])),
					conflict: false,
					allowActions: true));
			}

			if (unstaged.Count > 0)
			{
				list.Children(BuildChangeSection(
					Strings.GitUnstaged,
					unstaged,
					stagedSide: false,
					bulkLabel: Strings.GitStageAll,
					bulk: () => ApplyResult(GitWriter.Stage(_cwd(), [.. unstaged.Select(static change => change.Path)])),
					conflict: false,
					allowActions: true));
			}
		}

		return new ScrollViewer()
			.SmoothScrolling()
			.Content(list)
			.AutoVerticalScroll()
			.NoHorizontalScroll();
	}

	private UIElement BuildChangeSection(
		string title,
		IReadOnlyList<GitChange> changes,
		bool stagedSide,
		string? bulkLabel,
		Action? bulk,
		bool conflict,
		bool allowActions)
	{
		var section = new StackPanel().Spacing(1);
		var header = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				new TextBlock().Text(title).FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.SemiBold)
					.WithTheme((theme, text) => text.Foreground = conflict ? AppTheme.For(theme).Error : AppTheme.For(theme).TextPrimary),
				new TextBlock().Text(changes.Count.ToString()).FontSize(AppTheme.FontSizeMicro).FaintText());

		var row = new DockPanel().LastChildFill().Margin(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space2, 0));
		if (bulk is not null && bulkLabel is not null)
		{
			var action = SmallTextButton(bulkLabel, bulk);
			action.IsEnabled = !_busy && !_generating;
			row.Children(action.DockRight());
		}

		row.Children(header);
		section.Children(row);
		foreach (var treeRow in GitChangeTree.Build(changes, _collapsedDirectories))
		{
			section.Children(treeRow.IsDirectory
				? BuildDirectoryRow(treeRow, stagedSide, allowActions)
				: BuildFileRow(treeRow.File!, treeRow.Depth, stagedSide, conflict, allowActions));
		}

		return section;
	}

	private UIElement BuildDirectoryRow(GitTreeRow row, bool stagedSide, bool allowActions)
	{
		var collapsed = _collapsedDirectories.Contains(row.Path);
		var directoryContent = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(5)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				ThemedGlyph(collapsed ? GlyphKind.ChevronRight : GlyphKind.ChevronDown, AppStyles.RoleFaint, 2.5),
				AppIcons.Icon("folder-open", AppStyles.RoleSubtle, 13),
				new TextBlock().Text(row.Label).FontSize(AppTheme.FontSizeCaption).MutedText()
					.MaxWidth(Math.Max(80, 220 - row.Depth * 10))
					.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis),
				new TextBlock().Text(row.Changes.Count.ToString()).FontSize(AppTheme.FontSizeMicro).FaintText());
		FlatContent(directoryContent);

		var directoryButton = new Button()
			.Ghost()
			.Height(26)
			.Padding(new Thickness(AppTheme.Space2 + row.Depth * 10, 0, AppTheme.Space1, 0))
			.Content(directoryContent)
			.OnClick(() =>
			{
				if (!_collapsedDirectories.Remove(row.Path))
				{
					_collapsedDirectories.Add(row.Path);
				}

				// 折叠只影响改动树本身。
				RebuildList();
			});

		var action = GlyphButton(
			stagedSide ? GlyphKind.Minus : GlyphKind.Plus,
			stagedSide ? Strings.GitUnstageDirectory : Strings.GitStageDirectory,
			() =>
			{
				var paths = row.Changes.Select(static change => change.Path).ToArray();
				ApplyResult(stagedSide ? GitWriter.Unstage(_cwd(), paths) : GitWriter.Stage(_cwd(), paths));
			});
		action.IsEnabled = !_busy && !_generating;
		action.IsVisible = allowActions;

		return new DockPanel().LastChildFill().Children(action.DockRight(), directoryButton);
	}

	private UIElement BuildFileRow(GitChange change, int depth, bool stagedSide, bool conflict, bool allowActions)
	{
		var letter = conflict ? "U" : change.StatusLetter(stagedSide);
		var letterRole = LetterRole(letter);
		var rowContent = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				new TextBlock().Text(letter)
					.FontFamily(AppTheme.MonoFont)
					.FontSize(AppTheme.FontSizeMicro)
					.Width(14)
					.WithTheme((theme, text) => text.Foreground = RoleColor(AppTheme.For(theme), letterRole)),
				new TextBlock().Text(change.FileName)
					.FontSize(AppTheme.FontSizeCaption)
					.MutedText()
					.MaxWidth(Math.Max(80, 240 - depth * 10))
					.TextWrapping(TextWrapping.NoWrap)
					.TextTrimming(TextTrimming.CharacterEllipsis));
		FlatContent(rowContent);

		var row = new Button()
			.Ghost()
			.Height(27)
			.Padding(new Thickness(AppTheme.Space2 + depth * 10, 0, AppTheme.Space1, 0))
			.Content(rowContent)
			.OnClick(() => SelectDiff(change, stagedSide))
			.HoverTip(change.Path + "\n" + change.Label);
		row.Tag = change;

		var action = GlyphButton(
			stagedSide ? GlyphKind.Minus : GlyphKind.Plus,
			stagedSide ? Strings.GitUnstage : Strings.GitStage,
			() => ApplyResult(stagedSide ? GitWriter.Unstage(_cwd(), [change.Path]) : GitWriter.Stage(_cwd(), [change.Path])));
		action.IsEnabled = !_busy && !_generating;
		action.IsVisible = allowActions;
		action.Tag = change;

		var frame = new Border()
			.CornerRadius(AppTheme.RadiusXs)
			.WithTheme((theme, border) => border.Background = _diffRequest?.MatchesWorkingTree(change.Path, stagedSide) == true
				? AppTheme.For(theme).CardBackgroundAlt
				: AppTheme.For(theme).Transparent)
			.Child(new DockPanel().LastChildFill().Children(action.DockRight(), row));
		// 记下行框：选中态就地改底色，不为了高亮一行而重建整棵改动树。
		_rowFrames[RowKey(change.Path, stagedSide)] = frame;
		return frame;
	}

	private void SelectDiff(GitChange change, bool stagedSide)
	{
		var untracked = change.IndexStatus == "?" && change.WorkTreeStatus == "?";
		_diffRequest = _diffRequest?.MatchesWorkingTree(change.Path, stagedSide) == true
			? null
			: GitDiffRequest.WorkingTree(change.Path, stagedSide, untracked);
		if (_diffRequest is not null)
		{
			OpenChangesPane();
		}

		// 只重排 diff 那一块并就地改两行底色：树、滚动位置与光标下的悬停态都不动。
		RebuildDiff();
	}

	private UIElement? BuildDiffPane()
	{
		if (_diffRequest is not { } request)
		{
			return null;
		}

		var path = request.Path;
		string diff;
		string? error = null;
		switch (request.Kind)
		{
			case GitDiffKind.Staged:
				diff = GitStatusReader.StagedDiff(_cwd(), path);
				break;
			case GitDiffKind.Commit:
			{
				var content = GitHistoryReader.CommitFileDiff(
					_cwd(), request.CommitHash ?? string.Empty, request.BaseHash, path, request.OriginalPath);
				diff = content.Text;
				error = content.Error;
				break;
			}
			case GitDiffKind.Compare:
			{
				var content = GitHistoryReader.CompareFileDiff(
					_cwd(), request.BaseHash ?? string.Empty, request.TargetHash ?? string.Empty,
					path, request.OriginalPath);
				diff = content.Text;
				error = content.Error;
				break;
			}
			default:
				diff = GitStatusReader.Diff(_cwd(), path, request.Untracked);
				break;
		}

		var (added, removed) = CountDiffLines(diff);
		var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2);
		// 提交/比较的 diff 是历史内容，打开工作区文件会看错版本；这两个来源只给关闭。
		if (request.Kind is GitDiffKind.WorkingTree or GitDiffKind.Staged)
		{
			actions.Children(AppIcons.Button("square-pen", Strings.OpenInEditor, () => OpenInEditor(path)));
		}

		actions.Children(AppIcons.Button("x", Strings.GitCloseDiff, () =>
		{
			_diffRequest = null;
			Refresh();
		}));
		var header = new DockPanel().LastChildFill().Children(actions.DockRight(),
			new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new TextBlock().Text(request.Title).FontSize(AppTheme.FontSizeMicro)
						.MaxWidth(90).FontWeight(FontWeight.SemiBold).PrimaryText()
						.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis),
					new TextBlock().Text(path).FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro)
						.MaxWidth(170).SubtleText().TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis),
					new TextBlock().Text(GitDiffStat(added, removed)).FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeMicro).FaintText()));

		UIElement body = error is not null
			? new TextBlock().Text(Strings.GitDiffFailedReason(error))
				.FontSize(AppTheme.FontSizeMicro).TextWrapping(TextWrapping.Wrap)
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Error)
			: DiffView.Render(diff.Length > 0 ? diff : Strings.GitNoDiff);

		var pane = new StackPanel().Spacing(AppTheme.Space1)
			.Children(
				Separator(
					new Border()
						.Height(30)
						.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space1, 0))
						.Child(header),
					new Thickness(0, 1, 0, 1)),
				new ScrollViewer()
					.SmoothScrolling()
					.MaxHeight(180)
					.Content(body)
					.AutoVerticalScroll()
					.NoHorizontalScroll());

		return new Border().Padding(new Thickness(0, 0, 0, AppTheme.Space1)).Child(pane);
	}

	// ----------------------------------------------------------------- 更多区

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

		content.Children(BuildAssistantSettingsRow());
		content.Children(BuildStashSection());
		if (!status.HasRemotes)
		{
			content.Children(new TextBlock().Text(Strings.GitNoRemote)
				.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap));
		}

		return new StackPanel().Spacing(0)
			.Children(header, new ScrollViewer()
				.SmoothScrolling()
				.MaxHeight(_panes.More)
				.Content(content)
				.AutoVerticalScroll()
				.NoHorizontalScroll());
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
		var cwd = _cwd();
		var section = new StackPanel().Spacing(2);
		var push = ToolbarButton("package", Strings.GitStash, () => _ = RunGitAsync(() => GitWriter.StashPush(cwd), Strings.GitBusy));
		push.IsEnabled = !_busy && !_generating;
		section.Children(new DockPanel().LastChildFill().Children(push.DockRight(),
			new TextBlock().Text(Strings.GitStashes).FontSize(AppTheme.FontSizeMicro)
				.FontWeight(FontWeight.SemiBold).MutedText()));

		var stashes = GitWriter.Stashes(cwd);
		if (stashes.Count == 0)
		{
			section.Children(new TextBlock().Text(Strings.GitStashEmpty)
				.FontSize(AppTheme.FontSizeMicro).FaintText());
			return section;
		}

		foreach (var stash in stashes)
		{
			var captured = stash;
			var pop = ToolbarButton("rotate-ccw", Strings.GitStashPop, () => _ = RunGitAsync(() => GitWriter.StashPop(cwd), Strings.GitBusy));
			var drop = ToolbarButton("trash", Strings.GitStashResolved, () => _ = RunGitAsync(() => GitWriter.StashDrop(cwd, captured.Reference), Strings.GitBusy));
			pop.IsEnabled = !_busy && !_generating;
			drop.IsEnabled = !_busy && !_generating;
			var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(1).Children(pop, drop);
			section.Children(new DockPanel().LastChildFill().Children(actions.DockRight(),
				new TextBlock()
					.Text(captured.Description.Length > 0 ? captured.Description : captured.Reference)
					.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).MutedText()
					.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)));
		}

		return section;
	}

	// ----------------------------------------------------------------- 非仓库 / 通用

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
				new TextBlock().Text(canInit ? Strings.GitNotRepositoryHint(_cwd()) : Strings.GitNotRepository)
					.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap));

		if (canInit)
		{
			body.Children(new Button()
				.Primary()
				.Height(AppTheme.ControlHeightMd)
				.Content(new TextBlock().Text(Strings.GitInit).FontSize(AppTheme.FontSizeCaption))
				.OnClick(() => ApplyResult(GitWriter.Init(_cwd()))));
		}

		if (status.Error is { Length: > 0 } detail)
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
			_diffRequest = null;
			_branchesDirty = true;
			_graphDirty = true;
		}
		else
		{
			Toasts.Error(View, message);
			_lastFailureMessage = message;
			_lastFailureDetail = result.Detail;
		}

		Refresh();
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
		Refresh();

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

		var status = GitStatusReader.Read(_cwd());
		if (!status.Changes.Any(static change => change.HasStaged && !change.IsConflict))
		{
			SetSummaryFailure(GitSummaryFailure.EmptyDiff, Strings.GitStageBeforeGenerate);
			return;
		}

		var diff = GitStatusReader.StagedDiffAll(_cwd(), GitCommitMessage.MaxDiffCharacters);
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

	/// <summary>视觉验收：直接跑一次 AI 提交摘要（不等真实点击），用来截进度条与内联失败行。</summary>
	internal void GenerateSummaryForSmoke() => _ = GenerateCommitSummaryAsync();

	/// <summary>视觉验收：把摘要结果（或失败原因）打到 stdout，无人值守跑一次就能定位卡在哪一步。</summary>
	internal void ReportSummaryForSmoke() =>
		Console.WriteLine(
			"piweft-desktop: git summary draft=" +
			(_commitDraft.Length > 0 ? _commitDraft.Replace("\n", " \\n") : "(空)") +
			" failure=" + (_summaryFailure?.ToString() ?? "(无)") +
			" detail=" + (_lastFailureDetail ?? string.Empty).Replace("\n", " \\n"));

	/// <summary>解析提交摘要用哪个模型与档位：默认跟随会话，偏好里指了固定项就覆盖。同时把读到的偏好递出去，免得更下面再读一次盘。</summary>
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

	/// <summary>模型目录里是否真有可用的对话模型（固定模型被删除时 ResolveAssistant 会回退到会话模型）。</summary>
	private bool HasSummaryModel() =>
		_assistant?.Models().Any(static model => model.Type == ModelType.Chat) == true;

	/// <summary>记录并展示一次提交摘要失败；不同类型配不同动作提示（PiDeck 的错误码语义）。</summary>
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

	/// <summary>按等待上限爬升的进度（PiDeck 同款）：到 97% 停住等结果，不会先走满再回退。</summary>
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
		var full = Path.IsPathRooted(path) ? path : Path.Combine(_cwd(), path);
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

	// ----------------------------------------------------------------- 小部件

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

	/// <summary>
	/// 带差异计数的同步按钮（对齐 PiDeck 的 push/pull 徽标）：计数是「这一键现在能推出去/拉进来多少提交」，
	/// 只在该方向真有差异时出现；这一键正在执行时先收起，免得数字停在旧值上。
	/// <para>
	/// 布局照 PiDeck：图标保持居中，计数做成压在按钮右上角的小圆牌。箭头图标的右上是空角，
	/// 圆牌落在那里不遮笔画，也不必把图标挤歪 —— 把图标挪到左下（第一版的做法）会让整行看着歪。
	/// </para>
	/// </summary>
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

	/// <summary>点开某一行时把「更改」区展开（折叠状态下选文件看不到 diff）。</summary>
	private void OpenChangesPane()
	{
		if (_panes.ChangesOpen)
		{
			return;
		}

		_panes = _panes.WithChanges(true);
		_changesBody.IsVisible = true;
		SavePanes();
	}

	private enum Pane
	{
		Graph,
		Compare,
		More,
	}

	/// <summary>
	/// 把某一节的正文高度设成拖动算出来的绝对值。上限留给主区：更改区至少要能看见提交框与
	/// 几行改动，所以三节加起来不能把上面挤没。
	/// </summary>
	private void SetPaneBody(Pane pane, double body)
	{
		var clamped = GitPaneLayout.ClampBody(body, MaxPaneBody());
		_panes = pane switch
		{
			Pane.Graph => _panes.WithGraph(clamped, _panes.GraphOpen),
			Pane.Compare => _panes.WithCompare(clamped, _panes.CompareOpen),
			_ => _panes.WithMore(clamped, _panes.MoreOpen),
		};

		ApplyPaneHeights();
	}

	/// <summary>面板可用高度里留给「更改」区的下限之外的部分，就是三节正文的总上限。</summary>
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

	/// <summary>
	/// 把三节的高度、分隔条可见性与「谁来吸掉剩余高度」写成当前布局。
	/// 折叠的那一节只留节头，并且它的分隔条一并收起（没有可改的对象就不留一条能按到的线）。
	/// </summary>
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

		_changesBody.IsVisible = _panes.ChangesOpen && repo;
		_changesBody.Height = _panes.ChangesOpen ? double.NaN : 0;
		Apply(_graphHost, _graphSash, GraphRow, _panes.Graph, _panes.GraphOpen);
		Apply(_compareHost, _compareSash, CompareRow, _panes.Compare, _panes.CompareOpen);
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

	/// <summary>这一节正在吸剩余高度时不再用自己的 MaxHeight 自我封顶。</summary>
	private bool PaneAbsorbs(int row) => AbsorberRow() == row;

	private const int ChangesRow = 3;
	private const int GraphRow = 5;
	private const int CompareRow = 7;
	private const int MoreRow = 9;

	/// <summary>
	/// 吸掉剩余高度的那一节：按 更改 → 图 → 比较 → 更多 取第一个展开着的。
	/// 更改收起来时把它让给图，面板才不会在中间留一块空白。
	/// </summary>
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

	/// <summary>落盘面板布局；只在拖完或折叠时调用，不每移一像素写一次文件。</summary>
	private void SavePanes()
	{
		try
		{
			DesktopPreference.Load().WithGitPanes(_cwd(), _panes.Encode()).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 布局记住失败不影响使用，下一次拖动再试。
		}
	}

	/// <summary>
	/// 让按钮的显示内容不参与命中测试。不关掉时，鼠标在一行里走过图标/文字/计数会让命中叶子不断换，
	/// 每换一次都要重算悬停链并重绘一帧 —— 快速扫过列表时输入队列就是这么堆起来的（高亮「不跟手」）。
	/// 按钮本体仍然命中（整行一次），行尾的动作按钮是兄弟节点，不受影响。
	/// </summary>
	private static void FlatContent(StackPanel content) => content.IsHitTestVisible = false;

	/// <summary>主按钮文字：面板里所有 Primary 按钮的标签都居中，不贴着按钮左缘。
	/// 字色不写死：主按钮样式在可用 / 禁用两态自己换 Foreground，标签继承它才不会在禁用时变成
	/// 深色字压深色底（截图里那种「已经消失」的按钮）。</summary>
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
		return new Button()
			.Ghost()
			.Width(24)
			.Height(24)
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

	private static (int Added, int Removed) CountDiffLines(string diff)
	{
		var added = 0;
		var removed = 0;
		foreach (var line in diff.Split('\n'))
		{
			if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
			{
				continue;
			}

			if (line.StartsWith('+'))
			{
				added++;
			}
			else if (line.StartsWith('-'))
			{
				removed++;
			}
		}

		return (added, removed);
	}

	private static string GitDiffStat(int added, int removed) => Strings.GitDiffStat(added, removed);

	/// <summary>测试入口：模拟在「图」上面的分隔条上向上拖 <paramref name="pixels"/> DIP。</summary>
	internal void SetGraphHeightForTests(double body)
	{
		SetPaneBody(Pane.Graph, body);
		SavePanes();
	}

	/// <summary>视觉验收用：打印泳道与面板根的窗口坐标，供像素检查脚本对齐。</summary>
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

	/// <summary>视觉验收用：选中第一个改动，让 diff 区一起出现在截图里。</summary>
	public void SelectFirstChangeForSmoke()
	{
		var status = GitStatusReader.Read(_cwd());
		if (status.Changes.FirstOrDefault() is { } first)
		{
			var untracked = first.IndexStatus == "?" && first.WorkTreeStatus == "?";
			_diffRequest = GitDiffRequest.WorkingTree(first.Path, first.HasStaged, untracked);
			Refresh();
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
	}
}
