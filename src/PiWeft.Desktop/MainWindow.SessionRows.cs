using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;

namespace PiWeft.Desktop;

public sealed partial class MainWindow
{
	private IReadOnlySet<string> _pinnedSessionIds = new HashSet<string>();

	/// <summary>Virtualized rows keep time visible while titles ellipsize rather than forcing horizontal scrolling.</summary>
	private void ConfigureSessionRows()
	{
		HideScrollbarsWhenIdle(_sessionTree);
		_sessionTree.SmoothScrolling().CornerRadius(AppTheme.RadiusMd);
		_sessionTree.HorizontalScroll = ScrollMode.Disabled;
		_sessionTree.ItemHeight = 36;
		_sessionTree.Indent = 14;
		// 右侧多留滚动条的位置：滚动条是浮层式的（不占布局宽度），否则 ⋯ 按钮会被压在条下面。
		_sessionTree.ItemPadding = new Thickness(8, 2, 8 + AppTheme.Space3, 2);
		_sessionTree.ItemTemplate<TreeViewNode>(context =>
		{
			// 项目分组行带文件夹图标，会话行不带 —— 层级一眼可辨。
			var folder = new Image().Source(AppIcons.Get("folder-open", AppTheme.TextSubtle))
				.Width(14).Height(14).VerticalAlignment(VerticalAlignment.Center)
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

	/// <summary>「对话」视图：平铺的会话列表，每行一个会话，不属于任何项目。</summary>
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

	/// <summary>
	/// 列表的滚动条是 MewUI 的浮层条：不占布局宽度，默认常显，会压在行的右缘上。
	/// 打开自动隐藏后，静止时完全淡出、滚动或悬停条本身时淡入，平时列表里看不到条。
	/// </summary>
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

	/// <summary>行尾 ⋯ 按钮的统一分发：会话行与项目行各弹各的菜单。</summary>
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

		Add(Strings.ProjectMenuNewTask, () => OpenSession(_catalog.Create(cwd)));
		Add(Strings.ProjectMenuReveal, () => Shell.Open(cwd));
		Add(Strings.ProjectMenuRemove, () => RemoveProject(cwd));
		menu.Show(owner);
	}

	/// <summary>「对话 | 项目」切换：对话是平铺会话流，项目是按项目分组的任务树。</summary>
	private void ShowSidebarView(bool projects)
	{
		_projectsTab = projects;
		_sessionsTabButton.StyleName(projects ? AppStyles.GhostButton : AppStyles.SelectedButton);
		_projectsTabButton.StyleName(projects ? AppStyles.SelectedButton : AppStyles.GhostButton);
		_newSessionButton.IsVisible(!projects);
		_addProjectButton.IsVisible(projects);
		_sessionTree.IsVisible(projects);
		_chatsTree.IsVisible(!projects);
	}

	/// <summary>从会话行发起的重命名优先于「选中项」。</summary>
	private void StartRename(SessionListItem item)
	{
		_renamingPath = item.Path;
		_nameBox.Text = item.Name;
		_nameBox.IsVisible(true);
		_nameBox.Focus();
	}

	/// <summary>归档 = 移入会话目录的 .trash（pi 的会话是只追加的，保留一份可找回）。</summary>
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

			RefreshSessions();
			_banner.Text(Strings.SessionArchived);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Archive session", ex);
			_banner.Text(Strings.OpenSessionFailed(ex.Message));
		}
	}

	/// <summary>把项目从侧栏移除：会话文件保留，仅不再分组展示，并从最近项目里去掉。</summary>
	private void RemoveProject(string cwd)
	{
		_removedProjects.Add(Path.GetFullPath(cwd));
		var preference = DesktopPreference.Load();
		if (preference.RecentProjects.Contains(cwd, StringComparer.Ordinal))
		{
			preference = preference with
			{
				RecentProjects = [.. preference.RecentProjects.Where(p => !string.Equals(p, cwd, StringComparison.Ordinal))],
			};
			preference.Save();
		}

		RefreshSessions();
		_banner.Text(Strings.ProjectRemoved);
	}
}
