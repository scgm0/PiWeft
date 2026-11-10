using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Compatibility;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent;

/// 计划模式与目标模式的会话侧编排。两个模式都是「回合结束后做点事」：
/// 计划草案要弹确认选单、执行态要勾进度、目标态要自动续轮。
/// 事件订阅在构造时挂上，模式状态只影响系统提示与后续行为，不重建会话。
public sealed partial class CodingAgentSession
{
	public const string PlanEntryType = PiDeckProtocol.PlanEntryType;

	private readonly List<PlanStep> _planSteps = [];
	private bool _planExecution;
	/// 目标状态在 UI 线程（暂停/恢复/清除）与 agent 线程（回合结束推进轮次）两边写：
	/// 引用字段用 volatile 保证界面读到的不是过期快照，读写都进 <see cref="_goalGate"/>。
	private volatile GoalState _goal = GoalState.Initial;
	private readonly Lock _goalGate = new();
	/// 上一次落盘的目标全文：目标不变时条目省略 objective，避免 32 轮堆 32 份原文。
	/// 只在 <see cref="_goalGate"/> 内读写。
	private string _persistedGoalObjective = string.Empty;
	private volatile bool _pendingModeTurn;
	private volatile bool _drainingModeTurn;

	public IReadOnlyList<PlanStep> ActivePlanSteps => _planSteps;

	public bool PlanExecutionEnabled => _planExecution;

	public GoalState Goal => _goal;

	/// 计划执行态：写权限恢复，进度按 `[DONE:n]` 勾。
	private void StartPlanExecution()
	{
		// SetPlanMode(false) 会清空草案步骤（退出计划模式 = 丢弃计划），
		// 执行态要把同一份步骤留下来勾进度。
		var steps = _planSteps.ToList();
		SetPlanMode(false);
		_planSteps.Clear();
		_planSteps.AddRange(steps);
		_planExecution = true;
		_pendingModeTurn = true;
		PublishPlanWidget();
		PersistPlanState();

		// 两条自定义消息：步骤清单（可见）+ 执行指令。第二条触发新一轮。
		Session.AppendCustomMessage(
			PlanSteps.WidgetKey,
			MessageContent.FromText(PlanSteps.StepsMessage(_planSteps)),
			display: true);
		Session.AppendCustomMessage(
			PiDeckProtocol.PlanExecuteMessageType,
			MessageContent.FromText(PlanSteps.ExecuteMessage(_planSteps)),
			display: true);
	}

	private void CompletePlan()
	{
		if (_planSteps.Count > 0)
		{
			Session.AppendCustomMessage(
				PiDeckProtocol.PlanCompleteMessageType,
				MessageContent.FromText(PlanSteps.CompleteMessage(_planSteps)),
				display: true);
		}

		_planExecution = false;
		_planSteps.Clear();
		PublishPlanWidget();
		PersistPlanState();
	}

	public void SetGoal(string objective, bool resetRounds = true, int? maxRounds = null)
	{
		var text = objective.Trim();
		if (text.Length == 0)
		{
			return;
		}

		lock (_goalGate)
		{
			_goal = _goal with
			{
				Enabled = true,
				Phase = GoalState.Active,
				Objective = text,
				RoundsStarted = resetRounds ? 0 : _goal.RoundsStarted,
				MaxRounds = maxRounds is > 0 ? maxRounds.Value : _goal.MaxRounds,
				BlockReason = null,
			};
			PersistGoalState();
		}

		PublishGoalWidget();
		RefreshSystemPrompt();
	}

	public void PauseGoal()
	{
		lock (_goalGate)
		{
			if (!_goal.Enabled || _goal.Phase == GoalState.Paused)
			{
				return;
			}

			_goal = _goal with { Phase = GoalState.Paused };
			PersistGoalState();
		}

		PublishGoalWidget();
		RefreshSystemPrompt();
	}

	public void ClearGoal()
	{
		lock (_goalGate)
		{
			_goal = GoalState.Initial;
			// 清除必须落下空目标：省略 objective 会让重启时的回溯穿透到旧目标。
			PersistGoalState(force: true);
		}

		PublishGoalWidget();
		RefreshSystemPrompt();
	}

