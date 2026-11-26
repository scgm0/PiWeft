using System.Text;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Subagents;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.Desktop;

/// 交接简报的结构化输入：只装已经算好的值，不再碰 git 或会话。
internal sealed record HandoffBriefInput(
	string Title,
	string Cwd,
	string Model,
	string Thinking,
	string SessionFile,
	GoalState Goal,
	IReadOnlyList<PlanStep> PlanSteps,
	bool PlanExecutionEnabled,
	IReadOnlyList<TodoItem> Todos,
	string Branch,
	int Ahead,
	int Behind,
	string? GitError,
	IReadOnlyList<GitChange> Uncommitted,
	IReadOnlyList<ChatFileChange> TurnChanges,
	IReadOnlyList<SubagentRun> Subagents,
	IReadOnlyList<string> RecentUserMessages,
	string LastAssistant,
	DateTimeOffset Now);

/// 交接简报：把当前会话的进度压成一页 Markdown，交给另一个人或另一个会话接手。
/// 只写“现在在哪、改了什么、还差什么”，不复述完整对话；每条有长度上限。
internal static class HandoffBrief
{
	/// 一条用户消息最多保留的字数；简报的用途是让人接得上，不是重放对话。
	public const int UserMessageLimit = 280;

	public const int AssistantLimit = 700;

	/// 未提交改动最多列出的行数，其余按数量带过。
	public const int MaxUncommitted = 30;

