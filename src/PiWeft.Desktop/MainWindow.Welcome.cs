using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Security;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.Desktop;

/// 没有会话打开时的起始页接线：目标目录、项目菜单、模型/思考/安全选择、开新会话并发送首条消息。
/// 界面的形状与工具栏由 <see cref="WelcomeSurface"/> 自己定义，这里只提供数据与动作。
public sealed partial class MainWindow
{
	// 起始页选的安全档位（会话文件还不存在，先记在窗口上，建好会话再落成会话级覆盖）。
	private string? _startSecurityLevel;

	// 起始页选过模型/思考后就以草稿为准；settings.defaultModel 会盖过「最近模型」，
	// 不记草稿的话下一次刷新就会把用户刚选的值弹回去。
	private Model? _startModel;
	private string? _startThinking;

	private WelcomeSurface BuildWelcomeSurface()
	{
		var surface = new WelcomeSurface();
		surface.Submitted += StartSessionFromWelcome;
		surface.TargetRequested += ShowStartTargetMenu;
		surface.ModelPicked += OnWelcomeModelPicked;
		surface.ThinkingPicked += OnWelcomeThinkingPicked;
		surface.SecurityPicked += OnWelcomeSecurityPicked;
		surface.NotifyError += message => Toasts.Error(surface.View, message);
		surface.QuickMessagesSettingsRequested += OpenQuickMessagesSettings;
		surface.ResourceProvider = StartResources;
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

	/// 起始页提交：先开一个新会话，再把工具栏上的选择与首条消息一起带进去。
	/// 与「新建」同一个落点规则，只是少了先去会话里打一遍字的步骤。
	private void StartSessionFromWelcome(string text)
	{
		if (_environment is null)
		{
			_banner.Text(Strings.WelcomeNotReady);
			return;
		}

		var cwd = _startTargetCwd.Length > 0 ? _startTargetCwd : _chatWorkspace;
		// 先快照：草稿在提交成功后会清空，附件列表不能跟着没。
		var model = _welcomeSurface.Model;
		var thinking = _welcomeSurface.ThinkingLevel;
		var mode = _welcomeSurface.Mode;
		var security = _startSecurityLevel;
		var images = _welcomeSurface.PendingImages.ToArray();
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

			// 起始页的模型来自启动环境，目标目录可能有一份不同的同名条目：按目标注册表再取一次。
			var startModel = model is null ? null : EnvironmentFor(cwd).Registry.Get(model.Provider, model.Id) ?? model;
			tab.ApplyStartChoices(startModel, thinking, mode, security, images);
			_startSecurityLevel = null;
			_startModel = null;
			_startThinking = null;
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

	/// 起始页底栏的模型铭牌：与新建会话同一条解析顺序（settings.defaultModel 优先，
	/// 否则「上次用过的模型」），思考档取该模型的每模型默认值。
	private void RefreshWelcomeModel()
	{
		if (_welcomeSurface is null)
		{
			return;
		}

		if (_environment is null)
		{
			_welcomeSurface.SetModel(null, string.Empty);
			return;
		}

		_welcomeSurface.SetModels(_models);
		var model = _startModel ?? InitialSessionModel();
		if (model is null)
		{
			_welcomeSurface.SetModel(null, string.Empty);
			return;
		}

		var thinking = _startThinking ?? InitialThinkingLevel(model);
		_welcomeSurface.SetModel(model, thinking);
	}

	private Model? InitialSessionModel()
	{
		if (_environment is null)
		{
			return null;
		}

		// settings.defaultModel 存在时会盖过「上次用过的模型」，起始页要显示真正会用的那个。
		var remembered = _environment.Settings.DefaultModel is { Length: > 0 }
			? null
			: RememberedModel(_environment);
		return remembered ?? _environment.Model;
	}

	private string InitialThinkingLevel(Model model)
	{
		if (_environment is null)
		{
			return ThinkingLevels.Off;
		}

		var settings = _environment.Settings;
		var level = settings.ModelThinkingLevels.GetValueOrDefault($"{model.Provider}/{model.Id}")
			?? settings.DefaultThinkingLevel
			?? ThinkingLevels.Off;
		return ThinkingLevelResolver.Clamp(model, level);
	}

	private void OnWelcomeModelPicked(Model model)
	{
		if (_welcomeSurface is null)
		{
			return;
		}

		// 与会话里的模型选择一致：选过就记成「最近模型」，下一个会话直接用它。
		try
		{
			DesktopPreference.Load().RememberModel(model.Provider, model.Id).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Save start surface model", ex);
		}

		// 切模型保留当前强度，能用的档位夹紧；新会话会显式收到这一档。
		var thinking = ThinkingLevelResolver.Clamp(model, _welcomeSurface.ThinkingLevel);
		_startModel = model;
		_startThinking = thinking;
		_welcomeSurface.SetModel(model, thinking);
	}

	private void OnWelcomeThinkingPicked(string level)
	{
		if (_welcomeSurface is null)
		{
			return;
		}

		_welcomeSurface.SetThinking(level);
		_startThinking = level;
		if (_welcomeSurface.Model is not { } model)
		{
			return;
		}

		// 与会话里的强度选择同一条落盘：每模型默认档位，下一次新建/重开都用它。
		try
		{
			CodingAgentSettings.SetModelThinkingLevel(model.Provider, model.Id, level);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Save start surface thinking level", ex);
		}
	}

	/// 安全档位在起始页只是待定：还没有会话文件，先按默认档位显示，新会话建好后落成会话级覆盖。
	private void RefreshWelcomeSecurity()
	{
		if (_welcomeSurface is null)
		{
			return;
		}

		var config = SecurityConfig.Load();
		var fallback = config.Resolve(null);
		var level = config.Enabled && _startSecurityLevel is { Length: > 0 } pending &&
			config.Find(pending) is { } chosen
				? chosen
				: fallback;
		var hasOverride = config.Enabled && _startSecurityLevel is { Length: > 0 } &&
			!string.Equals(level.Level, config.DefaultLevelId, StringComparison.Ordinal);
		_welcomeSurface.SetSecurity(config, level, hasOverride);
	}

	private void OnWelcomeSecurityPicked(string? levelId)
	{
		var config = SecurityConfig.Load();
		_startSecurityLevel = string.IsNullOrEmpty(levelId) ||
			string.Equals(levelId, config.DefaultLevelId, StringComparison.Ordinal)
				? null
				: levelId;
		RefreshWelcomeSecurity();
	}

	/// 起始页的 + 菜单要列目标目录的技能与提示词：资源在打开选择器时按真实信任状态解析一次。
	private ResourceSnapshot StartResources()
	{
		var cwd = _startTargetCwd.Length > 0 ? _startTargetCwd : _chatWorkspace;
		try
		{
			// 与会话创建同一条信任判定（CreateSession：显式覆盖 > 启动环境），
			// 否则选择器里的项目技能会与真正开出的会话不一致。
			var trusted = _arguments.ProjectTrustOverride ?? _environment?.ProjectTrusted ?? true;
			return ResourceResolver.Resolve(cwd, includeProject: trusted);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Resolve start surface resources", ex);
			return ResourceSnapshot.Empty;
		}
	}

	private void OpenQuickMessagesSettings()
	{
		OpenSettingsDialog();
		if (_settingsView is { } quickMessages)
		{
			_ = quickMessages.SelectSectionAsync("quickMessages");
		}
	}
}
