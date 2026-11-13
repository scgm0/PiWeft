using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.CodingAgent.Subagents;

/// <summary>
/// 子代理的六个工具：<c>delegate</c> 启动，<c>wait_subagents</c> 等待，
/// <c>list_subagents</c> 查看，<c>read_subagent</c> 读正文，<c>send_to_subagent</c> 追加消息，
/// <c>stop_subagent</c> 停掉。
/// 名字与 pi 的 <c>subagent</c> 区分开：这里只做异步委派。
/// </summary>
public static class SubagentTools
{
	/// <summary>启动一个子代理。</summary>
	public const string DelegateName = "delegate";

	/// <summary>等待子代理结束一轮。</summary>
	public const string WaitName = "wait_subagents";

	/// <summary>列出本会话的子代理与可用角色。</summary>
	public const string ListName = "list_subagents";

	/// <summary>读子会话最近的助手正文。</summary>
	public const string ReadName = "read_subagent";

	/// <summary>给子代理追加一条消息。</summary>
	public const string SendName = "send_to_subagent";

	/// <summary>停掉子代理当前回合。</summary>
	public const string StopName = "stop_subagent";

	/// <summary>六个工具名，按注册顺序。</summary>
	public static IReadOnlyList<string> Names { get; } = [DelegateName, WaitName, ListName, ReadName, SendName, StopName];

	/// <summary>是否是子代理工具。</summary>
	public static bool IsName(string name) =>
		name is DelegateName or WaitName or ListName or ReadName or SendName or StopName;

	/// <summary>按名字取一个工具。会话构造时按名字逐个绑定，这里不重复构造整份清单。</summary>
	public static BuiltinTool Bind(string name, SubagentHub hub)
	{
		ArgumentNullException.ThrowIfNull(hub);
		foreach (var tool in Create(hub))
		{
			if (string.Equals(tool.Tool.Name, name, StringComparison.Ordinal))
			{
				return tool;
			}
		}

		throw new ArgumentOutOfRangeException(nameof(name), name, "unknown subagent tool");
	}

