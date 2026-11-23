using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;
using PiWeft.CodingAgent.Subagents;

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
		// TreeView 的行命中与 ScrollIntoView 按 TreeView.Padding 计算视口，内部 ScrollViewer
		// 的 Padding 也绑定到它。只改 ScrollViewer 会让内容与命中区域错位；这里显式扩右侧槽位，
		// 其它方向沿用主题默认的 4，滚动条、行背景与命中区域才会落在同一位置。
		_sessionTree.Padding(new Thickness(4, 4, 10, 4));
		_sessionTree.ItemPadding = new Thickness(8, 2, 8, 2);
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
				title.FontWeight(FontWeight.Normal).Text(node.Text);
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
		// 与「项目」视图保持一致：命中区域与滚动视口共用 TreeView.Padding。
		_chatsTree.Padding(new Thickness(4, 4, 10, 4));
		_chatsTree.ItemPadding = new Thickness(8, 2, 8, 2);
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
				title.FontWeight(FontWeight.Normal).Text(node.Text);
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

	private async void ArchiveSession(SessionListItem item)
	{
		try
		{
			// 关联子会话挂在父会话下面：父会话归档时一起收走，否则侧栏留下没有父的幽灵会话。
			var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item.Path };
			var links = PiWeft.CodingAgent.LinkedSessionStore.Load().Links
				.Where(link => string.Equals(link.ParentSessionId, item.Id, StringComparison.Ordinal))
				.ToArray();
			if (links.Length > 0)
			{
				// 损坏的会话文件可能撞 id：ToDictionary 会抛 ArgumentException，直接换成保留第一条。
				var byId = new Dictionary<string, SessionListItem>(StringComparer.Ordinal);
				foreach (var session in _catalog.List(null, 5000))
				{
					byId.TryAdd(session.Id, session);
				}

				foreach (var link in links)
				{
					if (byId.TryGetValue(link.ChildSessionId, out var child))
					{
						removedPaths.Add(child.Path);
					}
				}

				// 关系条目也要清：否则下次打开同名会话时还会挂出已归档的子会话。
				PiWeft.CodingAgent.LinkedSessionStore.Merge(
					item.Id,
					item.Cwd,
					new PiWeft.CodingAgent.LinkedSessionSnapshot([], []));
			}

			// 先关标签再删文件：CloseAsync 会 abort 并等收尾，反过来做的话
			// 收尾里的 Append 会把已移走的路径重建出一个无头会话。
			var tabs = _tabEntries.OfType<SessionTab>()
				.Where(tab => tab.Session.Session.SessionFile is { } path && removedPaths.Contains(path))
				.ToArray();

			// 有子代理在跑时只问一次；用户取消就什么都不删，标签与文件都保持原样。
			var running = 0;
			foreach (var tab in tabs)
			{
				if (!tab.DetachedSession && tab.Session.Subagents is { } hub)
				{
					running += hub.ActiveRunCount();
				}
			}

			if (running > 0 && !await MessageBox.ConfirmAsync(Strings.SubagentCloseConfirm(running)))
			{
				return;
			}

			foreach (var tab in tabs)
			{
				var id = SessionIdOf(tab);
				await CloseTabAsync(tab, confirmRunningSubagents: false).ConfigureAwait(true);
				// 子代理会话被归档：先停那一轮，再把运行记录摘掉，父 hub 才不会把它加回索引。
				if (_subagentStatus.TryGet(id, out var entry))
				{
					StopAndForgetSubagent(entry.ParentSessionId, id);
				}

				_subagentStatus.RemoveSession(id);
			}

			// 关联子会话没有标签时也要清掉它们名下的子代理索引。
			foreach (var link in links)
			{
				_subagentStatus.RemoveParent(link.ChildSessionId);
			}

			foreach (var path in removedPaths)
			{
				_catalog.Delete(path);
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

	// 项目路径规范化结果按原串缓存：侧栏刷新时 (标签 × 会话) 的双循环不再重复 GetFullPath。
	private readonly Dictionary<string, string> _projectKeys = new(StringComparer.Ordinal);
	// 每个标签的占用条目在 UI 线程串行收集；复用列表，稳定刷新不每次 new。
	private readonly List<WorkspaceNoticeEntry> _workspaceNotices = [];
	// 每个标签每次刷新都传同一个跳转回调：缓存一份，不再为每个标签分配闭包。
	private Action<string>? _workspaceOpen;
	private readonly HashSet<string> _changedSubagentSessions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, (int Count, int Writers)> _projectRowStats = new(StringComparer.Ordinal);

	private string ProjectKey(string path)
	{
		if (_projectKeys.TryGetValue(path, out var key))
		{
			return key;
		}

		key = SessionGroups.SafeProjectPath(path);
		_projectKeys[path] = key;
		return key;
	}

	/// 同一项目里其他会话在跑或改过文件时，给每个标签推工作台条目。
	/// 不再只取第一个：卡头按状态汇总数量，展开后可以分别跳到每个会话。
	private void UpdateWorkspaceNotices()
	{
		// 关联子会话挂在父行下面，不是用户眼里的「另一个会话」；也跳过它们的占用提示。
		HashSet<string> linkedChildren = _linkedLinks.Count > 0
			? [.. _linkedLinks.Select(static link => link.ChildSessionId)]
			: [];
		_workspaceOpen ??= id => _ = ActivateSessionAsync(id);
		foreach (var tab in _tabEntries.OfType<SessionTab>())
		{
			var tabId = SessionIdOf(tab);
			var tabKey = ProjectKey(tab.Session.Cwd);
			var ownFiles = _sessionActivity.Get(tabId)?.Files ?? [];
			_workspaceNotices.Clear();
			foreach (var item in _sessions)
			{
				if (string.Equals(item.Id, tabId, StringComparison.Ordinal) ||
					string.Equals(item.Origin, "subagent", StringComparison.Ordinal) ||
					linkedChildren.Contains(item.Id) ||
					!SessionGroups.SameKey(ProjectKey(item.Cwd), tabKey))
				{
					continue;
				}

				if (DescribeWorkspaceUsage(item, ownFiles) is { } usage)
				{
					_workspaceNotices.Add(new WorkspaceNoticeEntry(
						item.Id, usage.Kind, usage.Title, usage.ChangedFiles, usage.FileName, usage.Subagent));
				}
			}

			tab.SetWorkspaceNotice(_workspaceNotices, _workspaceOpen);
		}
	}

	private WorkspaceUsage? DescribeWorkspaceUsage(SessionListItem item, IReadOnlyList<string> ownFiles)
	{
		_subagentSummaries.TryGetValue(item.Id, out var summary);
		if (summary.Active > 0)
		{
			return new(WorkspaceNoticeKind.Running, item.Display, Subagent: true);
		}

		var activity = _sessionActivity.Get(item.Id);
		if (activity is { Streaming: true })
		{
			return new(WorkspaceNoticeKind.Running, item.Display);
		}

		if (summary.Changed > 0)
		{
			return new(WorkspaceNoticeKind.Changed, item.Display, Subagent: true);
		}

		if (activity is { ChangedFiles: > 0 } && ownFiles.Count > 0)
		{
			foreach (var file in activity.Files)
			{
				if (ownFiles.Contains(file, StringComparer.Ordinal))
				{
					return new(WorkspaceNoticeKind.SameFile, item.Display, FileName: Path.GetFileName(file));
				}
			}
		}

		return activity is { ChangedFiles: > 0 }
			? new(WorkspaceNoticeKind.Changed, item.Display, activity.ChangedFiles)
			: null;
	}

	/// 子代理状态边沿只改行文字，不重建 TreeViewNode；返回是否有节点文本真的变了。
	/// 集合用具体 HashSet：每个节点都要 Contains，接口参数会把去虚拟化留在门外。
	private void RefreshSessionRows(HashSet<string> sessionIds)
	{
		if (sessionIds.Count == 0)
		{
			return;
		}

		var preference = DesktopPreference.Load();
		var changed = RefreshNodeTexts(_sessionNodes, sessionIds, preference);
		changed |= RefreshNodeTexts(_chatNodes, sessionIds, preference);
		// 子代理的改动数也会进项目行的「N 个在写」，不能只刷会话行。
		changed |= RefreshProjectTexts();
		if (!changed)
		{
			return;
		}

		if (_sessionTree.ItemsSource is TreeItemsView<TreeViewNode> sessions)
		{
			sessions.Invalidate();
		}

		if (_chatsTree.ItemsSource is TreeItemsView<TreeViewNode> chats)
		{
			chats.Invalidate();
		}
	}

	private bool RefreshNodeTexts(
		IReadOnlyList<TreeViewNode> nodes,
		HashSet<string> sessionIds,
		DesktopPreference preference)
	{
		var changed = false;
		foreach (var node in nodes)
		{
			if (node.Tag is SessionListItem item && sessionIds.Contains(item.Id))
			{
				var text = SessionLine(item, preference, ChildCounts(node));
				if (!string.Equals(node.Text, text, StringComparison.Ordinal))
				{
					node.Text = text;
					changed = true;
				}
			}

			if (node.Children.Count > 0)
			{
				changed |= RefreshNodeTexts(node.Children, sessionIds, preference);
			}
		}

		return changed;
	}

	private bool RefreshProjectTexts()
	{
		var changed = false;
		foreach (var node in _sessionNodes)
		{
			if (node.Tag is not string cwd)
			{
				continue;
			}

			var count = 0;
			var writers = 0;
			CountProjectSessions(node, ref count, ref writers);
			if (_projectRowStats.TryGetValue(cwd, out var previous) &&
				previous.Count == count && previous.Writers == writers)
			{
				continue;
			}

			_projectRowStats[cwd] = (count, writers);
			var text = ProjectLabel(cwd, count, writers);
			if (string.Equals(node.Text, text, StringComparison.Ordinal))
			{
				continue;
			}

			node.Text = text;
			changed = true;
		}

		return changed;
	}

	private void CountProjectSessions(TreeViewNode node, ref int count, ref int writers)
	{
		foreach (var child in node.Children)
		{
			if (child.Tag is SessionListItem item)
			{
				count++;
				if (_subagentSummaries.TryGetValue(item.Id, out var summary) && summary.Changed > 0)
				{
					writers++;
				}
				else if (_sessionActivity.Get(item.Id) is { ChangedFiles: > 0 })
				{
					writers++;
				}
			}

			if (child.Children.Count > 0)
			{
				CountProjectSessions(child, ref count, ref writers);
			}
		}
	}

	private static string ChildCounts(TreeViewNode node)
	{
		if (node.Children.Count == 0)
		{
			return string.Empty;
		}

		var subagents = 0;
		var linked = 0;
		foreach (var child in node.Children)
		{
			if (child.Tag is SessionListItem { Origin: "subagent" })
			{
				subagents++;
			}
			else
			{
				linked++;
			}
		}

		return $" · {Strings.SidebarChildCounts(subagents, linked)}";
	}

	/// 父会话自己的活动：流式中，或最后一条有改动的助手行。
	/// 返回 true 表示侧栏要重建。子代理的改动由 <see cref="SubagentStatusIndex"/> 单独算。
	private bool UpdateSessionActivity(SessionTab tab)
	{
		var sessionId = SessionIdOf(tab);
		if (tab.IsStreaming)
		{
			return _sessionActivity.Update(sessionId, true, []);
		}

		IReadOnlyList<ChatFileChange> changed = [];
		foreach (var entry in tab.Chat.Entries())
		{
			if (entry.ChangedFiles.Count > 0)
			{
				// 只留最后一轮：拖几轮的会话不必把历史改动全部算进占用。
				changed = entry.ChangedFiles;
			}
		}

		return _sessionActivity.Update(sessionId, false, changed);
	}

	/// 子代理状态变化：把本窗口所有标签的 hub 快照汇进索引，真变了只刷新受影响行文本。
	/// 多个子代理同时收尾时，一帧只汇总一次；已关标签不在遍历里，迟到的 Changed 写不回来。
	private void ApplySubagentStatuses()
	{
		if (_sessionHostClosed)
		{
			return;
		}

		var changed = false;
		var changedSessions = _changedSubagentSessions;
		var current = CurrentTab();
		changedSessions.Clear();
		foreach (var tab in _tabEntries.OfType<SessionTab>())
		{
			if (tab.Session.Subagents is { } hub)
			{
				var runs = hub.RunsSnapshot();
				// 失败不会自己弹卡片：人可能在别的标签里，至少给一条轻提示。
				// 成功只在父标签不在前台时提示，当前标签里能看到卡片就不重复弹。
				foreach (var run in runs)
				{
					if (!run.IsTerminal ||
						!_subagentStatus.TryGet(run.SessionId, out var finished) ||
						!finished.IsActive)
					{
						continue;
					}

					if (run.State == SubagentState.Failed)
					{
						Toasts.Error(this, Strings.SubagentFailedNotice(run.Name, SubagentStatusIndex.FailureText(run) ?? string.Empty));
					}
					else if (run.State == SubagentState.Idle &&
						!ReferenceEquals(tab, current) &&
						(current is null || !string.Equals(SessionIdOf(current), run.SessionId, StringComparison.Ordinal)))
					{
						Toasts.Success(this, Strings.SubagentCompletedNotice(run.Name));
					}
				}

				if (hub.Team is { } team)
				{
					foreach (var run in runs)
					{
						if (!_subagentStatus.TryGet(run.SessionId, out var previous) ||
							!previous.IsActive ||
							!run.IsTerminal)
						{
							continue;
						}

						var task = team.Tasks.FirstOrDefault(candidate =>
							candidate.Status == TeamTaskStatus.InProgress &&
							string.Equals(candidate.Owner, run.Name, StringComparison.Ordinal));
						if (task is not null)
						{
							Toasts.Show(this, Strings.TeamTaskOwnerFinished(run.Name, task.Subject));
						}
					}
				}

				changed |= _subagentStatus.Update(SessionIdOf(tab), tab.Title, runs, changedSessions);
			}
		}

		if (changed && _sessionsLoaded)
		{
			// 不重建整棵树：只刷新受影响的会话行文本，再让 TreeItemsView 重置可见行绑定。
			_subagentSummaries = _subagentStatus.Summaries();
			RefreshSessionRows(changedSessions);
			// 工作台的跨会话占用也读这份摘要：子代理起止就是「另一个会话在跑」的信号，
			// 不跟上的话卡头和条目要等到下一次全量侧栏重建才更新。
			UpdateWorkspaceNotices();
		}
	}

	/// 删除子代理会话文件：先停掉那一轮，再把运行记录从父 hub 里摘掉。
	/// 只摘索引不够，父 hub 的下一份快照会把还在跑的条目加回来。
	private void StopAndForgetSubagent(string parentSessionId, string sessionId)
	{
		foreach (var tab in _tabEntries.OfType<SessionTab>())
		{
			if (string.Equals(SessionIdOf(tab), parentSessionId, StringComparison.Ordinal) &&
				tab.Session.Subagents is { } hub)
			{
				hub.Stop(sessionId);
				hub.Forget(sessionId);
				return;
			}
		}
	}

	/// hub 的事件来自子会话线程；同一帧里的多次 Changed 只排一次汇总。
	private void QueueSubagentStatusRefresh()
	{
		if (Interlocked.Exchange(ref _subagentStatusQueued, 1) == 1)
		{
			return;
		}

		Dispatch(() =>
		{
			Interlocked.Exchange(ref _subagentStatusQueued, 0);
			ApplySubagentStatuses();
		});
	}
}
