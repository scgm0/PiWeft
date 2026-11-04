using System.Globalization;
using System.Text.RegularExpressions;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// D-05：右侧面板的「检查点」分段。列出 <c>refs/pi-checkpoints</c> 下的工作区快照，
/// 每个快照可以展开成相对当前工作区的统一 diff，或回写工作区（不动分支与索引）。
/// <para>
/// 与 PiDeck 的 rewind 面板同一件事，范围收窄到文件：会话跳转仍走分支树 / fork，
/// 不把两种回退混在一个按钮里。恢复前先自动打一个 <c>before-restore</c> 快照，
/// 手滑一次还能退回来。
/// </para>
/// </summary>
public sealed class CheckpointsView
{
	/// <summary>git 命令失败时保留的原始输出长度（与 <see cref="Checkpoints"/> 的约定一致）。</summary>
	private readonly Func<string> _cwd;
	private readonly StackPanel _rows = new();
	private readonly List<CheckpointRow> _rowState = [];
	private readonly TextBlock _empty = new();
	private readonly TextBlock _count = new();
	private readonly ScrollViewer _scroll;

	/// <summary>每次刷新递增：后台算摘要/取 diff 的结果回来时据此丢弃过期结果。</summary>
	private int _generation;

	private bool _restoring;

	public CheckpointsView(Func<string> cwd)
	{
		_cwd = cwd;
		_rows.Spacing(AppTheme.Space2);

		_count.FontSize(AppTheme.FontSizeMicro).SubtleText().VerticalAlignment(VerticalAlignment.Center);
		_empty.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap)
			.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, 0)).IsVisible(false);

		var toolbar = new DockPanel()
			.LastChildFill()
			.Children(
				new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(2)
					.Children(
						AppIcons.Button("plus", Strings.CreateCheckpoint, () => _ = CreateAsync()),
						AppIcons.Button("refresh-cw", Strings.CheckpointRefresh, Refresh))
					.DockRight(),
				new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(AppTheme.Space2)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(
						new TextBlock().Text(Strings.Checkpoints).FontSize(AppTheme.FontSizeControl)
							.FontWeight(FontWeight.SemiBold).PrimaryText(),
						_count));

		_scroll = new ScrollViewer()
			.SmoothScrolling()
			.Content(new StackPanel()
				.Spacing(AppTheme.Space2)
				.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space3))
				.Children(_empty, _rows))
			.AutoVerticalScroll()
			.NoHorizontalScroll();

		View = new Border()
			.Child(new DockPanel()
				.LastChildFill()
				.Children(
					new Border()
						.Margin(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
						.Child(toolbar)
						.DockTop(),
					new Border()
						.Margin(new Thickness(0, 0, 0, 0))
						.Child(new Border()
							.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space2))
							.Child(new TextBlock()
								.Text(Strings.CheckpointHint)
								.FontSize(AppTheme.FontSizeMicro)
								.MutedText()
								.TextWrapping(TextWrapping.Wrap))
							.DockTop())
						.Child(_scroll)));

		Refresh();
	}

	public UIElement View { get; }

	/// <summary>重读检查点列表。只做一次 <c>for-each-ref</c>；每行的变更摘要在后台补齐。</summary>
	public void Refresh()
	{
		var generation = ++_generation;
		_rows.Clear();
		_count.Text(string.Empty);

		var cwd = _cwd();
		if (!Directory.Exists(cwd))
		{
			SetEmpty(Strings.NoSuchDirectory(cwd));
			return;
		}

		// `git status` + `for-each-ref` are external processes and can be slow on large
		// repositories. Never run them while the panel is being opened on the UI thread.
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			ApplyRefresh(cwd, generation, LoadRefresh(cwd));
			return;
		}

		_ = RefreshAsync(cwd, generation, dispatcher);
	}

	private sealed record RefreshData(GitStatus Status, IReadOnlyList<Checkpoint>? Checkpoints);

	private static RefreshData LoadRefresh(string cwd)
	{
		var status = GitStatusReader.Read(cwd, TimeSpan.FromSeconds(5));
		return new RefreshData(status, status.IsRepository ? Checkpoints.List(cwd) : null);
	}

	private async Task RefreshAsync(string cwd, int generation, IDispatcher dispatcher)
	{
		var data = await Task.Run(() => LoadRefresh(cwd)).ConfigureAwait(false);
		void Complete()
		{
			if (_generation != generation)
			{
				return;
			}

			ApplyRefresh(cwd, generation, data);
		}

		if (dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	private void ApplyRefresh(string cwd, int generation, RefreshData data)
	{
		if (!data.Status.IsRepository)
		{
			SetEmpty(string.Equals(data.Status.Error, Strings.GitUnavailable, StringComparison.Ordinal)
				? Strings.GitUnavailable
				: Strings.CheckpointNotRepository);
			return;
		}

		var checkpoints = data.Checkpoints ?? [];
		if (checkpoints.Count == 0)
		{
			SetEmpty(Strings.CheckpointEmpty);
			return;
		}

		_empty.IsVisible(false);
		_count.Text(Strings.CheckpointCount(checkpoints.Count));

		_rowState.Clear();
		foreach (var checkpoint in checkpoints)
		{
			var row = CreateRow(checkpoint);
			_rowState.Add(row);
			_rows.Children(row.Root);
		}

		_ = LoadSummariesAsync(cwd, _rowState, generation);
	}

	/// <summary>主题切换后刷新角色文本颜色；图标在 AppIcons 的存活表里统一跟随。</summary>
	public void ApplyTheme() => AppStyles.ApplyTextColors((Element)View);

	/// <summary>视觉验收：打印行数与每行摘要，供 xvfb 脚本断言面板真的渲染了数据。</summary>
	public void ReportForSmoke()
	{
		var labels = _rows.Children.Count;
		var first = _rowState.FirstOrDefault();
		Console.WriteLine(
			$"piweft-desktop: checkpoints rows={labels} count=\"{_count.Text}\" " +
			$"first-summary=\"{first?.Summary.Text ?? string.Empty}\" " +
			$"diff-visible={(first?.DiffHost.IsVisible == true)} diff-lines={first?.DiffLines ?? 0}");
	}

	/// <summary>视觉验收：展开第一条快照的变更概览（不打开编辑器）。</summary>
	public void ExpandFirstDiffForSmoke()
	{
		if (_rowState.FirstOrDefault() is { } first)
		{
			_ = ToggleDiffAsync(first);
		}
	}

	private void SetEmpty(string message)
	{
		_empty.Text(message).IsVisible(true);
		_count.Text(string.Empty);
	}

	private CheckpointRow CreateRow(Checkpoint checkpoint)
	{
		var title = new TextBlock()
			.Text(DescribeLabel(checkpoint.Label))
			.FontSize(AppTheme.FontSizeCaption)
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis);
		if (checkpoint.Label.Length > 0)
		{
			title.PrimaryText();
		}
		else
		{
			title.MutedText();
		}

		var time = new TextBlock()
			.Text(GitHistoryReader.RelativeTime(checkpoint.Created, DateTimeOffset.Now))
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.HoverTip(checkpoint.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));

		var summary = new TextBlock()
			.Text(Strings.CheckpointLoading)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis);

		var diffHost = new Border()
			.Margin(new Thickness(0, AppTheme.Space2, 0, 0))
			.IsVisible(false);

		var row = new CheckpointRow(checkpoint, summary, diffHost);

		// 差异按需加载：一个几十行的补丁不值得在打开面板时就为每个快照各跑一次 git。
		var diffButton = AppIcons.Button("file-diff", Strings.CheckpointDiff, () => _ = ToggleDiffAsync(row));
		var restoreButton = AppIcons.Button("rotate-ccw", Strings.RestoreCheckpoint, () => _ = RestoreAsync(row));

		var header = new DockPanel()
			.LastChildFill()
			.Children(
				new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(2)
					.Children(restoreButton, diffButton)
					.DockRight(),
				new StackPanel()
					.Spacing(1)
					.Children(
						new StackPanel()
							.Orientation(Orientation.Horizontal)
							.Spacing(AppTheme.Space2)
							.Children(title, time),
						summary));

		var body = new StackPanel()
			.Spacing(0)
			.Children(header, diffHost);

		row.Root = new Border()
			.StyleName(AppStyles.Panel)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space2, AppTheme.Space2, AppTheme.Space2))
			.Child(body);

		return row;
	}

	/// <summary>后台补齐每行的变更摘要；50 个快照就是 50 次 git，串行跑在后台线程，不冻 UI。</summary>
	private async Task LoadSummariesAsync(string cwd, List<CheckpointRow> rows, int generation)
	{
		await Task.Run(() =>
		{
			foreach (var row in rows)
			{
				if (Volatile.Read(ref _generation) != generation)
				{
					return;
				}

				var (summary, error) = Checkpoints.ShortStat(cwd, row.Checkpoint.Sha);
				Dispatch(() =>
				{
					if (Volatile.Read(ref _generation) != generation)
					{
						return;
					}

					row.Summary.Text(error is { Length: > 0 }
						? Strings.CheckpointStatFailed
						: summary.Length == 0
							? Strings.CheckpointUnchanged
							: DescribeStat(summary));
				});
			}
		}).ConfigureAwait(false);
	}

	private async Task ToggleDiffAsync(CheckpointRow row)
	{
		if (row.DiffHost.IsVisible)
		{
			row.DiffHost.IsVisible(false).Child(null);
			row.DiffLoaded = false;
			return;
		}

		if (row.DiffLoaded)
		{
			row.DiffHost.IsVisible(true);
			return;
		}

		var generation = _generation;
		row.DiffHost.IsVisible(true).Child(new TextBlock()
			.Text(Strings.CheckpointLoading)
			.FontSize(AppTheme.FontSizeMicro).MutedText());

		var cwd = _cwd();
		var (stat, error) = await Task.Run(() => Checkpoints.Stat(cwd, row.Checkpoint.Sha)).ConfigureAwait(false);
		Dispatch(() =>
		{
			if (generation != _generation)
			{
				return;
			}

			if (error is { Length: > 0 })
			{
				row.DiffHost.Child(new TextBlock()
					.Text(Strings.CheckpointDiffFailed(error))
					.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap));
				return;
			}

			if (stat.Length == 0)
			{
				row.DiffHost.Child(new TextBlock()
					.Text(Strings.CheckpointUnchanged)
					.FontSize(AppTheme.FontSizeMicro).MutedText());
				row.DiffLoaded = true;
				return;
			}

			row.DiffHost.Child(BuildStatPreview(row, stat));
			row.DiffLines = stat.Length == 0 ? 0 : stat.Split('\n').Length;
			row.DiffLoaded = true;
		});
	}

	/// <summary>
	/// 变更概览：固定 180 DIP 的虚拟化行列表（内部滚动由 ItemsControl 自己持有），
	/// 不把整份 stat 撑进回滚列表。这里不用手写的 ScrollViewer：MewUI 的嵌套平滑滚动
	/// 会把滚轮判给外层，表现为「有滚动条但滚不动」；ItemsControl 的滚动路径与文件列表一致，
	/// 是已经验证可用的。
	/// </summary>
	private UIElement BuildStatPreview(CheckpointRow row, string stat)
	{
		var lines = stat.Split('\n');
		var list = new ItemsControl()
			.FixedHeightPresenter(16)
			.Height(180)
			.ItemsSource(ItemsView.Create(lines))
			.ItemPadding(Thickness.Zero)
			.BorderThickness(0)
			.Background(AppTheme.Transparent)
			.Padding(Thickness.Zero)
			.SmoothScrolling();
		list.ItemTemplate = new DelegateTemplate<string>(
			_ => new TextBlock()
				.FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeMicro)
				.MutedText()
				.TextWrapping(TextWrapping.NoWrap)
				.TextTrimming(TextTrimming.CharacterEllipsis),
			(view, line, _, _) => ((TextBlock)view).Text(line));

		return new StackPanel()
			.Spacing(AppTheme.Space1)
			.Children(
				AppIcons.TextButton("external-link", Strings.CheckpointOpenPatch, () => _ = OpenPatchAsync(row)),
				new TextBlock()
					.Text(Strings.CheckpointPatchHint)
					.FontSize(AppTheme.FontSizeMicro)
					.FaintText()
					.TextWrapping(TextWrapping.Wrap),
				list);
	}

	/// <summary>完整补丁写到临时文件再交给编辑器：面板不做几万行的文本布局。</summary>
	private async Task OpenPatchAsync(CheckpointRow row)
	{
		var cwd = _cwd();
		var (patch, error) = await Task.Run(() => Checkpoints.Patch(cwd, row.Checkpoint.Sha)).ConfigureAwait(false);
		if (error is { Length: > 0 })
		{
			Toasts.Error(View, Strings.CheckpointPatchFailed(error));
			return;
		}

		var fileName = $"piweft-checkpoint-{row.Checkpoint.Id}.patch";
		foreach (var invalid in Path.GetInvalidFileNameChars())
		{
			fileName = fileName.Replace(invalid, '-');
		}

		var path = Path.Combine(Path.GetTempPath(), fileName);
		try
		{
			await File.WriteAllTextAsync(path, patch).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Toasts.Error(View, Strings.CheckpointPatchFailed(exception.Message));
			return;
		}

		Dispatch(() =>
		{
			if (Shell.OpenInEditor(path))
			{
				Toasts.Success(View, Strings.OpenedInEditor(path));
			}
			else
			{
				Toasts.Error(View, Strings.OpenFailed(path));
			}
		});
	}

	private async Task RestoreAsync(CheckpointRow row)
	{
		if (_restoring)
		{
			return;
		}

		var confirmed = await MessageBox.ConfirmAsync(
			Strings.CheckpointRestoreConfirm(DescribeLabel(row.Checkpoint.Label)));
		if (!confirmed)
		{
			return;
		}

		_restoring = true;
		var cwd = _cwd();
		try
		{
			var failure = await Task.Run(() =>
			{
				// 先给自己留退路：恢复到一半发现选错了，至少还能退回恢复前的工作区。
				Checkpoints.Create(cwd, "before-restore");
				return Checkpoints.Restore(cwd, row.Checkpoint.Sha);
			}).ConfigureAwait(false);

			if (failure is { Length: > 0 })
			{
				Toasts.Error(View, failure);
			}
			else
			{
				Toasts.Success(View, Strings.CheckpointRestored);
			}
		}
		finally
		{
			_restoring = false;
		}

		Refresh();
	}

	private async Task CreateAsync()
	{
		var cwd = _cwd();
		var checkpoint = await Task.Run(() => Checkpoints.Create(cwd, "manual")).ConfigureAwait(false);
		if (checkpoint is null)
		{
			Toasts.Error(View, Strings.CheckpointCreateFailed);
			return;
		}

		await Task.Run(() => Checkpoints.Prune(cwd)).ConfigureAwait(false);
		Toasts.Success(View, Strings.CheckpointCreated);
		Refresh();
	}

	/// <summary>快照标签：内置三类之外的自定义标签原样显示。</summary>
	private static string DescribeLabel(string label) => label switch
	{
		"" => Strings.CheckpointManual,
		"auto" => Strings.CheckpointAuto,
		"manual" => Strings.CheckpointManual,
		"before-restore" => Strings.CheckpointBeforeRestore,
		_ => label,
	};

	/// <summary>把 `3 files changed, 12 insertions(+), 4 deletions(-)` 转成中文摘要。</summary>
	private static string DescribeStat(string shortStat)
	{
		var files = Match(shortStat, @"(\d+) files? changed");
		var insertions = Match(shortStat, @"(\d+) insertions?\(\+\)");
		var deletions = Match(shortStat, @"(\d+) deletions?\(-\)");
		return Strings.CheckpointStat(files, insertions, deletions);
	}

	private static int Match(string text, string pattern)
	{
		var match = Regex.Match(text, pattern);
		return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
			? value
			: 0;
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

	private sealed class CheckpointRow(Checkpoint checkpoint, TextBlock summary, Border diffHost)
	{
		public Checkpoint Checkpoint { get; } = checkpoint;

		public TextBlock Summary { get; } = summary;

		public Border DiffHost { get; } = diffHost;

		public bool DiffLoaded { get; set; }

		/// <summary>已加载的 stat 行数，供视觉验收断言「没有展开成几万行」。</summary>
		public int DiffLines { get; set; }

		public Border Root { get; set; } = null!;
	}
}
