using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Microsoft.Extensions.Logging;
using PiWeft.Cli;
using ZLogger;

namespace PiWeft.Desktop;

/// 设置页的日志查看器：级别、关键词、分页筛选最近的日志文件，并提供复制、导出诊断包与清空。
/// <remarks>
/// 只读最近几个文件的尾部（口径见 <see cref="LogRecords"/>）：筛选要跟着输入实时重跑，
/// 全量扫盘会让面板自己变成卡顿来源。更早的记录交给「打开日志目录」。
/// </remarks>
internal sealed class LogViewer : StackPanel
{
	private const int PageSize = 50;
	private const double RowHeight = 18;

	private static readonly (string Label, LogLevel Level)[] Filters =
	[
		(Strings.LogLevelAll, LogLevel.Trace),
		(Strings.LogLevelDebug, LogLevel.Debug),
		(Strings.LogLevelInfo, LogLevel.Information),
		(Strings.LogLevelWarning, LogLevel.Warning),
		(Strings.LogLevelError, LogLevel.Error),
	];

	private readonly string _agentDir;
	private readonly string _cwd;
	private readonly string _directory;
	private readonly ItemsControl _list;
	private readonly TextBlock _status;
	private readonly TextBlock _pageLabel;
	private readonly DispatcherTimer _debounce;
	private LogLevel _level = LogLevel.Trace;
	private string _search = string.Empty;
	private int _page;
	private int _generation;
	private LogRow[] _rows = [];

	/// 最近一次查询任务。设置分区是同步构建的，测试要等结果落地才能断言。
	internal Task PendingReload { get; private set; } = Task.CompletedTask;

