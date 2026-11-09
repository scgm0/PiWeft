using Aprillz.MewUI;
using System.Text.Json.Nodes;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// 界面偏好：主题、窗口状态、置顶、最近项目/模型与快捷键。存放在 <c>&lt;agentDir&gt;/desktop.json</c>，
/// 与 pi 的会话、设置文件放在一起，便于一起备份或迁移。
/// 主题可以在这里持久化（启动前用 <c>ThemeManager.Default</c> 应用），
/// 也可以在运行期间用 MewUI 的 <c>Application.SetTheme</c> 切换；两者落到同一个值。
public sealed record DesktopPreference
{
	public const string DarkTheme = "dark";
	public const string LightTheme = "light";
	public const string SystemTheme = "system";

	public string Theme { get; init; } = DarkTheme;

	public IReadOnlyList<string> Pinned { get; init; } = [];

	/// 侧栏「项目」分段里展开过的项目（按工作目录记录）。默认全部折叠：
	/// 会话多的机器上，全部展开等于每次都要把整份清单铺出来。
	public IReadOnlyList<string> ExpandedProjects { get; init; } = [];

	/// 用户显式添加过的项目（工作目录）。侧栏「项目」的主来源仍是会话分组；
	/// 这份清单让还没有会话的目录也能占一行，添加动作才有可见结果。
	public IReadOnlyList<string> Projects { get; init; } = [];

	/// 用户从侧栏移除过的项目。会话文件留在磁盘上，扫描仍会分组，靠这份清单挡住（对齐 PiDeck 的 dismissed-project-paths）。
	public IReadOnlyList<string> DismissedProjects { get; init; } = [];

	public IReadOnlyList<string> RecentProjects { get; init; } = [];
	public IReadOnlyList<string> RecentModels { get; init; } = [];

	public SoundAlertSettings SoundAlert { get; init; } = new();

