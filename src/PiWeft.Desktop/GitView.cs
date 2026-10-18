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
	private readonly TextBlock _notice = new();
	private string? _selectedPath;

	/// <summary>跨刷新保留的草稿，避免提交信息在状态刷新时被清掉。</summary>
	private string _commitDraft = string.Empty;
	private string _branchDraft = string.Empty;
	private string? _statusMessage;

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
						_notice.FontSize(AppTheme.FontSizeCaption).AccentText().IsVisible(false),
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
		_notice.Text(_statusMessage ?? string.Empty).IsVisible(_statusMessage is { Length: > 0 });
		_statusMessage = null;

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
		BuildCheckpointControls();

		if (status.Changes.Count == 0)
		{
			_content.Children(new TextBlock()
				.Text(Strings.GitCleanHint)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText());
			return;
		}

		BuildCommitControls();
		BuildChangeList(status);
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

	/// <summary>检查点：创建与恢复。快照写入低层引用，不产生分支或提交。</summary>
	private void BuildCheckpointControls()
	{
		var cwd = _cwd();
		var create = AppIcons.Button("plus", Strings.CreateCheckpoint, () =>
		{
			var checkpoint = Checkpoints.Create(cwd);
			_statusMessage = checkpoint is null ? Strings.CheckpointMissing : Strings.CheckpointCreated;
			if (checkpoint is not null)
			{
				Checkpoints.Prune(cwd);
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
					_statusMessage = Checkpoints.Restore(cwd, captured.Sha) ?? Strings.CheckpointRestored;
					Refresh();
				}).DockRight(),
				new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption).MutedText()));
		}
	}

	private void BuildCommitControls()
	{
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

		_content.Children(new DockPanel().LastChildFill().Children(
			commit.DockRight(),
			message.Margin(new Thickness(0, 0, AppTheme.Space2, 0))));
	}

	private void BuildChangeList(GitStatus status)
	{
		foreach (var change in status.Changes)
		{
			var captured = change;
			var selected = string.Equals(_selectedPath, change.Path, StringComparison.Ordinal);
			var staged = change.IndexStatus is not (" " or "?");

			var action = staged
				? AppIcons.Button("rotate-ccw", Strings.GitUnstage, () => ApplyResult(GitWriter.Unstage(_cwd(), [captured.Path])))
				: AppIcons.Button("plus", Strings.GitStage, () => ApplyResult(GitWriter.Stage(_cwd(), [captured.Path])));

			_content.Children(new DockPanel().LastChildFill().Children(
				action.DockRight(),
				new Button()
					.StyleName(selected ? AppStyles.CardButton : AppStyles.CardMutedButton)
					.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
					.OnClick(() => ToggleDiff(captured))
					.Content(new DockPanel()
						.LastChildFill()
						.Children(
							new TextBlock()
								.Text(staged ? Strings.GitStaged : change.Label)
								.FontSize(AppTheme.FontSizeMicro)
								.Width(58)
								.AccentText()
								.DockLeft(),
							new TextBlock()
								.Text(change.Path)
								.FontFamily(AppTheme.MonoFont)
								.FontSize(AppTheme.FontSizeCaption)
								.MutedText()
								.TextWrapping(TextWrapping.NoWrap)))));
		}

		if (_selectedPath is { Length: > 0 } path)
		{
			var untracked = status.Changes.FirstOrDefault(change =>
				string.Equals(change.Path, path, StringComparison.Ordinal))?.Label == "未跟踪";

			var captured = path;
			_content.Children(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.Children(
					AppIcons.Button("square-pen", Strings.OpenInEditor, () => OpenInEditor(captured)),
					new TextBlock()
						.Text(captured)
						.FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeMicro)
						.SubtleText()));

			var diff = GitStatusReader.Diff(_cwd(), path, untracked);
			_content.Children(DiffView.Render(diff.Length > 0 ? diff : Strings.GitNoDiff));
		}
	}

	private void ApplyResult(GitResult result)
	{
		_statusMessage = result.Ok
			? (result.Message.Length > 0 ? result.Message : Strings.GitDone)
			: result.Message;

		if (result.Ok)
		{
			_selectedPath = null;
		}

		Refresh();
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

	/// <summary>点开或收起某个文件的 diff。</summary>
	private void ToggleDiff(GitChange change)
	{
		_selectedPath = string.Equals(_selectedPath, change.Path, StringComparison.Ordinal) ? null : change.Path;
		Refresh();
	}

	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();
}
