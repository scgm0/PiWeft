using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Subagents;

/// <summary>共享任务板上的一个任务；不可变快照，每次修改 revision 加一。</summary>
public sealed record TeamTask
{
	/// <summary>任务 id，<c>task-1</c> 起。</summary>
	public required string Id { get; init; }

	/// <summary>修改版本；每次 CAS 成功加一。</summary>
	public int Revision { get; init; }

	/// <summary>一行标题。</summary>
	public required string Subject { get; init; }

	/// <summary>任务详情，claim 前看这里。</summary>
	public string Description { get; init; } = string.Empty;

	/// <summary>pending / in_progress / completed / deleted。</summary>
	public string Status { get; init; } = TeamTaskStatus.Pending;

	/// <summary>必须先完成的任务 id。</summary>
	public IReadOnlyList<string> BlockedBy { get; init; } = [];

	/// <summary>建议的写范围，只提示重叠，不锁文件。</summary>
	public IReadOnlyList<string> WriteScopes { get; init; } = [];

	/// <summary>当前 owner 的名字；未领取为 null。</summary>
	public string? Owner { get; init; }

	/// <summary>创建时间，Unix 毫秒。</summary>
	public long CreatedAt { get; init; }

	/// <summary>最近修改时间，Unix 毫秒。</summary>
	public long UpdatedAt { get; init; }
}

/// <summary>任务状态值。</summary>
public static class TeamTaskStatus
{
	public const string Pending = "pending";
	public const string InProgress = "in_progress";
	public const string Completed = "completed";
	public const string Deleted = "deleted";
}

/// <summary>一次任务操作的结果：给模型的一句话、结构化视图与错误码。</summary>
public sealed record TeamOutcome(bool Ok, string Text, JsonObject? Details = null);

/// <summary>
/// 父会话的共享任务板：跨会话的工单、owner、依赖与 CAS。
/// 任务记录 append 到父会话文件，重启后按最后一条快照恢复；
/// 只在 <c>settings.subagents.team</c> 打开时创建。
/// </summary>
public sealed class TeamBoard
{
	/// <summary>任务记录的 custom entry 类型。</summary>
	public const string EntryType = "piweft-team-task";

	/// <summary>一个团队最多保留多少个未删除任务。</summary>
	public const int MaxTasks = 256;

	/// <summary>子代理系统提示里的团队说明。</summary>
	public const string Policy =
		"Agent Teams is available. Shared task board: list, get, claim with the current expected_revision, " +
		"do the work, then complete. The lead can reassign tasks; other members claim their own. " +
		"Task readiness never starts anyone — send a message when another agent should act. " +
		"Write scopes are advisory, not locks.";

	private readonly CodingAgentSession _owner;
	private readonly Func<string, bool> _isMember;
	private readonly Lock _gate = new();
	private readonly List<TeamTask> _tasks = [];
	private int _next = 1;

	/// <summary>任务板变化；界面据此刷新，任务变更本身不唤醒任何成员。</summary>
	public event Action? Changed;

	/// <summary>任务板只在 Team 模式打开时创建。</summary>
	public TeamBoard(CodingAgentSession owner, Func<string, bool> isMember)
	{
		_owner = owner;
		_isMember = isMember;
		Restore();
	}

	/// <summary>所有任务（含已删除墓碑），按创建顺序。</summary>
	public IReadOnlyList<TeamTask> Tasks
	{
		get
		{
			lock (_gate)
			{
				return [.. _tasks];
			}
		}
	}

