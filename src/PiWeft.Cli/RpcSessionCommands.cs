using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Skills;
using PiWeft.CodingAgent.Themes;

namespace PiWeft.Cli;

/// 会话、发现与导出相关的 RPC 命令。它们对应 pi 的 <c>rpc-commands.md</c>：同样的命令名、
/// 同样的响应形状与数据字段，因此按 pi 写的客户端无需改动即可使用。
public sealed partial class CliRunner
{
	private bool _autoRetry;

	private JsonObject StateRecord(CodingAgentSession session) => new()
	{
		["model"] = ModelRecordWithThinking(session.Model),
		["thinkingLevel"] = session.ThinkingLevel,
		["isStreaming"] = session.IsStreaming,
		["isCompacting"] = session.IsCompacting,
		["steeringMode"] = QueueModeText(session.SteeringMode),
		["followUpMode"] = QueueModeText(session.FollowUpMode),
		["sessionFile"] = session.Session.SessionFile,
		["sessionId"] = session.Session.SessionId,
		["sessionName"] = session.SessionName,
		["autoCompactionEnabled"] = session.AutoCompactionEnabled,
		["planMode"] = session.PlanModeEnabled,
		["projectTrusted"] = session.ProjectTrusted,
		// pi 的 messageCount 是对话消息数，不含运行时注入的系统提示词。
		["messageCount"] = session.Messages.Count(static message => message is not SystemMessage),
		["pendingMessageCount"] = session.PendingMessageCount,

		// pi 形状之外的补充字段：桌面客户端会直接展示。
		["cwd"] = session.Cwd,
		["tools"] = new JsonArray([.. session.Tools.Select(static tool => (JsonNode?)JsonValue.Create(tool.Name))]),
		["entryCount"] = session.Session.Entries.Count,
		["errorMessage"] = session.ErrorMessage,
		["cacheWarming"] = session.CacheWarmingStatus is { } warming ? CacheWarmingRecord(warming) : null,
	};

	private static JsonObject CacheWarmingRecord(PiWeft.CodingAgent.Compaction.CacheWarmingStatus status)
	{
		var record = new JsonObject
		{
			["state"] = status.State,
			["reason"] = status.Reason,
			["nextWarmAt"] = status.NextWarmAt?.ToUnixTimeMilliseconds(),
			["extensionOverride"] = status.ExtensionOverride,
		};
		if (status.Decision is { } decision)
		{
			record["decision"] = new JsonObject
			{
				["phase"] = decision.Idle ? "idle" : "streaming",
				["warmCost"] = decision.WarmCost,
				["missCost"] = decision.MissCost,
				["continuationProbability"] = decision.ContinuationProbability,
				["expectedSavings"] = decision.ExpectedSavings,
				["economicsAvailable"] = decision.EconomicsAvailable,
				["action"] = decision.Action,
			};
		}

		return record;
	}

	private static string QueueModeText(AgentQueueMode mode) => mode == AgentQueueMode.All ? "all" : "one-at-a-time";

	private JsonObject SessionStatsRecord(CodingAgentSession session)
	{
		var entries = session.Session.Entries;
		var userMessages = 0;
		var assistantMessages = 0;
		var toolCalls = 0;
		var toolResults = 0;

		foreach (var entry in entries)
		{
			switch (entry)
			{
				case SessionMessageEntry { Message: UserMessage }:
					userMessages++;
					break;

				case SessionMessageEntry { Message: AssistantMessage assistant }:
					assistantMessages++;
					toolCalls += assistant.Content.OfType<ToolCall>().Count();
					break;

				case SessionMessageEntry { Message: ToolResultMessage }:
					toolResults++;
					break;
			}
		}

		var (_, cost) = session.UsageTotals();
		var totals = session.UsageBreakdown();
		var usage = new JsonObject
		{
			["input"] = totals.Input,
			["output"] = totals.Output,
			["cacheRead"] = totals.CacheRead,
			["cacheWrite"] = totals.CacheWrite,
			["total"] = totals.Input + totals.Output + totals.CacheRead + totals.CacheWrite,
		};

		var stats = new JsonObject
		{
			["sessionFile"] = session.Session.SessionFile,
			["sessionId"] = session.Session.SessionId,
			["userMessages"] = userMessages,
			["assistantMessages"] = assistantMessages,
			["toolCalls"] = toolCalls,
			["toolResults"] = toolResults,
			["totalMessages"] = userMessages + assistantMessages + toolResults,
			["tokens"] = usage,
			["cost"] = cost,
		};

		if (session.Model.ContextWindow > 0)
		{
			var (contextTokens, _) = session.ContextTokens();
			stats["contextUsage"] = new JsonObject
			{
				["tokens"] = contextTokens,
				["contextWindow"] = session.Model.ContextWindow,
				["percent"] = Math.Round(100.0 * contextTokens / session.Model.ContextWindow, 2),
			};
		}

		return stats;
	}

