using Aprillz.MewUI;
using System.Text.Json.Nodes;
using PiWeft.Cli;

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

	/// <summary>最近打开的项目和使用的模型，最新的在前，最多各八项。</summary>
	public IReadOnlyList<string> RecentProjects { get; init; } = [];
	public IReadOnlyList<string> RecentModels { get; init; } = [];

	/// <summary>用户自定义的快捷键（动作标识 → 组合串）。</summary>
	public IReadOnlyDictionary<string, string> Shortcuts { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>上一次的窗口尺寸与位置（DIP）；空表示用默认值。</summary>
	public double? WindowWidth { get; init; }

	public double? WindowHeight { get; init; }

	public double? WindowLeft { get; init; }

	public double? WindowTop { get; init; }

	/// <summary>工作区侧栏状态与拖拽宽度（DIP）。</summary>
	public bool SidebarCollapsed { get; init; }
	public double SidebarWidth { get; init; } = AppTheme.SidebarWidth;
	public double ChatFontSize { get; init; } = 16;
	public double ReadingWidth { get; init; } = 1060;

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
				SidebarCollapsed = root?["sidebarCollapsed"] is JsonValue collapsed && collapsed.TryGetValue<bool>(out var hidden) && hidden,
				SidebarWidth = Math.Clamp(Number(root, "sidebarWidth", positive: true) ?? AppTheme.SidebarWidth, 200, 480),
				ChatFontSize = Math.Clamp(Number(root, "chatFontSize", positive: true) ?? 16, 14, 22),
				ReadingWidth = Math.Clamp(Number(root, "readingWidth", positive: true) ?? 1060, 760, 1360),
				WindowWidth = Number(root, "windowWidth", positive: true),
				WindowHeight = Number(root, "windowHeight", positive: true),
				WindowLeft = Number(root, "windowLeft"),
				WindowTop = Number(root, "windowTop"),
				Pinned = ReadPinned(root),
				Shortcuts = ReadShortcuts(root),
				RecentProjects = ReadList(root, "recentProjects"),
				RecentModels = ReadList(root, "recentModels"),
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
		root["sidebarCollapsed"] = SidebarCollapsed;
		root["sidebarWidth"] = double.IsFinite(SidebarWidth) ? Math.Clamp(SidebarWidth, 200, 480) : AppTheme.SidebarWidth;
		root["chatFontSize"] = double.IsFinite(ChatFontSize) ? Math.Clamp(ChatFontSize, 14, 22) : 16;
		root["readingWidth"] = double.IsFinite(ReadingWidth) ? Math.Clamp(ReadingWidth, 760, 1360) : 1060;
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
