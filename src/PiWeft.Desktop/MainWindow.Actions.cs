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
				Title = Strings.OpenProject,
				Owner = this,
				InitialDirectory = CurrentSession()?.Cwd ?? _cwd,
			});
			if (folder is not null)
			{
				OpenProject(folder);
			}
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Choose project directory", ex);
			_banner.Text(Strings.ProjectOpenFailed(ex.Message));
		}
	}

	private void OpenProject(string cwd)
	{
		if (Path.GetFullPath(cwd) == Path.GetFullPath(_cwd))
		{
			Activate();
			return;
		}

		_banner.Text(ProjectLauncher.Open(cwd) ? Strings.ProjectOpened : Strings.ProjectOpenFailed(cwd));
	}

	/// <summary>菜单闭包只捕获稳定的标签身份，执行时重新查索引，重排后仍作用于原标签。</summary>
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