	private static JsonObject EntriesRecord(SessionManager manager, IReadOnlyList<SessionEntry> entries) => new()
	{
		["entries"] = new JsonArray([.. entries.Select(EntryRecord)]),
		["leafId"] = manager.LeafId,
	};

	private static JsonObject TreeRecord(SessionManager manager) => new()
	{
		["tree"] = new JsonArray([.. manager.BuildTree().Select(TreeNodeRecord)]),
		["leafId"] = manager.LeafId,
	};

	private static JsonNode TreeNodeRecord(SessionTreeNode node) => new JsonObject
	{
		["entry"] = EntryRecord(node.Entry),
		["children"] = new JsonArray([.. node.Children.Select(TreeNodeRecord)]),
		["label"] = node.Label,
		["labelTimestamp"] = node.LabelTimestamp,
	};

	private static JsonNode EntryRecord(SessionEntry entry) => JsonNode.Parse(SessionJson.Serialize(entry))!;

	private JsonObject CommandsRecord(CodingAgentSession session)
	{
		var commands = new List<JsonNode?>();

		// 扩展命令：pi 的 get_commands 把它们报成 source:"extension"，PiDeck 的斜杠菜单
		// 与 /reload 的来源校验都靠 sourceInfo.path。
		foreach (var command in session.Extensions?.Commands ?? [])
		{
			commands.Add(new JsonObject
			{
				["name"] = command.Name,
				["description"] = command.Description,
				["source"] = "extension",
				["sourceInfo"] = SourceInfoRecord(command.SourcePath ?? string.Empty, "extension"),
			});
		}

		// pi 的 /reload 只在 TUI 里执行；PiDeck 经 pi-deck-runtime.ts 注册成扩展命令，
		// 再按 basename 找回来。本端口把它列为同名的内置扩展命令，由 prompt 路径执行。
		commands.Add(new JsonObject
		{
			["name"] = "reload",
			["description"] = "Reload extensions, skills, prompts, themes, and context files",
			["source"] = "extension",
			["sourceInfo"] = SourceInfoRecord(
				Path.Combine(SessionPaths.GetAgentDir(), "extensions", "pi-deck-runtime.ts"),
				"extension"),
		});

		foreach (var template in session.PromptTemplates)
		{
			commands.Add(new JsonObject
			{
				["name"] = template.Name,
				["description"] = template.Description,
				["source"] = "prompt",
				["sourceInfo"] = SourceInfoRecord(template.FilePath, "prompt"),
			});
		}

		foreach (var skill in session.DiscoveredSkills)
		{
			commands.Add(new JsonObject
			{
				["name"] = $"skill:{skill.Name}",
				["description"] = skill.Description,
				["source"] = "skill",
				["sourceInfo"] = SourceInfoRecord(skill.FilePath, "skill"),
			});
		}

		return new JsonObject { ["commands"] = new JsonArray([.. commands]) };
	}

