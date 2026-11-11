namespace PiWeft.CodingAgent.Compatibility;

/// PiDeck 的私有协议面：这些名字是 PiDeck 内置扩展的标识，不是 pi 的协议。
/// 但 PiDeck 的渲染层与历史读取器按它们取数据，把 `customPiPath` 指向本端口时
/// 两边必须用同一套名字与字段形状，所以集中在这里，改名前先确认 PiDeck 仍读。
/// <para>
/// 对齐基准：PiDeck 0.10.0 的 <c>resources/extensions/pi-deck-ask-question.ts</c>、
/// <c>pi-deck-todo.ts</c>、<c>pi-deck-todo-state.ts</c>、<c>pi-deck-plan-mode.ts</c>、
/// <c>pi-deck-goal-mode.ts</c>。PiDeck 侧对应读取点：<c>src/main/feishu/AskCard.ts</c>、
/// <c>src/main/pi/extensionUiGate.ts</c>、<c>src/main/pi/SessionHistoryReader.ts</c>、
/// <c>src/main/ipc/sessionIpc.ts</c>、<c>src/renderer/src/components/session/{SessionTodoStrip,SessionGoalStrip,agentTodoParser}.tsx</c>、
/// <c>src/renderer/src/utils/askUi.ts</c>、<c>src/shared/composerModeMarkers.ts</c>。
/// </para>
public static class PiDeckProtocol
{
	/// <c>ask_question</c> 批量信封：标题是 JSON，键必须为 1。
	public const string AskEnvelopeKey = "__piDeckBatchAsk";

	/// 信封的固定 placeholder 哨兵值。
	public const string AskEnvelopePlaceholder = "__piDeckBatchAsk__";

	/// 计划模式标记：PiDeck composer 注入在消息首行，我们剥掉后开只读。
	public const string PlanModeMarker = "__PI_DECK_PLAN_MODE__";

	/// 目标模式标记：同上，正文即 objective。
	public const string GoalModeMarker = "__PI_DECK_GOAL_MODE__";

	/// 计划草案确认选单的标题前缀：PiDeck 的 <c>askUi.ts</c> 据此换专用卡片。
	public const string PlanNextMarker = "[PI_DECK_PLAN_NEXT]";

	/// 待办快照条目：v3 JSON（<c>version/activePlan/previousPlan/nextPlanId/nextTodoId</c>）。
	public const string TodoEntryType = "pi-deck-todo";

	/// 压缩/分支摘要后补注计划用的不可见消息类型。
	public const string TodoBriefEntryType = "pi-deck-todo-brief";

	/// 待办 widget key；行格式见 <c>TodoTool.WidgetLines</c>。
	public const string TodoWidgetKey = "pi-deck-todo";

	/// 待办 widget 首行的计划身份元数据前缀（后接 <c>leafId:planId</c>）。
	public const string TodoPlanMetadataPrefix = "[[pid:todo-plan:";

	/// 计划模式状态条目：<c>enabled/todos[].step,text,completed/executing</c>。
	public const string PlanEntryType = "pi-deck-plan-mode";

	/// 计划步骤 widget key；行格式见 <c>PlanSteps.WidgetLines</c>。
	public const string PlanWidgetKey = "pi-deck-plan-todos";

	/// 执行指令消息：PiDeck 的 plan 扩展自己也发同名的可见消息。
	/// 我们沿用它，让 PiDeck 打开同一份会话时能认出执行阶段。
	public const string PlanExecuteMessageType = "pi-deck-plan-execute";

	/// 计划完成消息：同上，沿用 PiDeck 的名字。
	public const string PlanCompleteMessageType = "pi-deck-plan-complete";

	/// 目标模式状态条目：<c>enabled/phase/objective/roundsStarted/maxGoalRounds/blockReason</c>。
	public const string GoalEntryType = "pi-deck-goal-mode";

	/// 目标 widget key；行格式见 <c>GoalMode.WidgetLines</c>。
	public const string GoalWidgetKey = "pi-deck-goal";

	// 以下三个是 PiDeck 自己写的隐藏上下文消息类型。本端口不写它们（改用系统提示章节），
	// 登记在这里是为了不撞名，也便于核对 PiDeck 升级时有没有改语义。
	public const string PlanContextMessageType = "pi-deck-plan-mode-context";

	public const string PlanExecutionContextMessageType = "pi-deck-plan-execution-context";

	public const string GoalContextMessageType = "pi-deck-goal-context";
}

/// 只属于本端口的内部名字。不进 PiDeck 的协议面，也不该借用 <c>pi-deck-</c> 前缀。
public static class PiWeftProtocol
{
	/// 目标续轮消息的 custom message 类型：以 <c>display:false</c> 落在会话里，不占时间线。
	public const string GoalContinueRole = "piweft-goal-continue";

	/// 手动继续生成的空标记：内容为空、<c>display:false</c>，只用来开一轮新的请求。
	public const string ContinueGenerationRole = "piweft-continue";

	/// 关联会话的消息类型：子会话首轮与互相转发的消息都带上它，与用户自己发的消息区分。
	public const string LinkedMessageType = "piweft-linked-session-message";

	/// 子会话回合回报的 custom message 类型：可见，内容带一句「这是子会话自述」的限定。
	public const string LinkedReportMessageType = "piweft-linked-session-report";
}
