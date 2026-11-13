using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.CodingAgent.Security;

/// PiDeck 安全快照的桥接：PiDeck 把 `settings.securityConfig` 写成 `security-policy.json`，
/// 通过 `PIDECK_SECURITY_CONFIG` 交给同进程的 pi；本端口直接读这份快照，让用户在 PiDeck
/// 里设的档位在切到 PiWeft 后仍然生效。
/// <para>
/// 快照形状见 PiDeck <c>shared/types/security.ts</c>：<c>schemaVersion/enabled/defaultLevelId/
/// levels[]/sessionLevels</c>。sessionLevels 的键是 PiDeck 的会话 id（<c>PIDECK_SESSION_ID</c>），
/// 不是会话文件路径；没有该变量时退回按会话文件路径查。
/// </para>
/// <para>
/// 与扩展同一套 fail-safe：没设环境变量用本端口设置；设了但文件读不到/格式不对时按 off 放行，
/// 不让一个坏快照把工作流锁死。文件按 mtime + 长度缓存，扩展改档后下一次工具调用就能看到。
/// </para>
public static class PiDeckSecuritySnapshot
{
	public const string ConfigPathVariable = "PIDECK_SECURITY_CONFIG";

	public const string SessionIdVariable = "PIDECK_SESSION_ID";

	/// 与扩展的 RELOAD_THROTTLE_MS 同值：工具调用会连着来（一次 code 模式能触发几十个），
	/// 每次 stat 一遍文件是白付的系统调用。窗口内按 mtime + 长度判断是否换过档。
	private static readonly TimeSpan ReloadThrottle = TimeSpan.FromSeconds(2);

	private static readonly Lock Gate = new();

	private static CachedSnapshot? _cache;

	private sealed record CachedSnapshot(
		string Path,
		long Length,
		long ModifiedTicks,
		long CheckedAt,
		SecurityConfig Config);

	/// 只供测试清缓存：生产每次读盘按 mtime 自动失效。
	internal static void ResetCache()
	{
		lock (Gate)
		{
			_cache = null;
		}
	}

