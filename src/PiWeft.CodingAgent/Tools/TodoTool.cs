using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Tools;

/// <summary>待办项；状态取值 <c>pending</c>、<c>in_progress</c>、<c>completed</c>。</summary>
public sealed record TodoItem(int Id, string Text, string Status);

/// <summary>一个待办计划；条目至少一条。</summary>
public sealed record TodoPlan(int Id, IReadOnlyList<TodoItem> Items);

/// <summary>
/// 与 PiDeck 的 <c>pi-deck-todo-state.ts</c> 对齐的 v3 快照。
/// <c>PreviousPlan</c> 是最近一次被 <c>replace</c> 取代的计划，供 <c>restore</c> 使用。
/// </summary>
public sealed record TodoState(TodoPlan? ActivePlan, TodoPlan? PreviousPlan, int NextPlanId, int NextTodoId)
{
	public static TodoState Empty { get; } = new(null, null, 1, 1);
}

/// <summary>
/// 原生 <c>todo</c> 工具：用与 PiDeck 相同的 v3 自定义条目（<c>customType: "pi-deck-todo"</c>）
/// 持久化当前分支的待办计划。
/// <para>
/// 设计要点与 PiDeck 一致：状态只在显式工具调用时改变；计划的最新视图由**本次变更的
/// toolResult** 携带（append-only，不打断提示前缀缓存），不做每轮注入。切换分支时从该分支
/// 的最后一个合法快照重建，因此旧分支的计划自动恢复。
/// </para>
/// </summary>
public static class TodoTool
{
	/// <summary>PiDeck 使用的快照条目类型。</summary>
	public const string EntryType = "pi-deck-todo";

	/// <summary>工具名。</summary>
	public const string Name = "todo";

	private const int MaxTextLength = 1000;
	private const int MaxCount = 100;
	private const int MaxModelVisibleItems = 30;
	private const int MaxErrorListedIds = 10;

	private static readonly string[] Statuses = ["pending", "in_progress", "completed"];
	private static readonly string[] Actions = ["list", "add", "update", "delete", "replace", "restore", "clear"];

	/// <summary>构建工具。<paramref name="session"/> 返回当前会话（可能因切换会话而变化）。</summary>
	public static BuiltinTool Create(Func<SessionManager?> session) => new(
		new AgentTool
		{
			Name = Name,
			Label = "Todo",
			Description =
				"Manage the current todo plan. Actions: list, add, update (id + status/text), delete (id, removing a single item), " +
				"replace (atomically begin a new plan), restore (undo the latest replacement), and clear (intentionally remove it).",
			Parameters = Parameters(),
			Execute = invocation => Task.FromResult(Execute(session(), invocation)),
		},
		"List or change the current todo plan (list / add / update / delete / replace / restore / clear)",
		[
			"Maintain the actionable plan with the todo tool. After finishing an item, call action=update with its id and status=completed. Remove one obsolete item with action=delete and its id. Use action=replace only to rebuild the whole plan. If an id is uncertain, call action=list first.",
			"Start a new or materially re-scoped task with one action=replace call containing the complete new plan. Never infer that boundary from completed items, idle time, a user message, or session start.",
			"If a replacement was mistaken, call action=restore immediately. Use action=clear only when intentionally discarding the active plan.",
			"Todo state is per-branch: switching branches restores that branch's plan.",
		]);

