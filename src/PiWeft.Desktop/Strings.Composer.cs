using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Subagents;

namespace PiWeft.Desktop;

/// 输入区「+」菜单与技能/提示词选择器的文案。措辞对齐 PiDeck：菜单项只写动作，
/// 模式分组单独一行标签；选择器标题与空态说清楚数据从哪来。
public static partial class Strings
{
	public const string ComposerAddTitle = "添加附件、技能、提示词或模式";

	public const string ComposerAddAttach = "上传附件";

	public const string ComposerAddSkill = "选择技能";

	public const string ComposerAddPrompt = "选择提示词";

	public const string ComposerAddMode = "模式";

	/// 选择器脚注：选中条目只插入命令，会不会发送由用户按回车决定。
	public const string ComposerInsertHint = "点击将命令插入输入框，回车即可发送执行";

	public const string ModeGoal = "目标";

	public const string ModeGoalHint = "围绕一条目标自动连续推进，直到完成、阻塞或暂停。";

	public const string ModePlan = "计划";

	/// 计划步骤卡的标题。与「待办」并列，不再混在「扩展」卡里。
	public const string PlanCardTitle = "计划";

	/// 计划卡上的两个出口：草案态开始执行，执行态结束自动推进（保留已勾步骤）。
	public const string PlanStartExecution = "开始执行";

	/// 执行态卡头：完成数、总数与剩余。比单纯 n/m 更能看出还有几步。
	public static string PlanProgress(int completed, int total) =>
		$"计划进度 {completed}/{total} · {total - completed} 待处理";

	public const string PlanStopExecution = "结束执行";

	/// 计划评审卡的抬头：一眼区分普通问询与计划确认。
	public const string PlanReviewTitle = "计划评审";

	/// 长正文（计划全文）的复制出口：卡片里的文本不可选中，只能走这一条。
	public const string CopyDetail = "复制全文";

	public const string ExpandDetail = "展开全文";

	public const string CollapseDetail = "收起全文";

	public const string ModePlanHint = "只读分析并生成计划，确认后再执行；开启期间停用 edit / write / powershell。";

	/// 占位提示随模式变：普通模式说通用输入，两种模式各说这一句该写什么。
	internal static string ComposerPlaceholderFor(ComposerMode mode) => mode switch
	{
		ComposerMode.Plan => "输入任务以生成计划，确认前保持只读…",
		ComposerMode.Goal => "输入要达成的目标，发送后自动连续推进…",
		_ => ComposerPlaceholder,
	};

	public const string ModeGoalExit = "暂停目标并退出目标模式";

	public const string ModePlanExit = "退出计划模式";

	/// 流式期间点模式：切换排队到本轮收尾。
	internal static string ModeQueued(ComposerMode mode) => mode switch
	{
		ComposerMode.Goal => "本轮结束后切换到目标模式。",
		ComposerMode.Plan => "本轮结束后切换到计划模式。",
		_ => "本轮结束后退出当前模式。",
	};

	public const string ModeQueueCancelled = "已取消模式切换。";

	public const string ModePendingSuffix = " · 本轮后";

	public const string ModePendingExitSuffix = " · 本轮后退出";

	public const string ModeChipPendingTip = "本轮结束后生效，再点一次取消。";

	// 页签悬停：◐ 只说明「有东西在跑」，具体是计划还是目标由这一行补。
	public const string TabWaitingAnswer = "等待回答";

	public const string TabWaitingAnswerShort = "待答";

	public const string TabPlanShort = "计划";

	public static string TabGoalShort(int rounds, int max) => $"目标 {rounds}/{max}";

	public static string TabSubagentsShort(int count) => $"{count} 子代理";

	public const string TabRunning = "运行";

	public const string TabSubagentAwaiting = "子代理在等你回答";

	public static string TabSubagentsRunning(int count) => $"{count} 个子代理在跑";

	public const string SidebarWaiting = "待答";

	public const string SidebarPlan = "计划";

	public static string SidebarGoal(int rounds, int max) => $"目标 {rounds}/{max}";

	public const string TabPlanRunning = "计划执行中";

	public static string TabGoalRunning(int rounds, int max) => $"目标进行中（{rounds}/{max}）";

	public const string SkillPickerTitle = "选择技能";

	public const string SkillPickerPlaceholder = "搜索技能名称或描述…";

	public const string SkillPickerEmpty = "暂无可用技能。可在 ~/.pi/agent/skills 或项目资源里添加。";