	/// 读快照并映射成本端口的配置。没有设置环境变量时返回 <see langword="null" />。
	public static SecurityConfig? Load()
	{
		var path = Environment.GetEnvironmentVariable(ConfigPathVariable);
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			var now = Environment.TickCount64;
			lock (Gate)
			{
				// 节流窗口内直接用上次结果：省掉每次工具调用的 stat。
				if (_cache is { } cached &&
					string.Equals(cached.Path, path, StringComparison.Ordinal) &&
					now - cached.CheckedAt < ReloadThrottle.TotalMilliseconds)
				{
					return cached.Config;
				}
			}

			var info = new FileInfo(path);
			if (!info.Exists)
			{
				var missing = SecurityConfig.Off with { Enabled = false };
				lock (Gate)
				{
					_cache = new CachedSnapshot(path, -1, 0, now, missing);
				}

				return missing;
			}

			lock (Gate)
			{
				if (_cache is { } cached &&
					string.Equals(cached.Path, path, StringComparison.Ordinal) &&
					cached.Length == info.Length &&
					cached.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
				{
					_cache = cached with { CheckedAt = now };
					return cached.Config;
				}
			}

			var text = File.ReadAllText(path);
			SecurityConfig config;
			try
			{
				config = Parse(text);
			}
			catch (JsonException)
			{
				config = SecurityConfig.Off with { Enabled = false };
			}

			// 坏快照也进缓存：否则每次工具调用都要重读一遍同一个坏文件。
			lock (Gate)
			{
				_cache = new CachedSnapshot(path, info.Length, info.LastWriteTimeUtc.Ticks, now, config);
			}

			return config;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
		{
			return SecurityConfig.Off with { Enabled = false };
		}
	}

	/// 当前会话生效的档位：有 PiDeck 快照时以它为准，否则用本端口设置。
	public static ToolSecurity Resolve(SecurityConfig fallback, string? sessionFile)
	{
		if (Load() is not { } snapshot)
		{
			return fallback.Resolve(sessionFile);
		}

		if (!snapshot.Enabled)
		{
			return ToolSecurity.Off;
		}

		// 会话 id 不缓存：同一进程里可能有多个会话（桌面），环境变量按会话换。
		if (Environment.GetEnvironmentVariable(SessionIdVariable) is { Length: > 0 } sessionId &&
			snapshot.SessionLevels.TryGetValue(sessionId, out var overridden) &&
			snapshot.Find(overridden) is { } level)
		{
			return level;
		}

		return snapshot.Find(snapshot.DefaultLevelId) ?? ToolSecurity.Off;
	}

	/// 解析快照文本；形状非法时抛 <see cref="JsonException" />。
	internal static SecurityConfig Parse(string text)
	{
		if (JsonNode.Parse(text) is not JsonObject root)
		{
			throw new JsonException("security snapshot must be an object");
		}

		if (Integer(root["schemaVersion"]) is not 1)
		{
			throw new JsonException("unsupported security snapshot schemaVersion");
		}

		var enabled = Flag(root["enabled"]) ?? true;
		var defaultLevel = Text(root["defaultLevelId"])?.Trim() ?? ToolSecurity.OffLevel;
		var levels = new List<ToolSecurity>();
		if (root["levels"] is JsonArray rawLevels)
		{
			foreach (var raw in rawLevels)
			{
				if (raw is JsonObject level && ParseLevel(level) is { } parsed)
				{
					levels.Add(parsed);
				}
			}
		}

		if (levels.Count == 0)
		{
			levels.AddRange(ToolSecurity.Builtins);
		}

		var sessionLevels = new Dictionary<string, string>(StringComparer.Ordinal);
		if (root["sessionLevels"] is JsonObject rawSessions)
		{
			foreach (var pair in rawSessions)
			{
				if (Text(pair.Value) is { Length: > 0 } levelId)
				{
					sessionLevels[pair.Key] = levelId;
				}
			}
		}

		return new SecurityConfig
		{
			Enabled = enabled,
			DefaultLevelId = defaultLevel,
			Levels = levels,
			SessionLevels = sessionLevels,
		};
	}

	private static ToolSecurity? ParseLevel(JsonObject level)
	{
		var id = Text(level["id"])?.Trim();
		if (string.IsNullOrEmpty(id))
		{
			return null;
		}

		var defaultAction = ToolAction.Normalize(Text(level["defaultAction"]));
		// PiDeck 只管控 MANAGED_TOOLS；未列出的工具一律放行。本端口的 Evaluate 会对任意工具
		// 应用 DefaultAction，因此把 8 个受管工具逐个写进 ToolActions，再把 DefaultAction 设成
		// allow，等价于扩展的「其它工具直接 return undefined」。
		var actions = new Dictionary<string, string>(StringComparer.Ordinal);
		if (level["toolActions"] is JsonObject rawActions)
		{
			foreach (var (tool, value) in rawActions)
			{
				if (Text(value) is { } action)
				{
					actions[tool] = ToolAction.Normalize(action);
				}
			}
		}

		foreach (var tool in ToolSecurity.ManagedTools)
		{
			actions.TryAdd(tool, defaultAction);
		}

		var pathPolicy = Text(level["pathPolicy"])?.Trim().ToLowerInvariant() switch
		{
			"workspace" => ToolPathPolicy.Workspace,
			"custom" => ToolPathPolicy.Custom,
			_ => ToolPathPolicy.Unrestricted,
		};

		return new ToolSecurity
		{
			Level = id,
			Name = Text(level["name"]) is { Length: > 0 } name ? name : id,
			Description = Text(level["description"]) ?? string.Empty,
			Builtin = Flag(level["builtin"]) == true,
			ProtectSensitivePaths = Flag(level["protectSensitivePaths"]) == true,
			PathPolicy = pathPolicy,
			CustomAllowDirs = Strings(level["customAllowDirs"]),
			DenyDirs = Strings(level["denyDirs"]),
			DenyBashPatterns = Strings(level["denyBashPatterns"]),
			ToolActions = actions,
			DefaultAction = ToolAction.Allow,
		};
	}

	private static IReadOnlyList<string> Strings(JsonNode? node)
	{
		if (node is not JsonArray array)
		{
			return [];
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

	/// 取值都用 TryGetValue：`GetValue<T>` 在类型不符时抛 InvalidOperationException，
	/// 一个手改坏的快照就会把异常带进工具调用路径。
	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static int? Integer(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

	private static bool? Flag(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
