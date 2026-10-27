using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Security;

/// <summary>
/// 安全门的整体配置：总开关 + 默认档位 + 档位表 + 按会话覆盖。
/// 形状与持久化位置对齐 PiDeck 的 <c>security-config</c>（本项目存在 <c>settings.json</c> 的
/// <c>security</c> 段里，见 <c>CodingAgentSettings</c>）。
/// <para>
/// <see cref="Enabled"/> 默认 <see langword="true"/> 但 <see cref="DefaultLevelId"/> 默认
/// <c>off</c>：门是挂着的，只是没设任何限制 —— 引入它不改变任何既有行为，也不破坏 pi 兼容语义。
/// </para>
/// </summary>
public sealed record SecurityConfig
{
	/// <summary>配置结构版本；改形状时 +1，读旧版本时按字段缺省解释。</summary>
	public const int SchemaVersion = 1;

	/// <summary>总开关。关闭时完全放行，与档位表里的内容无关。</summary>
	public bool Enabled { get; init; } = true;

	/// <summary>未设置会话级覆盖时使用的档位 id。</summary>
	public string DefaultLevelId { get; init; } = ToolSecurity.OffLevel;

	/// <summary>档位表：内建 <c>off</c>/<c>standard</c>/<c>strict</c> 加用户自定义档。</summary>
	public IReadOnlyList<ToolSecurity> Levels { get; init; } = ToolSecurity.Builtins;

	/// <summary>
	/// 会话级覆盖：会话文件路径 → 档位 id。与 PiDeck 的 <c>sessionLevels</c> 同语义 ——
	/// 项目里多数会话可以放心跑，个别要动生产代码的临时收紧，不该逼用户改全局档位。
	/// </summary>
	public IReadOnlyDictionary<string, string> SessionLevels { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>完全放行的配置（等价于没装这道门）。</summary>
	public static SecurityConfig Off { get; } = new();

	/// <summary>
	/// 单档位的隐式转换：让 <c>new { Security = ToolSecurity.Strict }</c> 这类写法继续有效 ——
	/// 该档位成为默认档，并替换掉同 id 的内建档（自定义档则追加）。
	/// </summary>
	public static implicit operator SecurityConfig(ToolSecurity level) =>
		(Off with { DefaultLevelId = level.Level }).WithLevel(level);

	/// <summary>按 id 找档位；找不到返回 <see langword="null"/>（调用方退回默认档）。</summary>
	public ToolSecurity? Find(string? levelId)
	{
		if (levelId is not { Length: > 0 } wanted)
		{
			return null;
		}

		foreach (var level in Levels)
		{
			if (string.Equals(level.Level, wanted, StringComparison.OrdinalIgnoreCase))
			{
				return level;
			}
		}

		return null;
	}

	/// <summary>
	/// 某个会话生效的档位。关闭总开关时返回 <see cref="ToolSecurity.Off"/>；
	/// 覆盖指向不存在的档位时退回默认档，默认档也不存在时按 <c>off</c> 处理 ——
	/// 配置写错不该把用户的工作流卡死。
	/// </summary>
	public ToolSecurity Resolve(string? sessionFilePath)
	{
		if (!Enabled)
		{
			return ToolSecurity.Off;
		}

		if (sessionFilePath is { Length: > 0 } path &&
			SessionLevels.TryGetValue(path, out var overrideLevel) &&
			Find(overrideLevel) is { } assigned)
		{
			return assigned;
		}

		return Find(DefaultLevelId) ?? ToolSecurity.Off;
	}

	/// <summary>界面/命令行列出可选档位。</summary>
	public IReadOnlyList<ToolSecurity> Options => Levels;

	/// <summary>替换或追加一个档位（同 id 覆盖，保持原位置）。</summary>
	public SecurityConfig WithLevel(ToolSecurity level)
	{
		var levels = new List<ToolSecurity>(Levels.Count + 1);
		var replaced = false;
		foreach (var existing in Levels)
		{
			if (string.Equals(existing.Level, level.Level, StringComparison.Ordinal))
			{
				levels.Add(level);
				replaced = true;
			}
			else
			{
				levels.Add(existing);
			}
		}

		if (!replaced)
		{
			levels.Add(level);
		}

		return this with { Levels = levels };
	}

	/// <summary>删除自定义档位；内建档不可删。删掉的正是默认档时回退 <c>off</c>。</summary>
	public SecurityConfig WithoutLevel(string levelId)
	{
		var target = Find(levelId);
		if (target is null || target.Builtin)
		{
			return this;
		}

		var levels = Levels.Where(level => !string.Equals(level.Level, levelId, StringComparison.Ordinal)).ToList();
		var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (path, assigned) in SessionLevels)
		{
			// 引用被删档位的覆盖直接清掉，别留下指向不存在档位的脏数据。
			if (!string.Equals(assigned, levelId, StringComparison.OrdinalIgnoreCase))
			{
				sessions[path] = assigned;
			}
		}

		return this with
		{
			Levels = levels,
			SessionLevels = sessions,
			DefaultLevelId = string.Equals(DefaultLevelId, levelId, StringComparison.OrdinalIgnoreCase)
				? ToolSecurity.OffLevel
				: DefaultLevelId,
		};
	}