	public const string SkillPickerNoMatch = "未匹配到技能";

	public const string PromptPickerTitle = "提示词模板";

	public const string PromptPickerPlaceholder = "搜索模板名称或描述…";

	public const string PromptPickerEmpty = "暂无提示词模板。可在 ~/.pi/agent/prompts 或项目资源里添加。";

	public const string PromptPickerNoMatch = "未匹配到模板";

	/// 带图片但不带文字的发送：目标模式仍然需要一个 objective，和 PiDeck 同一条兜底指令。
	public const string GoalContinueInstruction = "请继续当前目标。";

	// 目标条：阶段名与 PiDeck 的 dshTools.goalPhase 一致；按钮只写动作。
	public const string GoalPause = "暂停";

	public const string GoalResume = "恢复";

	/// 目标停在轮次上限时，恢复按钮的提示换成加轮次：直接恢复会立刻再撞上限。
	public static string GoalExtendRounds => $"再加 {GoalMode.ExtraRounds} 轮并继续";

	/// token 预算阻塞的恢复：从当前用量重新起算，不清空已花掉的会话统计。
	public const string GoalResetTokenBudget = "从当前用量重新计算 token 预算并继续";

	public const string GoalClear = "清除";

	public const string GoalEdit = "编辑目标";

	public const string GoalExpandHint = "点击展开目标全文";

	public const string GoalEditTitle = "编辑目标";

	public const string GoalSaveEdit = "保存";

	public const string GoalEditHint = "Enter 保存，Shift+Enter 换行；可拖入文件插入 @ 引用。";

	public const string GoalCancelEdit = "取消";

	public const string GoalClearConfirmTitle = "清除当前目标？";

	public const string GoalClearConfirmMessage = "将清除当前目标并停止自动推进，此操作不可撤销。";

	public static string GoalPhase(string phase) => phase switch
	{
		GoalState.Active => "进行中",
		GoalState.Blocked => "已阻塞",
		GoalState.Complete => "已完成",
		_ => "已暂停",
	};

	public static string GoalProgress(string phase, int rounds, int max, long usedTokens = 0, long maxTokens = 0) =>
		maxTokens > 0
			? $"{phase} · {rounds}/{max} · {TrajectoryFormat.CompactTokens(usedTokens)}/{TrajectoryFormat.CompactTokens(maxTokens)} tk"
			: $"{phase} · {rounds}/{max}";

	public static string GoalResumeFailed(string reason) => $"恢复目标失败：{reason}";

	/// 目标条的暂停/清除失败：与恢复失败分开措辞，用户知道是哪一步没成。
	public static string GoalActionFailed(string reason) => $"目标操作失败：{reason}";

	/// 目标结束记录在时间线上的标签：完成与阻塞分开。
	public const string ReportGoalComplete = "目标完成";

	public const string ReportGoalBlocked = "目标阻塞";

	/// 计划执行过程的时间线标签：这些是可见 custom message，不该冒充用户提问。
	public const string ReportPlanSteps = "计划步骤";

	public const string ReportPlanExecute = "计划执行";

	public const string ReportPlanComplete = "计划完成";

	// 关联会话：提议卡片与子会话条。措辞对齐 PiDeck 的 linkedSessions.*。
	public const string LinkedSessionsTitle = "子会话";

	public const string LinkedProposalOffer = "提议开新会话";

	public const string LinkedProposalOwn = "建议的后续任务";

	public const string LinkedProposalPending = "等你决定";

	public const string LinkedNewSession = "新会话";

	public const string LinkedDoHere = "在此做";

	public const string LinkedDismiss = "不要";

	public const string LinkedOpen = "打开";

	public static string LinkedSessionsMeta(int children, int proposals) => (children, proposals) switch
	{
		(0, > 0) => $"{proposals} 个提议",
		(> 0, 0) => $"{children} 个子会话",
		_ => $"{proposals} 个提议 · {children} 个子会话",
	};

	public static string LinkedTurns(int turns) => $"{turns} 轮";

