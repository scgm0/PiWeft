using Aprillz.MewUI;
using System.Text.Json.Nodes;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// <summary>
/// 界面偏好：主题、窗口状态、置顶、最近项目/模型与快捷键。存放在 <c>&lt;agentDir&gt;/desktop.json</c>，
/// 与 pi 的会话、设置文件放在一起，便于一起备份或迁移。
/// </summary>
/// <remarks>
/// 主题既可以在这里持久化（启动前用 <c>ThemeManager.Default</c> 应用），也可以在运行期间通过
/// MewUI 的 <c>Application.SetTheme</c> 立即切换；两者最终落到同一个取值。
/// </remarks>
public sealed record DesktopPreference
{
	public const string DarkTheme = "dark";
	public const string LightTheme = "light";
	public const string SystemTheme = "system";

	/// <summary>主题取值 <c>dark</c>、<c>light</c> 或 <c>system</c>，默认深色。</summary>
	public string Theme { get; init; } = DarkTheme;

	/// <summary>被置顶的会话 id；置顶的排在侧栏最前面。</summary>
	public IReadOnlyList<string> Pinned { get; init; } = [];

	/// <summary>
	/// 侧栏「项目」分段里展开过的项目（按工作目录记录）。默认全部折叠：
	/// 会话多的机器上，全部展开等于每次都要把整份清单铺出来。
	/// </summary>
	public IReadOnlyList<string> ExpandedProjects { get; init; } = [];

	/// <summary>最近打开的项目和使用的模型，最新的在前，最多各八项。</summary>
	public IReadOnlyList<string> RecentProjects { get; init; } = [];
	public IReadOnlyList<string> RecentModels { get; init; } = [];

	/// <summary>提示音（PiDeck 的 <c>soundAlert</c> 同义）：总开关、音量、仅失焦与三类事件。</summary>
	public SoundAlertSettings SoundAlert { get; init; } = new();

	/// <summary>用户自定义的快捷键（动作标识 → 组合串）。</summary>
	public IReadOnlyDictionary<string, string> Shortcuts { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>上一次的窗口尺寸与位置（DIP）；空表示用默认值。</summary>
	public double? WindowWidth { get; init; }

	public double? WindowHeight { get; init; }

	public double? WindowLeft { get; init; }

	public double? WindowTop { get; init; }

	/// <summary>
	/// 工作区侧栏状态与拖拽宽度（DIP）。默认收起：侧栏展开要扫一遍全部会话摘要
	/// （几百份时是上百 MB 的读盘），收着就先不付这笔钱，展开时再加载。
	/// </summary>
	public bool SidebarCollapsed { get; init; } = true;
	public double SidebarWidth { get; init; } = AppTheme.SidebarWidth;

	/// <summary>侧栏上次停在「项目」分段；默认是「对话」。</summary>
	public bool SidebarProjects { get; init; }

	/// <summary>
	/// 性能诊断：默认关闭（生产零开销）。打开后按秒采样内存与 UI 队列延迟，
	/// 并把关键路径耗时写进 <c>&lt;agentDir&gt;/diagnostics/</c>，用于优化前后对照。
	/// </summary>
	public bool Diagnostics { get; init; }

	/// <summary>
	/// 右侧功能面板的拖拽宽度（DIP）。面板本身属于会话、默认收起，因此折叠状态与分段
	/// 不落盘；只有宽度是窗口级、跨会话共享的偏好。
	/// </summary>
	public double RightPanelWidth { get; init; } = AppTheme.RightPanelWidth;

	/// <summary>
	/// 「对话」分段的聊天目录；空表示使用默认的 <see cref="DefaultChatWorkspace"/>。
	/// 与 PiDeck 的内置 Chat 项目一样，这块工作区不属于任何项目，适合日常对话。
	/// </summary>
	public string ChatWorkspace { get; init; } = string.Empty;

	public double ChatFontSize { get; init; } = 16;
	public double ReadingWidth { get; init; } = 880;

	/// <summary>
	/// Git 提交摘要默认模型，形如 <c>provider/id</c>；空表示跟随当前会话的模型。
	/// 思考级别同理，空表示跟随当前会话。
	/// </summary>
	public string GitCommitModel { get; init; } = string.Empty;

	public string GitCommitThinking { get; init; } = string.Empty;

	/// <summary>
	/// 提交摘要的提示词模板；空表示使用 <see cref="GitCommitMessage.DefaultPromptTemplate"/>。
	/// <c>{diff}</c> 会被替换成暂存区 diff（对齐 PiDeck 的 <c>gitCommitMessagePrompt</c>）。
	/// </summary>
	public string GitCommitPrompt { get; init; } = string.Empty;

	/// <summary>
	/// 用户最后一次真正发过消息的模型（<c>provider/id</c>）。对齐 PiDeck 的 <c>lastUsedModel</c>：
	/// 只在发送时记录（选而未发不算），新会话在没有显式默认模型时沿用它。
	/// </summary>
	public string LastUsedModel { get; init; } = string.Empty;

	/// <summary>
	/// Git 面板各节的高展开态，按仓库工作目录记（值形如 <c>图=200:1|比较=160:0|…</c>，
	/// 见 <see cref="GitPaneLayout"/>）。每个仓库的用法不一样：有人天天看提交图，
	/// 有人只用更改列表，所以折叠状态与拖动出来的高度都得分仓库存。
	/// </summary>
	public IReadOnlyDictionary<string, string> GitPanes { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>
	/// 命令 Dock 使用的 shell 可执行文件；空表示自动（<c>$SHELL</c> → bash → sh）。
	/// 只影响终端面板，不改动系统登录 shell。
	/// </summary>
	public string TerminalShell { get; init; } = string.Empty;

	public bool IsLight => Theme == LightTheme;

	public bool IsSystem => Theme == SystemTheme;

	/// <summary>要应用的 MewUI 主题模式。</summary>
	public ThemeVariant Variant => Theme switch
	{
		LightTheme => ThemeVariant.Light,
		SystemTheme => ThemeVariant.System,
		_ => ThemeVariant.Dark,
	};

	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? PiWeft.CodingAgent.Session.SessionPaths.GetAgentDir(), "desktop.json");

	/// <summary>「对话」的默认聊天目录：<c>&lt;agentDir&gt;/chat-workspace</c>。</summary>
	public static string DefaultChatWorkspace(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? PiWeft.CodingAgent.Session.SessionPaths.GetAgentDir(), "chat-workspace");

	/// <summary>实际的聊天目录：优先用户自定义路径（展开 <c>~</c> 并转绝对路径），否则用默认目录。</summary>
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

	/// <summary>读取偏好；文件缺失或损坏时返回默认值。</summary>
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
				ExpandedProjects = ReadStringArray(root?["expandedProjects"]),
				Diagnostics = root?["diagnostics"] is JsonValue diagnostics && diagnostics.TryGetValue<bool>(out var on) && on,
				RightPanelWidth = Math.Clamp(Number(root, "rightPanelWidth", positive: true) ?? AppTheme.RightPanelWidth,
					AppTheme.RightPanelMinWidth, AppTheme.RightPanelMaxWidth),
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
				RecentProjects = ReadList(root, "recentProjects"),
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

	/// <summary>写回偏好，保留文件中其它字段。</summary>
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
		root["sidebarWidth"] = double.IsFinite(SidebarWidth) ? Math.Clamp(SidebarWidth, 200, 480) : AppTheme.SidebarWidth;
		root["rightPanelWidth"] = double.IsFinite(RightPanelWidth)
			? Math.Clamp(RightPanelWidth, AppTheme.RightPanelMinWidth, AppTheme.RightPanelMaxWidth)
			: AppTheme.RightPanelWidth;
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

				// 快捷键里会出现 "+"，默认转义会写成 + —— 文件要能让人直接看懂和手改。
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
	}