	/// <c>get_resources</c> / <c>reload_resources</c> 的数据：已解析的技能、提示词模板、主题与包。
	/// 桌面管理界面与 <c>pi</c> 的 RPC 客户端共用同一份形状。
	private static JsonObject ResourcesRecord(CodingAgentSession session)
	{
		var skills = new List<JsonNode?>(session.Resources.Skills.Count);
		foreach (var skill in session.Resources.Skills)
		{
			skills.Add(new JsonObject
			{
				["name"] = skill.Name,
				["description"] = skill.Description,
				["path"] = skill.FilePath,
				["scope"] = ScopeText(skill.Origin.Scope),
				["source"] = skill.Origin.Display,
				["disableModelInvocation"] = skill.DisableModelInvocation,
			});
		}

		var prompts = new List<JsonNode?>(session.Resources.Templates.Count);
		foreach (var template in session.Resources.Templates)
		{
			prompts.Add(new JsonObject
			{
				["name"] = template.Name,
				["path"] = template.FilePath,
				["scope"] = ScopeText(template.Origin.Scope),
				["source"] = template.Origin.Display,
			});
		}

		var themes = new List<JsonNode?>(session.Resources.Themes.Count);
		foreach (var theme in session.Resources.Themes)
		{
			themes.Add(new JsonObject
			{
				["name"] = theme.Name,
				["path"] = theme.FilePath,
				["scope"] = ScopeText(theme.Origin.Scope),
				["source"] = theme.Origin.Display,
				["valid"] = theme.IsValid,
				["error"] = theme.Error,
				["appearance"] = theme.Theme is { } parsed
					? (parsed.Appearance == ThemeAppearance.Light ? "light" : "dark")
					: null,
			});
		}

		var packages = new List<JsonNode?>(session.Resources.Packages.Count);
		foreach (var package in session.Resources.Packages)
		{
			packages.Add(new JsonObject
			{
				["name"] = package.Name,
				["source"] = package.Source,
				["root"] = package.Root,
				["scope"] = ScopeText(package.Scope),
			});
		}

		return new JsonObject
		{
			["skills"] = new JsonArray([.. skills]),
			["prompts"] = new JsonArray([.. prompts]),
			["themes"] = new JsonArray([.. themes]),
			["packages"] = new JsonArray([.. packages]),
			["diagnostics"] = new JsonArray([.. session.Resources.Diagnostics.Select(static text => (JsonNode?)JsonValue.Create(text))]),
		};
	}

	private static string ScopeText(ResourceScope scope) => scope switch
	{
		ResourceScope.Project => "project",
		ResourceScope.Package => "package",
		ResourceScope.Builtin => "builtin",
		_ => "user",
	};

	private JsonObject SourceInfoRecord(string path, string source)
	{
		// agent 目录下的资源属于用户级，其余位于项目下的属于项目级。
		var agentDir = Path.GetFullPath(SessionPaths.GetAgentDir());
		var inAgentDir = path.StartsWith(agentDir, StringComparison.Ordinal);

		return new JsonObject
		{
			["path"] = path,
			["source"] = "local",
			["scope"] = inAgentDir ? "user" : "project",
			["origin"] = "top-level",
		};
	}

	private JsonObject? CycleModel(CodingAgentSession session)
	{
		var models = CycleModels(session);
		if (models.Count <= 1)
		{
			return null;
		}

		var index = models.FindIndex(model =>
			model.Provider == session.Model.Provider && model.Id == session.Model.Id);
		var next = models[(index + 1 + models.Count) % models.Count];

		session.SetModel(next);
		var level = ThinkingLevelResolver.Clamp(next, session.ThinkingLevel);
		session.SetThinkingLevel(level);

		return new JsonObject
		{
			["model"] = ModelRecordWithThinking(next),
			["thinkingLevel"] = level,
			["isScoped"] = _arguments?.Models is { Count: > 0 },
		};
	}

	private static JsonObject? CycleThinkingLevel(CodingAgentSession session)
	{
		var levels = ThinkingLevelResolver.GetSupported(session.Model);
		if (levels.Count <= 1)
		{
			return null;
		}

		var index = levels.ToList().IndexOf(session.ThinkingLevel);
		var next = levels[(index + 1 + levels.Count) % levels.Count];
		session.SetThinkingLevel(next);

		return new JsonObject { ["level"] = session.ThinkingLevel };
	}

	private List<Model> CycleModels(CodingAgentSession session)
	{
		var models = new List<Model>(environment.ListModels());
		var patterns = _arguments?.Models is { Count: > 0 } cliPatterns
			? cliPatterns
			: [.. session.Settings.EnabledModels];
		if (patterns.Count > 0)
		{
			models = [.. models.Where(model => patterns.Any(pattern => CliEnvironment.MatchesModelPattern(model, pattern)))];
		}

		if (models.All(model => model.Provider != session.Model.Provider || model.Id != session.Model.Id))
		{
			models.Insert(0, session.Model);
		}

		return models;
	}

	private string ExportHtml(CodingAgentSession session, string? outputPath)
	{
		var path = outputPath is { Length: > 0 }
			? Path.GetFullPath(outputPath)
			: Path.Combine(
				_arguments?.SessionDir ?? session.Session.SessionDir,
				$"{session.Session.SessionId}.html");

		var directory = Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, SessionHtml.Render(session));
		return path;
	}
}