	/// 带预算时把上限一起写出来：12k/50k tk 比单看 12k 有意义。
	public static string SubagentUsage(long tokens, double cost, long maxTokens = 0, double maxCost = 0)
	{
		var shown = tokens > 0
			? TrajectoryFormat.CompactTokens(tokens)
			: maxTokens > 0 ? "0" : string.Empty;
		var usage = maxTokens > 0
			? $"{shown}/{TrajectoryFormat.CompactTokens(maxTokens)} tk"
			: shown.Length > 0 ? $"{shown} tk" : string.Empty;
		var costText = maxCost > 0
			? $"${cost:0.0000}/${maxCost:0.0000}"
			: cost > 0 ? $"${cost:0.0000}" : string.Empty;
		if (costText.Length == 0)
		{
			return usage;
		}

		return usage.Length == 0 ? costText : $"{usage} · {costText}";
	}

	public static string LinkedState(string state) => state switch
	{
		LinkedSessionLink.Starting => "启动中",
		LinkedSessionLink.Running => "运行中",
		LinkedSessionLink.Idle => "等待中",
		LinkedSessionLink.Failed => "失败",
		_ => "已停止",
	};

	public static string LinkedProposalFailed(string reason) => $"提议操作失败：{reason}";

	// 子代理：只做异步委派，措辞与工具名区分开。
	public const string SubagentCardTitle = "子代理";

	/// 侧栏子行只写需要被看见的状态：已完成与历史项不占位置。
	public const string SidebarSubagentStarting = "启动中";

	public const string SidebarSubagentRunning = "运行中";

	public const string SidebarSubagentAwaiting = "等待回答";

	public const string SidebarSubagentQuestion = "问父会话";

	public const string SidebarSubagentFailed = "失败";

	public static string SidebarSubagentChildrenRunning(int count) => $"{count} 个子代理在跑";

	public static string SidebarSubagentChildrenAwaiting(int count) => $"{count} 个等待回答";

	/// 父行的子项计数：子代理与关联会话分开，只有一种时不提另一种。
	public static string SidebarChildCounts(int subagents, int linked) => (subagents, linked) switch
	{
		(> 0, > 0) => $"{subagents} 子代理 · {linked} 关联",
		(> 0, 0) => $"{subagents} 子代理",
		_ => $"{linked} 关联",
	};

	public const string SubagentStop = "停止";

	/// 失败行的「接着做」：不要求用户再写一遍任务。
	public const string SubagentContinue = "继续";

	public const string SubagentContinuePrompt = "上一轮没完成，接着做；先说你已经做到哪、卡在哪。";

	public static string SubagentContinueFailed(string reason) => $"继续失败：{reason}";

	/// 子代理停在 ask_parent 上时，卡片展开区里的问题与内联回答。
	public const string SubagentQuestion = "问题";

	public const string SubagentAnswer = "回答";

	public const string SubagentAnswerPlaceholder = "回答子代理的问题";

	public const string SubagentAnswerEmpty = "回答不能为空。";

	/// 卡片展开区的自由输入：运行中会在当前回合的工具边界插入。
	public const string SubagentSend = "发送";

	public const string SubagentMessagePlaceholder = "给子代理发消息；运行中会在当前回合的工具边界插入";

	public const string SubagentMessageEmpty = "消息不能为空。";

	public static string SubagentSendFailed(string reason) => $"发送失败：{reason}";

	public static string SubagentAnswerFailed(string name) =>
		$"回答失败：「{name}」的问题可能已被父会话或超时取走。";

	/// 通过 output_schema 校验的结果：模型与 list_subagents 都看得到，卡片也该有。
	public const string SubagentStructured = "结构化结果";

	/// 本次运行发起的工具调用数：与 token 一起看，能分清「多轮小步」与「一次长任务」。
	public static string SubagentToolCalls(int count) => $"{count} 次工具调用";

	/// 子代理本轮改过的文件；shell 只能标「可能写入」。
	public static string SubagentChanges(int files, bool mayWrite) => (files, mayWrite) switch
	{
		(> 0, true) => $"{files} 个文件 · 可能写入",
		(> 0, false) => $"{files} 个文件",
		(0, true) => "可能写入",
		_ => string.Empty,
	};

	public const string SubagentChangedFiles = "改动文件";

	public static string SubagentWorktree(string? branch) =>
		branch is { Length: > 0 } ? $"隔离副本 · {branch}" : "隔离副本";

	public const string SubagentWorktreePath = "隔离副本";

	public const string SubagentWorktreeOpen = "打开目录";

	public const string SubagentWorktreeApply = "应用改动";

	public const string SubagentWorktreeDiff = "查看改动";

	public const string SubagentWorktreeDiscard = "丢弃副本";

	public static string SubagentWorktreeDiscardConfirm(string name) =>
		$"删除子代理「{name}」的隔离副本与未应用的改动？主工作区不受影响。";