	/// <summary>建一个未领取、pending 的任务。</summary>
	public TeamOutcome Create(
		string caller,
		string subject,
		string description,
		IReadOnlyList<string>? blockedBy,
		IReadOnlyList<string>? writeScopes)
	{
		var title = (subject ?? string.Empty).Trim();
		if (title.Length == 0)
		{
			return Fail("subject is required", "TEAM_INVALID_ARGUMENT");
		}

		if (title.Length > 200)
		{
			return Fail("subject must be at most 200 characters", "TEAM_INVALID_ARGUMENT");
		}

		var body = (description ?? string.Empty).Trim();
		if (body.Length == 0)
		{
			return Fail("description is required", "TEAM_INVALID_ARGUMENT");
		}

		if (body.Length > 16_384)
		{
			return Fail("description must be at most 16384 characters", "TEAM_INVALID_ARGUMENT");
		}

		lock (_gate)
		{
			if (_tasks.Count(static task => task.Status != TeamTaskStatus.Deleted) >= MaxTasks)
			{
				return Fail($"team task limit {MaxTasks} reached", "TEAM_TASK_LIMIT");
			}

			var dependencies = ValidateDependencies(blockedBy ?? [], self: null);
			if (dependencies.Error is { Length: > 0 } dependencyError)
			{
				return Fail(dependencyError, "TEAM_INVALID_ARGUMENT");
			}

			var scopes = NormalizeScopes(writeScopes ?? []);
			if (scopes.Error is { Length: > 0 } scopeError)
			{
				return Fail(scopeError, "TEAM_INVALID_ARGUMENT");
			}

			var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			var task = new TeamTask
			{
				Id = $"task-{_next++}",
				Revision = 1,
				Subject = title,
				Description = body,
				BlockedBy = dependencies.Ids,
				WriteScopes = scopes.Ids,
				CreatedAt = now,
				UpdatedAt = now,
			};
			_tasks.Add(task);
			Persist(task);
			return Success(task);
		}
	}

	/// <summary>列出未删除任务；status / owner / unowned 过滤。</summary>
	public TeamOutcome List(string? status, string? owner, bool unowned)
	{
		if (unowned && owner is { Length: > 0 })
		{
			return Fail("owner and unowned cannot be combined", "TEAM_INVALID_ARGUMENT");
		}

		var normalizedStatus = (status ?? string.Empty).Trim();
		if (normalizedStatus.Length > 0 &&
			normalizedStatus is not (TeamTaskStatus.Pending or TeamTaskStatus.InProgress or TeamTaskStatus.Completed))
		{
			return Fail("status must be pending, in_progress or completed", "TEAM_INVALID_ARGUMENT");
		}

		var normalizedOwner = (owner ?? string.Empty).Trim();
		lock (_gate)
		{
			var tasks = _tasks
				.Where(static task => task.Status != TeamTaskStatus.Deleted)
				.Where(task => normalizedStatus.Length == 0 || task.Status == normalizedStatus)
				.Where(task => !unowned || task.Owner is null)
				.Where(task => normalizedOwner.Length == 0 ||
					string.Equals(task.Owner, normalizedOwner, StringComparison.Ordinal))
				.ToArray();
			var text = new StringBuilder(256);
			var completed = tasks.Count(static task => task.Status == TeamTaskStatus.Completed);
			var active = tasks.Count(static task => task.Status == TeamTaskStatus.InProgress);
			text.Append("team tasks: ").Append(tasks.Length)
				.Append(" (completed ").Append(completed)
				.Append(", in progress ").Append(active)
				.Append(", pending ").Append(tasks.Length - completed - active)
				.Append(')');
			foreach (var task in tasks)
			{
				text.Append('\n').Append(FormatLine(task));
			}

			var views = new JsonArray([.. tasks.Select(task => (JsonNode)View(task, ready: ReadyLocked(task)))]);
			return new TeamOutcome(true, text.ToString(), new JsonObject { ["tasks"] = views });
		}
	}

	/// <summary>按 id 取任务，含已删除墓碑。</summary>
	public TeamOutcome Get(string id)
	{
		lock (_gate)
		{
			var task = FindLocked(id);
			return task is null
				? Fail($"team task \"{id}\" not found", "TEAM_TASK_NOT_FOUND")
				: new TeamOutcome(true, FormatLine(task), new JsonObject { ["task"] = View(task, ready: ReadyLocked(task)) });
		}
	}

