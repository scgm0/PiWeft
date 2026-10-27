using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Import;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// <summary>
/// 导入外部会话：把 Claude Code / Codex CLI 留在本机的会话搬进 pi 的会话目录。
/// <para>
/// 与 PiDeck 的「导入会话」弹窗同一件事，但这里只做**只读列举 + 逐条导入**：外部会话库可能不存在、
/// 可能很大，弹窗一打开就把几万条会话读进内存是不可接受的 —— 列举在后台线程按头部摘要跑，
/// 有上限（<see cref="SessionImportService.MaxSessions"/>），导入才做全量读取。
/// </para>
/// <para>
/// 空态要说明「去哪里找过」，而不是只说「没有」：用户下一步就得靠这句话判断是没装那个工具、
/// 还是会话库被放在了别处。
/// </para>
/// </summary>
public sealed class SessionImportDialog
{
	private const double PanelWidth = 720;
	private const double RowHeight = 52;
	private readonly Action _afterImport;
	// 来源根目录与产物目录：生产为 null（取当前用户/默认会话目录），测试注入夹具。
	private readonly string? _home;
	private readonly string? _sessionDir;
	private readonly StackPanel _sources = new();
	private readonly StackPanel _rows = new();
	private readonly TextBlock _status = new();
	private readonly ScrollViewer _scroll;
	private Window? _window;
	private string? _sourceFilter;
	private IReadOnlyList<ImportedSession> _sessions = [];
	private bool _loading;
	private bool _importing;

	/// <summary>收起后触发，宿主据此把焦点还给内容区。</summary>
	public event Action? Closed;

	public SessionImportDialog(Action afterImport, string? home = null, string? sessionDir = null)
	{
		_afterImport = afterImport;
		_home = home;
		_sessionDir = sessionDir;

		_status.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap);

		_scroll = new ScrollViewer()
			.SmoothScrolling()
			.Content(_rows)
			.MaxHeight(420)
			.AutoVerticalScroll()
			.NoHorizontalScroll();

		_sources.Spacing(AppTheme.Space1);

		var close = AppIcons.Button("x", Strings.CloseTab, Hide);
		var importAll = new Button()
			.StyleName(AppStyles.GhostButton)
			.Height(AppTheme.ControlHeightSm)
			.Content(new TextBlock().Text(Strings.ImportAll).FontSize(AppTheme.FontSizeMicro).SubtleText())
			.OnClick(ImportAll)
			.HoverTip(Strings.ImportAll);

		var body = new StackPanel()
			.Spacing(AppTheme.Space3)
			.Children(
				new DockPanel()
					.LastChildFill()
					.Children(
						new StackPanel()
							.Orientation(Orientation.Horizontal)
							.Spacing(AppTheme.Space1)
							.Children(importAll, close)
							.DockRight(),
						new TextBlock().Text(Strings.ImportTitle).FontSize(AppTheme.FontSizeTitle)
							.FontWeight(FontWeight.SemiBold).PrimaryText()
							.VerticalAlignment(VerticalAlignment.Center)),
				_sources,
				_scroll,
				_status);