	public const string SubagentMayWriteHint = "跑过 shell 命令，无法确定写了哪些文件；展开子会话或看 git diff 确认。";

	public static string SidebarSubagentChanged(int count) => $"{count} 个改了文件";

	public static string SidebarSubagentFiles(int count) => $"{count} 文件";

	public const string SidebarSubagentMayWrite = "可能写入";

	public static string ProjectWriting(int count) => $"{count} 个在写";

	public const string SidebarSessionRunning = "运行中";

	public const string WorkspaceNoticeOpen = "查看";

	public const string WorkspaceNoticeTitle = "其他会话";

	public static string WorkspaceNoticeMeta(int count, int running, int changed, int sameFile)
	{
		var parts = new List<string>(3);
		if (running > 0) parts.Add($"{running} 个正在运行");
		if (changed > 0) parts.Add($"{changed} 个改过文件");
		if (sameFile > 0) parts.Add($"{sameFile} 个在改同一文件");
		return parts.Count == 0 ? $"{count} 个会话" : $"{count} 个会话 · {string.Join(" · ", parts)}";
	}

	public const string WorkbenchTitle = "工作台";

	public const string WorkbenchGoal = "目标";

	public const string WorkbenchCollapse = "收起工作台";

	public const string TeamTasksTitle = "团队任务";

	public static string TeamTasksMeta(int active, int pending, int total) =>
		$"{active} 进行中 · {pending} 待领取 · 共 {total}";

	public static string TeamTaskStatusText(string status) => status switch
	{
		TeamTaskStatus.InProgress => "进行中",
		TeamTaskStatus.Completed => "已完成",
		TeamTaskStatus.Deleted => "已删除",
		_ => "待领取",
	};

	public const string TeamTaskUnowned = "待领取";

	public static string TeamTaskBlockedBy(string ids) => $"依赖：{ids}";

	public static string TeamTaskWriteScopes(string scopes) => $"写范围：{scopes}";

	public static string TeamTaskRevision(int revision) => $"revision {revision}";

	public static string TeamTaskOpenOwner(string owner) => $"打开「{owner}」";

	public const string TeamTaskCopy = "复制详情";

	public const string TeamTaskOwnerEnded = "已结束";

	public static string TeamTaskOwnerFinished(string owner, string subject) =>
		$"「{owner}」结束了，但任务「{subject}」仍在进行中。";

	public static string TeamTasksShowAll(int remaining) => $"显示其余 {remaining} 个";

	public const string TeamTasksShowLess = "收起";

	public static string TeamTasksFilterOwner(string owner) => $"只看「{owner}」";

	public static string TeamTasksFilterMeta(string owner, int count) => $"只看「{owner}」 · {count} 个";

	public const string TeamTasksClearFilter = "清除筛选";

	public static string TeamTasksOwnerEmpty(string owner) => $"「{owner}」名下没有未删除的任务。";

	public static string WorkspaceNoticeRunning(string title) => $"「{title}」正在这个项目里运行。";

	public static string WorkspaceNoticeSubagentRunning(string title) => $"「{title}」的子代理正在这个项目里运行。";

	public static string WorkspaceNoticeChanged(string title, int files) => $"「{title}」改过这个项目的 {files} 个文件。";

	public static string WorkspaceNoticeSubagentChanged(string title) => $"「{title}」的子代理改过这个项目的文件。";

	public static string WorkspaceNoticeSameFile(string title, string file) => $"「{title}」也在改 {file}。";

	public static string SubagentCardMeta(int active, int total, int shown = -1, long tokens = 0, double cost = 0)
	{
		var text = active > 0 ? $"{active} 个在跑 · {total} 个" : $"{total} 个";
		if (tokens > 0)
		{
			text = $"{text} · {TrajectoryFormat.CompactTokens(tokens)} tk";
		}

		if (cost > 0)
		{
			text = $"{text} · ${cost:0.0000}";
		}

		// 卡片只画最近 20 行：多出来的要告诉用户去哪里看，不然像是丢了。
		return shown >= 0 && shown < total ? $"{text} · 显示 {shown} · 命令面板看全部" : text;
	}

	public static string SubagentStateText(SubagentState state) => state switch
	{
		SubagentState.Starting => "启动中",
		SubagentState.Running => "运行中",
		// Idle 是「这一轮做完了，还能继续发消息」，不是「在等什么」。
		SubagentState.Idle => "已完成",
		SubagentState.Failed => "失败",
		_ => "已停止",
	};

