using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// Git 面板：分支切换/新建、暂存与取消暂存、提交，以及每个文件的 diff。
/// 写操作走 <see cref="GitWriter"/>（参数列表传参、有界输出、命令超时）。
/// </summary>
public sealed class GitView
{
	private readonly Func<string> _cwd;
	private readonly StackPanel _content = new();
	private readonly TextBlock _summary = new();
	private string? _selectedPath;

	/// <summary>跨刷新保留的草稿，避免提交信息在状态刷新时被清掉。</summary>
	private string _commitDraft = string.Empty;
	private string _branchDraft = string.Empty;

	/// <summary>上一次失败操作里 git 的原始输出。分类后的提示往往很概括，原文得留在面板里可查。</summary>
	private string? _lastFailureDetail;

	public GitView(Func<string> cwd)
	{
		_cwd = cwd;
		_content.Spacing(AppTheme.Space2);

		var refresh = AppIcons.Button("refresh-cw", Strings.RefreshGit, Refresh);
		_summary.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap)
			.VerticalAlignment(VerticalAlignment.Center);

		// 面板页签已经写着「Git」，这里不再重复大标题：第一行直接是分支与改动摘要 + 刷新。
		// 底色由右侧面板统一给（与左侧栏同色）。
		View = new Border()
			.Child(new ScrollViewer().SmoothScrolling()
				.Content(new StackPanel()
					.Spacing(AppTheme.Space3)
					.Padding(new Thickness(AppTheme.Space3))
					.Children(
						new DockPanel()
							.LastChildFill()
							.Children(refresh.DockRight(), _summary),
						_content))
				.AutoVerticalScroll()
				.NoHorizontalScroll());