	public static string Build(HandoffBriefInput input)
	{
		var body = new StringBuilder(2048);
		body.Append("# 交接简报：").Append(SingleLine(input.Title, 120)).Append('\n').Append('\n');
		body.Append("- 时间：").Append(input.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append('\n');
		body.Append("- 目录：`").Append(input.Cwd).Append("`\n");
		body.Append("- 模型：").Append(input.Model);
		if (input.Thinking.Length > 0)
		{
			body.Append(" · 思考 ").Append(input.Thinking);
		}

		body.Append('\n');
		if (input.SessionFile.Length > 0)
		{
			body.Append("- 会话文件：`").Append(input.SessionFile).Append("`\n");
		}

		AppendGoal(body, input.Goal);
		AppendPlan(body, input.PlanSteps, input.PlanExecutionEnabled);
		AppendTodos(body, input.Todos);
		AppendRepository(body, input);
		AppendSubagents(body, input.Subagents);
		AppendConversation(body, input.RecentUserMessages, input.LastAssistant);
		AppendNextSteps(body, input);
		return body.ToString();
	}

	/// 接手建议从当前状态生成：冲突、未提交、分支落后、子代理在跑、计划里第一条未完成。
	/// 没有这些事实时不写空话，只留一句先跑构建与测试。
	private static void AppendNextSteps(StringBuilder body, HandoffBriefInput input)
	{
		var steps = new List<string>(5);
		if (input.Goal.BlockReason is { Length: > 0 } block)
		{
			steps.Add($"先处理目标阻塞：{SingleLine(block, 160)}。");
		}

		if (input.Uncommitted.Any(static change => change.IsConflict))
		{
			steps.Add("先解决合并冲突，再提交。");
		}
		else if (input.Uncommitted.Count > 0)
		{
			steps.Add($"工作区有 {input.Uncommitted.Count} 个未提交文件，先决定提交还是 stash。");
		}

		if (input.Ahead > 0 || input.Behind > 0)
		{
			steps.Add($"分支与上游差 ↑{input.Ahead} ↓{input.Behind}，先同步再继续。");
		}

		var running = input.Subagents.Count(static run => run.IsActive);
		if (running > 0)
		{
			steps.Add($"还有 {running} 个子代理在跑，先看它们的结论。");
		}

		if (input.PlanSteps.FirstOrDefault(static step => !step.Completed) is { } pending)
		{
			steps.Add($"按计划继续第 {pending.Step} 步：{SingleLine(pending.Text, 120)}。");
		}
		else if (input.Todos.FirstOrDefault(static todo => todo.Status != "completed") is { } todo)
		{
			steps.Add($"下一步待办 #{todo.Id}：{SingleLine(todo.Text, 120)}。");
		}

		body.Append("## 接手建议\n");
		body.Append("- 先跑构建与相关测试，确认当前工作区状态。\n");
		foreach (var step in steps.Take(4))
		{
			body.Append("- ").Append(step).Append('\n');
		}
	}

	private static void AppendGoal(StringBuilder body, GoalState goal)
	{
		body.Append('\n').Append("## 目标\n");
		if (goal.Objective.Trim() is not { Length: > 0 } objective)
		{
			body.Append("无。\n");
			return;
		}

		body.Append("> ").Append(objective.Replace("\n", "\n> ", StringComparison.Ordinal)).Append('\n').Append('\n');
		body.Append("- 阶段：").Append(Strings.GoalPhase(goal.Phase)).Append(" · 轮次 ").Append(goal.RoundsStarted)
			.Append('/').Append(goal.MaxRounds).Append('\n');
		if (goal.BlockReason is { Length: > 0 } reason)
		{
			body.Append("- 阻塞原因：").Append(reason).Append('\n');
		}
	}

	private static void AppendPlan(StringBuilder body, IReadOnlyList<PlanStep> steps, bool executing)
	{
		body.Append('\n').Append("## 计划\n");
		if (steps.Count == 0)
		{
			body.Append("无。\n");
			return;
		}

		if (executing)
		{
			body.Append("- 状态：执行中\n");
		}

		foreach (var step in steps)
		{
			body.Append(step.Completed ? "- [x] " : "- [ ] ")
				.Append(step.Step).Append(". ").Append(SingleLine(step.Text, 200)).Append('\n');
		}
	}

	private static void AppendTodos(StringBuilder body, IReadOnlyList<TodoItem> todos)
	{
		body.Append('\n').Append("## 待办\n");
		if (todos.Count == 0)
		{
			body.Append("无。\n");
			return;
		}

		foreach (var todo in todos)
		{
			body.Append("- `").Append(TodoStatus(todo.Status)).Append("` #").Append(todo.Id).Append(' ')
				.Append(SingleLine(todo.Text, 200)).Append('\n');
		}
	}

	private static void AppendRepository(StringBuilder body, HandoffBriefInput input)
	{
		body.Append('\n').Append("## 工作区\n");
		if (input.GitError is { Length: > 0 } error)
		{
			body.Append("- Git：").Append(SingleLine(error, 160)).Append('\n');
		}
		else
		{
			body.Append("- 分支：").Append(input.Branch.Length > 0 ? input.Branch : "HEAD");
			if (input.Ahead > 0 || input.Behind > 0)
			{
				body.Append("（↑").Append(input.Ahead).Append(" ↓").Append(input.Behind).Append('）');
			}

			body.Append('\n');
			body.Append("- 未提交：").Append(input.Uncommitted.Count).Append(" 个文件\n");
			var listed = Math.Min(input.Uncommitted.Count, MaxUncommitted);
			for (var index = 0; index < listed; index++)
			{
				var change = input.Uncommitted[index];
				body.Append("  - `").Append(change.Path).Append("`（").Append(change.Label).Append("）\n");
			}

			if (input.Uncommitted.Count > listed)
			{
				body.Append("  - 其余 ").Append(input.Uncommitted.Count - listed).Append(" 个未列出\n");
			}
		}

		if (input.TurnChanges.Count == 0)
		{
			return;
		}

		body.Append("- 最近一轮工具改动：\n");
		foreach (var change in input.TurnChanges)
		{
			body.Append("  - `").Append(change.Path).Append("` ×").Append(change.Count)
				.Append(" +").Append(change.Added).Append(" −").Append(change.Removed).Append('\n');
		}
	}

	private static void AppendSubagents(StringBuilder body, IReadOnlyList<SubagentRun> runs)
	{
		if (runs.Count == 0)
		{
			return;
		}

		body.Append('\n').Append("## 子代理\n");
		foreach (var run in runs)
		{
			body.Append("- ").Append(run.Name).Append(" · ").Append(Strings.SubagentStateText(run.State));
			if (run.Label.Length > 0)
			{
				body.Append(" · ").Append(SingleLine(run.Label, 120));
			}

			if (run.ChangedFiles.Count > 0)
			{
				body.Append(" · 改过 ").Append(run.ChangedFiles.Count).Append(" 个文件");
			}

			body.Append('\n');
		}
	}

	private static void AppendConversation(
		StringBuilder body,
		IReadOnlyList<string> userMessages,
		string assistant)
	{
		if (userMessages.Count == 0 && assistant.Length == 0)
		{
			return;
		}

		body.Append('\n').Append("## 最近的对话\n");
		foreach (var message in userMessages)
		{
			body.Append("- 用户：").Append(SingleLine(message, UserMessageLimit)).Append('\n');
		}

		if (assistant.Length > 0)
		{
			body.Append("- 助手：").Append(Tail(assistant, AssistantLimit)).Append('\n');
		}
	}

	private static string TodoStatus(string status) => status switch
	{
		"completed" => "已完成",
		"in_progress" => "进行中",
		"pending" => "待办",
		_ => status,
	};

	/// 压成一行：简报里的每条都要能在一行内读完，换行会把列表拆散。
	private static string SingleLine(string text, int limit)
	{
		var flattened = text.Replace("\r\n", " ", StringComparison.Ordinal)
			.Replace('\n', ' ')
			.Replace('\r', ' ')
			.Trim();
		return flattened.Length <= limit ? flattened : flattened[..limit] + "…";
	}

	/// 助手回复取尾部：结论通常在最后一段。
	private static string Tail(string text, int limit)
	{
		var flattened = text.Replace("\r\n", " ", StringComparison.Ordinal)
			.Replace('\n', ' ')
			.Replace('\r', ' ')
			.Trim();
		return flattened.Length <= limit ? flattened : "…" + flattened[^limit..];
	}
}