	public const string SubagentResumable = "可继续";

	/// 连续回报上限后不再自动投递；正文仍在，wait_subagents 能取。
	public const string SubagentDeliverySuppressed = "回报未自动投递 · wait 可取";

	public const string SidebarSubagentNotDelivered = "未投递";

	/// 已发给子代理、还在 steering 队列里，模型尚未在下一个工具边界读到。
	public const string SubagentSteerPending = "消息待送达";

	public const string SidebarSubagentSteerPending = "待送达";

	/// <summary>子代理在自己的标签里打开问询卡：状态行与卡片提示都用这一句。</summary>
	public const string SubagentAwaitingState = "等待回答";

	/// <summary>子代理停在 ask_parent 上：先等父会话回答，超时后转给用户。</summary>
	public const string SubagentQuestionState = "问父会话";

	public const string SubagentAwaitingHint = "子代理在等你回答：问询卡已弹出，直接回答即可。";

	public const string SubagentProgress = "进度";

	/// 当前工具加「多久以前」。长任务里当前工具名可能十分钟不变，单看它分不出在跑还是卡住。
	public static string SubagentActivity(string activity, long ageMs, long staleAfterMs) =>
		ageMs >= staleAfterMs
			? $"{activity} · {SubagentActivityAge(ageMs)}没有新动作"
			: $"{activity} · {SubagentActivityAge(ageMs)}";

	private static string SubagentActivityAge(long millis) => millis switch
	{
		< 5_000 => "刚刚",
		< 60_000 => $"{millis / 1_000} 秒前",
		< 3_600_000 => $"{millis / 60_000} 分钟前",
		_ => $"{millis / 3_600_000} 小时前",
	};

	public const string SubagentBrief = "任务";

	public const string SubagentResult = "最近回复";

	/// <summary>失败行的展开段取这个标题：那里的正文是错误原因，不是助手回复。</summary>
	public const string SubagentFailure = "失败原因";

	/// <summary>宿主跑过验证命令时，展开区的段落标题。</summary>
	public const string SubagentVerifySection = "验证";

	public static string SubagentVerifyPassed(string command) => $"验证通过：{command}";

	public static string SubagentVerifyFailed(string command, int exitCode) => $"验证失败：{command}（退出码 {exitCode}）";

	public static string SubagentVerifyTimedOut(string command) => $"验证超时：{command}";

	public static string SubagentVerifyExit(int exitCode) => $"退出码 {exitCode}";

	/// <summary>回复被模型输出上限截断：内容是真的，但可能缺尾巴。</summary>
	public const string SubagentTruncated = "回复被 max tokens 截断";

	public static string SubagentAwaitingInput(string name) => $"子代理「{name}」在等你回答。";

	public const string ReportExpandHint = "展开子代理回报全文";

	// 注入的回报：不是用户写的气泡，单独一种行。
	public const string ReportSubagentResult = "子代理回报";

	// 子代理收到的是父会话发来的，标签从收件方看。
	public const string ReportParentMessage = "父会话消息";

	public const string ReportLinkedChild = "子会话回报";

	public const string ReportLinkedMessage = "关联会话消息";

	public const string QueuedInjected = "回报";

	public static string QueuedInjectedTip(string preview) => $"等待下一轮处理的回报：{preview}";

	public static string SubagentStopFailed(string name) => $"停不掉子代理「{name}」，它可能已经结束。";

	/// <summary>会话文件已删除或打不开：点「打开」后要有一句反馈，不能静默不动。</summary>
	public const string SessionOpenFailed = "打不开这个会话：会话文件可能已被删除。";

	/// <summary>后台子代理失败时的轻提示；正文只取失败原因首行。</summary>
	public static string SubagentFailedNotice(string name, string reason) => reason.Length > 0
		? $"子代理「{name}」失败：{reason}"
		: $"子代理「{name}」失败。";

	/// <summary>子代理完成且父标签不在前台时的轻提示。</summary>
	public static string SubagentCompletedNotice(string name) => $"子代理「{name}」完成了这一轮。";

	/// 关父标签会停掉没有自己标签的子代理；批量关闭时只问一次。
	public static string SubagentCloseConfirm(int running) =>
		$"还有 {running} 个子代理在跑。关掉标签会停掉它们，结果不会再送回本会话。";
}