	public IReadOnlyDictionary<string, string> Shortcuts { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	public double? WindowWidth { get; init; }

	public double? WindowHeight { get; init; }

	public double? WindowLeft { get; init; }

	public double? WindowTop { get; init; }

	/// 工作区侧栏状态与拖拽宽度（DIP）。默认收起：展开要扫全部会话摘要
	/// （几百份时是上百 MB 读盘），收着就不付这笔钱。
	public bool SidebarCollapsed { get; init; } = true;
	public double SidebarWidth { get; init; } = AppTheme.SidebarWidth;

	public bool SidebarProjects { get; init; }

	/// 性能诊断：默认关闭（生产零开销）。打开后按秒采样内存与 UI 队列延迟，
	/// 并把关键路径耗时写进 <c>&lt;agentDir&gt;/diagnostics/</c>，用于优化前后对照。
	public bool Diagnostics { get; init; }

	/// 等待期的功能提示轮播（状态条右侧那行小字）。默认显示：这是唯一能曝光
	/// 「没有可见入口」能力的地方；关掉只关提示，不影响功能。
	public bool RespondingTips { get; init; } = true;

	/// 右侧功能面板的拖拽宽度（DIP）。面板属于会话且默认收起，折叠状态与分段不落盘；
	/// 只有宽度是窗口级偏好，跨会话共享。
	public double RightPanelWidth { get; init; } = AppTheme.RightPanelWidth;

	/// 右侧「轨迹」分段底部检查器的高度（DIP）。拖动分隔条时记下，重开面板沿用它；
	/// 范围与 <see cref="TrajectoryView"/> 的拖动限制一致。
	public double TrajectoryInspectorHeight { get; init; } = TrajectoryView.DefaultInspectorHeight;

	/// 「对话」分段的聊天目录；空表示使用默认的 <see cref="DefaultChatWorkspace"/>。
	/// 与 PiDeck 的内置 Chat 项目一样，这块工作区不属于任何项目，适合日常对话。
	public string ChatWorkspace { get; init; } = string.Empty;

	public double ChatFontSize { get; init; } = 16;
	public double ReadingWidth { get; init; } = 880;

	/// Git 提交摘要默认模型，形如 <c>provider/id</c>；空表示跟随当前会话的模型。
	/// 思考级别同理，空表示跟随当前会话。
	public string GitCommitModel { get; init; } = string.Empty;

	public string GitCommitThinking { get; init; } = string.Empty;

	/// 提交摘要的提示词模板；空表示使用 <see cref="GitCommitMessage.DefaultPromptTemplate"/>。
	/// <c>{diff}</c> 会被替换成暂存区 diff（对齐 PiDeck 的 <c>gitCommitMessagePrompt</c>）。
	public string GitCommitPrompt { get; init; } = string.Empty;

	/// 用户最后一次真正发过消息的模型（<c>provider/id</c>）。对齐 PiDeck 的 <c>lastUsedModel</c>：
	/// 只在发送时记录（选而未发不算），新会话在没有显式默认模型时沿用它。
	public string LastUsedModel { get; init; } = string.Empty;

	/// Git 面板各节的高展开态，按仓库工作目录记（值形如 <c>图=200:1|比较=160:0|…</c>，
	/// 见 <see cref="GitPaneLayout"/>）。每个仓库的用法不一样：有人天天看提交图，
	/// 有人只用更改列表，所以折叠状态与拖动出来的高度都得分仓库存。
	public IReadOnlyDictionary<string, string> GitPanes { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// 命令 Dock 使用的 shell 可执行文件；空表示自动（<c>$SHELL</c> → bash → sh）。
	/// 只影响终端面板，不改动系统登录 shell。
	public string TerminalShell { get; init; } = string.Empty;

	public bool IsLight => Theme == LightTheme;

	public bool IsSystem => Theme == SystemTheme;

	public ThemeVariant Variant => Theme switch
	{
		LightTheme => ThemeVariant.Light,
		SystemTheme => ThemeVariant.System,
		_ => ThemeVariant.Dark,
	};

	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? PiWeft.CodingAgent.Session.SessionPaths.GetAgentDir(), "desktop.json");

	public static string DefaultChatWorkspace(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? PiWeft.CodingAgent.Session.SessionPaths.GetAgentDir(), "chat-workspace");

	public string ResolveChatWorkspace(string? agentDir = null)
	{
		if (string.IsNullOrWhiteSpace(ChatWorkspace))
		{
			return DefaultChatWorkspace(agentDir);
		}

		try
		{
			return System.IO.Path.GetFullPath(UserPath.ExpandTilde(ChatWorkspace.Trim()));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return DefaultChatWorkspace(agentDir);
		}
	}

	public static DesktopPreference Load(string? agentDir = null)
	{
		var path = Path(agentDir);
		if (!File.Exists(path))
		{
			return new DesktopPreference();
		}

		try
		{
			var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
			var theme = root?["theme"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
			return new DesktopPreference
			{
				// 取值可以是内建名，也可以是自定义 pi 主题名；解析失败时由 DesktopTheme 回退。
				Theme = string.IsNullOrWhiteSpace(theme) ? DarkTheme : theme,
				// 键缺失时保留默认值（收起），不能因为「读不到」就当成展开。
				SidebarCollapsed = root?["sidebarCollapsed"] is JsonValue collapsed && collapsed.TryGetValue<bool>(out var hidden)
					? hidden
					: new DesktopPreference().SidebarCollapsed,
				SidebarWidth = Math.Clamp(Number(root, "sidebarWidth", positive: true) ?? AppTheme.SidebarWidth, 200, 480),
				SidebarProjects = root?["sidebarProjects"] is JsonValue tab && tab.TryGetValue<bool>(out var projects) && projects,
				ExpandedProjects = ReadProjectPaths(root?["expandedProjects"]),
				Diagnostics = root?["diagnostics"] is JsonValue diagnostics && diagnostics.TryGetValue<bool>(out var on) && on,
				// 默认开：键缺失或类型不对都算开，只有显式写 false 才关。
				RespondingTips = root?["respondingTips"] is not JsonValue respondingTips ||
					!respondingTips.TryGetValue<bool>(out var tipsOff) || tipsOff,
				RightPanelWidth = Math.Clamp(Number(root, "rightPanelWidth", positive: true) ?? AppTheme.RightPanelWidth,
					AppTheme.RightPanelMinWidth, AppTheme.RightPanelMaxWidth),
				TrajectoryInspectorHeight = Math.Clamp(
					Number(root, "trajectoryInspectorHeight", positive: true) ?? TrajectoryView.DefaultInspectorHeight,
					TrajectoryView.MinInspectorHeight, TrajectoryView.MaxInspectorHeight),
				ChatWorkspace = root?["chatWorkspace"] is JsonValue chat && chat.TryGetValue<string>(out var chatWorkspace) ? chatWorkspace : string.Empty,
				ChatFontSize = Math.Clamp(Number(root, "chatFontSize", positive: true) ?? 16, 14, 22),
				ReadingWidth = Math.Clamp(Number(root, "readingWidth", positive: true) ?? 880, 760, 1360),
				GitCommitModel = root?["gitCommitModel"] is JsonValue commitModel && commitModel.TryGetValue<string>(out var commitModelText)
					? commitModelText.Trim()
					: string.Empty,
				GitCommitThinking = root?["gitCommitThinking"] is JsonValue commitThinking && commitThinking.TryGetValue<string>(out var commitThinkingText)
					? commitThinkingText.Trim()
					: string.Empty,
				GitCommitPrompt = root?["gitCommitPrompt"] is JsonValue commitPrompt && commitPrompt.TryGetValue<string>(out var commitPromptText)
					? commitPromptText
					: string.Empty,
				LastUsedModel = root?["lastUsedModel"] is JsonValue lastModel && lastModel.TryGetValue<string>(out var lastModelText)
					? lastModelText.Trim()
					: string.Empty,
				GitPanes = ReadStringMap(root?["gitPanes"]),
				TerminalShell = root?["terminalShell"] is JsonValue shell && shell.TryGetValue<string>(out var shellPath)
					? shellPath.Trim()
					: string.Empty,
				WindowWidth = Number(root, "windowWidth", positive: true),
				WindowHeight = Number(root, "windowHeight", positive: true),
				WindowLeft = Number(root, "windowLeft"),
				WindowTop = Number(root, "windowTop"),
				Pinned = ReadPinned(root),
				Shortcuts = ReadShortcuts(root),
				Projects = ReadProjectPaths(root?["projects"]),
				DismissedProjects = ReadProjectPaths(root?["dismissedProjects"]),
				RecentProjects = ReadProjectPaths(root?["recentProjects"]),
				RecentModels = ReadList(root, "recentModels"),
				SoundAlert = SoundAlertJson.Parse(root?["soundAlert"]),
			};
		}
		catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<DesktopPreference>(), "Read desktop preferences", ex);
			return new DesktopPreference();
		}
	}