	/// 恢复目标并补一轮续跑。只把 phase 标回 active 不会让空闲的代理动起来；
	/// 运行中则只排队，由本轮收尾后的续轮接上。空目标返回 false。
	public async Task<bool> ResumeGoalAsync(CancellationToken cancellationToken = default)
	{
		if (_goal.Objective.Length == 0)
		{
			return false;
		}

		SetGoal(_goal.Objective, resetRounds: false);
		if (IsRunning)
		{
			// 这一轮结束时 goal 还是 active，agent_end 会把它算作一轮并排下一轮。
			return true;
		}

		lock (_goalGate)
		{
			QueueGoalContinuation();
		}
		await RunPendingModeTurnAsync(cancellationToken).ConfigureAwait(false);
		return true;
	}

	/// 从会话分支恢复模式状态。计划只在执行态续接（草案恢复回来永远勾不动）；
	/// 目标只恢复为暂停，重启后自动续跑等于绕过用户。
	private void RestoreModeState()
	{
		JsonNode? planData = null;
		var goalEntries = new List<JsonNode?>();
		foreach (var entry in Session.GetBranch())
		{
			if (entry is CustomEntry custom)
			{
				if (custom.CustomType == PlanEntryType)
				{
					planData = custom.Data;
				}
				else if (custom.CustomType == GoalMode.EntryType)
				{
					goalEntries.Add(custom.Data);
				}
			}
		}

		if (planData is JsonObject plan && plan["executing"]?.GetValue<bool>() == true &&
			plan["todos"] is JsonArray todos)
		{
			_planExecution = true;
			_planSteps.Clear();
			foreach (var node in todos)
			{
				if (node is JsonObject item &&
					item["step"] is JsonValue stepValue && stepValue.TryGetValue<int>(out var step) &&
					item["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var text))
				{
					_planSteps.Add(new PlanStep(
						step,
						text,
						item["completed"]?.GetValue<bool>() == true));
				}
			}
		}

		_goal = GoalMode.Decode(goalEntries);
		// 恢复出来的目标已经有带 objective 的记录，下一次落盘不必重复写。
		_persistedGoalObjective = _goal.Objective;
	}

	/// 计划状态单独落盘：PiDeck 的 plan 扩展在 session_start 读这条恢复执行态与步骤。
	private void PersistPlanState()
	{
		var todos = new JsonArray();
		foreach (var step in _planSteps)
		{
			todos.Add((JsonNode)new JsonObject
			{
				["step"] = step.Step,
				["text"] = step.Text,
				["completed"] = step.Completed,
			});
		}

		Session.AppendCustom(PlanEntryType, new JsonObject
		{
			["enabled"] = PlanModeEnabled,
			["todos"] = todos,
			["executing"] = _planExecution,
		});
	}

	/// 目标状态单独落盘：字段名与 PiDeck 的 goal 扩展一致。
	/// 目标全文只在变化时写，其余轮次省略 objective 字段。调用方必须持有 <see cref="_goalGate"/>。
	private void PersistGoalState(bool force = false)
	{
		var includeObjective = force || _goal.Objective != _persistedGoalObjective;
		_persistedGoalObjective = _goal.Objective;
		Session.AppendCustom(GoalMode.EntryType, GoalMode.Encode(_goal, includeObjective));
	}

	private void PublishPlanWidget()
	{
		if (UiHost is not { HasUi: true } host)
		{
			return;
		}

		var lines = PlanSteps.WidgetLines(_planSteps, _planExecution);
		host.SetWidget(new UiWidgetRequest(
			PlanSteps.WidgetKey,
			lines.Count > 0 ? lines : null,
			null));
	}

	private void PublishGoalWidget()
	{
		if (UiHost is not { HasUi: true } host)
		{
			return;
		}

		var lines = GoalMode.WidgetLines(_goal);
		host.SetWidget(new UiWidgetRequest(
			GoalMode.WidgetKey,
			lines.Count > 0 ? lines : null,
			null));
	}

	/// 模式章节：计划执行态给剩余步骤，目标态给规则与目标全文。
	private string? PlanExecutionSection()
	{
		if (!_planExecution || _planSteps.Count == 0)
		{
			return null;
		}

		var remaining = _planSteps.Where(static step => !step.Completed).ToList();
		var builder = new StringBuilder("[EXECUTING PLAN]\n");
		builder.Append("Execute remaining steps in order. After completing a step, include [DONE:n] in your response.");
		if (remaining.Count > 0)
		{
			builder.Append("\n\n").Append(string.Join(
				'\n',
				remaining.Select(static step => $"{step.Step}. {step.Text}")));
		}

		return builder.ToString();
	}

	private string? GoalSection() => _goal.Running ? GoalMode.Rules(_goal.Objective) : null;

	private ValueTask ModeLifecycleAsync(AgentEvent @event)
	{
		switch (@event)
		{
			case TurnEndEvent turn:
				HandleModeTurnEnd(turn);
				return ValueTask.CompletedTask;
			case AgentEndEvent end:
				return HandleModeAgentEndAsync(end);
			default:
				return ValueTask.CompletedTask;
		}
	}

	/// 回合结束时只勾计划进度。目标轮次必须等整轮结束（agent_end）再算：
	/// 一次 run 里可能有多条助手消息（工具调用后还有续写），按 turn_end 计数会多算轮次，
	/// 还会把续轮指令插到工具结果和下一步之间。
	private void HandleModeTurnEnd(TurnEndEvent turn)
	{
		if (!_planExecution || _planSteps.Count == 0 || turn.Message is not AssistantMessage assistant)
		{
			return;
		}

		var text = MessageText.Of(assistant);
		if (PlanSteps.MarkCompleted(text, _planSteps) > 0)
		{
			PublishPlanWidget();
			PersistPlanState();
		}
	}

	private async ValueTask HandleModeAgentEndAsync(AgentEndEvent end)
	{
		// 目标模式：整轮结束后判断终态，否则推进轮次并安排下一轮。
		if (_goal.Running && !end.WillRetry)
		{
			AdvanceGoal(end);
		}

		// 执行态全部完成：写完成消息并清空进度。
		if (_planExecution && _planSteps.Count > 0 && _planSteps.All(static step => step.Completed))
		{
			CompletePlan();
		}

		if (!PlanModeEnabled || _planExecution || _planSteps.Count > 0 || UiHost is not { HasUi: true } host)
		{
			return;
		}

		var lastAssistant = MessageText.LastAssistant(end.Messages);
		if (lastAssistant is null || lastAssistant.StopReason is not StopReason.Stop)
		{
			return;
		}

		var steps = PlanSteps.Extract(MessageText.Of(lastAssistant));
		if (steps.Count == 0)
		{
			return;
		}

		_planSteps.Clear();
		_planSteps.AddRange(steps);
		PublishPlanWidget();
		PersistPlanState();

		while (true)
		{
			string? choice;
			try
			{
				// 用本轮令牌：用户按停止时选单要能立刻收掉，否则整次运行卡在等界面。
				choice = await host.SelectAsync(
					new UiSelectRequest(
						PlanSteps.NextTitle(_planSteps),
						[
							$"{PlanSteps.ExecuteOption}|恢复写权限，按步骤改代码并勾进度",
							$"{PlanSteps.ContinueOption}|结束本轮，保持只读；再发消息后 AI 才继续",
						],
						null),
					CurrentRunToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			if (choice is not null && choice.StartsWith(PlanSteps.ExecuteOption, StringComparison.Ordinal))
			{
				StartPlanExecution();
				return;
			}

			if (choice is not null && choice.StartsWith(PlanSteps.ContinueOption, StringComparison.Ordinal))
			{
				// 结束本轮、保持只读；用户再发消息才继续。
				return;
			}

			// 关闭选单 = 退出计划模式，不默认执行。
			SetPlanMode(false);
			return;
		}
	}

	private void AdvanceGoal(AgentEndEvent end)
	{
		var lastAssistant = MessageText.LastAssistant(end.Messages);
		if (lastAssistant is null)
		{
			// 没有助手回复就不续轮，避免空转把轮次烧光（PiDeck 同义）。
			lock (_goalGate)
			{
				PersistGoalState();
			}
			PublishGoalWidget();
			return;
		}

		// 中止/出错不算一轮：不推进轮次、不自动续轮，否则「点了停止」会立刻再跑起来。
		if (lastAssistant.StopReason is StopReason.Aborted or StopReason.Error)
		{
			lock (_goalGate)
			{
				PersistGoalState();
			}
			PublishGoalWidget();
			return;
		}

		(string Text, string Type)? notify = null;
		var refreshPrompt = false;
		lock (_goalGate)
		{
			if (!_goal.Running)
			{
				// 这一轮进行中用户暂停或清除了目标：不再推进轮次，也不再处理终态标记。
				// 暂停/清除自己已经落盘，这里不重复写条目。
			}
			else if (GoalMode.DetectTerminal(MessageText.Of(lastAssistant)) is { } terminal)
			{
				_goal = terminal.Phase == GoalState.Complete
					? _goal with { Phase = GoalState.Complete, Enabled = false, BlockReason = null }
					: _goal with { Phase = GoalState.Blocked, BlockReason = terminal.Reason };
				PersistGoalState();
				refreshPrompt = true;
				notify = terminal.Phase == GoalState.Complete
					? ("目标已完成。", "info")
					: ($"目标已阻塞：{terminal.Reason}", "warning");
			}
			else
			{
				_goal = _goal with { RoundsStarted = _goal.RoundsStarted + 1 };
				if (_goal.RoundsStarted >= _goal.MaxRounds)
				{
					_goal = _goal with
					{
						Phase = GoalState.Blocked,
						BlockReason = $"reached max rounds ({_goal.MaxRounds})",
					};
					PersistGoalState();
					refreshPrompt = true;
					notify = ($"目标已阻塞：达到轮次上限（{_goal.MaxRounds}）。", "warning");
				}
				else
				{
					PersistGoalState();
					// 续轮与状态更新在同一把锁里决定：暂停不会在两者之间插进来。
					QueueGoalContinuation();
				}
			}
		}

		PublishGoalWidget();
		if (notify is { } message)
		{
			UiHost?.Notify(new UiNotifyRequest(message.Text, message.Type));
		}

		if (refreshPrompt)
		{
			RefreshSystemPrompt();
		}
	}

	/// 续轮指令写进分支的隐藏消息：本轮已收尾，下一轮由 <see cref="RunPendingModeTurnAsync"/> 起。
	/// 调用方必须持有 <see cref="_goalGate"/>。
	private void QueueGoalContinuation()
	{
		Session.AppendCustomMessage(
			GoalMode.ContinueRole,
			MessageContent.FromText(GoalMode.ContinuationText(_goal)),
			display: false);
		_pendingModeTurn = true;
	}

	/// 当前运行的取消令牌：界面等待（计划确认选单）要用它，停止按钮才能打断。
	private CancellationToken CurrentRunToken
	{
		get
		{
			lock (_abortGate)
			{
				return _runAbort?.Token ?? CancellationToken.None;
			}
		}
	}

	/// 计划执行要一轮新的模型请求：本轮已经完全收尾后才能开始。
	private async Task RunPendingModeTurnAsync(CancellationToken cancellationToken)
	{
		if (!_pendingModeTurn || _drainingModeTurn)
		{
			return;
		}

		_drainingModeTurn = true;
		try
		{
			// 一轮接一轮：嵌套的 ContinueAsync 走到这里会直接返回（防重入），
			// 由最外层循环继续把新排上的续轮跑完，否则第三轮起会停住。
			while (_pendingModeTurn)
			{
				_pendingModeTurn = false;
				// 刚追加的执行指令/续轮指令还在会话文件里，代理的实时消息列表没有它们：
				// 先按分支投影同步，否则 ContinueAsync 会以「最后一条是 assistant」拒绝。
				_agent.SetMessages(Session.BuildProjectedMessages());
				await ContinueAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			_drainingModeTurn = false;
		}
	}
}
