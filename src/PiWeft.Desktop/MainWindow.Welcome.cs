using PiWeft.Cli;

namespace PiWeft.Desktop;

/// 没有会话打开时的起始页接线：目标目录、项目菜单、开新会话并发送首条消息。
/// 界面的形状由 <see cref="WelcomeSurface"/> 自己定义，这里只提供数据与动作。
public sealed partial class MainWindow
{
	private WelcomeSurface BuildWelcomeSurface()
	{
		var surface = new WelcomeSurface();
		surface.Submitted += StartSessionFromWelcome;
		surface.TargetRequested += ShowStartTargetMenu;
		return surface;
	}

	private void SetStartTarget(string cwd)
	{
		var path = SessionGroups.SafeProjectPath(string.IsNullOrWhiteSpace(cwd) ? _chatWorkspace : cwd);
		if (path.Length == 0)
		{
			path = _chatWorkspace;
		}

		_startTargetCwd = path;
		var label = SessionGroups.SamePath(path, _chatWorkspace) ? Strings.SidebarTabChats : ShortCwd(path);
		_welcomeSurface.SetTarget(label, path);
	}

	/// 起始页的目标目录：对话、启动目录、最近用过的项目与会话分组，按这个顺序去重。
	/// 最近用过的排在前面：多数时候用户从这里接着干。
	private IReadOnlyList<(string Cwd, string Label)> StartTargets()
	{
		var preference = DesktopPreference.Load();
		var targets = new List<(string Cwd, string Label)>();
		var seen = new HashSet<string>(SessionGroups.PathComparer);

		void Add(string? cwd)
		{
			if (string.IsNullOrWhiteSpace(cwd))
			{
				return;
			}

			var path = SessionGroups.SafeProjectPath(cwd);
			if (!seen.Add(path))
			{
				return;
			}

			var label = SessionGroups.SamePath(path, _chatWorkspace) ? Strings.SidebarTabChats : ShortCwd(path);
			targets.Add((path, label));
		}

		Add(_chatWorkspace);
		Add(_cwd);
		foreach (var path in preference.RecentProjects) Add(path);
		foreach (var path in preference.Projects) Add(path);
		// 项目清单可能还没扫：会话分组里的目录也要能选，不能因为侧栏收起就少一项。
		foreach (var node in _sessionNodes)
		{
			if (node.Tag is string cwd) Add(cwd);
		}

		return targets;
	}

	private void ShowStartTargetMenu() => _startTargetMenu.Show(
		_welcomeSurface.TargetButton,
		new StartTargetMenu.Options(
			[.. StartTargets().Select(target => new StartTargetMenu.Item(target.Cwd, target.Label))],
			_startTargetCwd,
			SetStartTarget,
			() => _ = AddStartTargetProjectAsync()));

	private async Task AddStartTargetProjectAsync()
	{
		if (await ChooseProjectAsync().ConfigureAwait(true) is { } folder)
		{
			SetStartTarget(folder);
		}
	}

	/// 起始页提交：先开一个新会话，再把首条消息发出去。与「新建」同一个落点规则，
	/// 只是少了先去会话里打一遍字的步骤。
	private void StartSessionFromWelcome(string text)
	{
		if (_environment is null)
		{
			_banner.Text(Strings.WelcomeNotReady);
			return;
		}

		var cwd = _startTargetCwd.Length > 0 ? _startTargetCwd : _chatWorkspace;
		try
		{
			if (SessionGroups.SamePath(cwd, _chatWorkspace))
			{
				Directory.CreateDirectory(_chatWorkspace);
			}

			if (!Directory.Exists(cwd))
			{
				_banner.Text(Strings.StartSessionFailed(Strings.NoSuchDirectory(cwd)));
				return;
			}

			OpenSession(_catalog.Create(cwd));
			if (CurrentTab() is not { } tab)
			{
				// 环境缺失时 OpenSession 静默返回：草稿留着，用户还能重试。
				_banner.Text(Strings.WelcomeNotReady);
				return;
			}

			_welcomeSurface.ClearDraft();
			RefreshSessions(force: true);
			_ = tab.SendAsync(text);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Start session from welcome", ex);
			_banner.Text(Strings.StartSessionFailed(ex.Message));
		}
	}

	/// 起始页底栏的模型名跟着「上次用过的模型」走；环境未就绪时整段不占位。
	private void RefreshWelcomeModel()
	{
		if (_welcomeSurface is null)
		{
			return;
		}

		if (_environment is null)
		{
			_welcomeSurface.SetModel(string.Empty);
			return;
		}

		var model = RememberedModel(_environment) ?? _environment.Model;
		_welcomeSurface.SetModel(model.Name.Length > 0 ? model.Name : model.Id);
	}
}