		// 浮层必须是整窗容器：窗口级的 OverlayLayer 把孩子按客户区摆，
		// 只放一张定宽卡片会既没有压暗层、也定位不到中间。
		var card = new Border()
			.StyleName(AppStyles.Dialog)
			.Width(PanelWidth)
			.Padding(new Thickness(AppTheme.Space4))
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center)
			.Child(body);

		View = new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0xE0, 0, 0, 0)).BorderThickness(0).CornerRadius(0)
				.Cursor(CursorType.Arrow).Focusable(false).OnClick(Hide),
			card.OnMouseDown(args => args.Handled = true))
			.OnKeyDown(key =>
			{
				if (key.Key == Key.Escape)
				{
					key.Handled = true;
					Hide();
				}
			});
	}

	public UIElement View { get; }

	public bool IsOpen => View.IsVisible;

	/// <summary>当前列出的会话（供测试断言过滤是否生效）。</summary>
	internal IReadOnlyList<ImportedSession> Sessions => _sessions;

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window)
		{
			return;
		}

		if (IsOpen)
		{
			return;
		}

		_window = window;
		TransientOverlay.Open(window, View, Hide);
		AppStyles.ApplyTextColors(View);
		View.IsVisible(true);
		Reload();
	}

	public void Hide()
	{
		if (!IsOpen)
		{
			return;
		}

		View.IsVisible(false);
		if (_window is { } window)
		{
			TransientOverlay.Remove(window, View);
			_window = null;
		}

		Closed?.Invoke();
	}

	/// <summary>重新扫描来源并重建列表（导入后也走它，让已导入的条目排名更新）。</summary>
	internal void Reload()
	{
		if (!IsOpen)
		{
			return;
		}

		_loading = true;
		_status.Text(Strings.ImportScanning);
		_sources.Clear();
		_rows.Clear();

		// 扫描要读成百上千个文件头部：放后台线程，UI 线程只做重建。
		Task.Run(() =>
		{
			var sources = SessionImportService.DiscoverSources(_home);
			var sessions = SessionImportService.DiscoverAll(home: _home);
			return (Sources: sources, Sessions: sessions);
		}).ContinueWith(
			task =>
			{
				var (sources, sessions, error) = task.IsFaulted
					? ((IReadOnlyList<SessionImportSource>)[], (IReadOnlyList<ImportedSession>)[], task.Exception?.GetBaseException().Message)
					: (task.Result.Sources, task.Result.Sessions, null);
				Dispatch(() => FinishLoading(sources, sessions, error));
			},
			TaskScheduler.Default);
	}

	/// <summary>
	/// 同步扫描并重建（测试与宿主自检用）。真机上走 <see cref="Reload"/> 的后台路径 ——
	/// 在 UI 线程读上千个文件头部会把窗口卡住。
	/// </summary>
	internal void ReloadSync() =>
		FinishLoading(
			SessionImportService.DiscoverSources(_home),
			SessionImportService.DiscoverAll(home: _home),
			null);

	private void FinishLoading(
		IReadOnlyList<SessionImportSource> sources,
		IReadOnlyList<ImportedSession> sessions,
		string? error)
	{
		_loading = false;
		// 不在这里判 IsOpen：后台扫描回来时浮层可能刚被收起，重建本身无害；
		// 而 ReloadSync（测试/自检）根本没有 Show 过。
		_sessions = sessions;
		BuildSourceFilter(sources);
		RenderRows();

		_status.Text(error is { Length: > 0 }
			? Strings.ImportScanFailed(error)
			: sessions.Count == 0
				? Strings.ImportEmptyHint
				: Strings.ImportHint);
	}

	/// <summary>来源分段：全部 / 每个有内容的来源。一个都没有时整行不占位。</summary>
	private void BuildSourceFilter(IReadOnlyList<SessionImportSource> sources)
	{
		_sources.Clear();
		var available = sources.Where(static source => source.Count > 0).ToList();
		_sources.IsVisible(available.Count > 1);

		if (available.Count <= 1)
		{
			return;
		}

		var buttons = new List<UIElement>
		{
			Segment(Strings.ImportAllSources, _sourceFilter is null, () =>
			{
				_sourceFilter = null;
				RenderRows();
			}),
		};

		foreach (var source in available)
		{
			var key = source.Key;
			buttons.Add(Segment($"{source.Label} · {source.Count}", _sourceFilter == key, () =>
			{
				_sourceFilter = key;
				RenderRows();
			}));
		}

		_sources.Children(new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space1)
			.Children([.. buttons]));
	}

	private Button Segment(string label, bool active, Action onClick) => new Button()
		.StyleName(active ? AppStyles.SelectedButton : AppStyles.GhostButton)
		.Height(AppTheme.ControlHeightSm)
		.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
		.Content(active
			? new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).AccentText()
			: new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).SubtleText())
		.OnClick(onClick);

	private void RenderRows()
	{
		_rows.Clear();
		if (_loading)
		{
			return;
		}

		var visible = _sourceFilter is null
			? _sessions
			: [.. _sessions.Where(session => string.Equals(session.Source, _sourceFilter, StringComparison.Ordinal))];

		if (visible.Count == 0)
		{
			_rows.Children(new TextBlock()
				.Text(Strings.ImportEmptyHint)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText()
				.TextWrapping(TextWrapping.Wrap));
			return;
		}

		foreach (var session in visible)
		{
			_rows.Children(BuildRow(session));
		}
	}

	private UIElement BuildRow(ImportedSession session)
	{
		var captured = session;
		var import = new Button()
			.StyleName(AppStyles.GhostButton)
			.Height(AppTheme.ControlHeightSm)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.Content(new TextBlock().Text(Strings.ImportOne).FontSize(AppTheme.FontSizeMicro).SubtleText())
			.OnClick(() => ImportOne(captured));

		var title = session.Title.Length > 0 ? session.Title : Strings.ImportUntitled;

		return new DockPanel()
			.Height(RowHeight)
			.LastChildFill()
			.Children(
				import.DockRight(),
				new StackPanel()
					.Spacing(AppTheme.Space1)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(
						new DockPanel()
							.LastChildFill()
							.Children(
								new TextBlock()
									.Text(session.SourceLabel)
									.FontSize(AppTheme.FontSizeMicro)
									.Width(84)
									.SubtleText()
									.DockLeft(),
								new TextBlock()
									.Text(title)
									.FontSize(AppTheme.FontSizeCaption)
									.PrimaryText()
									.TextWrapping(TextWrapping.NoWrap)),
						new DockPanel()
							.LastChildFill()
							.Children(
								new TextBlock()
									.Text(session.Modified.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
									.FontFamily(AppTheme.MonoFont)
									.FontSize(AppTheme.FontSizeMicro)
									.FaintText()
									.Width(84)
									.DockLeft(),
								new TextBlock()
									.Text(ShortCwd(session.Cwd))
									.FontFamily(AppTheme.MonoFont)
									.FontSize(AppTheme.FontSizeMicro)
									.FaintText()
									.TextWrapping(TextWrapping.NoWrap))));
	}

	private void ImportOne(ImportedSession session)
	{
		if (_importing)
		{
			return;
		}

		var result = SessionImportService.Import(session, _sessionDir);
		if (result.Ok)
		{
			Toasts.Success(View, Strings.ImportDone(session.SourceLabel, result.MessageCount));
			_afterImport();
			return;
		}

		Toasts.Error(View, Strings.ImportFailed(result.Error ?? string.Empty));
	}

	private void ImportAll()
	{
		if (_loading || _importing)
		{
			return;
		}

		var targets = _sourceFilter is null
			? _sessions
			: [.. _sessions.Where(session => string.Equals(session.Source, _sourceFilter, StringComparison.Ordinal))];

		if (targets.Count == 0)
		{
			Toasts.Error(View, Strings.ImportEmptyHint);
			return;
		}

		// 导入要逐条解析源文件：几百条会在 UI 线程上卡成一片白。甩到后台线程，界面只留一句进度。
		_importing = true;
		_status.Text(Strings.ImportImporting(targets.Count));
		var pending = (IReadOnlyList<ImportedSession>)targets;
		var sessionDir = _sessionDir;

		Task.Run(() =>
		{
			var imported = 0;
			var failed = 0;
			foreach (var session in pending)
			{
				if (SessionImportService.Import(session, sessionDir).Ok)
				{
					imported++;
				}
				else
				{
					failed++;
				}
			}

			return (Imported: imported, Failed: failed);
		}).ContinueWith(
			task => Dispatch(() =>
			{
				_importing = false;
				if (task.IsFaulted)
				{
					Toasts.Error(View, Strings.ImportFailed(task.Exception?.GetBaseException().Message ?? string.Empty));
					_status.Text(Strings.ImportHint);
					return;
				}

				if (task.Result.Failed == 0)
				{
					Toasts.Success(View, Strings.ImportAllDone(task.Result.Imported));
				}
				else
				{
					Toasts.Error(View, Strings.ImportAllPartial(task.Result.Imported, task.Result.Failed));
				}

				// 重新列举：已导入的会话在源库里还在，但产物的落盘位置已经变了。
				Reload();
				_afterImport();
			}),
			TaskScheduler.Default);
	}

	/// <summary>把家目录缩成 <c>~</c>：路径列宽有限，完整路径在悬停提示里才有意义。</summary>
	private static string ShortCwd(string path)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal))
		{
			return "~" + path[home.Length..];
		}

		return path;
	}

	private static void Dispatch(Action action)
	{
		if (Application.IsRunning && Application.Current.Dispatcher is { } dispatcher)
		{
			dispatcher.Invoke(action);
			return;
		}

		action();
	}
}