	private static JsonObject Parameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["action"] = new JsonObject
			{
				["type"] = "string",
				["enum"] = new JsonArray([.. Actions.Select(static action => (JsonNode?)JsonValue.Create(action))]),
			},
			["text"] = new JsonObject { ["type"] = "string", ["description"] = "Todo text (for add / update)" },
			["status"] = new JsonObject
			{
				["type"] = "string",
				["enum"] = new JsonArray([.. Statuses.Select(static status => (JsonNode?)JsonValue.Create(status))]),
			},
			["id"] = new JsonObject { ["type"] = "number", ["description"] = "Todo ID (for update / delete)" },
			["items"] = new JsonObject
			{
				["type"] = "array",
				["description"] = "Complete replacement plan (required for replace)",
				["items"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["text"] = new JsonObject { ["type"] = "string", ["description"] = "Todo text in a replacement plan" },
						["status"] = new JsonObject
						{
							["type"] = "string",
							["enum"] = new JsonArray([.. Statuses.Select(static status => (JsonNode?)JsonValue.Create(status))]),
						},
					},
					["required"] = new JsonArray("text"),
				},
			},
		},
		["required"] = new JsonArray("action"),
		["additionalProperties"] = false,
	};

	private static AgentToolResult Execute(SessionManager? session, AgentToolInvocation invocation)
	{
		if (session is null)
		{
			return AgentToolResult.Error("No active session for the todo tool.");
		}

		var state = Reconstruct(session);
		var action = Text(invocation.Arguments["action"]) ?? string.Empty;
		var outcome = Reduce(state, action, invocation.Arguments);
		if (!outcome.Ok)
		{
			return AgentToolResult.Error(outcome.Error!);
		}

		if (outcome.Changed)
		{
			session.AppendCustom(EntryType, Encode(outcome.State));
		}

		var text = new StringBuilder(ResultText(action, outcome));
		if (outcome.Changed && outcome.State.ActivePlan is { } plan)
		{
			// append-only：计划全貌只随本次变更的 toolResult 进入历史。
			text.Append("\n\n").Append(FormatPlan(plan));
		}

		return AgentToolResult.Text(text.ToString());
	}

	/// <summary>从当前分支最后一个合法 v3 快照重建状态；无快照视为空计划。</summary>
	public static TodoState Current(SessionManager session) => Reconstruct(session);

	/// <summary>从当前分支最后一个合法 v3 快照重建状态；无快照视为空计划。</summary>
	private static TodoState Reconstruct(SessionManager session)
	{
		JsonNode? latest = null;
		foreach (var entry in session.GetBranch())
		{
			if (entry is CustomEntry { CustomType: EntryType } custom)
			{
				latest = custom.Data;
			}
		}

		return Decode(latest) ?? TodoState.Empty;
	}

	// ------------------------------------------------------------------ 解码 / 编码

	/// <summary>解码 v3 快照；版本不符或任一计划非法时返回 <c>null</c>（视为无计划，不迁移）。</summary>
	public static TodoState? Decode(JsonNode? data)
	{
		if (data is not JsonObject obj || PositiveInt(obj["version"]) != 3)
		{
			return null;
		}

		var hasActive = obj["activePlan"] is JsonObject;
		var hasPrevious = obj["previousPlan"] is JsonObject;
		var active = hasActive ? DecodePlan(obj["activePlan"]) : null;
		var previous = hasPrevious ? DecodePlan(obj["previousPlan"]) : null;
		if ((hasActive && active is null) || (hasPrevious && previous is null))
		{
			return null;
		}

		var largestPlanId = Math.Max(active?.Id ?? 0, previous?.Id ?? 0);
		var highestTodoId = 0;
		foreach (var item in Enumerate(active, previous))
		{
			highestTodoId = Math.Max(highestTodoId, item.Id);
		}

		return new TodoState(
			active,
			previous,
			Math.Max(PositiveInt(obj["nextPlanId"]) ?? 1, largestPlanId + 1),
			Math.Max(PositiveInt(obj["nextTodoId"]) ?? 1, highestTodoId + 1));
	}

	private static IEnumerable<TodoItem> Enumerate(TodoPlan? active, TodoPlan? previous)
	{
		if (active is not null)
		{
			foreach (var item in active.Items)
			{
				yield return item;
			}
		}

		if (previous is not null)
		{
			foreach (var item in previous.Items)
			{
				yield return item;
			}
		}
	}

	private static TodoPlan? DecodePlan(JsonNode? node)
	{
		if (node is not JsonObject obj || PositiveInt(obj["id"]) is not { } id)
		{
			return null;
		}

		if (obj["todos"] is not JsonArray todos || todos.Count == 0)
		{
			return null;
		}

		var items = new List<TodoItem>(todos.Count);
		foreach (var raw in todos)
		{
			if (DecodeItem(raw) is not { } item)
			{
				return null; // 坏项整体拒绝，不静默截断
			}

			items.Add(item);
		}

		return new TodoPlan(id, items);
	}

	private static TodoItem? DecodeItem(JsonNode? node)
	{
		if (node is not JsonObject obj)
		{
			return null;
		}

		var id = PositiveInt(obj["id"]);
		var text = Text(obj["text"]);
		var status = Text(obj["status"]);
		if (id is null || string.IsNullOrWhiteSpace(text) || status is null || !Statuses.Contains(status, StringComparer.Ordinal))
		{
			return null;
		}

		return new TodoItem(id.Value, text, status);
	}

	/// <summary>编码 v3 快照，字段与 PiDeck 完全一致。</summary>
	public static JsonObject Encode(TodoState state)
	{
		var obj = new JsonObject { ["version"] = 3 };
		if (state.ActivePlan is { } active)
		{
			obj["activePlan"] = EncodePlan(active);
		}

		if (state.PreviousPlan is { } previous)
		{
			obj["previousPlan"] = EncodePlan(previous);
		}

		obj["nextPlanId"] = state.NextPlanId;
		obj["nextTodoId"] = state.NextTodoId;
		return obj;
	}

	private static JsonObject EncodePlan(TodoPlan plan) => new()
	{
		["id"] = plan.Id,
		["todos"] = new JsonArray([.. plan.Items.Select(static item => (JsonNode?)new JsonObject
		{
			["id"] = item.Id,
			["text"] = item.Text,
			["status"] = item.Status,
		})]),
	};

	// ------------------------------------------------------------------ reducer

	private sealed record Outcome(
		bool Ok,
		string? Error,
		TodoState State,
		bool Changed,
		int TodoCount,
		TodoItem? Added = null,
		TodoItem? Updated = null,
		TodoItem? Deleted = null,
		bool UpdatedStatus = false,
		bool UpdatedText = false,
		int? ActivePlanId = null);

	private static Outcome Fail(string error) => new(false, error, TodoState.Empty, false, 0);

	private static Outcome Reduce(TodoState current, string action, JsonObject args)
	{
		switch (action)
		{
			case "list":
				return new Outcome(true, null, current, false, current.ActivePlan?.Items.Count ?? 0);

			case "add":
			{
				if (!TryCheckedText(args["text"], "text required for add", out var text, out var textError))
				{
					return Fail(textError!);
				}

				if (!TryCheckedStatus(args["status"], "pending", out var status, out var statusError))
				{
					return Fail(statusError!);
				}

				var item = new TodoItem(current.NextTodoId, text, status);
				if (current.ActivePlan is { } active)
				{
					if (active.Items.Count >= MaxCount)
					{
						return Fail($"todo plan is full (max {MaxCount} items)");
					}

					var items = new List<TodoItem>(active.Items) { item };
					return new Outcome(
						true, null,
						current with { ActivePlan = new TodoPlan(active.Id, items) },
						true, items.Count, Added: item);
				}

				var plan = new TodoPlan(current.NextPlanId, [item]);
				return new Outcome(
					true, null,
					current with { ActivePlan = plan, NextPlanId = current.NextPlanId + 1, NextTodoId = current.NextTodoId + 1 },
					true, 1, Added: item);
			}

			case "update":
			{
				if (PositiveInt(args["id"]) is not { } id)
				{
					return Fail(args["id"] is null ? "id required for update" : "id must be a positive safe integer");
				}

				if (args["status"] is null && args["text"] is null)
				{
					return Fail("update requires status or text");
				}

				if (current.ActivePlan is not { } active)
				{
					return Fail(UnknownIdError(id, null));
				}

				var index = IndexOf(active, id);
				if (index < 0)
				{
					return Fail(UnknownIdError(id, active));
				}

				var target = active.Items[index];
				var nextText = target.Text;
				var nextStatus = target.Status;
				var changedText = false;
				var changedStatus = false;

				if (args["status"] is not null)
				{
					if (!TryCheckedStatus(args["status"], target.Status, out nextStatus, out var statusError))
					{
						return Fail(statusError!);
					}

					changedStatus = !string.Equals(nextStatus, target.Status, StringComparison.Ordinal);
				}

				if (args["text"] is not null)
				{
					if (!TryCheckedText(args["text"], "text required for update", out nextText, out var textError))
					{
						return Fail(textError!);
					}

					changedText = !string.Equals(nextText, target.Text, StringComparison.Ordinal);
				}

				var updated = new TodoItem(id, nextText, nextStatus);
				if (!changedStatus && !changedText)
				{
					return new Outcome(
						true, null, current, false, active.Items.Count,
						Updated: updated, UpdatedStatus: false, UpdatedText: false);
				}

				var items = new List<TodoItem>(active.Items);
				items[index] = updated;
				return new Outcome(
					true, null,
					current with { ActivePlan = new TodoPlan(active.Id, items) },
					true, items.Count, Updated: updated, UpdatedStatus: changedStatus, UpdatedText: changedText);
			}

			case "delete":
			{
				if (PositiveInt(args["id"]) is not { } id)
				{
					return Fail(args["id"] is null ? "id required for delete" : "id must be a positive safe integer");
				}

				if (current.ActivePlan is not { } active || IndexOf(active, id) < 0)
				{
					return Fail(UnknownIdError(id, current.ActivePlan));
				}

				var target = active.Items[IndexOf(active, id)];
				var items = active.Items.Where(item => item.Id != id).ToList();
				var nextActive = items.Count > 0 ? new TodoPlan(active.Id, items) : null;
				return new Outcome(
					true, null,
					current with { ActivePlan = nextActive },
					true, items.Count, Deleted: target);
			}

			case "replace":
			{
				if (args["items"] is not JsonArray items || items.Count == 0)
				{
					return Fail("items required for replace");
				}

				if (items.Count > MaxCount)
				{
					return Fail($"plan exceeds {MaxCount} items");
				}

				var todos = new List<TodoItem>(items.Count);
				var candidateId = current.NextTodoId;
				for (var index = 0; index < items.Count; index++)
				{
					if (items[index] is not JsonObject item)
					{
						return Fail($"items[{index}].text required for replace");
					}

					if (item.ContainsKey("done"))
					{
						return Fail($"items[{index}].done is not supported; use status");
					}

					if (!TryCheckedText(item["text"], $"items[{index}].text required for replace", out var text, out var textError))
					{
						return Fail(textError!);
					}

					if (!TryCheckedStatus(item["status"], "pending", out var status, out var statusError))
					{
						return Fail(statusError!);
					}

					todos.Add(new TodoItem(candidateId, text, status));
					candidateId++;
				}

				var previous = current.ActivePlan;
				return new Outcome(
					true, null,
					current with
					{
						ActivePlan = new TodoPlan(current.NextPlanId, todos),
						PreviousPlan = previous,
						NextPlanId = current.NextPlanId + 1,
						NextTodoId = current.NextTodoId + todos.Count,
					},
					true, todos.Count);
			}

			case "restore":
			{
				if (current.PreviousPlan is not { } previous)
				{
					return Fail("no replaced plan is available to restore");
				}

				var outgoing = current.ActivePlan;
				return new Outcome(
					true, null,
					current with { ActivePlan = previous, PreviousPlan = outgoing },
					true, previous.Items.Count, ActivePlanId: previous.Id);
			}

			case "clear":
			{
				if (current.ActivePlan is null && current.PreviousPlan is null)
				{
					return new Outcome(true, null, current, false, 0);
				}

				return new Outcome(true, null, current with { ActivePlan = null, PreviousPlan = null }, true, 0);
			}

			default:
				return Fail($"unknown action: {action}; valid actions: {string.Join(", ", Actions)}");
		}
	}

	private static int IndexOf(TodoPlan plan, int id)
	{
		for (var index = 0; index < plan.Items.Count; index++)
		{
			if (plan.Items[index].Id == id)
			{
				return index;
			}
		}

		return -1;
	}

	private static string UnknownIdError(int id, TodoPlan? plan)
	{
		var ids = plan?.Items.Select(static item => item.Id).ToList() ?? [];
		if (ids.Count == 0)
		{
			return $"#{id} not found; current ids: none";
		}

		if (ids.Count <= MaxErrorListedIds)
		{
			return $"#{id} not found; current ids: {string.Join(", ", ids)}";
		}

		var prefix = string.Join(", ", ids.Take(MaxErrorListedIds));
		return $"#{id} not found; current ids: {prefix}, … ({ids.Count - MaxErrorListedIds} more; call list)";
	}

	private static bool TryCheckedText(JsonNode? node, string missingError, out string text, out string? error)
	{
		text = string.Empty;
		error = null;
		if (Text(node) is not { } raw || raw.Trim().Length == 0)
		{
			error = missingError;
			return false;
		}

		var trimmed = raw.Trim();
		if (trimmed.Length > MaxTextLength)
		{
			error = $"todo text exceeds {MaxTextLength} character limit";
			return false;
		}

		text = trimmed;
		return true;
	}

	private static bool TryCheckedStatus(JsonNode? node, string fallback, out string status, out string? error)
	{
		status = fallback;
		error = null;
		if (node is null)
		{
			return true;
		}

		if (Text(node) is not { } value || !Statuses.Contains(value, StringComparer.Ordinal))
		{
			error = $"status must be one of: {string.Join(", ", Statuses)}";
			return false;
		}

		status = value;
		return true;
	}

	// ------------------------------------------------------------------ 输出

	private static string ResultText(string action, Outcome result) => action switch
	{
		"list" => FormatPlan(result.State.ActivePlan),
		"add" => $"Added todo #{result.Added?.Id}: {result.Added?.Text}{CountSuffix(result.TodoCount)}",
		"update" => result.Updated is { } updated
			? !result.UpdatedStatus && !result.UpdatedText
				? $"Todo #{updated.Id} already {updated.Status}{CountSuffix(result.TodoCount)}"
				: $"Updated todo #{updated.Id} {UpdateChanges(result, updated)}{CountSuffix(result.TodoCount)}"
			: string.Empty,
		"replace" => $"Replaced the current plan with {result.TodoCount} todos",
		"delete" => $"Deleted todo #{result.Deleted?.Id}: {result.Deleted?.Text}{CountSuffix(result.TodoCount)}",
		"restore" => $"Restored todo plan #{result.ActivePlanId}{CountSuffix(result.TodoCount)}",
		_ => "Cleared the current todo plan",
	};

	private static string UpdateChanges(Outcome result, TodoItem item)
	{
		var changes = new List<string>(2);
		if (result.UpdatedStatus)
		{
			changes.Add($"→ {item.Status}");
		}

		if (result.UpdatedText)
		{
			changes.Add($"text: {item.Text}");
		}

		return string.Join(", ", changes);
	}

	private static string CountSuffix(int count) =>
		count == 1 ? " (1 todo in plan)" : $" ({count} todos in plan)";

	/// <summary>模型可见的计划正文：首行统计 + 每行 <c>[status] #id: text</c>。</summary>
	public static string FormatPlan(TodoPlan? plan)
	{
		if (plan is null)
		{
			return "No todos";
		}

		var completed = 0;
		var inProgress = 0;
		var pending = 0;
		foreach (var item in plan.Items)
		{
			switch (item.Status)
			{
				case "completed": completed++; break;
				case "in_progress": inProgress++; break;
				default: pending++; break;
			}
		}

		var builder = new StringBuilder();
		builder.Append("Todo plan #").Append(plan.Id).Append(": ")
			.Append(completed).Append(" completed, ")
			.Append(inProgress).Append(" in progress, ")
			.Append(pending).Append(" pending (")
			.Append(plan.Items.Count).Append(" items)");

		var visible = Math.Min(plan.Items.Count, MaxModelVisibleItems);
		for (var index = 0; index < visible; index++)
		{
			var item = plan.Items[index];
			builder.Append('\n').Append('[').Append(item.Status).Append("] #").Append(item.Id).Append(": ").Append(item.Text);
		}

		if (plan.Items.Count > visible)
		{
			builder.Append("\n… ").Append(plan.Items.Count - visible).Append(" more items (output truncated)");
		}

		return builder.ToString();
	}

	// ------------------------------------------------------------------ JSON 辅助

	private static int? PositiveInt(JsonNode? node)
	{
		if (node is null || node.GetValueKind() != JsonValueKind.Number)
		{
			return null;
		}

		return node.AsValue().TryGetValue<int>(out var value) && value > 0 ? value : null;
	}

	private static string? Text(JsonNode? node) =>
		node is not null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
}
