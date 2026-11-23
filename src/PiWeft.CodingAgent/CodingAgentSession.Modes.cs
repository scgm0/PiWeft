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
	/// UI 线程读（模式切换）与 agent 线程写（执行/完成）：bool 读写原子，volatile 保证可见性。
	private volatile bool _planExecution;
	/// 目标状态在 UI 线程（暂停/恢复/清除）与 agent 线程（回合结束推进轮次）两边写：
	/// 引用字段用 volatile 保证界面读到的不是过期快照，读写都进 <see cref="_goalGate"/>。
	private volatile GoalState _goal = GoalState.Initial;
	private readonly Lock _goalGate = new();
	/// 上一次落盘的目标全文：目标不变时条目省略 objective，避免 32 轮堆 32 份原文。
	/// 只在 <see cref="_goalGate"/> 内读写。
	private string _persistedGoalObjective = string.Empty;
	private volatile bool _pendingModeTurn;
	/// 0/1：续轮循环单入。两次 Prompt 收尾与后台恢复可能同时进来。
	private int _drainingModeTurn;

	/// 本次计划模式已提示过「未识别到计划」：模型没按格式写时提示一次，
	/// 每次进入计划模式重置。不限次的话，模式里的每个普通问答都会弹一遍。
	private volatile bool _planDraftNotified;

	/// 本轮 submit_plan 已提交计划：agent_end 直接用结构化步骤，不再解析回复文本。
	private volatile bool _planToolSubmitted;

	/// 计划评审选单里的修改意见：它要起一轮普通回复，不受「目标暂停/执行结束」栅栏约束。
	private volatile bool _pendingFeedbackTurn;

	/// 最新一条目标续轮消息的条目 id。续轮每轮写一条，历史里会堆 Round 行；
	/// 追加新的一条时把旧的从模型投影里摘掉，会话文件不动。
	private string? _goalContinuationEntryId;

	/// 执行指令消息的条目 id：结束/完成时把它换成一句收尾，模型不会再读到「继续执行」。
	private string? _planExecuteEntryId;

	public IReadOnlyList<PlanStep> ActivePlanSteps => _planSteps;

	public bool PlanExecutionEnabled => _planExecution;

	public GoalState Goal => _goal;

	/// 计划执行态：写权限恢复，进度按 `[DONE:n]` 勾。
	private void StartPlanExecution()
	{
		// 不调 SetPlanMode(false)：它会先推一次空 widget、写一条 executing:false 记录，
		// 而且 ApplyTools 在 _planExecution 还在 false 时构建提示，执行段会漏掉。
		PlanModeEnabled = false;
		_planModeFromMarker = false;
		_planToolSubmitted = false;
		_planExecution = true;
		// ApplyTools 会重建系统提示，此时 execution 段与步骤都已就位。
		ApplyTools();
		_pendingModeTurn = true;
		PublishPlanWidget();
		PersistPlanState();

		// 两条自定义消息：步骤清单（可见）+ 执行指令。第二条触发新一轮。
		Session.AppendCustomMessage(
			PlanSteps.WidgetKey,
			MessageContent.FromText(PlanSteps.StepsMessage(_planSteps)),
			display: true);
		_planExecuteEntryId = Session.AppendCustomMessage(
			PiDeckProtocol.PlanExecuteMessageType,
			MessageContent.FromText(PlanSteps.ExecuteMessage(_planSteps)),
			display: true);
	}

	/// 结束计划执行，回到可切模式的普通状态。`keepSteps` 为 true 时步骤留在草案态：
	/// 执行中切走模式、用户点「结束执行」都不应该把已勾的进度一起丢掉。
	/// 计划模式的结构化提交：模型传步骤数组，清洗后就是计划卡与评审用的同一份数据。
	/// 失败返回错误结果，让模型自己修正格式，不静默丢步骤。
	public AgentToolResult SubmitPlanFromTool(IReadOnlyList<string> steps)
	{
		if (!PlanModeEnabled || _planExecution)
		{
			return AgentToolResult.Error("submit_plan is only available while planning.");
		}

		var normalized = PlanSteps.Normalize(steps);
		if (normalized.Count == 0)
		{
			return AgentToolResult.Error("submit_plan needs at least one step longer than three characters.");
		}

		_planSteps.Clear();
		_planSteps.AddRange(normalized);
		_planToolSubmitted = true;
		PublishPlanWidget();
		PersistPlanState();
		return AgentToolResult.Text("Plan submitted for review. Stop here and wait for the user's decision.");
	}

	/// 从草案直接开始执行：桌面上「先不执行」后仍可启动，不必退出重进。
	public async Task StartPlanExecutionFromDraftAsync(CancellationToken cancellationToken = default)
	{
		if (_planExecution || !PlanModeEnabled || _planSteps.Count == 0)
		{
			return;
		}

		StartPlanExecution();
		await RunPendingModeTurnAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>手动勾选或取消一条计划步骤；勾完最后一条按计划完成处理。</summary>
	/// <returns>步骤存在且状态确实变化时为 true。</returns>
	public bool SetPlanStepCompleted(int step, bool completed)
	{
		if (!_planExecution)
		{
			return false;
		}

		var index = _planSteps.FindIndex(item => item.Step == step);
		if (index < 0 || _planSteps[index].Completed == completed)
		{
			return false;
		}

		_planSteps[index] = _planSteps[index] with { Completed = completed };
		PublishPlanWidget();
		PersistPlanState();
		RefreshSystemPrompt();
		if (completed && AllPlanStepsCompleted())
		{
			CompletePlan();
		}

		return true;
	}

	public void StopPlanExecution(bool keepSteps)
	{
		if (!_planExecution)
		{
			return;
		}

		_planExecution = false;
		// 已排上的执行轮不再起跑：结束执行后不该再自动发一轮请求。
		_pendingModeTurn = false;
		if (!keepSteps)
		{
			_planSteps.Clear();
		}

		ReplacePlanExecutionInstruction("Plan execution was stopped by the user.");
		PublishPlanWidget();
		PersistPlanState();
		RefreshSystemPrompt();
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
		_planToolSubmitted = false;
		_planSteps.Clear();
		ReplacePlanExecutionInstruction("Plan execution finished.");
		PublishPlanWidget();
		PersistPlanState();
		// 执行段要跟着下线，否则下一轮请求还带着一张已完成的计划。
		RefreshSystemPrompt();
	}

	public void SetGoal(string objective, bool resetRounds = true, int? maxRounds = null)
	{
		var text = objective.Trim();
		if (text.Length == 0)
		{
			return;
		}

		// 新目标用设置里的默认上限；暂停/阻塞后恢复同一目标时保留它自己的上限。
		var newGoal = _goal.Objective.Length == 0 || _goal.Phase == GoalState.Complete;
		var defaultMaxRounds = newGoal ? Settings.Goal.MaxRounds : _goal.MaxRounds;
		// 预算从设置取一次并记住起点：恢复同一目标不重新计算已花掉的 token。
		var maxTokens = newGoal ? Settings.Goal.MaxTokens : _goal.MaxTokens;
		var tokensAtStart = newGoal ? Statistics().Tokens : _goal.TokensAtStart;
		lock (_goalGate)
		{
			_goal = _goal with
			{
				Enabled = true,
				Phase = GoalState.Active,
				Objective = text,
				RoundsStarted = resetRounds ? 0 : _goal.RoundsStarted,
				MaxRounds = maxRounds is > 0 ? maxRounds.Value : defaultMaxRounds,
				BlockReason = null,
				BlockCode = null,
				ConsecutiveFailures = 0,
				MaxTokens = maxTokens,
				TokensAtStart = tokensAtStart,
			};
			PersistGoalState();
		}

		ForgetGoalContinuation();
		PublishGoalWidget();
		RefreshSystemPrompt();
	}

	/// 暂停目标。`notify` 只在「用户没点暂停，但下一条普通消息把目标顶掉了」时打开：
	/// 那种情况没有可见的点击动作，不给一条提示，读者会以为目标还在跑。
	/// `abortRun` 用于界面上的暂停：只改 phase 停不下已经在跑的轮次，模型还能继续动工具。
	public void PauseGoal(bool notify = false, bool abortRun = false)
	{
		var changed = false;
		lock (_goalGate)
		{
			if (!_goal.Enabled || _goal.Phase == GoalState.Paused)
			{
				return;
			}

			_goal = _goal with { Phase = GoalState.Paused, BlockReason = null, BlockCode = null };
			PersistGoalState();
			changed = true;
		}

		if (changed)
		{
			ForgetGoalContinuation();
		}

		PublishGoalWidget();
		RefreshSystemPrompt();
		if (changed && abortRun)
		{
			// 先落 phase 再中止：轮次收尾时看到 paused，不会推进轮次或排下一轮。
			Abort();
		}

		if (notify && changed)
		{
			UiHost?.Notify(new UiNotifyRequest("目标已暂停，切回目标模式后从当前进度继续。", "info"));
		}
	}

	/// 编辑目标全文。保留阶段、已用轮次与阻塞原因：改的是内容，不是生命周期。
	/// 编辑不自动起一轮，恢复仍由用户点。
	public void EditGoal(string objective)
	{
		var text = objective.Trim();
		if (text.Length == 0)
		{
			return;
		}

		lock (_goalGate)
		{
			if (_goal.Objective.Length == 0 || _goal.Objective == text)
			{
				return;
			}

			_goal = _goal with { Objective = text };
			PersistGoalState();
		}

		PublishGoalWidget();
		RefreshSystemPrompt();
	}

	/// 增加轮次上限并回到 active。用户点「再加 N 轮」不是改目标：轮次保留。
	public void ExtendGoalRounds(int extra)
	{
		if (extra <= 0)
		{
			return;
		}

		lock (_goalGate)
		{
			if (_goal.Objective.Length == 0)
			{
				return;
			}

			_goal = _goal with
			{
				Enabled = true,
				Phase = GoalState.Active,
				MaxRounds = _goal.MaxRounds + extra,
				BlockReason = null,
				BlockCode = null,
				ConsecutiveFailures = 0,
			};
			PersistGoalState();
		}

		ForgetGoalContinuation();
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

		ForgetGoalContinuation();
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

		// 停在轮次上限：先加预算，否则恢复后第一轮就再次撞上限。
		// 放在这里，命令、PiDeck 与桌面按钮走同一条恢复路径。
		if (_goal.BlockCode == GoalMode.RoundLimitCode)
		{
			ExtendGoalRounds(GoalMode.ExtraRounds);
		}
		else if (_goal.BlockCode == GoalMode.TokenLimitCode)
		{
			// token 预算从当前用量重新起算：不重置的话恢复后第一轮立刻又阻塞。
			lock (_goalGate)
			{
				_goal = _goal with { TokensAtStart = Statistics().Tokens };
				PersistGoalState();
			}
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
			else if (entry is CustomMessageEntry { CustomType: PiDeckProtocol.PlanExecuteMessageType } execute)
			{
				// 恢复执行态时记住执行指令，结束/完成才能把它换成收尾说明。
				_planExecuteEntryId = execute.Id;
			}
		}

		var stalePlanDraft = false;
		if (planData is JsonObject plan)
		{
			if (plan["executing"]?.GetValue<bool>() == true && plan["todos"] is JsonArray todos)
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
			else if (plan["todos"] is JsonArray { Count: > 0 })
			{
				// PiDeck 0.12：非执行态的草案永远勾不动，重开只会挂成「N 待处理」。
				// 丢弃后要写回一条空状态，否则会话文件里的旧 todos 每次都被读回来。
				stalePlanDraft = true;
			}
		}

		_goal = GoalMode.Decode(goalEntries);
		// 恢复出来的目标已经有带 objective 的记录，下一次落盘不必重复写。
		_persistedGoalObjective = _goal.Objective;
		// 旧续轮条目的 id 属于上一个分支，重启后不能拿它去改投影。
		_goalContinuationEntryId = null;

		if (stalePlanDraft)
		{
			PersistPlanState();
		}

		// 恢复执行态时 ApplyTools 已经按「无执行态」构建过提示：这里补一次，
		// 否则重开后的第一次请求没有 [EXECUTING PLAN] 与剩余步骤。
		RefreshSystemPrompt();
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

		var remaining = new List<PlanStep>(_planSteps.Count);
		foreach (var step in _planSteps)
		{
			if (!step.Completed)
			{
				remaining.Add(step);
			}
		}

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
			// 执行段列的是剩余步骤：勾掉一步就要重建，否则下一轮还看到已完成的步。
			RefreshSystemPrompt();
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

		// 不因已有草案早退：用户「先不执行」后继续讨论，模型给出新计划时要能覆盖草案。
		if (!PlanModeEnabled || _planExecution || UiHost is not { HasUi: true } host)
		{
			return;
		}

		var lastAssistant = MessageText.LastAssistant(end.Messages);
		if (lastAssistant is null || lastAssistant.StopReason is not StopReason.Stop)
		{
			return;
		}

		var replyText = MessageText.Of(lastAssistant);
		// 优先用 submit_plan 提交的结构化步骤；没有这个工具时才解析回复里的 Plan: 段落。
		var toolSubmitted = _planToolSubmitted;
		_planToolSubmitted = false;
		var steps = toolSubmitted ? [.. _planSteps] : PlanSteps.Extract(replyText);
		if (steps.Count == 0)
		{
			// 有编号行却没标题：模型写了计划但没按格式。提示一次，不自动重试。
			if (!_planDraftNotified && PlanSteps.HasNumberedLines(replyText))
			{
				_planDraftNotified = true;
				host.Notify(new UiNotifyRequest(
					PlanSteps.HasPlanHeading(replyText)
						? "计划步骤为空或被过滤：每步至少四个字符，不要用斜杠命令。已保持只读。"
						: "未识别到计划：回复需要以 `Plan:` 开头的编号步骤结尾。已保持只读。",
					"warning"));
			}
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
							$"{PlanSteps.ContinueOption}|结束本轮，保持只读；也可以直接在下方输入修改意见",
						],
						null),
					CurrentRunToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			if (choice is { Length: > 0 } selected &&
				UiSelectText.SplitOption(selected).Label == PlanSteps.ExecuteOption)
			{
				StartPlanExecution();
				return;
			}

			if (choice is { Length: > 0 } selectedContinue &&
				UiSelectText.SplitOption(selectedContinue).Label == PlanSteps.ContinueOption)
			{
				// 结束本轮、保持只读；用户再发消息才继续。
				return;
			}

			if (choice is { Length: > 0 } feedback)
			{
				// 选单里的自定义文本 = 修改意见：保持只读，把意见作为用户消息交给下一轮重写计划。
				// 旧草案留着：模型没给出新计划时，用户还能回到它，而不是凭空丢一份计划。
				Session.AppendMessage(new UserMessage
				{
					Content = MessageContent.FromText(feedback),
					Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				});
				_pendingFeedbackTurn = true;
				_pendingModeTurn = true;
				return;
			}

			// 关闭选单只收起评审：草案与只读状态都留着，误按 Esc 不该让整份计划作废。
			// 退出计划模式走输入区 chip 或 /plan off。
			host.Notify(new UiNotifyRequest(
				"计划已保留。可点输入区的模式按钮退出计划模式。",
				"info"));
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
		// phase 要跟着落地：停在「进行中」会让读者以为目标还在推进。
		if (lastAssistant.StopReason is StopReason.Aborted or StopReason.Error or StopReason.Length)
		{
			var targetChanged = false;
			var retry = false;
			GoalState? blockedGoal = null;
			lock (_goalGate)
			{
				if (_goal.Running)
				{
					if (lastAssistant.StopReason == StopReason.Aborted)
					{
						_goal = _goal with
						{
							Phase = GoalState.Paused,
							BlockReason = null,
							BlockCode = null,
							ConsecutiveFailures = 0,
						};
					}
					else
					{
						// 请求失败与输出截断都是可重试的：连续 3 轮同因才阻塞，
						// 一次网络抖动不该把整条目标停在 blocked 上。
						var failures = _goal.ConsecutiveFailures + 1;
						var (reason, code) = lastAssistant.StopReason == StopReason.Length
							? ("output length limit", "output-limit")
							: ("request failed", "request-failed");
						if (failures >= GoalMode.MaxConsecutiveFailures)
						{
							_goal = _goal with
							{
								Phase = GoalState.Blocked,
								BlockReason = reason,
								BlockCode = code,
								ConsecutiveFailures = failures,
							};
							blockedGoal = _goal;
						}
						else
						{
							_goal = _goal with { ConsecutiveFailures = failures };
							retry = true;
						}
					}

					targetChanged = true;
				}

				PersistGoalState();
				if (retry && _goal.Running)
				{
					// 与正常续轮同一把锁：暂停不会在失败与重试之间插进来。
					QueueGoalContinuation();
				}
			}

			if (blockedGoal is { } blocked)
			{
				AppendGoalEndedMessage(blocked);
			}

			PublishGoalWidget();
			if (targetChanged)
			{
				RefreshSystemPrompt();
			}

			if (retry)
			{
				UiHost?.Notify(new UiNotifyRequest(
					$"目标本轮失败，正在重试（{_goal.ConsecutiveFailures}/{GoalMode.MaxConsecutiveFailures}）。",
					"warning"));
			}

			return;
		}

		(string Text, string Type)? notify = null;
		var refreshPrompt = false;
		GoalState? endedGoal = null;
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
					? _goal with
					{
						Phase = GoalState.Complete,
						Enabled = false,
						BlockReason = null,
						BlockCode = null,
					}
					: _goal with
					{
						Phase = GoalState.Blocked,
						BlockReason = terminal.Reason,
						BlockCode = "reported",
					};
				PersistGoalState();
				endedGoal = _goal;
				refreshPrompt = true;
				notify = terminal.Phase == GoalState.Complete
					? ("目标已完成。", "info")
					: ($"目标已阻塞：{terminal.Reason}", "warning");
			}
			else
			{
				// token 预算与轮次一样是硬上限：达到就停，不再排下一轮。
				var usedTokens = Statistics().Tokens - _goal.TokensAtStart;
				if (_goal.MaxTokens > 0 && usedTokens >= _goal.MaxTokens)
				{
					_goal = _goal with
					{
						Phase = GoalState.Blocked,
						BlockReason = $"reached token budget ({_goal.MaxTokens})",
						BlockCode = GoalMode.TokenLimitCode,
					};
					PersistGoalState();
					endedGoal = _goal;
					refreshPrompt = true;
					notify = ($"目标已阻塞：达到 token 预算（{_goal.MaxTokens}）。", "warning");
				}
				else
				{
					_goal = _goal with
					{
						RoundsStarted = _goal.RoundsStarted + 1,
						ConsecutiveFailures = 0,
					};
					if (_goal.RoundsStarted >= _goal.MaxRounds)
					{
						_goal = _goal with
						{
							Phase = GoalState.Blocked,
							BlockReason = $"reached max rounds ({_goal.MaxRounds})",
							BlockCode = GoalMode.RoundLimitCode,
						};
						PersistGoalState();
						endedGoal = _goal;
						refreshPrompt = true;
						notify = ($"目标已阻塞：达到轮次上限（{_goal.MaxRounds}）。", "warning");
					}
					else
					{
						PersistGoalState();
						// 子代理还在跑：先不排下一轮，等它们的回报拉起父会话。
						// 否则父目标会和子代理并行空转，烧掉轮次。
						if (Subagents is not { } hub || !hub.Runs.Any(static run => run.IsActive))
						{
							// 续轮与状态更新在同一把锁里决定：暂停不会在两者之间插进来。
							QueueGoalContinuation();
						}
					}
				}
			}
		}

		if (endedGoal is { } goal)
		{
			AppendGoalEndedMessage(goal);
		}

		if (notify is not null)
		{
			// 完成或阻塞：续轮指令已经说完，从投影里收掉，别让模型继续读到它。
			ForgetGoalContinuation();
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

	/// 目标结束留一条时间线记录：目标条随后隐藏，用户仍能回看结果与原因。
	private void AppendGoalEndedMessage(GoalState goal)
	{
		var text = goal.Phase == GoalState.Complete
			? $"目标已完成：{goal.Objective}\n轮次：{goal.RoundsStarted}/{goal.MaxRounds}"
			: $"目标已阻塞：{goal.BlockReason ?? "blocked"}\n目标：{goal.Objective}\n轮次：{goal.RoundsStarted}/{goal.MaxRounds}";
		// 结束记录给一份 token 收据：目标跑了多少，回看时不必翻统计页。
		var tokens = Math.Max(0, Statistics().Tokens - goal.TokensAtStart);
		if (tokens > 0)
		{
			text += goal.MaxTokens > 0
				? $"\nToken：{tokens}/{goal.MaxTokens}"
				: $"\nToken：{tokens}";
		}
		Session.AppendCustomMessage(
			PiWeftProtocol.GoalEndedMessageType,
			MessageContent.FromText(text),
			display: true);
	}

	/// 续轮指令写进分支的隐藏消息：本轮已收尾，下一轮由 <see cref="RunPendingModeTurnAsync"/> 起。
	/// 调用方必须持有 <see cref="_goalGate"/>。
	private void QueueGoalContinuation()
	{
		var previous = _goalContinuationEntryId;
		_goalContinuationEntryId = Session.AppendCustomMessage(
			GoalMode.ContinueRole,
			MessageContent.FromText(GoalMode.ContinuationText(_goal)),
			display: false);
		_pendingModeTurn = true;
		if (previous is { Length: > 0 })
		{
			DropGoalContinuation(previous);
		}
	}

	/// 把最后一条续轮指令从模型投影里摘掉。会话文件保留原条目，分叉/回退仍可读。
	private void ForgetGoalContinuation()
	{
		string? id;
		lock (_goalGate)
		{
			id = _goalContinuationEntryId;
			_goalContinuationEntryId = null;
		}

		if (id is { Length: > 0 })
		{
			DropGoalContinuation(id);
		}
	}

	private void DropGoalContinuation(string entryId)
	{
		try
		{
			Session.AppendContextEdit(entryId, MessageContent.FromText(GoalMode.FinishedRoundText));
		}
		catch (InvalidOperationException)
		{
			// 条目已不在当前分支（切会话或回退分支）：没有可替换的投影。
		}
	}

	/// 执行指令是 user 消息；直接删除会让角色不再交替，用一句收尾替换更稳。
	private void ReplacePlanExecutionInstruction(string replacement)
	{
		var id = _planExecuteEntryId;
		_planExecuteEntryId = null;
		if (id is not { Length: > 0 })
		{
			return;
		}

		try
		{
			Session.AppendContextEdit(id, MessageContent.FromText(replacement));
		}
		catch (InvalidOperationException)
		{
			// 条目已不在当前分支：不必再改投影。
		}
	}

	/// 全部步骤已完成。计划步骤不多，但每次勾选都会问一次：手写循环，不生成委托。
	private bool AllPlanStepsCompleted()
	{
		for (var index = 0; index < _planSteps.Count; index++)
		{
			if (!_planSteps[index].Completed)
			{
				return false;
			}
		}

		return true;
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
		if (!_pendingModeTurn || Interlocked.CompareExchange(ref _drainingModeTurn, 1, 0) == 1)
		{
			return;
		}
		try
		{
			// 一轮接一轮：嵌套的 ContinueAsync 走到这里会直接返回（防重入），
			// 由最外层循环继续把新排上的续轮跑完，否则第三轮起会停住。
			while (_pendingModeTurn)
			{
				_pendingModeTurn = false;
				var feedback = _pendingFeedbackTurn;
				_pendingFeedbackTurn = false;
				// 排队后用户可能已经暂停/清除目标或结束了计划执行：这一轮不再起跑。
				// 不复查的话，暂停后仍会多跑一轮，表现为「停了还在动」。
				// 计划评审意见是用户主动提交的，放行。
				if (!feedback && !_planExecution && !_goal.Running)
				{
					break;
				}

				// 刚追加的执行指令/续轮指令还在会话文件里，代理的实时消息列表没有它们：
				// 先按分支投影同步，否则 ContinueAsync 会以「最后一条是 assistant」拒绝。
				_agent.SetMessages(Session.BuildProjectedMessages());
				await ContinueAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			Interlocked.Exchange(ref _drainingModeTurn, 0);
		}

		// 生产者在循环退出与复位之间置的 pending 不能丢：复位后复查一次。
		if (_pendingModeTurn)
		{
			await RunPendingModeTurnAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// 处理宿主斜杠命令（与 pi 的 extension command 同序）。
	/// <para>
	/// PiDeck 0.12 的目标条、计划开关与重载菜单都把这些命令当 prompt 发下来；
	/// 不接管就会被写进用户消息、交给模型当正文回答。命中后不写会话，RPC 回
	/// <c>disposition:"handled"</c>，PiDeck 据此撤掉乐观用户消息。
	/// </para>
	/// 桌面发送路径的扩展命令异步入口。只处理扩展命令；内置命令仍走同步版本。
	/// 返回 true 表示命令已接管（失败也返回 true，和同步版本一致）。
	public async Task<bool> TryExecuteExtensionCommandAsync(string text)
	{
		// 取一次：/reload 可能在 await 期间把 Extensions 置空，第二处硬解引用会 NRE。
		var extensions = Extensions;
		if (extensions is null)
		{
			return false;
		}

		var trimmed = text.Trim();
		if (trimmed.Length < 2 || trimmed[0] != '/')
		{
			return false;
		}

		var space = trimmed.IndexOf(' ');
		var name = space < 0 ? trimmed[1..] : trimmed[1..space];
		var argument = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
		return await extensions.TryRunCommandAsync(name, argument).ConfigureAwait(false);
	}

	public bool TryExecuteSystemCommand(string text, CancellationToken cancellationToken, out string? message)
	{
		message = null;
		var trimmed = text.Trim();
		if (trimmed.Length < 2 || trimmed[0] != '/')
		{
			return false;
		}

		var space = trimmed.IndexOf(' ');
		var name = space < 0 ? trimmed[1..] : trimmed[1..space];
		var argument = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

		// 扩展命令优先：pi 的 prompt 先走 _tryExecuteExtensionCommand，再轮到内置命令。
		if (Extensions?.TryRunCommand(name, argument) == true)
		{
			return true;
		}

		switch (name)
		{
			case "reload":
				// 重载会重建工具与扩展；运行中换工具面会让进行中的回合与工具表错位。
				if (IsStreaming || IsCompacting)
				{
					message = "Wait for the current response to finish before reloading.";
					return true;
				}

				ReloadResources();
				message = $"已重载 {Resources.Skills.Count} 个技能、{Resources.Templates.Count} 个提示词模板、{Resources.Themes.Count} 个主题。";
				return true;

			case "plan":
			{
				// 与 pi-deck-plan-mode 的 /plan 同义：on/off 显式切换，其余参数切换当前状态。
				var normalized = argument.ToLowerInvariant();
				if (normalized is "on" or "enable" or "enabled")
				{
					// 计划与目标互斥：与桌面菜单一致，进入只读前先暂停目标。
					if (_goal.Running)
					{
						// 切模式也要说一声：目标条只是从「进行中」变成「已暂停」，不解释会被当成没生效。
						PauseGoal(notify: true, abortRun: true);
					}

					SetPlanMode(true);
				}
				else if (normalized is "off" or "disable" or "disabled" or "normal")
				{
					SetPlanMode(false);
				}
				else
				{
					if (!PlanModeEnabled && _goal.Running)
					{
						PauseGoal(notify: true, abortRun: true);
					}

					SetPlanMode(!PlanModeEnabled);
				}

				message = PlanModeEnabled ? "计划模式已开启：只读探索，edit / write / powershell 已停用。" : "计划模式已关闭。";
				return true;
			}

			case "goal":
			{
				// 与 pi-deck-goal-mode 的 /goal 同义：pause/clear/resume 控制已有目标，
				// 其余参数就是新目标，空参数查看进度。
				var normalized = argument.ToLowerInvariant();
				if (normalized is "pause" or "off" or "disable")
				{
					PauseGoal(abortRun: true);
					message = "目标模式已暂停。";
				}
				else if (normalized is "clear" or "reset")
				{
					ClearGoal();
					message = "目标已清除。";
				}
				else if (normalized is "resume" or "on" or "enable")
				{
					if (_goal.Objective.Length == 0)
					{
						message = "没有可恢复的目标，请先输入目标。";
					}
					else
					{
						// 目标与计划互斥：恢复目标要退出只读，否则第一轮在写工具缺失时跑。
						if (PlanModeEnabled)
						{
							SetPlanMode(false);
						}

						StartGoalContinuationInBackground(cancellationToken);
						message = $"目标已恢复：{_goal.Objective}";
					}
				}
				else if (argument.Length == 0)
				{
					message = _goal.Objective.Length == 0
						? "当前没有目标。发送一条目标，或使用 /goal <目标>。控制：/goal pause|resume|clear。"
						: $"目标（{_goal.Phase}）{_goal.RoundsStarted}/{_goal.MaxRounds}\n{_goal.Objective}\n控制：/goal pause|resume|clear。";
				}
				else
				{
					if (PlanModeEnabled)
					{
						SetPlanMode(false);
					}

					// 已有未完成目标时改文本：保留轮次与总预算，清零会让暂停后的一次改写白烧前面的轮次。
					SetGoal(argument, resetRounds: _goal.Objective.Length == 0 || _goal.Phase == GoalState.Complete);
					message = $"目标模式已开启：{_goal.Objective}";
				}

				return true;
			}

			default:
				return false;
		}
	}

	/// 恢复目标要真的起一轮，但不能阻塞 prompt 的 handled 响应：PiDeck 的目标条只等 RPC 回执，
	/// 等整轮跑完会把按钮卡成 busy。异常在后台收口，交给会话的错误事件/通知。
	private void StartGoalContinuationInBackground(CancellationToken cancellationToken)
	{
		// 恢复要真的起一轮，但不能阻塞 prompt 的 handled 响应。
		// 复用 ResumeGoalAsync：轮次上限、SetGoal、排队与起轮只维护一份。
		_ = Task.Run(async () =>
		{
			try
			{
				await ResumeGoalAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// 会话关闭/用户中止：续轮取消是正常收尾。
			}
			catch (Exception exception)
			{
				UiHost?.Notify(new UiNotifyRequest($"目标续轮失败：{exception.Message}", "error"));
			}
		});
	}
}
