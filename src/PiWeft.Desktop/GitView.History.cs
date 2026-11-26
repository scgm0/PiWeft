using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;

namespace PiWeft.Desktop;

public sealed partial class GitView
{
	private void LoadGraph(IReadOnlyList<string> branches)
	{
		if (!_panes.GraphOpen)
		{
			return;
		}

		if (_graphRef.Length > 0 && !branches.Contains(_graphRef, StringComparer.Ordinal))
		{
			_graphRef = string.Empty;
			_graphDirty = true;
			_graphVersion++;
			_graphLoading = false;
		}

		if (_graphLoaded && !_graphDirty)
		{
			return;
		}

		// The graph runs `git log` and a second `git rev-list --count`; on a large repository that
		// is exactly the work that made opening the panel freeze. Load it off the UI thread and
		// render the cached/empty state first. Headless tests keep the synchronous path.
		// 合计只在状态刷新/换筛选后重新数：翻页之间提交总数不会变。
		var count = _graphCountDirty || _graphTotal == 0;
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			var (log, total) = ReadGraph(_cwd(), _graphPage, _graphRef, count);
			ApplyGraph(log, total);
			return;
		}

		if (_graphLoading)
		{
			return;
		}

		_graphLoading = true;
		var version = ++_graphVersion;
		var cwd = _cwd();
		var page = _graphPage;
		var graphRef = _graphRef;
		_ = LoadGraphAsync(cwd, page, graphRef, count, version, dispatcher);
	}

	private async Task LoadGraphAsync(
		string cwd,
		int page,
		string graphRef,
		bool count,
		int version,
		IDispatcher dispatcher)
	{
		var (log, total) = await Task.Run(() => ReadGraph(cwd, page, graphRef, count)).ConfigureAwait(false);

		void Complete()
		{
			if (_disposed || version != _graphVersion)
			{
				return;
			}

			_graphLoading = false;
			ApplyGraph(log, total);
			RebuildGraph();
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	/// 读图：异常不能漏出 fire-and-forget 任务，否则 _graphLoading 会一直挂着（下一次状态刷新才自愈）。
	private static (GitLogResult Log, int? Total) ReadGraph(string cwd, int page, string graphRef, bool count)
	{
		try
		{
			var log = GitHistoryReader.Log(cwd, page, graphRef.Length > 0 ? graphRef : null, graphRef.Length == 0);
			int? total = count && log.Error is null
				? GitHistoryReader.Count(cwd, graphRef.Length > 0 ? graphRef : null, graphRef.Length == 0)
				: null;
			return (log, total);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<GitView>(), "Read Git graph", ex);
			return (new GitLogResult([], Strings.GitUnavailable), null);
		}
	}

	private void ApplyGraph(GitLogResult log, int? total)
	{
		_graphCommits = log.Commits;
		_graphError = log.Error;
		if (total is { } counted)
		{
			_graphTotal = counted;
			_graphCountDirty = false;
		}

		_graphLoaded = true;
		_graphDirty = false;
	}

	private UIElement BuildGraphHeader(IReadOnlyList<string> branches)
	{
		var title = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				ThemedGlyph(_panes.GraphOpen ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3),
				new TextBlock().Text(Strings.GitGraph).FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.SemiBold).PrimaryText());
		if (_graphTotal > 0)
		{
			title.Children(new TextBlock().Text(_graphTotal.ToString())
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		title.IsHitTestVisible = false;

		var titleButton = new Button()
			.Ghost()
			.Height(28)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space2, 0))
			.OnClick(() =>
			{
				_panes = _panes.WithGraph(_panes.Graph, !_panes.GraphOpen);
				ApplyPaneHeights();
				SavePanes();
				RebuildGraph();
			})
			.Content(title);

		var right = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(1)
			.VerticalAlignment(VerticalAlignment.Center);
		if (branches.Count > 0)
		{
			Button? filter = null;
			filter = new Button()
				.Ghost()
				.Height(22)
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Content(new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(3)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(
						new TextBlock().Text(_graphRef.Length > 0 ? _graphRef : Strings.GitGraphFilterAll)
							.FontSize(AppTheme.FontSizeMicro).MutedText()
							.MaxWidth(80).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis),
						ThemedGlyph(GlyphKind.ChevronDown, AppStyles.RoleFaint, 2.5)))
				.HoverTip(Strings.GitGraphFilter)
				.OnClick(() => ShowGraphFilterMenu(filter!, branches));
			right.Children(filter);
		}

		right.Children(ToolbarButton("refresh-cw", Strings.RefreshGit, () =>
		{
			_graphPage = 40;
			_branchesDirty = true;
			_graphDirty = true;
			ResetCommitDetails();
			Refresh();
		}));

		return Separator(
			new Border()
				.Height(30)
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Child(new DockPanel().LastChildFill().Children(right.DockRight(), titleButton)),
			new Thickness(0, 1, 0, 0));
	}

	/// 图的内容：错误 / 空态 / 提交行 / 「加载更多」。滚动视口由外面持有，这里只给内容。
	private UIElement BuildGraphContent()
	{
		var content = new StackPanel().Spacing(0);
		if (_graphError is { Length: > 0 } error)
		{
			content.Children(new TextBlock().Text(error)
				.FontSize(AppTheme.FontSizeMicro).TextWrapping(TextWrapping.Wrap)
				.Margin(new Thickness(AppTheme.Space2, AppTheme.Space2))
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Error));
		}
		else if (_graphCommits.Count == 0)
		{
			content.Children(new TextBlock()
				.Text(_graphRef.Length > 0 ? Strings.GitGraphNoFilteredCommits : Strings.GitGraphEmpty)
				.FontSize(AppTheme.FontSizeMicro).FaintText()
				.Margin(new Thickness(AppTheme.Space2, AppTheme.Space2)));
		}
		else
		{
			foreach (var row in GitCommitGraph.Build(_graphCommits))
			{
				content.Children(BuildCommitRow(row));
			}

			if (_graphCommits.Count >= _graphPage &&
				(_graphTotal == 0 || _graphCommits.Count < _graphTotal))
			{
				var more = SmallTextButton(Strings.GitLoadMore, () =>
				{
					_graphPage += 40;
					_graphDirty = true;
					// 翻页不重读仓库状态：只让图自己多取一页。
					RebuildGraph();
				});
				more.HorizontalAlignment(HorizontalAlignment.Stretch);
				content.Children(more);
			}
		}

		return content;
	}

	private UIElement BuildCommitRow(GitGraphRow row)
	{
		var commit = row.Commit;
		var expanded = _expandedCommits.Contains(commit.Hash);
		var current = commit.IsHead;
		var laneWidth = GitCommitGraph.Width(row);
		var lanes = new CommitGraphLanes();
		lanes.SetRow(row, current);
		lanes.Width(laneWidth).Height(GitCommitGraph.RowHeight);

		var subject = commit.Subject.Length > 0 ? commit.Subject : Strings.GitNoCommitMessage;
		var subjectPanel = new DockPanel().LastChildFill().Children(
			ThemedGlyph(expanded ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3.5).DockLeft(),
			new TextBlock().Text(subject).FontSize(AppTheme.FontSizeCaption).MutedText()
				.MaxWidth(200).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis));

		var info = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(4)
			.VerticalAlignment(VerticalAlignment.Center);
		if (PrimaryRef(commit.Refs) is { } reference)
		{
			info.Children(ReferenceChip(reference.Label, reference.Role));
		}

		info.Children(new TextBlock()
			.Text(commit.AuthorName)
			.FontSize(AppTheme.FontSizeMicro)
			.FaintText()
			.MaxWidth(64)
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis));

		var grid = new Grid().Columns("auto,*,auto").Spacing(4).Children(
			lanes.Column(0),
			subjectPanel.Column(1),
			info.Column(2));
		grid.IsHitTestVisible = false;
		var button = new Button()
			.Ghost()
			.Height(28)
			.Padding(new Thickness(0, 0, 4, 0))
			.OnClick(() => ToggleCommit(commit))
			.Content(grid)
			.HoverTip($"{commit.ShortHash} · {commit.AuthorName}\n" +
				$"{GitHistoryReader.RelativeTime(commit.AuthorDate, DateTimeOffset.Now)} · " +
				$"{commit.AuthorDate.ToLocalTime():yyyy-MM-dd HH:mm}\n{subject}");

		var body = new StackPanel().Spacing(0).Children(
			new Border()
				.CornerRadius(AppTheme.RadiusXs)
				.WithTheme((theme, border) => border.Background = current
					? AppTheme.For(theme).CardBackgroundAlt
					: AppTheme.Transparent)
				.Child(button));

		if (expanded)
		{
			body.Children(BuildCommitDetail(commit, laneWidth));
		}

		return body;
	}

	private void ToggleCommit(GitCommitEntry commit)
	{
		if (!_expandedCommits.Remove(commit.Hash))
		{
			_expandedCommits.Add(commit.Hash);
			EnsureCommitDetail(commit);
		}

		// 只是展开/收起详情，不需要重读仓库状态。
		RebuildGraph();
	}

	/// 详情文件列表可能要读一次 <c>git show --name-status</c>；大提交上那是秒级操作，放线程池。
	/// 无窗口测试没有调度器，保持同步路径。
	private void EnsureCommitDetail(GitCommitEntry commit)
	{
		var hash = commit.Hash;
		if (_commitDetails.ContainsKey(hash) || _commitErrors.ContainsKey(hash) || !_commitLoading.Add(hash))
		{
			return;
		}

		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			ApplyCommitDetail(hash, ReadCommitDetail(_cwd(), hash), _commitDetailVersion);
			return;
		}

		var cwd = _cwd();
		_ = LoadCommitDetailAsync(cwd, hash, _commitDetailVersion, dispatcher);
	}

	private async Task LoadCommitDetailAsync(string cwd, string hash, int version, IDispatcher dispatcher)
	{
		var detail = await Task.Run(() => ReadCommitDetail(cwd, hash)).ConfigureAwait(false);

		void Complete()
		{
			if (_disposed || version != _commitDetailVersion)
			{
				return;
			}

			ApplyCommitDetail(hash, detail, version);
			RebuildGraph();
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	private void ApplyCommitDetail(string hash, GitCommitDetail? detail, int version)
	{
		if (version != _commitDetailVersion)
		{
			return;
		}

		_commitLoading.Remove(hash);
		if (detail is null)
		{
			_commitErrors[hash] = Strings.GitCommitDetailUnavailable;
		}
		else
		{
			_commitDetails[hash] = detail;
		}
	}

	/// 刷新图、换筛选时丢掉旧的详情；在途的读取按版本号丢弃。
	/// 展开集合一起清：详情没了还保持展开的话，那一行只剩一块空白，还得手动收起再展开。
	private void ResetCommitDetails()
	{
		_commitDetailVersion++;
		_commitLoading.Clear();
		_commitDetails.Clear();
		_commitErrors.Clear();
		_commitFilePages.Clear();
		_expandedCommits.Clear();
	}

	private UIElement BuildCommitDetail(GitCommitEntry commit, double laneWidth)
	{
		var body = new StackPanel()
			.Spacing(2)
			.Margin(new Thickness(Math.Max(20, laneWidth + 8), 0, AppTheme.Space2, AppTheme.Space1));
		if (_commitErrors.TryGetValue(commit.Hash, out var error))
		{
			body.Children(new TextBlock().Text(error).FontSize(AppTheme.FontSizeMicro)
				.TextWrapping(TextWrapping.Wrap)
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Error));
			return body;
		}

		if (_commitLoading.Contains(commit.Hash))
		{
			body.Children(new TextBlock().Text(Strings.GitCommitFilesLoading)
				.FontSize(AppTheme.FontSizeMicro).FaintText());
			return body;
		}

		if (!_commitDetails.TryGetValue(commit.Hash, out var detail))
		{
			return body;
		}

		var full = detail.Commit.FullMessage.Length > 0 ? detail.Commit.FullMessage : detail.Commit.Subject;
		if (full.Length > 0 && (full.Contains('\n') || !string.Equals(full, detail.Commit.Subject, StringComparison.Ordinal)))
		{
			body.Children(new TextBlock().Text(full).FontSize(AppTheme.FontSizeMicro)
				.MutedText().TextWrapping(TextWrapping.Wrap));
		}

		body.Children(new TextBlock()
			.Text($"{detail.Commit.AuthorName} <{detail.Commit.AuthorEmail}> · " +
				$"{detail.Commit.AuthorDate.ToLocalTime():yyyy-MM-dd HH:mm} · {detail.Commit.ShortHash}")
			.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro)
			.FaintText().TextWrapping(TextWrapping.Wrap));
		body.Children(new TextBlock()
			.Text(Strings.GitCommitFiles(detail.Files.Count, detail.Insertions, detail.Deletions))
			.FontSize(AppTheme.FontSizeMicro).FaintText());

		if (detail.Files.Count == 0)
		{
			body.Children(new TextBlock().Text(Strings.GitNoCommitFiles)
				.FontSize(AppTheme.FontSizeMicro).FaintText());
			return body;
		}

		var parent = detail.Commit.Parents.FirstOrDefault();
		var page = Math.Max(CommitFilePageSize, _commitFilePages.GetValueOrDefault(commit.Hash, CommitFilePageSize));
		var shown = Math.Min(detail.Files.Count, page);
		var rows = new StackPanel().Spacing(2);
		for (var index = 0; index < shown; index++)
		{
			rows.Children(BuildCommitFileRow(detail.Commit, parent, detail.Files[index]));
		}

		body.Children(rows);
		if (shown < detail.Files.Count)
		{
			var hash = commit.Hash;
			var loaded = shown;
			Button? more = null;
			more = SmallTextButton(Strings.GitLoadMore, () =>
			{
				// 只追加下一页：重建整节会把滚动位置丢回顶部。
				var next = Math.Min(detail.Files.Count, loaded + CommitFilePageSize);
				for (var index = loaded; index < next; index++)
				{
					rows.Children(BuildCommitFileRow(detail.Commit, parent, detail.Files[index]));
				}

				loaded = next;
				_commitFilePages[hash] = next;
				if (next >= detail.Files.Count)
				{
					more!.IsVisible = false;
				}
			});
			more.HorizontalAlignment(HorizontalAlignment.Stretch);
			body.Children(more);
		}

		return body;
	}

	private UIElement BuildCommitFileRow(GitCommitEntry commit, string? parent, GitCommitFile file) =>
		FileLikeRow(file, () => ShowDiff(GitDiffRequest.Commit(commit.Hash, parent, file.Path, file.OriginalPath)));

	private UIElement FileLikeRow(GitCommitFile file, Action onClick)
	{
		// 与更改列表同一套排版：文件名占剩余宽度，行尾停靠增删数与五格比例条。
		var rowContent = new DockPanel().LastChildFill();
		if (BuildDiffStat(file.DiffStat, AppTheme.FontSizeMicro) is { } stat)
		{
			rowContent.Children(stat.Margin(new Thickness(8, 0, 2, 0)).DockRight());
		}

		rowContent.Children(
			new TextBlock().Text(file.Letter).FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeMicro).Width(12)
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, text) => text.Foreground = RoleColor(AppTheme.For(theme), LetterRole(file.Letter)))
				.DockLeft(),
			new TextBlock().Text(file.FileName).FontSize(AppTheme.FontSizeMicro)
				.MutedText().MaxWidth(160).TextWrapping(TextWrapping.NoWrap)
				.TextTrimming(TextTrimming.CharacterEllipsis)
				.VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(6, 0, 0, 0)));
		rowContent.IsHitTestVisible = false;

		var row = new Button()
			.Ghost()
			.Height(24)
			.Padding(new Thickness(0, 0, AppTheme.Space1, 0))
			.Content(rowContent)
			.OnClick(onClick)
			.HoverTip(file.Path);

		return new Border()
			.CornerRadius(AppTheme.RadiusXs)
			.Child(row);
	}

	private void ShowGraphFilterMenu(Button owner, IReadOnlyList<string> branches)
	{
		var checkIcon = new IconTemplate(size => new GlyphElement()
			.Kind(GlyphKind.CheckMark)
			.GlyphSize(4)
			.Foreground(AppTheme.Accent)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center));
		var menu = new ContextMenu();
		var all = new Command("pi.git.graph.all", Strings.GitGraphFilterAll);
		owner.Commands.Register(all, () => SelectGraphRef(string.Empty));
		menu.Items.Add(new MenuItem(all) { Icon = _graphRef.Length == 0 ? checkIcon : null });
		for (var index = 0; index < branches.Count; index++)
		{
			var branch = branches[index];
			var command = new Command($"pi.git.graph.ref.{index}", branch);
			owner.Commands.Register(command, () => SelectGraphRef(branch));
			menu.Items.Add(new MenuItem(command)
			{
				Icon = string.Equals(_graphRef, branch, StringComparison.Ordinal) ? checkIcon : null,
			});
		}

		menu.Placement = MenuPlacement.Below;
		menu.MaxMenuHeight = 320;
		menu.Show(owner);
	}

	private void SelectGraphRef(string reference)
	{
		if (string.Equals(_graphRef, reference, StringComparison.Ordinal))
		{
			return;
		}

		_graphRef = reference;
		_graphPage = 40;
		_graphDirty = true;
		ResetCommitDetails();
		Refresh();
	}

	private static (string Label, string Role)? PrimaryRef(IReadOnlyList<string> refs)
	{
		var head = refs.FirstOrDefault(static reference => reference.StartsWith("HEAD ->", StringComparison.Ordinal));
		if (head is { Length: > 8 })
		{
			return (head["HEAD -> ".Length..].Trim(), AppStyles.RoleInfo);
		}

		var tag = refs.FirstOrDefault(static reference => reference.StartsWith("tag: ", StringComparison.Ordinal));
		if (tag is { Length: > 5 })
		{
			return (tag[5..].Trim(), AppStyles.RoleAccent);
		}

		var first = refs.FirstOrDefault(static reference => !reference.StartsWith("HEAD", StringComparison.Ordinal));
		return first is { Length: > 0 } ? (first, AppStyles.RoleMuted) : null;
	}

	private static UIElement ReferenceChip(string label, string role) => new Border()
		.CornerRadius(AppTheme.RadiusXs)
		.BorderThickness(1)
		.Padding(new Thickness(5, 0, 5, 0))
		.VerticalAlignment(VerticalAlignment.Center)
		.MaxWidth(78)
		.WithTheme((theme, border) => border.BorderBrush = RoleColor(AppTheme.For(theme), role))
		.Child(new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro)
			.MaxWidth(68).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
			.WithTheme((theme, text) => text.Foreground = RoleColor(AppTheme.For(theme), role)));


	private UIElement BuildComparePane(GitStatus status, IReadOnlyList<string> branches)
	{
		var title = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				ThemedGlyph(_panes.CompareOpen ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3),
				new TextBlock().Text(Strings.GitCompare).FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.SemiBold).MutedText());
		if (_compareResult is { Files.Count: > 0 } counted)
		{
			title.Children(new TextBlock().Text(counted.Files.Count.ToString())
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		title.IsHitTestVisible = false;

		var titleButton = new Button()
			.Ghost()
			.Height(28)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space2, 0))
			.OnClick(() =>
			{
				_panes = _panes.WithCompare(_panes.Compare, !_panes.CompareOpen);
				ApplyPaneHeights();
				SavePanes();
				RebuildCompare();
			})
			.Content(title);

		var header = Separator(
			new Border()
				.Height(30)
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Child(titleButton),
			new Thickness(0, 1, 0, 0));
		if (!_panes.CompareOpen)
		{
			return header;
		}

		// 上排（基准/目标/比较/状态行）停靠顶部，文件列表填剩余高度：节高固定时列表自带
		// 视口滚动，抽高那一节时也一起长，不会把列表和滚动条一起裁掉。
		var body = new DockPanel().LastChildFill()
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space2));
		if (branches.Count < 2)
		{
			// 装在 StackPanel 里再停靠：直接当填充子级排会被拉到整节高，文字画在中间。
			body.Children(new StackPanel().Children(new TextBlock().Text(Strings.GitCompareNeedsBranches)
				.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap)).DockTop());
			return new DockPanel().LastChildFill().Children(header.DockTop(), body);
		}

		NormalizeCompareSelection(status, branches);
		var basePicker = ComparePicker(branches, _compareBase);
		basePicker.SelectionChanged += _ =>
		{
			if (basePicker.SelectedIndex < 0 || basePicker.SelectedIndex >= branches.Count)
			{
				return;
			}

			_compareBase = branches[basePicker.SelectedIndex];
			DropCompareResult();
		};
		var targetPicker = ComparePicker(branches, _compareTarget);
		targetPicker.SelectionChanged += _ =>
		{
			if (targetPicker.SelectedIndex < 0 || targetPicker.SelectedIndex >= branches.Count)
			{
				return;
			}

			_compareTarget = branches[targetPicker.SelectedIndex];
			DropCompareResult();
		};

		var pickers = new Grid().Columns("*,*").Spacing(AppTheme.Space1).Children(
			CompareField(Strings.GitCompareBase, basePicker).Column(0),
			CompareField(Strings.GitCompareTarget, targetPicker).Column(1));
		var compare = new Button()
			.Primary()
			.Height(AppTheme.ControlHeightSm)
			.Content(new TextBlock().Text(Strings.GitCompareAction)
				.FontSize(AppTheme.FontSizeCaption))
			.OnClick(RunCompare);
		compare.IsEnabled = !_compareLoading;
		var top = new StackPanel().Spacing(AppTheme.Space1).Children(pickers, compare);
		body.Children(top.DockTop());
		UIElement? fileList = null;

		if (_compareLoading)
		{
			top.Children(new TextBlock().Text(Strings.GitCompareRunning)
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}
		else if (_compareError is { Length: > 0 } error)
		{
			top.Children(new TextBlock().Text(Strings.GitCompareFailedReason(error))
				.FontSize(AppTheme.FontSizeMicro).TextWrapping(TextWrapping.Wrap)
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Error));
		}
		else if (_compareResult is { } comparison)
		{
			top.Children(new TextBlock()
				.Text(Strings.GitAheadBehind(comparison.Ahead, comparison.Behind))
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).FaintText());
			if (comparison.Files.Count == 0)
			{
				top.Children(new TextBlock().Text(Strings.GitCompareEmpty)
					.FontSize(AppTheme.FontSizeMicro).FaintText());
			}
			else
			{
				fileList = BuildCompareFileList(comparison);
			}
		}
		else
		{
			top.Children(new TextBlock().Text(Strings.GitCompareHint)
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		if (fileList is not null)
		{
			body.Children(fileList);
		}

		return new DockPanel().LastChildFill().Children(header.DockTop(), body);
	}

	/// 比较结果的文件列表：只建前几十行，剩下的走「加载更多」；视口由外层给的高度决定。
	private UIElement BuildCompareFileList(GitBranchComparison comparison)
	{
		var label = _compareResultBase + "..." + _compareResultTarget;
		var rows = new StackPanel().Spacing(0);
		var shown = Math.Min(comparison.Files.Count, Math.Max(CompareFilePageSize, _comparePage));
		for (var index = 0; index < shown; index++)
		{
			rows.Children(BuildCompareFileRow(comparison, comparison.Files[index], label));
		}

		var files = new StackPanel().Spacing(0).Children(rows);
		if (shown < comparison.Files.Count)
		{
			var loaded = shown;
			Button? more = null;
			more = SmallTextButton(Strings.GitLoadMore, () =>
			{
				// 只追加下一页：重建整节会把滚动位置丢回顶部。
				var next = Math.Min(comparison.Files.Count, loaded + CompareFilePageSize);
				for (var index = loaded; index < next; index++)
				{
					rows.Children(BuildCompareFileRow(comparison, comparison.Files[index], label));
				}

				loaded = next;
				_comparePage = next;
				if (next >= comparison.Files.Count)
				{
					more!.IsVisible = false;
				}
			});
			more.HorizontalAlignment(HorizontalAlignment.Stretch);
			files.Children(more);
		}

		return new ScrollViewer()
			.SmoothScrolling()
			.Margin(new Thickness(0, AppTheme.Space1, 0, 0))
			// 右侧给滚动条留出浮层宽度。
			.Padding(new Thickness(0, 0, 10, 0))
			.Content(files)
			.AutoVerticalScroll()
			.NoHorizontalScroll();
	}

	private void NormalizeCompareSelection(GitStatus status, IReadOnlyList<string> branches)
	{
		if (!branches.Contains(_compareTarget, StringComparer.Ordinal))
		{
			_compareTarget = status.Branch.Length > 0 && branches.Contains(status.Branch, StringComparer.Ordinal)
				? status.Branch
				: branches[0];
		}

		if (!branches.Contains(_compareBase, StringComparer.Ordinal) ||
			string.Equals(_compareBase, _compareTarget, StringComparison.Ordinal))
		{
			_compareBase = branches.FirstOrDefault(branch => !string.Equals(branch, _compareTarget, StringComparison.Ordinal))
				?? _compareTarget;
		}
	}

	private static ComboBox ComparePicker(IReadOnlyList<string> branches, string selected)
	{
		var picker = new ComboBox()
			.ItemsSource(ItemsView.Create(branches.ToArray()))
			.Height(24)
			.FontSize(AppTheme.FontSizeCaption);
		picker.SelectedIndex = Math.Max(0, branches.ToList().IndexOf(selected));
		return picker;
	}

	private static UIElement CompareField(string label, UIElement control) => new StackPanel().Spacing(2).Children(
		new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).SubtleText(),
		control);

	/// 比较两个分支要读一次完整差异：大仓库上放 UI 线程会冻住面板，改到线程池。
	private void RunCompare()
	{
		if (_compareLoading)
		{
			return;
		}

		// 下拉允许把两侧选成同一个分支：先按同一套规则归一，再捕获，免得跑出 X...X 的空比较。
		var branches = CurrentBranches();
		if (branches.Count >= 2)
		{
			NormalizeCompareSelection(_lastStatus!, branches);
		}

		var baseRef = _compareBase;
		var targetRef = _compareTarget;
		_compareResult = null;
		_compareError = null;
		_comparePage = CompareFilePageSize;
		_compareLoading = true;

		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			ApplyCompare(baseRef, targetRef, ReadCompare(_cwd(), baseRef, targetRef));
			return;
		}

		RebuildCompare();
		var version = ++_compareVersion;
		var cwd = _cwd();
		_ = LoadCompareAsync(cwd, baseRef, targetRef, version, dispatcher);
	}

	private async Task LoadCompareAsync(string cwd, string baseRef, string targetRef, int version, IDispatcher dispatcher)
	{
		var comparison = await Task.Run(() => ReadCompare(cwd, baseRef, targetRef)).ConfigureAwait(false);

		void Complete()
		{
			if (_disposed || version != _compareVersion)
			{
				return;
			}

			ApplyCompare(baseRef, targetRef, comparison);
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	private void ApplyCompare(string baseRef, string targetRef, GitBranchComparison comparison)
	{
		_compareLoading = false;
		_compareResult = comparison;
		_compareResultBase = baseRef;
		_compareResultTarget = targetRef;
		_compareError = comparison.Error;
		_comparePage = CompareFilePageSize;
		RebuildCompare();
	}

	/// 换基准/目标时丢掉旧结果与在途读取：回来的那份已经对不上新的选择。
	private void DropCompareResult()
	{
		_compareVersion++;
		_compareLoading = false;
		_compareResult = null;
		_compareError = null;
		// 调用点就在被重建的下拉自身的事件里：这一拍重画会把下拉连同事件源一起换掉，推迟一拍。
		if (Application.IsRunning && Application.Current.Dispatcher is { } dispatcher)
		{
			dispatcher.BeginInvoke(() =>
			{
				if (!_disposed)
				{
					RebuildCompare();
				}
			});
		}
		else
		{
			RebuildCompare();
		}
	}

	private UIElement BuildCompareFileRow(GitBranchComparison result, GitCommitFile file, string label) =>
		FileLikeRow(file, () => ShowDiff(GitDiffRequest.Compare(
			result.BaseHash, result.TargetHash, label, file.Path, file.OriginalPath)));

	/// 读提交详情：解析异常不能漏出 fire-and-forget 任务，否则详情占位会一直停在「正在读取」。
	private static GitCommitDetail? ReadCommitDetail(string cwd, string hash)
	{
		try
		{
			return GitHistoryReader.Detail(cwd, hash);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<GitView>(), "Read commit detail", ex);
			return null;
		}
	}

	/// 读分支比较：同 ReadCommitDetail，失败按「比较失败」展示并收尾。
	private static GitBranchComparison ReadCompare(string cwd, string baseRef, string targetRef)
	{
		try
		{
			return GitHistoryReader.Compare(cwd, baseRef, targetRef);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<GitView>(), "Compare branches", ex);
			return new GitBranchComparison([], 0, 0, string.Empty, string.Empty, Strings.GitCompareFailed);
		}
	}

	private static string LetterRole(string letter) => letter switch
	{
		"A" => AppStyles.RoleSuccess,
		"D" or "U" => AppStyles.RoleError,
		"R" or "C" or "M" or "T" => AppStyles.RoleInfo,
		_ => AppStyles.RoleFaint,
	};
}