	public void Save(string? agentDir = null)
	{
		var path = Path(agentDir);
		var directory = System.IO.Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		JsonObject root;
		try
		{
			root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject();
		}
		catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
		{
			AppLog.Failure(AppLog.For<DesktopPreference>(), "Preserve desktop preferences", ex);
			root = new JsonObject();
		}

		root["theme"] = Theme;
		root["soundAlert"] = SoundAlertJson.Write(SoundAlert);
		root["sidebarCollapsed"] = SidebarCollapsed;
		root["sidebarProjects"] = SidebarProjects;
		// 折叠是默认状态，展开过的项目才落盘；都折起来时直接删键。
		if (ExpandedProjects.Count == 0)
		{
			root.Remove("expandedProjects");
		}
		else
		{
			root["expandedProjects"] = new JsonArray([.. ExpandedProjects.Select(static cwd => (JsonNode)JsonValue.Create(cwd)!)]);
		}
		// 诊断默认关闭：只在打开时才落盘，避免每次启动都写一个 false。
		if (Diagnostics)
		{
			root["diagnostics"] = true;
		}
		else
		{
			root.Remove("diagnostics");
		}
		// 等待提示默认显示：只在用户关掉时落盘一个 false。
		if (RespondingTips)
		{
			root.Remove("respondingTips");
		}
		else
		{
			root["respondingTips"] = false;
		}
		root["sidebarWidth"] = double.IsFinite(SidebarWidth) ? Math.Clamp(SidebarWidth, 200, 480) : AppTheme.SidebarWidth;
		root["rightPanelWidth"] = double.IsFinite(RightPanelWidth)
			? Math.Clamp(RightPanelWidth, AppTheme.RightPanelMinWidth, AppTheme.RightPanelMaxWidth)
			: AppTheme.RightPanelWidth;
		root["trajectoryInspectorHeight"] = double.IsFinite(TrajectoryInspectorHeight)
			? Math.Clamp(TrajectoryInspectorHeight, TrajectoryView.MinInspectorHeight, TrajectoryView.MaxInspectorHeight)
			: TrajectoryView.DefaultInspectorHeight;
		// 面板默认收起且按会话各自打开：旧的折叠状态与分段不再写盘。
		root.Remove("rightPanelCollapsed");
		root.Remove("rightPanelTab");
		// 聊天目录只有用户改过才落盘，默认目录跟随 agent 目录走，不写死进文件。
		if (string.IsNullOrWhiteSpace(ChatWorkspace))
		{
			root.Remove("chatWorkspace");
		}
		else
		{
			root["chatWorkspace"] = ChatWorkspace.Trim();
		}
		root["chatFontSize"] = double.IsFinite(ChatFontSize) ? Math.Clamp(ChatFontSize, 14, 22) : 16;
		root["readingWidth"] = double.IsFinite(ReadingWidth) ? Math.Clamp(ReadingWidth, 760, 1360) : 1060;
		if (string.IsNullOrWhiteSpace(GitCommitModel))
		{
			root.Remove("gitCommitModel");
		}
		else
		{
			root["gitCommitModel"] = GitCommitModel.Trim();
		}
		if (string.IsNullOrWhiteSpace(GitCommitThinking))
		{
			root.Remove("gitCommitThinking");
		}
		else
		{
			root["gitCommitThinking"] = GitCommitThinking.Trim();
		}
		// 提示词模板：与默认模板逐字相同的存空（跟随后续默认模板升级），内容保留换行不 Trim 正文。
		if (GitCommitMessage.IsDefaultTemplate(GitCommitPrompt) || string.IsNullOrWhiteSpace(GitCommitPrompt))
		{
			root.Remove("gitCommitPrompt");
		}
		else
		{
			root["gitCommitPrompt"] = GitCommitPrompt;
		}
		if (string.IsNullOrWhiteSpace(LastUsedModel))
		{
			root.Remove("lastUsedModel");
		}
		else
		{
			root["lastUsedModel"] = LastUsedModel.Trim();
		}
		// Git 面板布局：没有记录时不留空对象。
		if (GitPanes.Count == 0)
		{
			root.Remove("gitPanes");
		}
		else
		{
			var panes = new JsonObject();
			foreach (var (cwd, layout) in GitPanes)
			{
				panes[cwd] = layout;
			}

			root["gitPanes"] = panes;
		}

		if (string.IsNullOrWhiteSpace(TerminalShell))
		{
			root.Remove("terminalShell");
		}
		else
		{
			root["terminalShell"] = TerminalShell.Trim();
		}
		WriteProjectPaths(root, "projects", Projects);
		WriteProjectPaths(root, "dismissedProjects", DismissedProjects);
		WriteList(root, "recentProjects", RecentProjects);
		WriteList(root, "recentModels", RecentModels);

		// 窗口状态：没有值的字段保持文件里原有的内容。
		Write(root, "windowWidth", WindowWidth, positive: true);
		Write(root, "windowHeight", WindowHeight, positive: true);
		Write(root, "windowLeft", WindowLeft);
		Write(root, "windowTop", WindowTop);

		// 快捷键：只写用户改过的项，保持默认的人在文件里看不到噪音。
		if (Shortcuts.Count > 0)
		{
			var shortcuts = new JsonObject();
			foreach (var (id, text) in Shortcuts.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
			{
				shortcuts[id] = text;
			}

			root["shortcuts"] = shortcuts;
		}
		else
		{
			root.Remove("shortcuts");
		}

		// 置顶列表：有序数组，便于人工查看与编辑。
		if (Pinned.Count > 0)
		{
			root["pinned"] = new JsonArray([.. Pinned.Select(static id => (JsonNode?)JsonValue.Create(id))]);
		}
		else
		{
			root.Remove("pinned");
		}
		File.WriteAllText(
			path,
			root.ToJsonString(new System.Text.Json.JsonSerializerOptions
			{
				WriteIndented = true,

				// 快捷键里会出现 "+"，默认转义会写成 +，文件要能让人直接看懂和手改。
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
	}

	public DesktopPreference Cycle() => this with
	{
		Theme = Theme switch
		{
			DarkTheme => LightTheme,
			LightTheme => SystemTheme,
			_ => DarkTheme,
		},
	};

	public DesktopPreference WithShortcuts(IReadOnlyDictionary<string, string> shortcuts) =>
		this with { Shortcuts = shortcuts };

	public DesktopPreference WithSidebarProjects(bool projects) => this with { SidebarProjects = projects };

	public DesktopPreference WithTerminalShell(string shell) => this with { TerminalShell = shell ?? string.Empty };

	public DesktopPreference WithGitCommitModel(string model) => this with { GitCommitModel = (model ?? string.Empty).Trim() };

	public DesktopPreference WithGitCommitThinking(string level) => this with { GitCommitThinking = (level ?? string.Empty).Trim() };

	public DesktopPreference WithGitCommitPrompt(string template) =>
		this with { GitCommitPrompt = GitCommitMessage.IsDefaultTemplate(template) ? string.Empty : (template ?? string.Empty) };

	public DesktopPreference WithGitPanes(string cwd, string layout)
	{
		var map = new Dictionary<string, string>(GitPanes, StringComparer.Ordinal);
		var key = System.IO.Path.GetFullPath(cwd);
		if (string.IsNullOrWhiteSpace(layout))
		{
			map.Remove(key);
		}
		else
		{
			map[key] = layout;
		}

		return this with { GitPanes = map };
	}

	public DesktopPreference TogglePinned(string sessionId)
	{
		var pinned = Pinned.Contains(sessionId, StringComparer.Ordinal)
			? Pinned.Where(id => !string.Equals(id, sessionId, StringComparison.Ordinal)).ToList()
			: [sessionId, .. Pinned];

		return this with { Pinned = pinned };
	}

	public bool IsPinned(string sessionId) => Pinned.Contains(sessionId, StringComparer.Ordinal);

	public DesktopPreference WithWindow(double width, double height, double left, double top) =>
		this with { WindowWidth = width, WindowHeight = height, WindowLeft = left, WindowTop = top };

	public DesktopPreference RememberProject(string cwd) => this with
	{
		RecentProjects = Prepend(RecentProjects, SessionGroups.ProjectPath(cwd), SessionGroups.PathComparer),
	};

	/// 添加项目：记进清单，并从「已移除」里去掉同一目录的旧记录（同一个目录换了写法也算同一个）。
	public DesktopPreference WithProject(string cwd)
	{
		var path = SessionGroups.SafeProjectPath(cwd);
		return this with
		{
			Projects = [path, .. Projects.Where(item => !SessionGroups.SamePath(item, path))],
			DismissedProjects = [.. DismissedProjects.Where(item => !SessionGroups.SamePath(item, path))],
		};
	}

	/// 从侧栏移除项目：删掉清单记录并记进「已移除」。只动清单，磁盘上的会话文件保留。
	public DesktopPreference WithoutProject(string cwd)
	{
		var path = SessionGroups.SafeProjectPath(cwd);
		return this with
		{
			Projects = [.. Projects.Where(item => !SessionGroups.SamePath(item, path))],
			DismissedProjects = [path, .. DismissedProjects.Where(item => !SessionGroups.SamePath(item, path))],
		};
	}

	public DesktopPreference RememberModel(string provider, string id) => this with
	{
		RecentModels = Prepend(RecentModels, $"{provider}/{id}"),
	};

	/// 记录「最后一次实际使用的模型」。与 <see cref="RememberModel"/> 分开：前者决定新会话默认，
	/// 后者只影响模型选择器的排序；二者都在发送或显式选择时写入时已经很稳。
	public DesktopPreference RememberUsedModel(string provider, string id) => this with
	{
		LastUsedModel = string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id)
			? LastUsedModel
			: $"{provider}/{id}",
	};

	private static IReadOnlyList<string> Prepend(IReadOnlyList<string> items, string value, StringComparer? comparer = null)
	{
		var equality = comparer ?? StringComparer.Ordinal;
		return [value, .. items.Where(item => !equality.Equals(item, value)).Take(7)];
	}

	/// 项目路径按统一写法读：旧文件里同一目录可能同时存在带与不带结尾分隔符的两条记录。
	private static IReadOnlyList<string> ReadProjectPaths(JsonNode? node)
	{
		if (node is not JsonArray array) return [];
		var paths = new List<string>(array.Count);
		var seen = new HashSet<string>(array.Count, SessionGroups.PathComparer);
		foreach (var item in array)
		{
			if (item is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length == 0) continue;
			try
			{
				var path = SessionGroups.ProjectPath(text);
				if (seen.Add(path)) paths.Add(path);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
			{
				// 手改文件留下的坏路径读不出来，丢掉即可，不能让整份偏好失效。
			}
		}

		return paths;
	}

	private static void WriteProjectPaths(JsonObject root, string key, IReadOnlyList<string> items)
	{
		if (items.Count == 0)
		{
			root.Remove(key);
		}
		else
		{
			root[key] = new JsonArray([.. items.Select(static item => (JsonNode?)JsonValue.Create(item))]);
		}
	}

	private static IReadOnlyList<string> ReadList(JsonObject? root, string key) =>
		root?[key] is JsonArray array
			? [.. array.OfType<JsonValue>().Select(static value => value.TryGetValue<string>(out var text) ? text : null)
				.Where(static text => !string.IsNullOrWhiteSpace(text)).Cast<string>().Distinct(StringComparer.Ordinal).Take(8)]
			: [];

	private static void WriteList(JsonObject root, string key, IReadOnlyList<string> items)
	{
		if (items.Count == 0)
		{
			root.Remove(key);
		}
		else
		{
			root[key] = new JsonArray([.. items.Take(8).Select(static item => (JsonNode?)JsonValue.Create(item))]);
		}
	}

	private static List<string> ReadPinned(JsonObject? root)
	{
		var pinned = new List<string>();

		if (root?["pinned"] is JsonArray array)
		{
			foreach (var node in array)
			{
				if (node is JsonValue value && value.TryGetValue<string>(out var id) && id.Length > 0)
				{
					pinned.Add(id);
				}
			}
		}

		return pinned;
	}

	private static Dictionary<string, string> ReadStringMap(JsonNode? node)
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		if (node is not JsonObject obj)
		{
			return map;
		}

		foreach (var (key, value) in obj)
		{
			if (value is JsonValue json && json.TryGetValue<string>(out var text) &&
				text.Trim().Length > 0 && key.Trim().Length > 0)
			{
				map[key] = text.Trim();
			}
		}

		return map;
	}

	private static Dictionary<string, string> ReadShortcuts(JsonObject? root)
	{
		var shortcuts = new Dictionary<string, string>(StringComparer.Ordinal);

		if (root?["shortcuts"] is JsonObject obj)
		{
			foreach (var (id, node) in obj)
			{
				if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
				{
					shortcuts[id] = text;
				}
			}
		}

		return shortcuts;
	}

	private static double? Number(JsonObject? root, string name, bool positive = false) =>
		root?[name] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) && (!positive || number > 0) ? number : null;

	private static void Write(JsonObject root, string name, double? value, bool positive = false)
	{
		if (value is { } number && double.IsFinite(number) && (!positive || number > 0))
		{
			root[name] = number;
		}
		else
		{
			root.Remove(name);
		}
	}
}