	/// <summary>全部六个工具。</summary>
	public static IReadOnlyList<BuiltinTool> Create(SubagentHub hub)
	{
		ArgumentNullException.ThrowIfNull(hub);
		return
		[
			new BuiltinTool(
				new AgentTool
				{
					Name = DelegateName,
					Label = "Delegate",
					Description = DelegateDescription(hub.Catalog),
					Parameters = DelegateParameters(),
					Execute = invocation => ExecuteDelegateAsync(hub, invocation),
				},
				"Start a subagent on a self-contained task; its final reply arrives as a message",
				[
					"Write the task as a self-contained brief: scope, done criteria, how to verify, what not to touch. The child cannot see this conversation unless you pass context \"fork\".",
					"Start independent delegations in one assistant message and keep working while they run. When only waiting is left, call wait_subagents or end your turn; the result starts a new turn.",
					"Collect the subagent results you need before giving the user a final answer.",
					"Do not edit the same files from both sides. Bash, formatters and code generators can overwrite concurrent edits; coordinate them explicitly and review the final diff.",
				]),

			new BuiltinTool(
				new AgentTool
				{
					Name = WaitName,
					Label = "Wait Subagents",
					Description =
						"Wait until the named subagents finish a turn, or until the timeout (default 10 minutes). " +
						"Returns state and result text for each; results taken this way are not delivered again as a separate message. " +
						"Omit names to wait for every running subagent; pass any = true to return as soon as one finishes.",
					Parameters = WaitParameters(),
					Execute = invocation => ExecuteWaitAsync(hub, invocation),
				},
				"Wait for subagents to finish a turn",
				[
					"Use wait_subagents only when the next step depends on the result; otherwise end your turn and let the completion message wake you.",
				]),

			new BuiltinTool(
				new AgentTool
				{
					Name = ListName,
					Label = "List Subagents",
					Description =
						"List the subagents this session started and the agent roles available to delegate. " +
						"Rows marked finished come from earlier sessions and have no live session; their transcript is in the session file.",
					Parameters = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject(),
					},
					Execute = _ =>
					{
						var text = hub.Describe(out var details);
						return Task.FromResult(AgentToolResult.Text(text, details));
					},
				},
				"List running and finished subagents",
				[]),

			new BuiltinTool(
				new AgentTool
				{
					Name = ReadName,
					Label = "Read Subagent",
					Description =
						"Read the most recent assistant text of a subagent by name or session id. " +
						"Use it when the delivered result was truncated or after a restart when only the session file remains.",
					Parameters = ReadParameters(),
					Execute = invocation => Task.FromResult(ExecuteRead(hub, invocation)),
				},
				"Read a subagent's latest assistant text",
				[]),

			new BuiltinTool(
				new AgentTool
				{
					Name = SendName,
					Label = "Send To Subagent",
					Description =
						"Send a message to a subagent by name or session id. " +
						"mode follow_up (default) queues the child's next turn; mode steer injects it into the running turn. " +
						"This call does not wait for the reply.",
					Parameters = SendParameters(),
					Execute = invocation => ExecuteSendAsync(hub, invocation),
				},
				"Send a follow-up message to a subagent",
				[
					"Use send_to_subagent for extra requirements on an existing child; do not start a second subagent for the same task.",
					"Use mode \"steer\" only when the child is running and the guidance cannot wait for the next turn.",
				]),

			new BuiltinTool(
				new AgentTool
				{
					Name = StopName,
					Label = "Stop Subagent",
					Description = "Stop a subagent's current turn. The child session stays readable and can be inspected.",
					Parameters = TargetParameters("name or session id"),
					Execute = invocation => Task.FromResult(ExecuteStop(hub, invocation)),
				},
				"Stop a subagent's current turn",
				[]),
		];
	}

	private static async Task<AgentToolResult> ExecuteDelegateAsync(SubagentHub hub, AgentToolInvocation invocation)
	{
		var agent = JsonArgs.String(invocation.Arguments["agent"]);
		var task = JsonArgs.String(invocation.Arguments["task"]);
		if (string.IsNullOrWhiteSpace(agent))
		{
			return AgentToolResult.Error("agent is required");
		}

		if (string.IsNullOrWhiteSpace(task))
		{
			return AgentToolResult.Error("task is required");
		}

		var name = JsonArgs.String(invocation.Arguments["name"]);
		var label = JsonArgs.String(invocation.Arguments["label"]);
		var context = JsonArgs.String(invocation.Arguments["context"]);
		var schemaNode = invocation.Arguments["output_schema"];
		if (schemaNode is not null and not JsonObject)
		{
			return AgentToolResult.Error("output_schema must be a JSON object");
		}

		var outputSchema = schemaNode is JsonObject schema ? schema.DeepClone().AsObject() : null;
		var outcome = await hub.StartAsync(
			new SubagentStartRequest(agent, task, name, context) { Label = label, OutputSchema = outputSchema },
			invocation.CancellationToken).ConfigureAwait(false);

		return outcome.Ok
			? AgentToolResult.Text(outcome.Text, new JsonObject
			{
				["name"] = outcome.Name,
				["session_id"] = outcome.SessionId,
				["status"] = "started",
			})
			: AgentToolResult.Error(outcome.Text);
	}

	private static async Task<AgentToolResult> ExecuteWaitAsync(SubagentHub hub, AgentToolInvocation invocation)
	{
		var targets = ReadNames(invocation.Arguments["names"]);
		var any = JsonArgs.Bool(invocation.Arguments["any"]) ?? false;
		var timeoutMs = JsonArgs.Int(invocation.Arguments["timeout_ms"]);
		var timeout = timeoutMs is > 0
			? TimeSpan.FromMilliseconds(Math.Min(timeoutMs.Value, 1_800_000))
			: SubagentHub.DefaultWaitTimeout;

		// 等待期间把「在等谁、在干什么」推给界面：一次工具调用可能阻塞十分钟。
		var outcome = await hub.WaitAsync(
			targets,
			timeout,
			any,
			invocation.CancellationToken,
			invocation.OnUpdate is { } onUpdate
				? text => onUpdate(AgentToolResult.Text(text))
				: null).ConfigureAwait(false);
		return outcome.Ok
			? AgentToolResult.Text(outcome.Text, outcome.Details)
			: AgentToolResult.Error(outcome.Text);
	}

	private static AgentToolResult ExecuteRead(SubagentHub hub, AgentToolInvocation invocation)
	{
		var target = JsonArgs.String(invocation.Arguments["id"]);
		if (string.IsNullOrWhiteSpace(target))
		{
			return AgentToolResult.Error("id is required");
		}

		var maxChars = JsonArgs.Int(invocation.Arguments["max_chars"]) ?? 0;
		var outcome = hub.Read(target, maxChars);
		return outcome.Ok ? AgentToolResult.Text(outcome.Text, outcome.Details) : AgentToolResult.Error(outcome.Text);
	}

	private static async Task<AgentToolResult> ExecuteSendAsync(SubagentHub hub, AgentToolInvocation invocation)
	{
		var target = JsonArgs.String(invocation.Arguments["id"]);
		var message = JsonArgs.String(invocation.Arguments["message"]);
		if (string.IsNullOrWhiteSpace(target))
		{
			return AgentToolResult.Error("id is required");
		}

		if (string.IsNullOrWhiteSpace(message))
		{
			return AgentToolResult.Error("message is required");
		}

		var mode = JsonArgs.String(invocation.Arguments["mode"]);
		// 会话没打开时会先冷恢复（读会话文件、重建子会话），比一次工具调用稍慢。
		var outcome = await hub.SendAsync(target, message, mode, invocation.CancellationToken).ConfigureAwait(false);
		return outcome.Ok ? AgentToolResult.Text(outcome.Text) : AgentToolResult.Error(outcome.Text);
	}

	private static AgentToolResult ExecuteStop(SubagentHub hub, AgentToolInvocation invocation)
	{
		var target = JsonArgs.String(invocation.Arguments["id"]);
		if (string.IsNullOrWhiteSpace(target))
		{
			return AgentToolResult.Error("id is required");
		}

		var outcome = hub.Stop(target);
		return outcome.Ok ? AgentToolResult.Text(outcome.Text) : AgentToolResult.Error(outcome.Text);
	}

	private static IReadOnlyList<string>? ReadNames(JsonNode? node)
	{
		if (node is not JsonArray array || array.Count == 0)
		{
			return null;
		}

		var names = new List<string>(array.Count);
		foreach (var item in array)
		{
			if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
			{
				names.Add(text);
			}
		}

		return names.Count == 0 ? null : names;
	}

	private const int MaxListedAgents = 24;

	private const int MaxListedDescriptionChars = 200;

	private static string DelegateDescription(SubagentCatalog catalog)
	{
		var builder = new StringBuilder(256);
		builder.Append(
			"Start a subagent on a self-contained task. Returns immediately with the subagent name; " +
			"the final reply arrives as a message when the child finishes a turn. " +
			"Pass label for a short title in lists and cards; pass output_schema when the parent needs a validated JSON result. ");
		builder.Append("Available agents: ");
		var listed = 0;
		foreach (var definition in catalog.All)
		{
			if (listed == MaxListedAgents)
			{
				break;
			}

			if (listed > 0)
			{
				builder.Append("; ");
			}

			// 角色文件是用户可加的；描述没有上限时，每次请求都为一个长目录付 token。
			var description = definition.Description;
			if (description.Length > MaxListedDescriptionChars)
			{
				description = string.Concat(description.AsSpan(0, MaxListedDescriptionChars).TrimEnd(), "…");
			}

			builder.Append(definition.Name).Append(": ").Append(description);
			listed++;
		}

		var remaining = catalog.All.Count - listed;
		if (remaining > 0)
		{
			builder.Append("; +").Append(remaining).Append(" more (list_subagents)");
		}

		return builder.ToString();
	}

	private static JsonObject DelegateParameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["agent"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Agent role to run. See the tool description for the available names.",
			},
			["task"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Self-contained brief: scope, done criteria, how to verify, what not to touch. The child cannot see this conversation.",
			},
			["name"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Optional name for this child, unique among your subagents: 1-32 lower-case letters, digits or hyphens, starting with a letter.",
			},
			["label"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Optional one-line label for lists and cards; defaults to the first line of the task.",
			},
			["context"] = new JsonObject
			{
				["type"] = "string",
				["enum"] = new JsonArray("fresh", "fork"),
				["description"] =
					"Context mode. fresh gives the child only the task; " +
					"fork seeds it with this conversation up to the last completed turn.",
			},
			["output_schema"] = new JsonObject
			{
				["type"] = "object",
				["description"] =
					"Optional JSON Schema subset for the final reply. The child must return one JSON value matching it; " +
					"invalid output marks the run failed. Supported keywords: type, properties, required, items, enum, " +
					"additionalProperties. The root type must be object.",
			},
		},
		["required"] = new JsonArray("agent", "task"),
	};

	private static JsonObject WaitParameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["names"] = new JsonObject
			{
				["type"] = "array",
				["items"] = new JsonObject { ["type"] = "string" },
				["description"] = "Subagent names or session ids. Omit to wait for every running subagent.",
			},
			["timeout_ms"] = new JsonObject
			{
				["type"] = "integer",
				["description"] = "Maximum wait in milliseconds; defaults to 600000 (10 minutes), capped at 1800000.",
			},
			["any"] = new JsonObject
			{
				["type"] = "boolean",
				["description"] = "Return as soon as one target finishes; omit or false to wait for every target.",
			},
		},
	};

	private static JsonObject ReadParameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["id"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Subagent name or session id.",
			},
			["max_chars"] = new JsonObject
			{
				["type"] = "integer",
				["description"] = "Maximum characters to return; defaults to 12000, capped at 48000.",
			},
		},
		["required"] = new JsonArray("id"),
	};

	private static JsonObject SendParameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["id"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Subagent name or session id.",
			},
			["message"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "What to add or change. The child receives it as a user message.",
			},
			["mode"] = new JsonObject
			{
				["type"] = "string",
				["enum"] = new JsonArray("follow_up", "steer", "auto"),
				["description"] =
					"follow_up queues the child's next turn; steer injects into the running turn; " +
					"auto steers when running and falls back to follow_up. Defaults to follow_up.",
			},
		},
		["required"] = new JsonArray("id", "message"),
	};

	private static JsonObject TargetParameters(string description) => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["id"] = new JsonObject
			{
				["type"] = "string",
				["description"] = description,
			},
		},
		["required"] = new JsonArray("id"),
	};
}