	/// <summary>设置或清除某个会话的档位覆盖。</summary>
	public SecurityConfig WithSessionLevel(string sessionFilePath, string? levelId)
	{
		var sessions = new Dictionary<string, string>(SessionLevels, StringComparer.Ordinal);
		if (string.IsNullOrEmpty(levelId) || string.Equals(levelId, DefaultLevelId, StringComparison.Ordinal))
		{
			sessions.Remove(sessionFilePath);
		}
		else
		{
			sessions[sessionFilePath] = levelId;
		}

		return this with { SessionLevels = sessions };
	}

	/// <summary>丢掉指向不存在档位的覆盖与默认档（配置被手改坏时的自愈）。</summary>
	public SecurityConfig Sanitized()
	{
		var defaultLevel = Find(DefaultLevelId) is null ? ToolSecurity.OffLevel : DefaultLevelId;
		if (SessionLevels.Count == 0)
		{
			return defaultLevel == DefaultLevelId ? this : this with { DefaultLevelId = defaultLevel };
		}

		var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (path, levelId) in SessionLevels)
		{
			if (Find(levelId) is { } level)
			{
				sessions[path] = level.Level;
			}
		}

		return this with { DefaultLevelId = defaultLevel, SessionLevels = sessions };
	}

	/// <summary>
	/// 从 <c>&lt;agentDir&gt;/settings.json</c> 读安全配置。
	/// <para>
	/// <strong>只读全局设置</strong>：项目里的 <c>.pi/settings.json</c> 会随仓库一起分发，
	/// 让它能改自己的安全档位等于让一个仓库关掉用户机器上的防护，或者反过来把工作流卡死。
	/// 项目设置里的 <c>security</c> 段被忽略。
	/// </para>
	/// </summary>
	public static SecurityConfig Load(string? agentDir = null)
	{
		var path = System.IO.Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), "settings.json");
		if (!File.Exists(path))
		{
			return Off;
		}

		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
				? FromJson(root["security"])
				: Off;
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
		{
			// 设置文件本身读不出时按「没有安全门」处理：安全门不该在配置解析失败时
			// 自作主张地拦住或放行，其它读取路径会给出损坏提示。
			return Off;
		}
	}

	/// <summary>
	/// 写回 <c>&lt;agentDir&gt;/settings.json</c> 的 <c>security</c> 段：其它键原样保留
	/// （用户手写的 pi 配置不能被我们覆盖掉），回到默认形状时删掉该键；同目录临时文件 + 原子替换。
	/// </summary>
	public static void Save(SecurityConfig config, string? agentDir = null)
	{
		var directory = agentDir ?? SessionPaths.GetAgentDir();
		Directory.CreateDirectory(directory);
		var path = System.IO.Path.Combine(directory, "settings.json");

		JsonObject root;
		try
		{
			root = File.Exists(path)
				? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
				: new JsonObject();
		}
		catch (JsonException)
		{
			// 现有文件不是合法 JSON：不能凭空造一份把它覆盖掉（那会丢掉用户的全部设置）。
			throw new InvalidDataException($"设置文件不是合法的 JSON，未写入安全配置：{path}");
		}

		var node = config.ToJson();
		if (node.Count == 0)
		{
			root.Remove("security");
		}
		else
		{
			root["security"] = node;
		}

		var text = root.ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			// 目录与档位名里可能有中文：默认转义会写成 uXXXX 形式，手写配置的人看不懂。
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		});

		var temporary = path + ".tmp";
		File.WriteAllText(temporary, text, new UTF8Encoding(false));
		File.Move(temporary, path, overwrite: true);
	}

	/// 序列化成 <c>settings.json</c> 的 <c>security</c> 段。默认形状（开着门但什么都不限制）
	/// 不写任何键 —— 配置里塞一堆等于默认的字段会让手写配置的人看不懂。
	/// </summary>
	public JsonObject ToJson()
	{
		var root = new JsonObject();
		if (!Enabled)
		{
			root["enabled"] = false;
		}

		if (!string.Equals(DefaultLevelId, ToolSecurity.OffLevel, StringComparison.Ordinal))
		{
			root["level"] = DefaultLevelId;
		}

		var custom = Levels.Where(static level => !level.Builtin).ToList();
		// 内建档被改过也要落盘，否则界面里改完一刷新就回去了。
		var edited = Levels.Where(IsEdited).ToList();
		if (custom.Count > 0 || edited.Count > 0)
		{
			// 用 JsonArray(IReadOnlyList<JsonNode?>) 而不是 JsonArray().Add(x)：
			// 后者会绑定到泛型 Add<T>，在 trim/AOT 分析里报 IL2026/IL3050（与本仓库其它
			// 写 JSON 的地方同一规避手法）。
			var nodes = new List<JsonNode?>();
			foreach (var level in Levels)
			{
				if (!level.Builtin || IsEdited(level))
				{
					nodes.Add(LevelJson(level));
				}
			}

			root["levels"] = new JsonArray([.. nodes]);
		}

		if (SessionLevels.Count > 0)
		{
			var sessions = new JsonObject();
			foreach (var (path, levelId) in SessionLevels)
			{
				sessions[path] = JsonValue.Create(levelId);
			}

			root["sessionLevels"] = sessions;
		}

		return root;
	}

	/// <summary>
	/// 读取旧形状：安全段里直接平铺的档位字段（没有 <c>levels</c> 数组时）。
	/// 没有这类字段就返回 <see langword="null"/> —— 只有内建 id、总开关与会话覆盖时不该造出一个档位。
	/// </summary>
	private static ToolSecurity? FlatOverride(JsonObject root, string defaultLevel)
	{
		string[] keys = ["protectSensitivePaths", "pathPolicy", "customAllowDirs", "denyDirs", "denyBashPatterns", "defaultAction", "toolActions"];
		if (!keys.Any(root.ContainsKey))
		{
			return null;
		}

		var draft = new JsonObject { ["id"] = defaultLevel };
		foreach (var key in keys)
		{
			// 键存在才复制；显式 null 也带过去（ParseLevel 会按「没配置」处理）。
			if (root.TryGetPropertyValue(key, out var raw))
			{
				draft[key] = raw?.DeepClone();
			}
		}

		return ParseLevel(draft);
	}

	private bool IsEdited(ToolSecurity level)
	{
		var baseline = level.Level switch
		{
			ToolSecurity.StandardLevel => ToolSecurity.Standard,
			ToolSecurity.StrictLevel => ToolSecurity.Strict,
			ToolSecurity.OffLevel => ToolSecurity.Off,
			_ => null,
		};

		return baseline is not null && level != baseline;
	}

	private static JsonObject LevelJson(ToolSecurity level)
	{
		var node = new JsonObject
		{
			["id"] = level.Level,
			["name"] = level.Name,
		};

		if (level.Description.Length > 0)
		{
			node["description"] = level.Description;
		}

		if (!level.Builtin)
		{
			node["builtin"] = false;
		}

		if (level.ProtectSensitivePaths)
		{
			node["protectSensitivePaths"] = true;
		}

		if (level.PathPolicy != ToolPathPolicy.Unrestricted)
		{
			node["pathPolicy"] = level.PathPolicy.ToString().ToLowerInvariant();
		}

		AddList(node, "customAllowDirs", level.CustomAllowDirs);
		AddList(node, "denyDirs", level.DenyDirs);
		AddList(node, "denyBashPatterns", level.DenyBashPatterns);

		if (level.DefaultAction != ToolAction.Allow)
		{
			node["defaultAction"] = level.DefaultAction;
		}

		if (level.ToolActions.Count > 0)
		{
			var actions = new JsonObject();
			foreach (var (tool, action) in level.ToolActions)
			{
				actions[tool] = action;
			}

			node["toolActions"] = actions;
		}

		return node;
	}

	private static void AddList(JsonObject node, string name, IReadOnlyList<string> values)
	{
		if (values.Count == 0)
		{
			return;
		}

		node[name] = new JsonArray([.. values.Select(static value => (JsonNode?)JsonValue.Create(value))]);
	}

	/// <summary>
	/// 从 <c>settings.json</c> 的 <c>security</c> 段还原。缺字段按默认解释；
	/// 未识别的档位名一律按 <c>off</c>，绝不猜一个更严的档 —— 写错的配置不该卡死用户。
	/// </summary>
	public static SecurityConfig FromJson(JsonNode? node)
	{
		if (node is not JsonObject root)
		{
			return Off;
		}

		var enabled = root["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out var value) ? value : true;
		var defaultLevel = Text(root["level"]) ?? ToolSecurity.OffLevel;

		var levels = new List<ToolSecurity>(ToolSecurity.Builtins);

		// 上一轮的形状把这些字段平铺在 security 段本身（没有 levels 数组）。必须把它们
		// 套到 `level` 指名的档位上：否则用户改过的路径边界/目录/命令表会被静默忽略，
		// 配置看起来还在、实际退回了内建档 —— 那是最坏的一种失效（界面显示已限制）。
		// 上一轮的形状把这些字段平铺在 security 段本身（没有 levels 数组）。
		if (FlatOverride(root, defaultLevel) is { } flat)
		{
			var index = levels.FindIndex(level => string.Equals(level.Level, flat.Level, StringComparison.OrdinalIgnoreCase));
			if (index >= 0)
			{
				levels[index] = Merge(levels[index], flat);
			}
			else
			{
				levels.Add(flat);
			}
		}

		if (root["levels"] is JsonArray items)
		{
			foreach (var item in items)
			{
				if (item is not JsonObject levelNode)
				{
					continue;
				}

				var parsed = ParseLevel(levelNode);
				if (parsed is null)
				{
					continue;
				}

				var existing = levels.FindIndex(level => string.Equals(level.Level, parsed.Level, StringComparison.OrdinalIgnoreCase));
				if (existing >= 0)
				{
					// 内建档的覆盖：没写的字段沿用内建值，别让「只改了敏感文件开关」把命令表清空。
					levels[existing] = Merge(levels[existing], parsed);
				}
				else
				{
					levels.Add(parsed);
				}
			}
		}

		var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
		if (root["sessionLevels"] is JsonObject map)
		{
			foreach (var (path, levelId) in map)
			{
				if (Text(levelId) is { Length: > 0 } level && path.Length > 0)
				{
					sessions[path] = level;
				}
			}
		}

		return new SecurityConfig
		{
			Enabled = enabled,
			DefaultLevelId = defaultLevel,
			Levels = levels,
			SessionLevels = sessions,
		}.Sanitized();
	}

	private static ToolSecurity Merge(ToolSecurity baseline, ToolSecurity overlay) => baseline with
	{
		Name = overlay.Name == DefaultName(overlay.Level) ? baseline.Name : overlay.Name,
		Description = overlay.Description.Length == 0 ? baseline.Description : overlay.Description,
		ProtectSensitivePaths = overlay.ProtectSensitivePaths,
		PathPolicy = overlay.PathPolicy,
		CustomAllowDirs = overlay.CustomAllowDirs.Count > 0 ? overlay.CustomAllowDirs : baseline.CustomAllowDirs,
		DenyDirs = overlay.DenyDirs.Count > 0 ? overlay.DenyDirs : baseline.DenyDirs,
		DenyBashPatterns = overlay.DenyBashPatterns.Count > 0 ? overlay.DenyBashPatterns : baseline.DenyBashPatterns,
		ToolActions = overlay.ToolActions.Count > 0 ? overlay.ToolActions : baseline.ToolActions,
		DefaultAction = overlay.DefaultAction,
	};

	private static string DefaultName(string level) => level switch
	{
		ToolSecurity.StandardLevel => ToolSecurity.Standard.Name,
		ToolSecurity.StrictLevel => ToolSecurity.Strict.Name,
		_ => ToolSecurity.Off.Name,
	};

	private static ToolSecurity? ParseLevel(JsonObject node)
	{
		var id = Text(node["id"]);
		if (id is not { Length: > 0 })
		{
			return null;
		}

		var normalized = id.Trim().ToLowerInvariant();
		var baseline = normalized switch
		{
			ToolSecurity.StandardLevel => ToolSecurity.Standard,
			ToolSecurity.StrictLevel => ToolSecurity.Strict,
			ToolSecurity.OffLevel => ToolSecurity.Off,
			_ => ToolSecurity.Off with { Level = normalized, Builtin = false, Name = normalized },
		};

		var actions = new Dictionary<string, string>(StringComparer.Ordinal);
		if (node["toolActions"] is JsonObject toolActions)
		{
			foreach (var (tool, action) in toolActions)
			{
				if (Text(action) is { Length: > 0 } value)
				{
					actions[tool] = ToolAction.Normalize(value);
				}
			}
		}

		return baseline with
		{
			Level = normalized,
			Builtin = node["builtin"] is JsonValue builtin && builtin.TryGetValue<bool>(out var isBuiltin) ? isBuiltin : baseline.Builtin,
			Name = Text(node["name"]) ?? baseline.Name,
			Description = Text(node["description"]) ?? string.Empty,
			ProtectSensitivePaths = node["protectSensitivePaths"] is JsonValue protect && protect.TryGetValue<bool>(out var protectValue) && protectValue,
			PathPolicy = ParsePathPolicy(Text(node["pathPolicy"])),
			CustomAllowDirs = Clean(TextList(node["customAllowDirs"])) ?? baseline.CustomAllowDirs,
			DenyDirs = Clean(TextList(node["denyDirs"])) ?? baseline.DenyDirs,
			DenyBashPatterns = Clean(TextList(node["denyBashPatterns"])) ?? baseline.DenyBashPatterns,
			ToolActions = actions.Count > 0 ? actions : baseline.ToolActions,
			DefaultAction = ToolAction.Normalize(Text(node["defaultAction"])),
		};
	}

	private static ToolPathPolicy ParsePathPolicy(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		"workspace" => ToolPathPolicy.Workspace,
		"custom" => ToolPathPolicy.Custom,
		_ => ToolPathPolicy.Unrestricted,
	};

	private static IReadOnlyList<string>? Clean(IReadOnlyList<string>? values)
	{
		if (values is null)
		{
			return null;
		}

		var cleaned = new List<string>(values.Count);
		foreach (var value in values)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				cleaned.Add(value.Trim());
			}
		}

		// 空串必须拦在生效前：空 denyDirs 会被当成「根为 /」从而匹配一切路径。
		return cleaned;
	}

	private static IReadOnlyList<string>? TextList(JsonNode? node)
	{
		if (node is not JsonArray array)
		{
			return null;
		}

		var values = new List<string>(array.Count);
		foreach (var item in array)
		{
			if (Text(item) is { Length: > 0 } text)
			{
				values.Add(text);
			}
		}

		return values;
	}

	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
