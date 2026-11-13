using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// 会话搜索弹窗：侧栏「搜索会话」入口或 Ctrl+K 打开。先按名称、首条消息、路径与文件名过滤，
/// 再在后台扫会话正文追加内容命中；方向键选择、Enter 打开、Esc 或点空白处收起（背景不变暗）。
public sealed class SessionSearchPalette
{
	private const double RowHeight = 50;
	private const double PanelWidth = 640;
	// 结果列表不是虚拟化控件（每行都是带 hover 提示的按钮）：会话很多时只铺有界的一段，
	// 剩下的靠继续输入缩小范围。这与 PiDeck 的有界结果列表一致，避免一次搜索构造上千行。
	private const int MaxRows = 200;
	private readonly Func<IReadOnlyList<SessionListItem>> _sessions;
	private readonly Action<SessionListItem> _open;
	// 侧栏摘要改成按需加载后，搜索可能先于列表就绪打开：此时给出「正在读取」，不是「没有匹配」。
	private readonly Func<bool> _loading;
	private readonly TextBox _query;
	private readonly TextBlock _hint;
	private readonly StackPanel _results = new();
	private readonly ScrollViewer _scroll;
	private readonly List<Button> _rows = [];
	private readonly List<SessionListItem> _targets = [];
	private readonly TextBlock _status = new();
	private IReadOnlyList<SessionListItem> _matches = [];
	private int _selectedIndex;
	private int _version;
	private CancellationTokenSource? _contentSearch;
	private Window? _window;
	private readonly OverlayFade _fade;
	// 全量会话：侧栏只留最新 200 条，搜索要能翻到更旧的。打开弹窗后后台装一次，
	// 关闭时丢掉（侧栏刷新后会话文件已经变了）。
	private readonly Func<Task<IReadOnlyList<SessionListItem>>>? _everySession;
	private IReadOnlyList<SessionListItem>? _everySessionRows;
	// 「正在装」用独立标记，不能拿任务本身当标记：宿主缓存命中时装载同步跑完，
	// 任务赋值会落在清空之后，字段就永远非空，全量列表再也装不上。
	private bool _everySessionLoading;

	public event Action? Closed;

