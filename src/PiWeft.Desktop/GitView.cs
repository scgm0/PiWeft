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

	private GitDiffRequest? _diffRequest;
	private bool _graphExpanded = true;
	private bool _compareExpanded;
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
	private bool _changesExpanded = true;
	private bool _moreExpanded;
	private bool _busy;
	private string _busyMessage = string.Empty;
	private bool _generating;
	private bool _focusCommitAfterBuild;
	private bool _focusBranchInputAfterBuild;
	private TextBox? _branchInput;
	private CancellationTokenSource? _summaryAbort;
	private bool _disposed;
	private MultiLineTextBox? _commitInput;

	public GitView(Func<string> cwd, GitCommitAssistant? assistant = null, Action? openSettings = null)
	{
		_cwd = cwd;
		_assistant = assistant;
		_openSettings = openSettings;
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
		_commitInput = null;
		_branchInput = null;
		var status = GitStatusReader.Read(_cwd());
		if (_diffRequest is { Kind: GitDiffKind.WorkingTree or GitDiffKind.Staged } request &&
			!status.Changes.Any(change => string.Equals(change.Path, request.Path, StringComparison.Ordinal)))
		{
			_diffRequest = null;
		}

		foreach (var child in _host.Children.ToArray())
		{
			_host.Remove(child);
			UiLifetime.Release(child);
		}

		try
		{
			_host.Children(BuildContent(status));
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
		{
			// 单个控件构建失败不该让整个面板空白；保留可见错误并写日志，用户能复制给维护者。
			AppLog.Failure(AppLog.For<GitView>(), "Build Git panel", ex);
			_host.Children(new TextBlock().Text(ex.Message)
				.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap));
		}
		_host.InvalidateMeasure();
		if (_focusCommitAfterBuild && _commitInput is not null)
		{
			_focusCommitAfterBuild = false;
			_commitInput.Focus();
		}
		else if (_focusBranchInputAfterBuild && _branchInput is not null)
		{
			_focusBranchInputAfterBuild = false;
			_branchInput.Focus();
		}
	}

	private UIElement BuildContent(GitStatus status)
	{
		var root = new DockPanel().LastChildFill();
		root.Children(BuildBranchBar(status).DockTop());

		if (!status.IsRepository)
		{
			root.Children(BuildNotRepository(status));
			return root;
		}

		var branches = status.IsRepository && (_graphExpanded || _compareExpanded)
			? CurrentBranches()
			: [];
		root.Children(BuildBottomPanes(status, branches).DockBottom());
		root.Children(BuildChangesPane(status));
		return root;
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

			var pull = ToolbarButton("arrow-down", Strings.GitPull, () => _ = RunGitAsync(() => GitWriter.Pull(_cwd()), Strings.GitPullBusy));
			pull.IsEnabled = status.HasRemotes && status.HasUpstream && !status.Detached && !_busy && !_generating;
			right.Children(pull);

			var push = ToolbarButton("arrow-up", Strings.GitPush, () => _ = RunGitAsync(() => GitWriter.Push(_cwd()), Strings.GitPushBusy));
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
				ThemedGlyph(GlyphKind.ChevronDown, AppStyles.RoleFaint, 3));
		var tracking = new List<string>();
		if (status.Ahead > 0) tracking.Add($"↑{status.Ahead}");
		if (status.Behind > 0) tracking.Add($"↓{status.Behind}");
		if (tracking.Count > 0)
		{
			branchContent.Children(new TextBlock().Text(string.Join(' ', tracking))
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		Button? branchButton = null;
		branchButton = new Button()
			.Ghost()
			.Height(AppTheme.ControlHeightSm)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Content(branchContent);
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
			.Content(Label(Strings.GitNewBranch))
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
		if (!_changesExpanded)
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
				ThemedGlyph(_changesExpanded ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3),
				new TextBlock().Text(Strings.GitChanges).FontSize(AppTheme.FontSizeCaption)
					.FontWeight(FontWeight.SemiBold).PrimaryText());
		if (status.Changes.Count > 0)
		{
			headerContent.Children(new TextBlock().Text(status.Changes.Count.ToString())
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		var headerButton = new Button()
			.Ghost()
			.Height(28)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space2, 0))
			.OnClick(() =>
			{
				_changesExpanded = !_changesExpanded;
				Refresh();
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
			.Content(new TextBlock().Text(Strings.GitCommit).FontSize(AppTheme.FontSizeCaption).OnAccentText())
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
			body.Children(new ProgressBar()
				.IsIndeterminate(true)
				.Height(2)
				.Margin(new Thickness(0, AppTheme.Space1, 0, 0)));
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
		var enabled = _assistant is not null && staged > 0 && !_busy && !_generating && !busy;
		var tooltip = _assistant is null
			? Strings.GitSummary
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
		var directoryButton = new Button()
			.Ghost()
			.Height(26)
			.Padding(new Thickness(AppTheme.Space2 + row.Depth * 10, 0, AppTheme.Space1, 0))
			.OnClick(() =>
			{
				if (!_collapsedDirectories.Remove(row.Path))
				{
					_collapsedDirectories.Add(row.Path);
				}

				Refresh();
			})
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(5)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					ThemedGlyph(collapsed ? GlyphKind.ChevronRight : GlyphKind.ChevronDown, AppStyles.RoleFaint, 2.5),
					AppIcons.Icon("folder-open", AppStyles.RoleSubtle, 13),
					new TextBlock().Text(row.Label).FontSize(AppTheme.FontSizeCaption).MutedText()
						.MaxWidth(Math.Max(80, 220 - row.Depth * 10))
						.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis),
					new TextBlock().Text(row.Changes.Count.ToString()).FontSize(AppTheme.FontSizeMicro).FaintText()));

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
		var selected = _diffRequest?.MatchesWorkingTree(change.Path, stagedSide) == true;
		var letter = conflict ? "U" : change.StatusLetter(stagedSide);
		var letterRole = LetterRole(letter);

		var row = new Button()
			.StyleName(AppStyles.GhostButton)
			.Height(27)
			.Padding(new Thickness(AppTheme.Space2 + depth * 10, 0, AppTheme.Space1, 0))
			.OnClick(() => SelectDiff(change, stagedSide))
			.Content(new StackPanel()
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
						.TextTrimming(TextTrimming.CharacterEllipsis)))
			.HoverTip($"{change.Path}\n{change.Label}");
		row.Tag = change;

		var action = GlyphButton(
			stagedSide ? GlyphKind.Minus : GlyphKind.Plus,
			stagedSide ? Strings.GitUnstage : Strings.GitStage,
			() => ApplyResult(stagedSide ? GitWriter.Unstage(_cwd(), [change.Path]) : GitWriter.Stage(_cwd(), [change.Path])));
		action.IsEnabled = !_busy && !_generating;
		action.IsVisible = allowActions;
		action.Tag = change;

		var content = new DockPanel().LastChildFill().Children(action.DockRight(), row);
		return new Border()
			.CornerRadius(AppTheme.RadiusXs)
			.WithTheme((theme, border) => border.Background = selected
				? AppTheme.For(theme).CardBackgroundAlt
				: AppTheme.Transparent)
			.Child(content);
	}

	private void SelectDiff(GitChange change, bool stagedSide)
	{
		var untracked = change.IndexStatus == "?" && change.WorkTreeStatus == "?";
		_diffRequest = _diffRequest?.MatchesWorkingTree(change.Path, stagedSide) == true
			? null
			: GitDiffRequest.WorkingTree(change.Path, stagedSide, untracked);
		if (_diffRequest is not null)
		{
			_changesExpanded = true;
		}

		Refresh();
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
				_moreExpanded = !_moreExpanded;
				Refresh();
			})
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					ThemedGlyph(_moreExpanded ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3),
					new TextBlock().Text(Strings.GitMore).FontSize(AppTheme.FontSizeMicro)
						.FontWeight(FontWeight.SemiBold).MutedText()));

		var header = Separator(
			new Border()
				.Height(30)
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Child(headerButton),
			new Thickness(0, 1, 0, 0));

		if (!_moreExpanded)
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
				.MaxHeight(300)
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

		var resolved = ResolveAssistant();
		body.Children(new TextBlock().Text(Strings.GitSummary).FontSize(AppTheme.FontSizeMicro).MutedText());
		body.Children(new TextBlock()
			.Text(Strings.GitSummaryCurrent($"{resolved.Model.Provider}/{resolved.Model.Id}", Strings.ThinkingLevelName(resolved.Thinking)))
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
				.Content(new TextBlock().Text(Strings.GitInit).FontSize(AppTheme.FontSizeCaption).OnAccentText())
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
			Toasts.Show(View, Strings.GitSummaryBusy);
			return;
		}

		var status = GitStatusReader.Read(_cwd());
		if (!status.Changes.Any(static change => change.HasStaged && !change.IsConflict))
		{
			Toasts.Show(View, Strings.GitStageBeforeGenerate);
			return;
		}

		var diff = GitStatusReader.StagedDiffAll(_cwd(), GitCommitMessage.MaxDiffCharacters);
		if (!diff.Ok)
		{
			_lastFailureMessage = Strings.GitSummaryFailed;
			_lastFailureDetail = diff.Error;
			Toasts.Error(View, Strings.GitSummaryFailedReason(diff.Error ?? Strings.GitFailed));
			Refresh();
			return;
		}

		if (diff.Text.Trim().Length == 0)
		{
			Toasts.Show(View, Strings.GitStageBeforeGenerate);
			return;
		}

		var resolved = ResolveAssistant();
		var abort = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		_summaryAbort = abort;
		_generating = true;
		Refresh();

		string? generated = null;
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
			failure = Strings.GitSummaryTimeout;
		}
		catch (Exception ex)
		{
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
			if (failure is null && generated is { Length: > 0 } message)
			{
				_commitDraft = message;
				_focusCommitAfterBuild = true;
				Toasts.Success(View, Strings.GitSummaryGenerated);
			}
			else
			{
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

	private (Model Model, string Thinking) ResolveAssistant()
	{
		if (_assistant is not { } assistant)
		{
			throw new InvalidOperationException(Strings.GitSummaryNoModel);
		}

		var preference = DesktopPreference.Load();
		var current = assistant.CurrentModel();
		var models = assistant.Models();
		var model = current;
		if (preference.GitCommitModel is { Length: > 0 } fixedModel)
		{
			model = models.FirstOrDefault(candidate =>
				string.Equals($"{candidate.Provider}/{candidate.Id}", fixedModel, StringComparison.Ordinal)) ?? current;
		}

		var thinking = preference.GitCommitThinking is { Length: > 0 } fixedThinking
			? fixedThinking
			: assistant.CurrentThinking();
		return (model, thinking);
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
	}
}
