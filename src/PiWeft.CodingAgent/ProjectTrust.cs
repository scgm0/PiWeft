using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent;

/// <summary>
/// 项目信任：决定是否加载项目级资源（技能、提示词模板、MCP、项目设置）。
/// 与 pi 的 <c>trust.json</c> 同构——<c>&lt;agentDir&gt;/trust.json</c> 是「规范化绝对路径 → true/false/null」的对象，
/// 查找时从工作目录逐级向上取第一个决策。默认由 <c>settings.defaultProjectTrust</c> 决定。
/// </summary>
public static class ProjectTrust
{
	/// <summary>需要项目信任才会加载的项目配置项（与 pi 一致；不含 AGENTS.md 等上下文文件）。</summary>
	private static readonly string[] TrustRequiringConfigNames =
	[
		"settings.json",
		"mcp.json",
		"extensions",
		"skills",
		"prompts",
		"themes",
		"SYSTEM.md",
		"APPEND_SYSTEM.md",
	];

	/// <summary>信任存储文件路径。</summary>
	public static string StorePath(string agentDir) => Path.Combine(Path.GetFullPath(agentDir), "trust.json");

	/// <summary>读取某个工作目录的已存决策（向上查找父目录）；没有决策时返回 <c>null</c>。</summary>
	public static bool? Get(string agentDir, string cwd)
	{
		var data = Read(StorePath(agentDir));
		if (data.Count == 0)
		{
			return null;
		}

		var current = ContextFiles.Canonicalize(cwd);
		while (true)
		{
			if (data.TryGetValue(current, out var decision))
			{
				return decision;
			}

			var parent = Path.GetDirectoryName(current);
			if (parent is null || parent == current)
			{
				return null;
			}

			current = parent;
		}
	}

	/// <summary>写入一个决策；<paramref name="decision"/> 为 <c>null</c> 表示删除该条目。</summary>
	public static void Set(string agentDir, string cwd, bool? decision)
	{
		var path = StorePath(agentDir);
		var data = Read(path);
		var key = ContextFiles.Canonicalize(cwd);
		if (decision is null)
		{
			data.Remove(key);
		}
		else
		{
			data[key] = decision.Value;
		}

		Write(path, data);
	}

	/// <summary>
	/// 解析实际是否信任：显式开关优先，其次已存决策，最后默认值。
	/// <c>always</c> 与 <c>ask</c> 的区别是后者会被非交互场景拒绝；本端口默认 <c>always</c> 以免破坏既有行为。
	/// </summary>
	public static bool Resolve(string cwd, bool? trustOverride, string? defaultTrust, string? agentDir = null)
	{
		if (trustOverride is { } value)
		{
			return value;
		}

		if (!HasTrustRequiringProjectResources(cwd))
		{
			return true; // 没有需要保护的项目资源，无需信任
		}

		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		if (Get(resolvedAgentDir, cwd) is { } stored)
		{
			return stored;
		}

		return defaultTrust switch
		{
			"never" => false,
			"ask" => false,
			_ => true,
		};
	}

	/// <summary>
	/// 工作目录（或其祖先）是否存在受项目信任保护的项目资源。用户级 <c>~/.agents/skills</c> 不算。
	/// </summary>
	public static bool HasTrustRequiringProjectResources(string cwd)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var userAgentsSkills = string.IsNullOrEmpty(home)
			? null
			: ContextFiles.Canonicalize(Path.Combine(home, ".agents", "skills"));

		var current = ContextFiles.Canonicalize(cwd);
		var configDir = Path.Combine(current, SessionPaths.ConfigDirName);
		foreach (var name in TrustRequiringConfigNames)
		{
			if (File.Exists(Path.Combine(configDir, name)) || Directory.Exists(Path.Combine(configDir, name)))
			{
				return true;
			}
		}

		while (true)
		{
			var agentsSkills = Path.Combine(current, ".agents", "skills");
			if (Directory.Exists(agentsSkills) &&
				(userAgentsSkills is null || !string.Equals(ContextFiles.Canonicalize(agentsSkills), userAgentsSkills, StringComparison.Ordinal)))
			{
				return true;
			}

			var parent = Path.GetDirectoryName(current);
			if (parent is null || parent == current)
			{
				return false;
			}

			current = parent;
		}
	}

	/// <summary>
	/// 是否需要询问用户：需要保护的项目资源存在、没有已存决策，且默认值为 <c>ask</c>。
	/// 显式 <c>--approve/--no-approve</c> 与 <c>always</c>/<c>never</c> 都不会进入询问路径。
	/// </summary>
	public static bool HasPendingDecision(string cwd, string? agentDir = null, bool? trustOverride = null)
	{
		if (trustOverride is not null || !HasTrustRequiringProjectResources(cwd))
		{
			return false;
		}

		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		return Get(resolvedAgentDir, cwd) is null && CodingAgentSettings.GlobalDefaultProjectTrust(resolvedAgentDir) == "ask";
	}

	/// <summary>
	/// 通过宿主 UI 询问是否信任该项目；选择会写入 <c>trust.json</c>（「仅本次会话」不写）。
	/// 无 UI、取消或解析失败一律按不信任处理。
	/// </summary>
	public static async Task<bool> PromptAsync(
		string cwd,
		IUiHost host,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		if (!host.HasUi)
		{
			return false;
		}

		var question = new AskQuestion(
			"trust",
			"select",
			$"是否信任此项目？\n{cwd}\n\n信任后会加载 .pi 下的设置、MCP 与技能。",
			[
				new AskOption("信任（记住）", "trust", null),
				new AskOption("仅本次会话信任", "session", null),
				new AskOption("不信任（记住）", "deny", null),
			],
			null,
			null);

		var envelope = AskQuestions.BuildEnvelope([question], review: false);
		var raw = await host
			.InputAsync(new UiInputRequest(envelope, AskQuestions.EnvelopePlaceholder), cancellationToken)
			.ConfigureAwait(false);

		var (answers, _) = AskQuestions.ParseResponse(raw, [question]);
		var choice = answers.Count > 0 && answers[0].Value is { } value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
			? value.GetValue<string>()
			: string.Empty;

		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		switch (choice)
		{
			case "trust":
				Set(resolvedAgentDir, cwd, true);
				return true;
			case "session":
				return true;
			case "deny":
				Set(resolvedAgentDir, cwd, false);
				return false;
			default:
				return false; // 取消或未能解析
		}
	}

	private static Dictionary<string, bool> Read(string path)
	{
		var data = new Dictionary<string, bool>(StringComparer.Ordinal);
		if (!File.Exists(path))
		{
			return data;
		}

		try
		{
			var text = File.ReadAllText(path).TrimStart('\uFEFF');
			if (JsonNode.Parse(text) is not JsonObject obj)
			{
				return data;
			}

			foreach (var (key, value) in obj)
			{
				// 非法条目直接忽略，不让一个坏值让整个信任库不可用。
				if (value is JsonValue json && (json.GetValueKind() == JsonValueKind.True || json.GetValueKind() == JsonValueKind.False))
				{
					data[key] = json.GetValue<bool>();
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			return new Dictionary<string, bool>(StringComparer.Ordinal);
		}

		return data;
	}

	private static void Write(string path, Dictionary<string, bool> data)
	{
		var directory = Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		var sorted = new JsonObject();
		foreach (var key in data.Keys.OrderBy(static key => key, StringComparer.Ordinal))
		{
			sorted[key] = data[key];
		}

		var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
		File.WriteAllText(temporary, sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
		File.Move(temporary, path, overwrite: true);
	}
}