	/// <summary>CAS 修改：claim / release / complete / reopen / edit / set_dependencies / reassign / delete。</summary>
	public TeamOutcome Update(
		string caller,
		string id,
		int expectedRevision,
		string action,
		string? subject = null,
		string? description = null,
		IReadOnlyList<string>? blockedBy = null,
		IReadOnlyList<string>? writeScopes = null,
		string? owner = null)
	{
		var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
		lock (_gate)
		{
			var task = FindLocked(id);
			if (task is null)
			{
				return Fail($"team task \"{id}\" not found", "TEAM_TASK_NOT_FOUND");
			}

			if (task.Revision != expectedRevision)
			{
				return Fail(
					$"stale team task \"{id}\" revision {expectedRevision}; current revision is {task.Revision}",
					"TEAM_TASK_STALE_REVISION");
			}

			var isLead = string.Equals(caller, "lead", StringComparison.Ordinal);
			var isOwner = string.Equals(task.Owner, caller, StringComparison.Ordinal);
			var authorized = isLead || isOwner;
			TeamTask next;
			switch (verb)
			{
				case "claim":
				{
					if (task.Status != TeamTaskStatus.Pending)
					{
						return Fail($"task {task.Id} is {task.Status}, not pending", "TEAM_TASK_BLOCKED");
					}

					if (!Ready(task))
					{
						return Fail($"task {task.Id} has unfinished dependencies", "TEAM_TASK_BLOCKED");
					}

					if (task.Owner is { Length: > 0 })
					{
						return Fail($"task {task.Id} is already owned by {task.Owner}", "TEAM_TASK_BLOCKED");
					}

					next = task with { Status = TeamTaskStatus.InProgress, Owner = caller };
					break;
				}

				case "release":
				{
					if (!authorized)
					{
						return Fail($"only the owner or lead can release {task.Id}", "TEAM_UNAUTHORIZED");
					}

					next = task with { Status = TeamTaskStatus.Pending, Owner = null };
					break;
				}

				case "complete":
				{
					if (!authorized)
					{
						return Fail($"only the owner or lead can complete {task.Id}", "TEAM_UNAUTHORIZED");
					}

					next = task with { Status = TeamTaskStatus.Completed };
					break;
				}

				case "reopen":
				{
					if (!authorized)
					{
						return Fail($"only the owner or lead can reopen {task.Id}", "TEAM_UNAUTHORIZED");
					}

					next = task with
					{
						Status = TeamTaskStatus.Pending,
						Owner = null,
					};
					break;
				}

				case "edit":
				{
					if (!authorized)
					{
						return Fail($"only the owner or lead can edit {task.Id}", "TEAM_UNAUTHORIZED");
					}

					if (subject is null && description is null && writeScopes is null)
					{
						return Fail("edit needs subject, description or write_scopes", "TEAM_INVALID_ARGUMENT");
					}

					var title = subject is null ? task.Subject : subject.Trim();
					if (title.Length is 0 or > 200)
					{
						return Fail("subject must be 1-200 characters", "TEAM_INVALID_ARGUMENT");
					}

					var body = description is null ? task.Description : description.Trim();
					if (body.Length > 16_384)
					{
						return Fail("description must be at most 16384 characters", "TEAM_INVALID_ARGUMENT");
					}

					(string? Error, IReadOnlyList<string> Ids)? scopes =
						writeScopes is null ? null : NormalizeScopes(writeScopes);
					if (scopes?.Error is { Length: > 0 } scopeError)
					{
						return Fail(scopeError, "TEAM_INVALID_ARGUMENT");
					}

					next = task with
					{
						Subject = title,
						Description = body,
						WriteScopes = scopes is null ? task.WriteScopes : scopes.Value.Ids,
					};
					break;
				}

				case "set_dependencies":
				{
					if (!authorized)
					{
						return Fail($"only the owner or lead can set dependencies on {task.Id}", "TEAM_UNAUTHORIZED");
					}

					if (blockedBy is null)
					{
						return Fail("set_dependencies requires blocked_by", "TEAM_INVALID_ARGUMENT");
					}

					var dependencies = ValidateDependencies(blockedBy, self: task.Id);
					if (dependencies.Error is { Length: > 0 } dependencyError)
					{
						return Fail(dependencyError, "TEAM_INVALID_ARGUMENT");
					}

					next = task with { BlockedBy = dependencies.Ids };
					break;
				}

				case "reassign":
				{
					if (!isLead)
					{
						return Fail("only the lead can reassign tasks", "TEAM_UNAUTHORIZED");
					}

					var target = (owner ?? string.Empty).Trim();
					if (target.Length > 0 && !_isMember(target))
					{
						return Fail($"unknown team member \"{target}\"", "TEAM_INVALID_ARGUMENT");
					}

					next = target.Length > 0
						? task with { Owner = target, Status = TeamTaskStatus.InProgress }
						: task with { Owner = null, Status = TeamTaskStatus.Pending };
					break;
				}

				case "delete":
				{
					if (!authorized)
					{
						return Fail($"only the owner or lead can delete {task.Id}", "TEAM_UNAUTHORIZED");
					}

					next = task with { Status = TeamTaskStatus.Deleted };
					break;
				}

				default:
					return Fail(
						"action must be claim, release, complete, reopen, edit, set_dependencies, reassign or delete",
						"TEAM_INVALID_ARGUMENT");
			}

			next = next with
			{
				Revision = task.Revision + 1,
				UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			var index = _tasks.FindIndex(candidate => string.Equals(candidate.Id, task.Id, StringComparison.Ordinal));
			_tasks[index] = next;
			Persist(next);
			return Success(next);
		}
	}

	/// <summary>任务是否 pending 且依赖全部完成。</summary>
	public bool Ready(TeamTask task)
	{
		lock (_gate)
		{
			return ReadyLocked(task);
		}
	}

	private bool ReadyLocked(TeamTask task) =>
		task.Status == TeamTaskStatus.Pending &&
		task.BlockedBy.All(id => FindLocked(id) is { Status: TeamTaskStatus.Completed });

	private TeamTask? FindLocked(string id)
	{
		foreach (var task in _tasks)
		{
			if (string.Equals(task.Id, id, StringComparison.Ordinal))
			{
				return task;
			}
		}

		return null;
	}

	private (string? Error, IReadOnlyList<string> Ids) ValidateDependencies(
		IReadOnlyList<string> blockedBy,
		string? self)
	{
		var ids = new List<string>(blockedBy.Count);
		foreach (var raw in blockedBy)
		{
			var id = (raw ?? string.Empty).Trim();
			if (id.Length == 0)
			{
				continue;
			}

			if (string.Equals(id, self, StringComparison.Ordinal))
			{
				return ("a task cannot depend on itself", []);
			}

			if (FindLocked(id) is null)
			{
				return ($"dependency \"{id}\" not found", []);
			}

			if (!ids.Contains(id, StringComparer.Ordinal))
			{
				ids.Add(id);
			}
		}

		if (self is not null)
		{
			var visited = new HashSet<string>(StringComparer.Ordinal);
			foreach (var id in ids)
			{
				if (DependsOn(id, self, visited))
				{
					return ("blocked_by would create a dependency cycle", []);
				}
			}
		}

		return (null, ids);
	}

	private bool DependsOn(string start, string target, HashSet<string> visited)
	{
		if (string.Equals(start, target, StringComparison.Ordinal))
		{
			return true;
		}

		if (!visited.Add(start) || FindLocked(start) is not { } task)
		{
			return false;
		}

		foreach (var dependency in task.BlockedBy)
		{
			if (DependsOn(dependency, target, visited))
			{
				return true;
			}
		}

		return false;
	}

	private static (string? Error, IReadOnlyList<string> Ids) NormalizeScopes(IReadOnlyList<string> writeScopes)
	{
		var ids = new List<string>(writeScopes.Count);
		foreach (var raw in writeScopes)
		{
			var scope = (raw ?? string.Empty).Trim().Replace('\\', '/');
			if (scope.Length == 0)
			{
				continue;
			}

			if (Path.IsPathRooted(scope) ||
				scope.Split('/').Any(static part => string.Equals(part, "..", StringComparison.Ordinal)))
			{
				return ($"write scope \"{scope}\" must be relative and stay inside the workspace", []);
			}

			if (!ids.Contains(scope, StringComparer.Ordinal))
			{
				ids.Add(scope);
			}

			if (ids.Count >= 32)
			{
				break;
			}
		}

		return (null, ids);
	}

	private TeamOutcome Success(TeamTask task) => new(
		true,
		$"{task.Id} r{task.Revision} {task.Status} · {task.Subject}",
		new JsonObject { ["task"] = View(task, ready: ReadyLocked(task)) });

	private static TeamOutcome Fail(string message, string code) =>
		new(false, message, new JsonObject { ["code"] = code });

	private static JsonObject View(TeamTask task, bool ready) => new()
	{
		["id"] = task.Id,
		["revision"] = task.Revision,
		["subject"] = task.Subject,
		["description"] = task.Description,
		["status"] = task.Status,
		["owner"] = task.Owner,
		["blocked_by"] = new JsonArray([.. task.BlockedBy.Select(static id => (JsonNode)id)]),
		["write_scopes"] = new JsonArray([.. task.WriteScopes.Select(static scope => (JsonNode)scope)]),
		["ready"] = ready,
	};

	private static string FormatLine(TeamTask task)
	{
		var owner = task.Owner is { Length: > 0 } ? $" owner={task.Owner}" : " unowned";
		var dependencies = task.BlockedBy.Count > 0
			? $" blocked_by={string.Join(',', task.BlockedBy)}"
			: string.Empty;
		return $"- {task.Id} [{task.Status}]{owner} · {task.Subject}{dependencies}";
	}

	private void Persist(TeamTask task)
	{
		try
		{
			_owner.Session.AppendCustom(EntryType, new JsonObject
			{
				["id"] = task.Id,
				["revision"] = task.Revision,
				["subject"] = task.Subject,
				["description"] = task.Description,
				["status"] = task.Status,
				["owner"] = task.Owner,
				["blockedBy"] = new JsonArray([.. task.BlockedBy.Select(static id => (JsonNode)id)]),
				["writeScopes"] = new JsonArray([.. task.WriteScopes.Select(static scope => (JsonNode)scope)]),
				["createdAt"] = task.CreatedAt,
				["updatedAt"] = task.UpdatedAt,
			});
			Changed?.Invoke();
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
		{
			// 落盘失败不丢内存里的任务；下一次修改会继续尝试。
		}
	}

	private void Restore()
	{
		foreach (var entry in _owner.Session.Entries)
		{
			if (entry is not CustomEntry { CustomType: EntryType, Data: JsonObject data })
			{
				continue;
			}

			var id = Text(data["id"]);
			if (id is not { Length: > 0 })
			{
				continue;
			}

			var task = new TeamTask
			{
				Id = id,
				Revision = Number(data["revision"]),
				Subject = Text(data["subject"]) ?? string.Empty,
				Description = Text(data["description"]) ?? string.Empty,
				Status = Text(data["status"]) ?? TeamTaskStatus.Pending,
				BlockedBy = Strings(data["blockedBy"]),
				WriteScopes = Strings(data["writeScopes"]),
				Owner = Text(data["owner"]),
				CreatedAt = Number(data["createdAt"]),
				UpdatedAt = Number(data["updatedAt"]),
			};
			var index = _tasks.FindIndex(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
			if (index >= 0)
			{
				_tasks[index] = task;
			}
			else
			{
				_tasks.Add(task);
			}

			if (id.StartsWith("task-", StringComparison.Ordinal) &&
				int.TryParse(id.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
			{
				_next = Math.Max(_next, number + 1);
			}
		}
	}

	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static int Number(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

	private static IReadOnlyList<string> Strings(JsonNode? node)
	{
		if (node is not JsonArray array || array.Count == 0)
		{
			return [];
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
