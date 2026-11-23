using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.CodingAgent.Subagents;

/// <summary>
/// 共享任务板的四个工具：建、列、看、改。任何成员都能建；领取与完成按 owner 授权，
/// 重新指派只有 lead 能做。任务变更不唤醒任何人，要另一个成员动手时用 send_to_subagent。
/// </summary>
public static class TeamTools
{
	public const string CreateName = "team_task_create";
	public const string ListName = "team_task_list";
	public const string GetName = "team_task_get";
	public const string UpdateName = "team_task_update";

	/// <summary>工具名，按注册顺序。</summary>
	public static IReadOnlyList<string> Names { get; } = [CreateName, ListName, GetName, UpdateName];

	/// <summary>为一个调用者建四个任务工具；caller 是 lead 或子代理名。</summary>
	public static IReadOnlyList<BuiltinTool> Create(TeamBoard board, string caller)
	{
		ArgumentNullException.ThrowIfNull(board);
		return
		[
			CreateTool(board, caller),
			ListTool(board, caller),
			GetTool(board, caller),
			UpdateTool(board, caller),
		];
	}

	private static BuiltinTool CreateTool(TeamBoard board, string caller) => new(
		new AgentTool
		{
			Name = CreateName,
			Label = "Create Team Task",
			PlanAccess = ToolPlanAccess.Allowed,
			Description =
				"Create a task on the shared team board. Use it when work must be tracked across agents: " +
				"give a short subject, a self-contained description, optional blocked_by ids and advisory write_scopes. " +
				"The task starts pending and unowned; a member claims it with team_task_update.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["subject"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "One-line task title, at most 200 characters.",
					},
					["description"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "Self-contained details: scope, done criteria, how to verify, what not to touch.",
					},
					["blocked_by"] = new JsonObject
					{
						["type"] = "array",
						["items"] = new JsonObject { ["type"] = "string" },
						["description"] = "Task ids that must be completed before this one can be claimed.",
					},
					["write_scopes"] = new JsonObject
					{
						["type"] = "array",
						["items"] = new JsonObject { ["type"] = "string" },
						["description"] = "Advisory workspace-relative file or directory prefixes this task writes.",
					},
				},
				["required"] = new JsonArray("subject", "description"),
			},
			Execute = invocation =>
			{
				var outcome = board.Create(
					caller,
					JsonArgs.String(invocation.Arguments["subject"]) ?? string.Empty,
					JsonArgs.String(invocation.Arguments["description"]) ?? string.Empty,
					ReadStrings(invocation.Arguments["blocked_by"]),
					ReadStrings(invocation.Arguments["write_scopes"]));
				return Task.FromResult(Result(outcome));
			},
		},
		"Create a task on the shared team board.",
		["List, get, claim with the current revision, do the work, complete. Task readiness never starts anyone."]);

	private static BuiltinTool ListTool(TeamBoard board, string caller) => new(
		new AgentTool
		{
			Name = ListName,
			Label = "List Team Tasks",
			PlanAccess = ToolPlanAccess.Allowed,
			Description =
				"List tasks on the shared team board. Filters: status (pending / in_progress / completed), " +
				"owner (lead or a subagent name) or unowned true. Returns compact task views.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["status"] = new JsonObject
					{
						["type"] = "string",
						["enum"] = new JsonArray("pending", "in_progress", "completed"),
					},
					["owner"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "Agent name to filter by; cannot be combined with unowned.",
					},
					["unowned"] = new JsonObject
					{
						["type"] = "boolean",
						["description"] = "Only tasks without an owner.",
					},
				},
			},
			Execute = invocation =>
			{
				var outcome = board.List(
					JsonArgs.String(invocation.Arguments["status"]),
					JsonArgs.String(invocation.Arguments["owner"]),
					JsonArgs.Bool(invocation.Arguments["unowned"]) ?? false);
				return Task.FromResult(Result(outcome));
			},
		},
		"List shared team tasks.",
		["Use the current revision from list/get before claiming or editing."]);

	private static BuiltinTool GetTool(TeamBoard board, string caller) => new(
		new AgentTool
		{
			Name = GetName,
			Label = "Get Team Task",
			PlanAccess = ToolPlanAccess.Allowed,
			Description = "Read one task, including its current revision, owner, dependencies and readiness.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["id"] = new JsonObject { ["type"] = "string", ["description"] = "Task id such as task-1." },
				},
				["required"] = new JsonArray("id"),
			},
			Execute = invocation =>
			{
				var id = JsonArgs.String(invocation.Arguments["id"]) ?? string.Empty;
				return Task.FromResult(Result(board.Get(id)));
			},
		},
		"Read one shared team task.",
		["Use the returned revision as expected_revision for the next update."]);

	private static BuiltinTool UpdateTool(TeamBoard board, string caller) => new(
		new AgentTool
		{
			Name = UpdateName,
			Label = "Update Team Task",
			PlanAccess = ToolPlanAccess.Allowed,
			Description =
				"Compare-and-set update: id, expected_revision and action. Actions: claim, release, complete, reopen, " +
				"edit, set_dependencies, reassign (lead only), delete. claim requires pending and finished dependencies. " +
				"Stale revisions are rejected; always read the task first. Task changes do not start anyone.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["id"] = new JsonObject { ["type"] = "string", ["description"] = "Task id such as task-1." },
					["expected_revision"] = new JsonObject
					{
						["type"] = "integer",
						["description"] = "Revision from list/get; stale updates are rejected.",
					},
					["action"] = new JsonObject
					{
						["type"] = "string",
						["enum"] = new JsonArray(
							"claim", "release", "complete", "reopen",
							"edit", "set_dependencies", "reassign", "delete"),
					},
					["subject"] = new JsonObject { ["type"] = "string", ["description"] = "edit only." },
					["description"] = new JsonObject { ["type"] = "string", ["description"] = "edit only." },
					["blocked_by"] = new JsonObject
					{
						["type"] = "array",
						["items"] = new JsonObject { ["type"] = "string" },
						["description"] = "set_dependencies only.",
					},
					["write_scopes"] = new JsonObject
					{
						["type"] = "array",
						["items"] = new JsonObject { ["type"] = "string" },
						["description"] = "edit only.",
					},
					["owner"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "reassign only: lead or a subagent name; omit to unassign.",
					},
				},
				["required"] = new JsonArray("id", "expected_revision", "action"),
			},
			Execute = invocation =>
			{
				var id = JsonArgs.String(invocation.Arguments["id"]) ?? string.Empty;
				var revision = JsonArgs.Int(invocation.Arguments["expected_revision"]);
				if (revision is null)
				{
					return Task.FromResult(AgentToolResult.Error("expected_revision is required"));
				}

				var outcome = board.Update(
					caller,
					id,
					revision.Value,
					JsonArgs.String(invocation.Arguments["action"]) ?? string.Empty,
					JsonArgs.String(invocation.Arguments["subject"]),
					JsonArgs.String(invocation.Arguments["description"]),
					ReadStrings(invocation.Arguments["blocked_by"]),
					ReadStrings(invocation.Arguments["write_scopes"]),
					JsonArgs.String(invocation.Arguments["owner"]));
				return Task.FromResult(Result(outcome));
			},
		},
		"Claim, complete or edit a shared team task.",
		["Read the task first; send a message when another agent should act on the change."]);

	private static AgentToolResult Result(TeamOutcome outcome) =>
		outcome.Ok
			? AgentToolResult.Text(outcome.Text, outcome.Details)
			: AgentToolResult.Error(outcome.Text);

	private static IReadOnlyList<string>? ReadStrings(JsonNode? node)
	{
		if (node is not JsonArray array)
		{
			return null;
		}

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
}
