using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Git 面板的源代码管理图与比较更改。diff 请求仍由 <see cref="GitView"/> 主文件统一渲染。</summary>
public sealed partial class GitView
{
	private UIElement BuildBottomPanes(GitStatus status, IReadOnlyList<string> branches) =>
		new StackPanel().Spacing(0).Children(
			BuildGraphPane(branches),
			BuildComparePane(status, branches),
			BuildMorePane(status));

	// ----------------------------------------------------------------- 源代码管理图

	private void LoadGraph(IReadOnlyList<string> branches)
	{
		if (!_graphExpanded)
		{
			return;
		}

		if (_graphRef.Length > 0 && !branches.Contains(_graphRef, StringComparer.Ordinal))
		{
			_graphRef = string.Empty;
			_graphDirty = true;
		}

		if (_graphLoaded && !_graphDirty)
		{
			return;
		}

		var log = GitHistoryReader.Log(
			_cwd(),
			_graphPage,
			_graphRef.Length > 0 ? _graphRef : null,
			_graphRef.Length == 0);
		_graphCommits = log.Commits;
		_graphError = log.Error;
		_graphTotal = log.Error is null
			? GitHistoryReader.Count(_cwd(), _graphRef.Length > 0 ? _graphRef : null, _graphRef.Length == 0)
			: 0;
		_graphLoaded = true;
		_graphDirty = false;
	}