		Refresh();
	}

	public UIElement View { get; }

	/// <summary>主题切换后刷新角色文本颜色；图形在各自 WithTheme 里跟随。</summary>
	public void ApplyTheme() => AppStyles.ApplyTextColors(View);

	/// <summary>重新读取 Git 状态并重建清单。</summary>
	public void Refresh()
	{
		var status = GitStatusReader.Read(_cwd());
		_summary.Text(status.Summary());

		foreach (var child in _content.Children.ToArray()) UiLifetime.Release(child);
		_content.Clear();

		if (!status.IsRepository)
		{
			_content.Children(new TextBlock()
				.Text(Strings.GitNotRepositoryHint(_cwd()))
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText());

			if (status.Error is { Length: > 0 } detail)
			{
				_content.Children(new TextBlock()
					.Text(detail)
					.FontFamily(AppTheme.MonoFont)
					.FontSize(AppTheme.FontSizeMicro)
					.FaintText()
					.TextWrapping(TextWrapping.Wrap));
			}

			return;
		}

		BuildBranchControls(status);
		BuildRemoteControls(status);
		BuildCheckpointControls();
		BuildFailureDetail();

		if (status.Changes.Count == 0)
		{
			_content.Children(new TextBlock()
				.Text(Strings.GitCleanHint)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText());
			BuildStashControls();
			return;
		}

		BuildCommitControls(status);
		BuildChangeList(status);
		BuildStashControls();
	}

	private void BuildBranchControls(GitStatus status)
	{
		var branches = GitWriter.Branches(_cwd()).ToList();
		if (branches.Count == 0 && status.Branch.Length > 0)
		{
			branches.Add(status.Branch);
		}

		var branchBox = new ComboBox().ItemsSource(ItemsView.Create(branches)).MinWidth(120);
		branchBox.SelectedIndex = Math.Max(0, branches.IndexOf(status.Branch));
		branchBox.SelectionChanged += _ =>
		{
			var index = branchBox.SelectedIndex;
			if (index < 0 || index >= branches.Count || branches[index] == status.Branch)
			{
				return;
			}

			ApplyResult(GitWriter.SwitchBranch(_cwd(), branches[index]));
		};

		var newBranch = new TextBox().Text(_branchDraft).Placeholder(Strings.GitBranchPlaceholder);
		newBranch.OnTextChanged(value => _branchDraft = value);
		var create = new Button().Content(Label(Strings.GitNewBranch)).OnClick(() =>
		{
			var result = GitWriter.CreateBranch(_cwd(), newBranch.Text?.Trim() ?? string.Empty);
			if (result.Ok)
			{
				_branchDraft = string.Empty;
			}

			ApplyResult(result);
		});

		// 窄面板里控件竖排：分支名一栏，新建分支一栏。
		_content.Children(new StackPanel()
			.Spacing(AppTheme.Space1)
			.Children(
				Label(Strings.GitBranch),
				branchBox,
				new DockPanel()
					.LastChildFill()
					.Children(create.DockRight(), newBranch.Margin(new Thickness(0, 0, AppTheme.Space2, 0)))));
	}

	/// <summary>
	/// 远端同步：获取 / 拉取 / 推送。按钮的可用性由「有没有远端」「有没有上游」「有没有可推可拉的差异」
	/// 决定 —— 把按钮变灰比让用户点下去看报错好。
	/// </summary>
	private void BuildRemoteControls(GitStatus status)
	{
		var cwd = _cwd();

		var fetch = AppIcons.Button("download", Strings.GitFetch, () =>
		{
			ApplyResult(GitWriter.Fetch(cwd));
		});
		fetch.IsEnabled = status.HasRemotes;
		// 同一行里图标按钮与文字按钮混排：高度不统一会看出错位。
		fetch.Height(AppTheme.ControlHeightSm);

		var pull = new Button().Content(Label(Strings.GitPull)).OnClick(() =>
		{
			ApplyResult(GitWriter.Pull(cwd));
		});
		pull.Height(AppTheme.ControlHeightSm);
		pull.IsEnabled = status.HasRemotes && status.HasUpstream && !status.Detached;

		var push = new Button().Primary().Content(
			new TextBlock().Text(Strings.GitPush).FontSize(AppTheme.FontSizeCaption).OnAccentText()).OnClick(() =>
		{
			ApplyResult(GitWriter.Push(cwd));
		});
		push.Height(AppTheme.ControlHeightSm);
		// 无上游时也允许点：首次推送就是我们帮他建立跟踪的时刻，比让用户自己去终端 -u 强。
		push.IsEnabled = status.HasRemotes && !status.Detached;

		_content.Children(new DockPanel().LastChildFill().Children(
			new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space1)
				.Children(fetch, pull, push)
				.DockRight(),
			Label(Strings.GitRemotes)));

		if (!status.HasRemotes)
		{
			_content.Children(new TextBlock()
				.Text(Strings.GitNoRemote)
				.FontSize(AppTheme.FontSizeMicro)
				.FaintText()
				.TextWrapping(TextWrapping.Wrap));
		}
	}

	/// <summary>把工作区改动收进 stash，并列出已有条目可以应用。</summary>
	private void BuildStashControls()
	{
		var cwd = _cwd();
		var push = AppIcons.Button("package", Strings.GitStash, () => ApplyResult(GitWriter.StashPush(cwd)));

		_content.Children(new DockPanel().LastChildFill().Children(push.DockRight(), Label(Strings.GitStashes)));

		var stashes = GitWriter.Stashes(cwd);
		if (stashes.Count == 0)
		{
			_content.Children(new TextBlock().Text(Strings.GitStashEmpty).FontSize(AppTheme.FontSizeCaption).MutedText());
			return;
		}

		foreach (var stash in stashes)
		{
			var captured = stash;
			_content.Children(new DockPanel().LastChildFill().Children(
				new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(AppTheme.Space1)
					.Children(
						AppIcons.Button("rotate-ccw", Strings.GitStashPop, () => ApplyResult(GitWriter.StashPop(cwd))),
						AppIcons.Button("trash", Strings.GitStashResolved, () => ApplyResult(GitWriter.StashDrop(cwd, captured.Reference))))
					.DockRight(),
				new TextBlock()
					.Text(captured.Description.Length > 0 ? captured.Description : captured.Reference)
					.FontFamily(AppTheme.MonoFont)
					.FontSize(AppTheme.FontSizeMicro)
					.MutedText()
					.TextWrapping(TextWrapping.NoWrap)));
		}
	}

	/// <summary>检查点：创建与恢复。快照写入低层引用，不产生分支或提交。</summary>
	private void BuildCheckpointControls()
	{
		var cwd = _cwd();
		var create = AppIcons.Button("plus", Strings.CreateCheckpoint, () =>
		{
			var checkpoint = Checkpoints.Create(cwd);
			if (checkpoint is null) Toasts.Error(View, Strings.CheckpointMissing);
			else
			{
				Checkpoints.Prune(cwd);
				Toasts.Success(View, Strings.CheckpointCreated);
			}

			Refresh();
		});

		_content.Children(new DockPanel()
			.LastChildFill()
			.Children(create.DockRight(), Label(Strings.Checkpoints)));

		var checkpoints = Checkpoints.List(cwd);
		if (checkpoints.Count == 0)
		{
			_content.Children(new TextBlock().Text(Strings.CheckpointEmpty).FontSize(AppTheme.FontSizeCaption).MutedText());
			return;
		}

		foreach (var checkpoint in checkpoints)
		{
			var captured = checkpoint;
			var title = captured.Label.Length > 0
				? $"{captured.Created.LocalDateTime:MM-dd HH:mm:ss}  {captured.Label}"
				: $"{captured.Created.LocalDateTime:MM-dd HH:mm:ss}";

			_content.Children(new DockPanel().LastChildFill().Children(
				AppIcons.Button("rotate-ccw", Strings.RestoreCheckpoint, () =>
				{
					var error = Checkpoints.Restore(cwd, captured.Sha);
					if (error is null) Toasts.Success(View, Strings.CheckpointRestored);
					else Toasts.Error(View, error);
					Refresh();
				}).DockRight(),
				new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption).MutedText()));
		}
	}

	private void BuildCommitControls(GitStatus status)
	{
		var staged = status.Changes.Count(static change => change.HasStaged);
		var message = new TextBox().Text(_commitDraft).Placeholder(Strings.GitCommitPlaceholder);
		message.OnTextChanged(value => _commitDraft = value);

		// 主按钮是反色底：标签必须用强调色上的文字色，普通正文色会白底白字看不见。
		var commit = new Button().Primary().Content(
			new TextBlock().Text(Strings.GitCommit).FontSize(AppTheme.FontSizeCaption).OnAccentText()).OnClick(() =>
		{
			var result = GitWriter.Commit(_cwd(), message.Text?.Trim() ?? string.Empty);
			if (result.Ok)
			{
				_commitDraft = string.Empty;
			}

			ApplyResult(result);
		});

		// 输入框与主按钮同高（DESIGN.md 的控件高度档）：默认高度不一致会让这一行歪一档。
		message.Height(AppTheme.ControlHeightMd);
		commit.Height(AppTheme.ControlHeightMd);

		// 没有已暂存的改动时提交必然失败（"nothing to commit"）。主按钮变灰 + 一句提示，
		// 比让用户填完提交信息再被拒好。
		commit.IsEnabled = staged > 0;
		if (staged == 0)
		{
			_content.Children(new TextBlock()
				.Text(Strings.GitNothingStaged)
				.FontSize(AppTheme.FontSizeMicro)
				.FaintText());
		}

		_content.Children(new DockPanel().LastChildFill().Children(
			commit.DockRight(),
			message.Margin(new Thickness(0, 0, AppTheme.Space2, 0))));
	}

	/// <summary>
	/// 改动列表按「未暂存 / 已暂存」分两节，与 PiDeck 的资源树同构。
	/// 之前是平铺一行行、靠左侧一个「已暂存 / 未暂存」标签区分 —— 面板里改动一多，
	/// 想确认「我要提交的东西齐了吗」就得逐行读。
	/// <para>
	/// 未暂存在前：那是每次打开面板主要要处理的一堆，已暂存多数时候只是复核。
	/// </para>
	/// </summary>
	private void BuildChangeList(GitStatus status)
	{
		var unstaged = status.Changes.Where(static change => change.HasWorkTree).ToList();
		var staged = status.Changes.Where(static change => change.HasStaged).ToList();

		if (unstaged.Count > 0)
		{
			BuildChangeSection(
				Strings.GitUnstaged,
				unstaged,
				stagedSide: false,
				bulkLabel: Strings.GitStageAll,
				bulkAction: () => ApplyResult(GitWriter.Stage(_cwd(), [.. unstaged.Select(static change => change.Path)])));
		}

		if (staged.Count > 0)
		{
			BuildChangeSection(
				Strings.GitStaged,
				staged,
				stagedSide: true,
				bulkLabel: Strings.GitUnstageAll,
				bulkAction: () => ApplyResult(GitWriter.Unstage(_cwd(), [.. staged.Select(static change => change.Path)])));
		}

		BuildDiffPane(status);
	}

	/// <summary>一节改动：节头（标题 + 计数 + 批量动作）加裸行列表。</summary>
	private void BuildChangeSection(
		string title,
		IReadOnlyList<GitChange> changes,
		bool stagedSide,
		string bulkLabel,
		Action bulkAction)
	{
		// 批量动作用文字标签而不是图标：行内动作是图标（逐个文件），节头再放一个同样的图标
		// 上下叠着会分不清「这一下是整节还是这一行」。
		var bulk = new Button()
			.StyleName(AppStyles.GhostButton)
			.Height(AppTheme.ControlHeightSm)
			.Content(new TextBlock().Text(bulkLabel).FontSize(AppTheme.FontSizeMicro).SubtleText())
			.OnClick(bulkAction)
			.HoverTip(bulkLabel);

		var header = new DockPanel().LastChildFill().Children(
			bulk.DockRight(),
			new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space1)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.SemiBold).PrimaryText(),
					new TextBlock().Text(changes.Count.ToString()).FontSize(AppTheme.FontSizeMicro).FaintText()));

		_content.Children(header);

		foreach (var change in changes)
		{
			BuildChangeRow(change, stagedSide);
		}
	}

	/// <summary>
	/// 一行改动：裸行（默认不铺底色、悬停才浮起）+ 右侧行内动作。
	/// 以前每行是一张卡片，一屏十几个文件名就成了十几块卡片，与 DESIGN.md 的「表面靠色阶 + 发丝线」相悖。
	/// </summary>
	private void BuildChangeRow(GitChange change, bool stagedSide)
	{
		var captured = change;
		var selected = string.Equals(_selectedPath, change.Path, StringComparison.Ordinal);

		var action = stagedSide
			? AppIcons.Button("rotate-ccw", Strings.GitUnstage, () => ApplyResult(GitWriter.Unstage(_cwd(), [captured.Path])))
			: AppIcons.Button("plus", Strings.GitStage, () => ApplyResult(GitWriter.Stage(_cwd(), [captured.Path])));

		var row = new Button()
			.StyleName(selected ? AppStyles.SelectedButton : AppStyles.GhostButton)
			.Height(AppTheme.ControlHeightSm)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.OnClick(() => ToggleDiff(captured))
			.Content(new DockPanel()
				.LastChildFill()
				.Children(
					new TextBlock()
						.Text(change.Label)
						.FontSize(AppTheme.FontSizeMicro)
						.Width(40)
						.SubtleText()
						.VerticalAlignment(VerticalAlignment.Center)
						.DockLeft(),
					new TextBlock()
						.Text(change.Path)
						.FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeCaption)
						.MutedText()
						.TextWrapping(TextWrapping.NoWrap)
						.VerticalAlignment(VerticalAlignment.Center)));

		_content.Children(new DockPanel().LastChildFill().Children(
			action.DockRight(),
			row.Margin(new Thickness(0, 0, AppTheme.Space1, 0))));
	}

	/// <summary>选中文件的 diff：标题行给路径与增删统计，正文交给统一的 diff 渲染。</summary>
	private void BuildDiffPane(GitStatus status)
	{
		if (_selectedPath is not { Length: > 0 } path)
		{
			return;
		}

		var untracked = status.Changes.FirstOrDefault(change =>
			string.Equals(change.Path, path, StringComparison.Ordinal))?.Label == "未跟踪";

		var captured = path;
		var diff = GitStatusReader.Diff(_cwd(), path, untracked);
		var (added, removed) = CountDiffLines(diff);

		_content.Children(new DockPanel().LastChildFill().Children(
			AppIcons.Button("square-pen", Strings.OpenInEditor, () => OpenInEditor(captured)).DockRight(),
			new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new TextBlock()
						.Text(captured)
						.FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeMicro)
						.SubtleText()
						.TextWrapping(TextWrapping.NoWrap),
					new TextBlock()
						.Text(GitDiffStat(added, removed))
						.FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeMicro)
						.FaintText())));

		_content.Children(DiffView.Render(diff.Length > 0 ? diff : Strings.GitNoDiff));
	}

	/// <summary>数 diff 的增删行，跳过 <c>+++</c>/<c>---</c> 文件头。</summary>
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

	private void ApplyResult(GitResult result)
	{
		var message = result.Message.Length > 0 ? result.Message : Strings.GitDone;
		if (result.Ok)
		{
			Toasts.Success(View, message);
			_selectedPath = null;
			_lastFailureDetail = null;
		}
		else
		{
			Toasts.Error(View, message);
			_lastFailureDetail = result.Detail;
		}

		Refresh();
	}

	/// <summary>把上一次失败的原始终端输出排在面板末尾：分类提示用来「知道该做什么」，原文用来「排查为什么」。</summary>
	private void BuildFailureDetail()
	{
		if (_lastFailureDetail is not { Length: > 0 } detail)
		{
			return;
		}

		_content.Children(new TextBlock()
			.Text(detail)
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.FaintText()
			.TextWrapping(TextWrapping.Wrap));
	}

	/// <summary>在用户的编辑器里打开这个文件（$VISUAL/$EDITOR，其次系统默认程序）。</summary>
	private void OpenInEditor(string path)
	{
		var full = Path.IsPathRooted(path) ? path : Path.Combine(_cwd(), path);

		if (!File.Exists(full))
		{
			_summary.Text(Strings.FileMissing(full));
			return;
		}

		_summary.Text(Shell.OpenInEditor(full) ? Strings.OpenedInEditor(full) : Strings.OpenFailed(full));
	}

	/// <summary>视觉验收用：选中第一个改动，让 diff 区一起出现在截图里。</summary>
	public void SelectFirstChangeForSmoke()
	{
		var status = GitStatusReader.Read(_cwd());
		if (status.Changes.FirstOrDefault() is { } first)
		{
			_selectedPath = first.Path;
			Refresh();
		}
	}

	/// <summary>点开或收起某个文件的 diff。</summary>
	private void ToggleDiff(GitChange change)
	{
		_selectedPath = string.Equals(_selectedPath, change.Path, StringComparison.Ordinal) ? null : change.Path;
		Refresh();
	}

	/// <summary>分区标题。与改动分节的节头同字重，面板里的层级才有一致的读法。</summary>
	private static TextBlock Label(string text) =>
		new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.SemiBold).PrimaryText();
}
