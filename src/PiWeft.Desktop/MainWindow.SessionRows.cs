using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;

namespace PiWeft.Desktop;

public sealed partial class MainWindow
{
	private IReadOnlySet<string> _pinnedSessionIds = new HashSet<string>();

	private void ConfigureSessionRows()
	{
		HideScrollbarsWhenIdle(_sessionTree);
		_sessionTree.SmoothScrolling().CornerRadius(AppTheme.RadiusMd);
		// 侧栏滚动由 TreeView 的滚动级联接住，不会冒泡到窗口根。
		_sessionTree.MouseWheel += _ => Diagnostics.NoteInput();
		_sessionTree.HorizontalScroll = ScrollMode.Disabled;
		_sessionTree.ItemHeight = 36;
		_sessionTree.Indent = 14;
		// 右侧多留滚动条的位置：滚动条是浮层式的（不占布局宽度），否则 ⋯ 按钮会被压在条下面。
		_sessionTree.ItemPadding = new Thickness(8, 2, 8 + AppTheme.Space3, 2);
		_sessionTree.ItemTemplate<TreeViewNode>(context =>
		{
			// 项目分组行带文件夹图标，会话行不带，层级一眼可辨。
			var folder = AppIcons.Icon("folder-open", AppStyles.RoleSubtle, 14)
				.Margin(new Thickness(0, 0, 6, 0));
			var title = new TextBlock().FontSize(AppTheme.FontSizeControl).PrimaryText()
				.TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center);
			var time = new TextBlock().Width(62).FontSize(AppTheme.FontSizeMicro).SubtleText()
				.VerticalAlignment(VerticalAlignment.Center);
			// 行尾「⋯」按钮：会话行弹 重命名/归档，项目行弹 移除/查看文件/新建任务。
			Button more = null!;
			more = AppIcons.Button("ellipsis", Strings.MoreActions, () => RowMenuClick(more), size: 14).Ghost();
			context.Register("more", more);
			context.Register("folder", folder);
			context.Register("title", title);
			context.Register("time", time);
			return new DockPanel().LastChildFill()
				.OnMouseDown(_ => Diagnostics.NoteInput())
				.Children(more.DockRight(), time.DockRight(), new DockPanel().LastChildFill().Children(folder.DockLeft(), title));
		}, (row, node, _, context) =>
		{
			var more = context.Get<Button>("more")!;
			var folder = context.Get<Image>("folder")!;
			var title = context.Get<TextBlock>("title")!;
			var time = context.Get<TextBlock>("time")!;
			if (node.Tag is SessionListItem item)
			{
				title.FontWeight(FontWeight.Normal).Text((_pinnedSessionIds.Contains(item.Id) ? Strings.PinnedMark + " " : "") + item.Display);
				time.Text(TimeAgo(item.Modified)).IsVisible(true);
				folder.IsVisible(false);
				more.Tag = item;
				row.HoverTip(item.Display);
			}
			else
			{
				title.Text(node.Text).FontWeight(FontWeight.Medium);
				time.IsVisible(false);
				folder.IsVisible(true);
				more.Tag = node.Tag is string cwd ? cwd : node.Text;
				row.HoverTip(node.Text);
			}
		});
	}

	private void ConfigureChatsRows()
	{
		HideScrollbarsWhenIdle(_chatsTree);
		_chatsTree.SmoothScrolling().CornerRadius(AppTheme.RadiusMd);
		_chatsTree.HorizontalScroll = ScrollMode.Disabled;
		_chatsTree.ItemHeight = 36;
		_chatsTree.Indent = 0;
		// 与「项目」视图保持一致：行右侧留出滚动条的位置。
		_chatsTree.ItemPadding = new Thickness(8, 2, 8 + AppTheme.Space3, 2);
		_chatsTree.ItemTemplate<TreeViewNode>(context =>
		{
			var title = new TextBlock().FontSize(AppTheme.FontSizeControl).PrimaryText()
				.TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center);
			var time = new TextBlock().Width(62).FontSize(AppTheme.FontSizeMicro).SubtleText()
				.VerticalAlignment(VerticalAlignment.Center);
			Button more = null!;
			more = AppIcons.Button("ellipsis", Strings.MoreActions, () => RowMenuClick(more), size: 14).Ghost();
			context.Register("more", more);
			context.Register("title", title);
			context.Register("time", time);
			return new DockPanel().LastChildFill()
				.Children(more.DockRight(), time.DockRight(), title);
		}, (row, node, _, context) =>
		{
			var more = context.Get<Button>("more")!;
			var title = context.Get<TextBlock>("title")!;
			var time = context.Get<TextBlock>("time")!;
			if (node.Tag is SessionListItem item)
			{
				title.FontWeight(FontWeight.Normal).Text((_pinnedSessionIds.Contains(item.Id) ? Strings.PinnedMark + " " : "") + item.Display);
				time.Text(TimeAgo(item.Modified)).IsVisible(true);
				more.Tag = item;
				row.HoverTip(item.Display);
			}
			else
			{
				title.Text(node.Text).FontWeight(FontWeight.Normal);
				time.IsVisible(false);
				more.Tag = null;
				row.HoverTip(node.Text);
			}
		});
	}

	/// 列表的滚动条是 MewUI 的浮层条：不占布局宽度，默认常显，会压在行的右缘上。
	/// 打开自动隐藏后，静止时完全淡出、滚动或悬停条本身时淡入，平时列表里看不到条。
	private static void HideScrollbarsWhenIdle(Element view)
	{
		if (view is ScrollViewer scroll)
		{
			scroll.AutoHideScrollBars = true;
		}

		if (view is IVisualTreeHost host)
		{
			host.VisitChildren(child =>
			{
				HideScrollbarsWhenIdle(child);
				return true;
			});
		}
	}

	private void RowMenuClick(Button button)
	{
		switch (button.Tag)
		{
			case SessionListItem item:
				ShowSessionRowMenu(button, item);
				break;
			case string cwd:
				ShowProjectRowMenu(button, cwd);
				break;
		}
	}

	private void ShowSessionRowMenu(FrameworkElement owner, SessionListItem item)
	{
		var menu = new ContextMenu();
		void Add(string label, Action action)
		{
			var command = new Command($"pi.session-row.{menu.Items.Count}", label);
			owner.Commands.Register(command, action);
			menu.Items.Add(new MenuItem(command));
		}

		Add(Strings.SessionMenuRename, () => StartRename(item));
		Add(Strings.SessionMenuArchive, () => ArchiveSession(item));
		menu.Show(owner);
	}

	private void ShowProjectRowMenu(FrameworkElement owner, string cwd)
	{
		var menu = new ContextMenu();
		void Add(string label, Action action)
		{
			var command = new Command($"pi.project-row.{menu.Items.Count}", label);
			owner.Commands.Register(command, action);
			menu.Items.Add(new MenuItem(command));
		}

		Add(Strings.ProjectMenuNewTask, () => NewSessionIn(cwd));
		// 没有 cwd 的旧会话聚成「?」分组：它不是一个目录，没有可打开或移除的项目记录。
		if (cwd.Length > 0)
		{
			Add(Strings.ProjectMenuReveal, () => Shell.Open(cwd));
			Add(Strings.ProjectMenuRemove, () => RemoveProject(cwd));
		}

		menu.Show(owner);
	}

	private void ShowSidebarView(bool projects, bool persist = true)
	{
		_projectsTab = projects;
		_sessionsTabButton.StyleName(projects ? AppStyles.GhostButton : AppStyles.SelectedButton);
		_projectsTabButton.StyleName(projects ? AppStyles.SelectedButton : AppStyles.GhostButton);
		_newSessionButton.IsVisible(!projects);
		_addProjectButton.IsVisible(projects);
		_sessionTree.IsVisible(projects);
		_chatsTree.IsVisible(!projects);
		UpdateSidebarEmptyHints();

		if (!persist)
		{
			return;
		}

		// 与 PiDeck 一样记住上次停在哪个分段，下次启动直接回到同一视图。
		try
		{
			var preference = DesktopPreference.Load();
			if (preference.SidebarProjects != projects)
			{
				preference.WithSidebarProjects(projects).Save();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Save sidebar tab", ex);
		}
	}

	/// 分段里的空态提示：列表空了也不会只剩一片空白；扫描途中说明在读取，别显示成「还没有」。
	private void UpdateSidebarEmptyHints()
	{
		var scanning = !_sessionsLoaded && _sessionsLoad is not null;
		_projectsEmpty
			.Text(scanning ? Strings.SessionsScanning : Strings.ProjectsEmpty)
			.IsVisible(_projectsTab && _sessionNodes.Count == 0);
		_chatsEmpty
			.Text(scanning ? Strings.SessionsScanning : Strings.ChatsEmpty)
			.IsVisible(!_projectsTab && _chatNodes.Count == 0);
	}

	/// 扫描刚开始：<see cref="_sessionsLoad"/> 要到返回后才赋值，先手写一次「读取中」。
	private void ShowSidebarScanning()
	{
		if (_projectsTab) _projectsEmpty.Text(Strings.SessionsScanning).IsVisible(true);
		else _chatsEmpty.Text(Strings.SessionsScanning).IsVisible(true);
	}

	/// 添加项目后的揭示：侧栏收着先展开，再切到「项目」分段。少了这两步，
	/// 用户看到的只是原列表不变，以为添加没生效。
	private void RevealSidebarProjects()
	{
		if (!_sidebarPreferred)
		{
			_sidebarPreferred = true;
			ApplySidebarLayout();
			SaveWindowState();
		}

		ShowSidebarView(projects: true);
	}

	/// 展开项目行并滚进可视区。不抢当前选中项：选中行代表正在打开的会话。
	/// <paramref name="path"/> 是 <see cref="SessionGroups.ProjectPath"/> 规范化后的目录，与行标签同形。
	private void RevealProject(string path)
	{
		var node = _sessionNodes.FirstOrDefault(node =>
			node.Tag is string project && project.Length > 0 && SessionGroups.SameKey(project, path));
		if (node is null)
		{
			return;
		}

		_sessionTree.Expand(node);

		// ScrollIntoView 收的是展开后的可见行序号；自己数一遍，不去动选中项。
		var index = 0;
		foreach (var candidate in _sessionNodes)
		{
			if (ReferenceEquals(candidate, node))
			{
				_sessionTree.ScrollIntoView(index);
				return;
			}

			index += 1 + (_sessionTree.IsExpanded(candidate) ? candidate.Children.Count : 0);
		}
	}

	private void NewChatSession()
	{
		try
		{
			Directory.CreateDirectory(_chatWorkspace);
			NewSessionIn(_chatWorkspace);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Create chat session", ex);
			_banner.Text(Strings.ChatWorkspaceFailed(ex.Message));
		}
	}

	private void NewSessionFromCurrentView()
	{
		if (!_projectsTab)
		{
			NewChatSession();
			return;
		}

		NewSessionIn(CurrentSession()?.Cwd ?? _cwd);
	}

	private void NewSessionIn(string cwd)
	{
		// 没有工作目录的旧会话会聚成「?」分组：给它开新任务时退回窗口目录，避免空路径。
		OpenSession(_catalog.Create(string.IsNullOrWhiteSpace(cwd) ? _cwd : cwd));
		RefreshSessions(force: true);
	}

	private void StartRename(SessionListItem item)
	{
		_renamingPath = item.Path;
		_nameBox.Text = item.Name;
		_nameBox.IsVisible(true);
		_nameBox.Focus();
	}

	private void ArchiveSession(SessionListItem item)
	{
		try
		{
			_catalog.Delete(item.Path);
			foreach (var tab in _tabEntries.OfType<SessionTab>()
				.Where(tab => string.Equals(tab.Session.Session.SessionFile, item.Path, StringComparison.OrdinalIgnoreCase))
				.ToArray())
			{
				CloseTab(tab);
			}

			RefreshSessions(force: true);
			_banner.Text(Strings.SessionArchived);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Archive session", ex);
			_banner.Text(Strings.OpenSessionFailed(ex.Message));
		}
	}

	private void RemoveProject(string cwd)
	{
		// 「?」分组没有移除入口；这里兜一层，避免把空串写进已移除清单。
		if (cwd.Length == 0)
		{
			return;
		}

		try
		{
			DesktopPreference.Load().WithoutProject(cwd).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Remove project", ex);
			_banner.Text(Strings.ProjectRemoveFailed(ex.Message));
			return;
		}

		RefreshSessions(force: true);
		_banner.Text(Strings.ProjectRemoved);
	}
}