	public LogViewer(string agentDir, string cwd)
	{
		_agentDir = agentDir;
		_cwd = cwd;
		_directory = Path.Combine(agentDir, "logs");
		Spacing = AppTheme.Space3;

		var filter = new ComboBox()
			.ItemsSource(ItemsView.Create(Filters.Select(static entry => entry.Label).ToArray()))
			.Width(120)
			.SelectedIndex(0);
		filter.SelectionChanged += _ =>
		{
			var index = Math.Clamp(filter.SelectedIndex, 0, Filters.Length - 1);
			_level = Filters[index].Level;
			_ = Tracked(0);
		};

		// 输入停止后再查一次：每敲一个键都重读文件会让面板自己卡起来。
		_debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250));
		_debounce.Tick += () =>
		{
			_debounce.Stop();
			// 分区被重建或面板关闭后不再查：旧实例留着只会多读一次盘。
			if (this.FindVisualRoot() is null)
			{
				return;
			}

			_ = Tracked(0);
		};

		var search = new TextBox().PrimarySelection()
			.Placeholder(Strings.LogSearchPlaceholder)
			.MinWidth(220);
		search.OnTextChanged(value =>
		{
			_search = value;
			_debounce.Stop();
			_debounce.Start();
		});

		var toolbar = new DockPanel().LastChildFill().Children(
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
				.Children(new TextBlock().Text(Strings.LogFilterLevel)
					.FontSize(AppTheme.FontSizeMicro).SubtleText()
					.VerticalAlignment(VerticalAlignment.Center), filter)
				.DockRight(),
			search);

		var actions = new WrapPanel().Spacing(AppTheme.Space2).Children(
			AppIcons.TextButton("refresh-cw", Strings.LogRefresh, () => _ = Tracked(-1), strong: false),
			AppIcons.TextButton("copy", Strings.LogCopyPage, CopyPage, strong: false),
			AppIcons.TextButton("download", Strings.LogExportBundle, () => _ = ExportAsync(), strong: false),
			AppIcons.TextButton("trash", Strings.LogClear, () => _ = ClearAsync(), strong: false),
			AppIcons.TextButton("folder-open", Strings.OpenLogDirectory, OpenDirectory, strong: false));

		_status = new TextBlock()
			.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap);
		_pageLabel = new TextBlock()
			.FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont).MutedText()
			.VerticalAlignment(VerticalAlignment.Center);

		_list = new ItemsControl()
			.FixedHeightPresenter(RowHeight)
			.Height(360)
			.ItemsSource(ItemsView.Create(Array.Empty<LogRow>(), static row => row.Text))
			.ItemPadding(Thickness.Zero)
			.BorderThickness(0)
			.Background(AppTheme.Transparent)
			.Padding(Thickness.Zero)
			.SmoothScrolling();
		_list.ItemTemplate = new DelegateTemplate<LogRow>(
			_ => new LogLine(),
			(view, row, _, _) => ((LogLine)view).Bind(row));

		var paging = new DockPanel().LastChildFill().Children(
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
				.Children(
					AppIcons.Button("arrow-left", Strings.LogPagePrev, () => _ = Tracked(_page - 1)),
					_pageLabel,
					AppIcons.Button("arrow-right", Strings.LogPageNext, () => _ = Tracked(_page + 1)))
				.DockRight(),
			_status);

		AddRange(
			new TextBlock().Text(Strings.LogViewerHint(LogRecords.MaxFiles))
				.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap),
			toolbar,
			actions,
			new Border().Height(1).WithTheme(static (theme, line) => line.Background = theme.Palette.ControlBorder),
			_list,
			paging);

		_ = Tracked(-1);
	}

	/// 当前页的记录文本，按显示顺序。
	internal IReadOnlyList<string> Lines => [.. _rows.Select(static row => row.Text)];

	private Task Tracked(int page) => PendingReload = ReloadAsync(page);

	private async Task ReloadAsync(int page = -1)
	{
		if (page >= 0)
		{
			_page = page;
		}

		var generation = ++_generation;
		var query = new LogQuery
		{
			Page = _page,
			PageSize = PageSize,
			MinimumLevel = _level,
			Search = _search,
		};

		LogPage result;
		long bytes;
		int files;
		try
		{
			(result, bytes, files) = await Task.Run(() =>
			{
				// 目录只枚举一次：查询、总量、文件数都从同一份列表算。
				var all = LogRecords.Files(_directory);
				IReadOnlyList<FileInfo> scanned = all.Count > LogRecords.MaxFiles
					? [.. all.Take(LogRecords.MaxFiles)]
					: all;
				return (LogRecords.Query(query, scanned), LogRecords.TotalBytes(all), all.Count);
			}).ConfigureAwait(true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_status.Text = Strings.LogReadFailed(exception.Message);
			return;
		}

		// 结果回来时用户可能已经改了筛选：只让最后一次查询写界面。
		if (generation != _generation)
		{
			return;
		}

		// 删日志或换筛选后页码可能越界：落到最后一页再查一次，避免停在空白页。
		var pages = Math.Max(1, (result.Total + PageSize - 1) / PageSize);
		if (_page >= pages)
		{
			await ReloadAsync(page: pages - 1).ConfigureAwait(true);
			return;
		}

		_rows = [.. result.Records.Select(static record => new LogRow(
			$"{record.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  {LogRecords.Short(record.Level)}  {record.Category}  {record.Message}",
			record.Level))];
		_list.ItemsSource(ItemsView.Create(_rows, static row => row.Text));
		_status.Text = Strings.LogSummary(result.Total, files, bytes);
		_pageLabel.Text = Strings.LogPage(_page + 1, pages);
	}

	private void CopyPage()
	{
		var lines = _rows;
		if (lines.Length == 0)
		{
			Toast(Strings.LogCopyEmpty, ToastKind.Info);
			return;
		}

		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		var copied = clipboard?.TrySetText(string.Join('\n', lines.Select(static line => line.Text))) == true;
		Toast(copied ? Strings.Copied : Strings.ClipboardUnavailable, copied ? ToastKind.Success : ToastKind.Error);
	}

	private async Task ExportAsync()
	{
		Toast(Strings.LogExporting, ToastKind.Info);
		try
		{
			var path = await Task.Run(() => BugReport.CreateLogBundle(_agentDir, _cwd)).ConfigureAwait(true);
			Toast(Strings.LogExported(path), ToastKind.Success);
			AppLog.For<LogViewer>().ZLogInformation($"Diagnostics bundle exported: {path}");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or InvalidDataException or InvalidOperationException)
		{
			AppLog.Failure(AppLog.For<LogViewer>(), "Export diagnostics bundle", exception);
			Toast(Strings.LogExportFailed(exception.Message), ToastKind.Error);
		}
	}

	private async Task ClearAsync()
	{
		var files = LogRecords.Files(_directory);
		if (files.Count == 0)
		{
			Toast(Strings.LogClearEmpty, ToastKind.Info);
			return;
		}

		if (!await MessageBox.ConfirmAsync(Strings.LogClearConfirm(files.Count)).ConfigureAwait(true))
		{
			return;
		}

		var removed = LogRecords.Clear(_directory);
		// 清日志属敏感操作，留一条审计记录：事后能看出谁在什么时候清的。
		AppLog.For<LogViewer>().ZLogInformation($"Logs cleared: files={removed}");
		Toast(Strings.LogCleared(removed), removed > 0 ? ToastKind.Success : ToastKind.Info);
		await ReloadAsync(0).ConfigureAwait(true);
	}

	private void OpenDirectory()
	{
		try
		{
			Directory.CreateDirectory(_directory);
			if (!Shell.Open(_directory))
			{
				Toast(Strings.OpenFailed(_directory), ToastKind.Error);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Toast(Strings.OpenFailed(exception.Message), ToastKind.Error);
		}
	}

	private void Toast(string message, ToastKind kind) => Toasts.Show(this, message, kind);

	/// 一行日志的数据：文本与级别。视图按行复用，所以数据与控件分开。
	private readonly record struct LogRow(string Text, LogLevel Level);

	/// 一行日志。级别只影响颜色，版面按等宽字体对齐。
	private sealed class LogLine : TextBlock
	{
		private LogLevel _level = LogLevel.Information;

		public LogLine()
		{
			// 行文本用等宽字体：级别、类别、正文之间靠两个空格对齐，换比例字体就散了。
			FontFamily = AppTheme.MonoFont;
			FontSize = AppTheme.FontSizeMicro;
			TextWrapping = TextWrapping.NoWrap;
			TextTrimming = TextTrimming.CharacterEllipsis;
			VerticalAlignment = VerticalAlignment.Center;
		}

		public void Bind(LogRow row)
		{
			Text = row.Text;
			_level = row.Level;
			this.HoverTip(row.Text);
			Apply();
		}

		protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => Apply();

		private void Apply() => Foreground = _level switch
		{
			LogLevel.Critical or LogLevel.Error => AppTheme.For(Theme).Error,
			LogLevel.Warning => AppTheme.For(Theme).Warning,
			_ => AppTheme.For(Theme).TextMuted,
		};
	}
}