	/// <summary>按 深色 → 浅色 → 跟随系统 的顺序轮换。</summary>
	public DesktopPreference Cycle() => this with
	{
		Theme = Theme switch
		{
			DarkTheme => LightTheme,
			LightTheme => SystemTheme,
			_ => DarkTheme,
		},
	};

	/// <summary>切换某个会话的置顶状态。</summary>
	/// <summary>替换快捷键覆盖表（只存与默认不同的项）。</summary>
	public DesktopPreference WithShortcuts(IReadOnlyDictionary<string, string> shortcuts) =>
		this with { Shortcuts = shortcuts };

	/// <summary>记忆侧栏停留在「对话」还是「项目」分段。</summary>
	public DesktopPreference WithSidebarProjects(bool projects) => this with { SidebarProjects = projects };

	/// <summary>命令 Dock 使用的 shell；空字符串表示自动。</summary>
	public DesktopPreference WithTerminalShell(string shell) => this with { TerminalShell = shell ?? string.Empty };

	/// <summary>Git 提交摘要默认模型；空字符串表示跟随当前会话。</summary>
	public DesktopPreference WithGitCommitModel(string model) => this with { GitCommitModel = (model ?? string.Empty).Trim() };

	/// <summary>Git 提交摘要默认思考级别；空字符串表示跟随当前会话。</summary>
	public DesktopPreference WithGitCommitThinking(string level) => this with { GitCommitThinking = (level ?? string.Empty).Trim() };

	/// <summary>Git 提交摘要提示词模板；空字符串（或与默认模板相同）表示跟随内置默认。</summary>
	public DesktopPreference WithGitCommitPrompt(string template) =>
		this with { GitCommitPrompt = GitCommitMessage.IsDefaultTemplate(template) ? string.Empty : (template ?? string.Empty) };

	/// <summary>记住某个仓库的 Git 面板节高度与展开态；空串表示清除该仓库的记录。</summary>
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

	/// <summary>会话是否被置顶。</summary>
	public bool IsPinned(string sessionId) => Pinned.Contains(sessionId, StringComparer.Ordinal);

	/// <summary>记录窗口状态，返回新的偏好对象。</summary>
	public DesktopPreference WithWindow(double width, double height, double left, double top) =>
		this with { WindowWidth = width, WindowHeight = height, WindowLeft = left, WindowTop = top };

	public DesktopPreference RememberProject(string cwd) => this with
	{
		RecentProjects = Prepend(RecentProjects, System.IO.Path.GetFullPath(cwd)),
	};

	public DesktopPreference RememberModel(string provider, string id) => this with
	{
		RecentModels = Prepend(RecentModels, $"{provider}/{id}"),
	};

	/// <summary>
	/// 记录「最后一次实际使用的模型」。与 <see cref="RememberModel"/> 分开：前者决定新会话默认，
	/// 后者只影响模型选择器的排序；二者都在发送或显式选择时写入时已经很稳。
	/// </summary>
	public DesktopPreference RememberUsedModel(string provider, string id) => this with
	{
		LastUsedModel = string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id)
			? LastUsedModel
			: $"{provider}/{id}",
	};

	private static IReadOnlyList<string> Prepend(IReadOnlyList<string> items, string value) =>
		[value, .. items.Where(item => !string.Equals(item, value, StringComparison.Ordinal)).Take(7)];

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

	/// <summary>读字符串数组；形状不对的项直接跳过，坏文件不该让偏好整份失效。</summary>
	private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
	{
		if (node is not JsonArray array) return [];
		var values = new List<string>(array.Count);
		foreach (var item in array)
		{
			if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
			{
				values.Add(text);
			}
		}

		return values;
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
