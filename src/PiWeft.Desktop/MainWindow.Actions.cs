using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;

namespace PiWeft.Desktop;

public sealed partial class MainWindow
{
	private async Task ChooseProjectAsync()
	{
		try
		{
			// 优先用系统的文件夹选择器（Linux 走 XDG Desktop Portal，Windows/macOS 走原生对话框）；
			// 平台不提供原生实现时，MewUI 会自动回退到内置对话框。
			var folder = await FileDialog.SelectFolderAsync(new FolderDialogOptions
			{
				Title = Strings.AddProject,
				Owner = this,
				InitialDirectory = CurrentSession()?.Cwd ?? _cwd,
			});
			if (folder is not null)
			{
				await OpenProjectAsync(folder);
			}
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Choose project directory", ex);
			_banner.Text(Strings.ProjectAddFailed(ex.Message));
		}
	}

	/// 把文件夹加入侧栏「项目」。只改清单，不另开窗口：另一个进程的同尺寸窗口正好盖住当前窗口，
	/// 看起来像没反应；项目列表多出一行才是看得见的反馈，任务在行内菜单里新建。
	private async Task OpenProjectAsync(string cwd)
	{
		string path;
		try
		{
			path = SessionGroups.ProjectPath(cwd);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			_banner.Text(Strings.ProjectAddFailed(cwd));
			return;
		}

		if (!Directory.Exists(path))
		{
			_banner.Text(Strings.ProjectAddFailed(Strings.NoSuchDirectory(path)));
			return;
		}

		// 聊天目录属于「对话」分段：加进项目清单也只会是一行没有会话的空目录。
		if (SessionGroups.SamePath(path, _chatWorkspace))
		{
			_banner.Text(Strings.ProjectChatWorkspace);
			return;
		}

		try
		{
			DesktopPreference.Load().WithProject(path).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Save project list", ex);
			_banner.Text(Strings.ProjectAddFailed(ex.Message));
			return;
		}

		_banner.Text(string.Empty);

		// 侧栏首次展开要先扫会话，拿到数据才能定位新行。
		RevealSidebarProjects();
		await EnsureSessionsLoadedAsync().ConfigureAwait(true);
		if (_sessionHostClosed)
		{
			return;
		}

		RefreshSessions(force: true);
		RevealProject(path);
		Toasts.Success(this, Strings.ProjectAdded(ShortCwd(path)));
	}

	private ContextMenu TabMenu(FrameworkElement owner, object entry)
	{
		var menu = new ContextMenu();
		void Add(string label, Action action, Func<bool>? enabled = null)
		{
			var command = new Command($"pi.tab.{menu.Items.Count}", label);
			owner.Commands.Register(command, action, enabled ?? (() => _tabEntries.Contains(entry)));
			menu.Items.Add(new MenuItem(command));
		}

		Add(Strings.CloseCurrentTab, () => CloseEntry(entry));
		Add(Strings.KeepCurrentTab, () =>
		{
			foreach (var other in _tabEntries.Where(other => !ReferenceEquals(entry, other)).ToArray())
			{
				CloseEntry(other);
			}
		});
		Add(Strings.CloseTabsToRight, () =>
		{
			var index = _tabEntries.IndexOf(entry);
			if (index >= 0)
			{
				foreach (var other in _tabEntries.Skip(index + 1).ToArray())
				{
					CloseEntry(other);
				}
			}
		}, () => _tabEntries.IndexOf(entry) is var index && index >= 0 && index < _tabEntries.Count - 1);
		menu.AddSeparator();
		Add(Strings.MoveTabLeft, () => MoveTab(entry, -1), () => _tabEntries.IndexOf(entry) > 0);
		Add(Strings.MoveTabRight, () => MoveTab(entry, 1), () => _tabEntries.IndexOf(entry) is var index && index >= 0 && index < _tabEntries.Count - 1);
		if (entry is SessionTab tab)
		{
			menu.AddSeparator();
			Add(Strings.OpenProjectDirectory, () => Shell.Open(tab.Session.Cwd));
		}

		return menu;
	}

	private void CloseEntry(object entry)
	{
		if (entry is SessionTab tab)
		{
			CloseTab(tab);
		}
		else
		{
			CloseAuxTab(entry);
		}
	}

	private void MoveTab(object entry, int delta)
	{
		var index = _tabEntries.IndexOf(entry);
		var target = index + delta;
		if (index < 0 || target < 0 || target >= _tabEntries.Count)
		{
			return;
		}

		var selected = _tabControl.SelectedIndex is var selectedIndex && selectedIndex >= 0 && selectedIndex < _tabEntries.Count
			? _tabEntries[selectedIndex] : entry;
		(_tabEntries[index], _tabEntries[target]) = (_tabEntries[target], _tabEntries[index]);
		RebuildTabStrip();
		_tabControl.SelectedIndex = _tabEntries.IndexOf(selected);
		UpdateTabHeaders();
		UpdateStatus();
	}
}