	public SessionSearchPalette(
		Func<IReadOnlyList<SessionListItem>> sessions,
		Action<SessionListItem> open,
		Func<bool>? loading = null,
		Func<Task<IReadOnlyList<SessionListItem>>>? everySession = null)
	{
		_sessions = sessions;
		_open = open;
		_loading = loading ?? (static () => false);
		_everySession = everySession;

		// 提示文本自己画：框架的 Placeholder 聚焦即隐藏，而弹窗一打开就聚焦在输入框上。
		_hint = new TextBlock().Text(Strings.SessionSearch)
			.FontSize(AppTheme.FontSizeControl).SubtleText()
			.VerticalAlignment(VerticalAlignment.Center).IsHitTestVisible(false);

		_query = new TextBox().PrimarySelection().FontSize(AppTheme.FontSizeControl)
			.Background(AppTheme.Transparent).BorderThickness(0).Padding(Thickness.Zero)
			.OnTextChanged(value =>
			{
				_hint.IsVisible(value.Length == 0);
				Refresh(value);
			})
			.OnKeyDown(key =>
			{
				switch (key.Key)
				{
					case Key.Escape:
						key.Handled = true;
						Hide();
						break;
					case Key.Enter:
						key.Handled = true;
						if (_targets.Count > 0)
						{
							Open(_targets[_selectedIndex]);
						}
						break;
					case Key.Down:
					case Key.Up:
						key.Handled = true;
						Select(_selectedIndex + (key.Key == Key.Down ? 1 : -1));
						break;
				}
			});

		// 搜索行：放大镜 + 无边框输入框，右侧是收起提示（键名不翻译）。
		var icon = AppIcons.Icon("search", AppStyles.RoleSubtle, 16)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0));
		var esc = new Border().Padding(new Thickness(6, 1, 6, 1)).CornerRadius(AppTheme.RadiusXs)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, badge) => badge.Background = AppTheme.For(theme).CardBackgroundAlt)
			.Child(new TextBlock().Text("Esc").FontSize(AppTheme.FontSizeMicro).SubtleText());
		var searchRow = new DockPanel().LastChildFill().Children(esc.DockRight(),
			new DockPanel().LastChildFill().Children(icon.DockLeft(), new Grid().Children(_query, _hint)));

		_results.Spacing(2);
		_scroll = new ScrollViewer().SmoothScrolling().Content(_results).AutoVerticalScroll().NoHorizontalScroll()
			.MaxHeight(420);
		_status.FontSize(AppTheme.FontSizeCaption).MutedText().IsVisible(false);

		var panel = new Border()
			.StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusMd)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.Padding(new Thickness(AppTheme.Space3))
			.Child(new StackPanel().Spacing(AppTheme.Space3).Children(searchRow, _scroll, _status));

		// 空白处的透明捕获层只负责收起；卡片在其上，不受影响。
		View = new Grid().IsVisible(false).Children(
			new Button().Background(AppTheme.Transparent).Cursor(CursorType.Arrow)
				.Focusable(false).BorderThickness(0).CornerRadius(0).OnClick(Hide),
			new SearchSurface().MaxWidth(PanelWidth)
				.HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Top)
				.Margin(new Thickness(AppTheme.Space6, 56, AppTheme.Space6, AppTheme.Space6))
				.Content(panel))
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Escape) return;
				key.Handled = true;
				Hide();
			});
		_fade = new OverlayFade((FrameworkElement)View);
	}

	public UIElement View { get; }
	public bool IsOpen => View.IsVisible;

	internal IReadOnlyList<SessionListItem> Matches => _targets;

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (IsOpen)
		{
			if (_fade.IsClosing) _fade.Reopen();
			_query.Focus();
			return;
		}

		_query.Text = string.Empty;
		_hint.IsVisible(true);
		// 每次打开重新装全量列表：侧栏刷新后旧列表里的预览与修改时间已经不对了。
		_everySessionRows = null;
		_window = window;
		TransientOverlay.Open(window, View, Hide);
		AppStyles.ApplyTextColors(View);
		View.IsVisible(true);
		// 先可见再刷新：宿主缓存命中时全量列表同步就位，这一次就能直接渲染出完整结果。
		Refresh();
		_fade.Open();
		_query.Focus();
	}

	public void Hide()
	{
		if (!IsOpen || _fade.IsClosing) return;
		_contentSearch?.Cancel();
		_contentSearch = null;
		_version++;
		_fade.Close(CloseNow);
	}

	private void CloseNow()
	{
		View.IsVisible(false);
		if (_window is { } window)
		{
			TransientOverlay.Remove(window, View);
			_window = null;
		}

		_matches = [];
		_results.Clear();
		_rows.Clear();
		_targets.Clear();
		Closed?.Invoke();
	}

	public void Toggle(UIElement owner)
	{
		if (IsOpen)
		{
			Hide();
		}
		else
		{
			Show(owner);
		}
	}

	private void Open(SessionListItem session)
	{
		Hide();
		_open(session);
	}

	internal void Reload()
	{
		if (!IsOpen) return;
		Refresh();
	}

	private void Refresh(string? queryText = null)
	{
		// 全量列表没到之前先用侧栏的那 200 条，结果马上有；到了之后由下一轮 Refresh 换掉。
		var all = Volatile.Read(ref _everySessionRows) ?? _sessions();
		var query = queryText ?? _query.Text;
		_matches = SessionCatalog.Search(all, query);
		Render(_matches, []);
		StartContentSearch(all, query);
		StartEverySessionLoad();
	}

	/// 全量列表每次打开装一次：装的过程中用户继续敲字，等它回来再重渲染。
	private void StartEverySessionLoad()
	{
		if (_everySession is null || _everySessionRows is not null || _everySessionLoading)
		{
			return;
		}

		_everySessionLoading = true;
		_ = LoadEverySessionAsync();
	}

	private async Task LoadEverySessionAsync()
	{
		try
		{
			var rows = await _everySession!().ConfigureAwait(false);
			// 先存下再回 UI 线程：没有消息循环时（无窗口测试）也能拿到结果。
			Volatile.Write(ref _everySessionRows, rows);
			Dispatch(() =>
			{
				_everySessionLoading = false;
				if (IsOpen)
				{
					Refresh();
				}
			});
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionSearchPalette>(), "List all sessions for search", ex);
			_everySessionLoading = false;
		}
	}

	private void Render(IReadOnlyList<SessionListItem> metadata, IReadOnlyList<SessionCatalog.ContentMatch> content)
	{
		_results.Clear();
		_rows.Clear();
		_targets.Clear();
		_selectedIndex = 0;
		_scroll.SetScrollOffsets(0, 0);

		// 相邻会话同属一个工作目录时只在组首显示路径，列表读起来是「项目 → 会话」两行一块。
		string? group = null;
		foreach (var item in metadata)
		{
			if (_targets.Count >= MaxRows) break;
			var folder = Folder(item);
			var showFolder = !string.Equals(folder, group, StringComparison.Ordinal);
			group = folder;
			AddRow(item, "message-square", item.Display, showFolder ? folder : null);
		}

		var seen = metadata.Select(static item => item.Path).ToHashSet(StringComparer.Ordinal);
		foreach (var match in content)
		{
			if (_targets.Count >= MaxRows) break;
			if (!seen.Add(match.Session.Path)) continue;
			AddRow(match.Session, "file-text", match.Session.Display, $"{Strings.ContentHit} · {match.Excerpt}");
		}

		if (_targets.Count == 0)
		{
			// 列表还在后台扫：说「正在读取」，不要说「暂无会话」。
			// 全量列表到手就等于知道答案了：这时说「暂无会话」，不能说成还在读取。
			var known = Volatile.Read(ref _everySessionRows) ?? _sessions();
			var text = _everySessionRows is null && _loading()
				? Strings.SessionsScanning
				: known.Count == 0 ? Strings.NoSessions : Strings.NoMatches;
			_status.Text(text).IsVisible(true);
			return;
		}

		// 命中超过上限时明确说明已截断，读者知道还能靠继续输入缩小范围。
		var total = metadata.Count;
		if (total > _targets.Count)
		{
			_status.Text(Strings.SessionSearchTruncated(_targets.Count, total)).IsVisible(true);
		}
		else
		{
			_status.IsVisible(false);
		}
		Select(0);
	}

	private void AddRow(SessionListItem session, string icon, string title, string? detail)
	{
		var image = AppIcons.Icon(icon, AppStyles.RoleSubtle, 15)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0));
		var lines = new StackPanel().Spacing(1).VerticalAlignment(VerticalAlignment.Center).Children(
			new TextBlock().Text(title).FontSize(AppTheme.FontSizeControl).PrimaryText()
				.TextTrimming(TextTrimming.CharacterEllipsis));
		if (detail is { Length: > 0 })
		{
			lines.Children(new TextBlock().Text(detail).FontSize(AppTheme.FontSizeMicro).SubtleText()
				.TextTrimming(TextTrimming.CharacterEllipsis));
		}

		var row = new Button().Height(RowHeight)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.HoverTip($"{title}\n{Folder(session)}")
			.Content(new DockPanel().LastChildFill().Children(image.DockLeft(), lines))
			.OnClick(() => Open(session));
		_rows.Add(row);
		_targets.Add(session);
		_results.Children(row);
	}

	private static string Folder(SessionListItem item) =>
		item.Cwd.Length > 0 ? item.Cwd : Path.GetDirectoryName(item.Path) ?? item.Path;

	private void StartContentSearch(IReadOnlyList<SessionListItem> all, string? query)
	{
		_contentSearch?.Cancel();
		var trimmed = query?.Trim() ?? string.Empty;
		var version = ++_version;
		if (trimmed.Length == 0) return;

		var known = _matches.Select(static item => item.Path).ToHashSet(StringComparer.Ordinal);
		var candidates = all.Where(item => !known.Contains(item.Path)).ToList();
		if (candidates.Count == 0) return;

		var cancellation = new CancellationTokenSource();
		_contentSearch = cancellation;
		_ = SearchContentAsync(candidates, trimmed, version, cancellation);
	}

	private async Task SearchContentAsync(IReadOnlyList<SessionListItem> candidates, string query, int version,
		CancellationTokenSource cancellation)
	{
		try
		{
			await Task.Delay(220, cancellation.Token).ConfigureAwait(false);
			var matches = await Task.Run(() => SessionCatalog.SearchContent(candidates, query,
				cancellationToken: cancellation.Token), cancellation.Token).ConfigureAwait(false);
			Dispatch(() =>
			{
				if (version != _version || !IsOpen) return;
				Render(_matches, matches);
			});
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<SessionSearchPalette>(), "Search session content", ex);
		}
		finally
		{
			Dispatch(() =>
			{
				if (ReferenceEquals(_contentSearch, cancellation)) _contentSearch = null;
				cancellation.Dispose();
			});
		}
	}

	private void Select(int index)
	{
		if (_targets.Count == 0) return;

		_selectedIndex = Math.Clamp(index, 0, _targets.Count - 1);
		for (var position = 0; position < _rows.Count; position++)
		{
			_rows[position].StyleName(position == _selectedIndex ? AppStyles.SelectedButton : AppStyles.GhostButton);
		}

		var top = _selectedIndex * (RowHeight + 2);
		if (top < _scroll.VerticalOffset)
		{
			_scroll.SetScrollOffsets(0, top);
		}
		else if (top + RowHeight > _scroll.VerticalOffset + _scroll.ViewportHeight)
		{
			_scroll.SetScrollOffsets(0, top + RowHeight - _scroll.ViewportHeight);
		}
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

	private sealed class SearchSurface : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var width = Math.Min(PanelWidth, availableSize.Width);
			var measured = base.MeasureContent(new Size(width, availableSize.Height));
			return new Size(width, measured.Height);
		}
	}
}