	private UIElement BuildGraphPane(IReadOnlyList<string> branches)
	{
		LoadGraph(branches);

		var title = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				ThemedGlyph(_graphExpanded ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3),
				new TextBlock().Text(Strings.GitGraph).FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.SemiBold).PrimaryText());
		if (_graphTotal > 0)
		{
			title.Children(new TextBlock().Text(_graphTotal.ToString())
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		var titleButton = new Button()
			.Ghost()
			.Height(28)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space2, 0))
			.OnClick(() =>
			{
				_graphExpanded = !_graphExpanded;
				Refresh();
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
			_commitDetails.Clear();
			_commitErrors.Clear();
			Refresh();
		}));

		var header = Separator(
			new Border()
				.Height(30)
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Child(new DockPanel().LastChildFill().Children(right.DockRight(), titleButton)),
			new Thickness(0, 1, 0, 0));
		if (!_graphExpanded)
		{
			return header;
		}

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
				var more = SmallTextButton(Strings.GitGraphLoadMore, () =>
				{
					_graphPage += 40;
					_graphDirty = true;
					Refresh();
				});
				more.HorizontalAlignment(HorizontalAlignment.Stretch);
				content.Children(more);
			}
		}

		return new StackPanel().Spacing(0)
			.Children(header, new ScrollViewer()
				.SmoothScrolling()
				.MaxHeight(180)
				.Content(content)
				.AutoVerticalScroll()
				.NoHorizontalScroll());
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
			EnsureCommitDetail(commit.Hash);
		}

		Refresh();
	}

	private void EnsureCommitDetail(string hash)
	{
		if (_commitDetails.ContainsKey(hash) || _commitErrors.ContainsKey(hash))
		{
			return;
		}

		var detail = GitHistoryReader.Detail(_cwd(), hash);
		if (detail is null)
		{
			_commitErrors[hash] = Strings.GitCommitDetailUnavailable;
		}
		else
		{
			_commitDetails[hash] = detail;
		}
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
		foreach (var file in detail.Files)
		{
			body.Children(BuildCommitFileRow(detail.Commit, parent, file));
		}

		return body;
	}

	private UIElement BuildCommitFileRow(GitCommitEntry commit, string? parent, GitCommitFile file)
	{
		var selected = _diffRequest?.MatchesCommit(commit.Hash, file.Path) == true;
		var row = new Button()
			.Ghost()
			.Height(24)
			.Padding(new Thickness(0, 0, AppTheme.Space1, 0))
			.OnClick(() =>
			{
				_diffRequest = GitDiffRequest.Commit(commit.Hash, parent, file.Path, file.OriginalPath);
				_changesExpanded = true;
				Refresh();
			})
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new TextBlock().Text(file.Letter).FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeMicro).Width(12)
						.WithTheme((theme, text) => text.Foreground = RoleColor(AppTheme.For(theme), LetterRole(file.Letter))),
					new TextBlock().Text(file.FileName).FontSize(AppTheme.FontSizeMicro)
						.MutedText().MaxWidth(160).TextWrapping(TextWrapping.NoWrap)
						.TextTrimming(TextTrimming.CharacterEllipsis)))
			.HoverTip(file.Path);

		return new Border()
			.CornerRadius(AppTheme.RadiusXs)
			.WithTheme((theme, border) => border.Background = selected
				? AppTheme.For(theme).CardBackgroundAlt
				: AppTheme.Transparent)
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
		_expandedCommits.Clear();
		_commitDetails.Clear();
		_commitErrors.Clear();
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

	// ----------------------------------------------------------------- 比较更改

	private UIElement BuildComparePane(GitStatus status, IReadOnlyList<string> branches)
	{
		var title = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				ThemedGlyph(_compareExpanded ? GlyphKind.ChevronDown : GlyphKind.ChevronRight, AppStyles.RoleSubtle, 3),
				new TextBlock().Text(Strings.GitCompare).FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.SemiBold).MutedText());
		if (_compareResult is { Files.Count: > 0 } result)
		{
			title.Children(new TextBlock().Text(result.Files.Count.ToString())
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		var titleButton = new Button()
			.Ghost()
			.Height(28)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space2, 0))
			.OnClick(() =>
			{
				_compareExpanded = !_compareExpanded;
				Refresh();
			})
			.Content(title);

		var header = Separator(
			new Border()
				.Height(30)
				.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
				.Child(titleButton),
			new Thickness(0, 1, 0, 0));
		if (!_compareExpanded)
		{
			return header;
		}

		var body = new StackPanel().Spacing(AppTheme.Space1)
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space2));
		if (branches.Count < 2)
		{
			body.Children(new TextBlock().Text(Strings.GitCompareNeedsBranches)
				.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap));
			return new StackPanel().Children(header, body);
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
			_compareResult = null;
			_compareError = null;
		};
		var targetPicker = ComparePicker(branches, _compareTarget);
		targetPicker.SelectionChanged += _ =>
		{
			if (targetPicker.SelectedIndex < 0 || targetPicker.SelectedIndex >= branches.Count)
			{
				return;
			}

			_compareTarget = branches[targetPicker.SelectedIndex];
			_compareResult = null;
			_compareError = null;
		};

		var pickers = new Grid().Columns("*,*").Spacing(AppTheme.Space1).Children(
			CompareField(Strings.GitCompareBase, basePicker).Column(0),
			CompareField(Strings.GitCompareTarget, targetPicker).Column(1));
		var compare = new Button()
			.Primary()
			.Height(AppTheme.ControlHeightSm)
			.Content(new TextBlock().Text(Strings.GitCompareAction)
				.FontSize(AppTheme.FontSizeCaption).OnAccentText())
			.OnClick(RunCompare);
		body.Children(pickers, compare);

		if (_compareError is { Length: > 0 } error)
		{
			body.Children(new TextBlock().Text(Strings.GitCompareFailedReason(error))
				.FontSize(AppTheme.FontSizeMicro).TextWrapping(TextWrapping.Wrap)
				.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Error));
		}
		else if (_compareResult is { } comparison)
		{
			body.Children(new TextBlock()
				.Text(Strings.GitAheadBehind(comparison.Ahead, comparison.Behind))
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).FaintText());
			if (comparison.Files.Count == 0)
			{
				body.Children(new TextBlock().Text(Strings.GitCompareEmpty)
					.FontSize(AppTheme.FontSizeMicro).FaintText());
			}
			else
			{
				var files = new StackPanel().Spacing(0);
				foreach (var file in comparison.Files)
				{
					files.Children(BuildCompareFileRow(comparison, file));
				}

				body.Children(new ScrollViewer()
					.SmoothScrolling()
					.MaxHeight(140)
					.Content(files)
					.AutoVerticalScroll()
					.NoHorizontalScroll());
			}
		}
		else
		{
			body.Children(new TextBlock().Text(Strings.GitCompareHint)
				.FontSize(AppTheme.FontSizeMicro).FaintText());
		}

		return new StackPanel().Spacing(0).Children(header, body);
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

	private void RunCompare()
	{
		var comparison = GitHistoryReader.Compare(_cwd(), _compareBase, _compareTarget);
		_compareResult = comparison;
		_compareResultBase = _compareBase;
		_compareResultTarget = _compareTarget;
		_compareError = comparison.Error;
		Refresh();
	}

	private UIElement BuildCompareFileRow(GitBranchComparison result, GitCommitFile file)
	{
		var selected = _diffRequest?.MatchesCompare(result.BaseHash, result.TargetHash, file.Path) == true;
		var label = $"{_compareResultBase}...{_compareResultTarget}";
		var row = new Button()
			.Ghost()
			.Height(24)
			.Padding(new Thickness(0, 0, AppTheme.Space1, 0))
			.OnClick(() =>
			{
				_diffRequest = GitDiffRequest.Compare(
					result.BaseHash, result.TargetHash, label, file.Path, file.OriginalPath);
				_changesExpanded = true;
				Refresh();
			})
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new TextBlock().Text(file.Letter).FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeMicro).Width(12)
						.WithTheme((theme, text) => text.Foreground = RoleColor(AppTheme.For(theme), LetterRole(file.Letter))),
					new TextBlock().Text(file.FileName).FontSize(AppTheme.FontSizeMicro)
						.MutedText().MaxWidth(240).TextWrapping(TextWrapping.NoWrap)
						.TextTrimming(TextTrimming.CharacterEllipsis)))
			.HoverTip(file.Path);

		return new Border()
			.CornerRadius(AppTheme.RadiusXs)
			.WithTheme((theme, border) => border.Background = selected
				? AppTheme.For(theme).CardBackgroundAlt
				: AppTheme.Transparent)
			.Child(row);
	}

	private static string LetterRole(string letter) => letter switch
	{
		"A" => AppStyles.RoleSuccess,
		"D" or "U" => AppStyles.RoleError,
		"R" or "C" or "M" or "T" => AppStyles.RoleInfo,
		_ => AppStyles.RoleFaint,
	};
}
