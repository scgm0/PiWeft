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

	public const string ModePlanHint = "只读分析并生成计划，确认后再执行；开启期间停用 edit / write。";

	public const string ModeGoalExit = "暂停目标并退出目标模式";

	public const string ModePlanExit = "退出计划模式";

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

	public const string GoalClear = "清除";

	public const string GoalClearConfirmTitle = "清除当前目标？";

	public const string GoalClearConfirmMessage = "将清除当前目标并停止自动推进，此操作不可撤销。";

	public static string GoalPhase(string phase) => phase switch
	{
		GoalState.Active => "进行中",
		GoalState.Blocked => "已阻塞",
		GoalState.Complete => "已完成",
		_ => "已暂停",
	};

	public static string GoalProgress(string phase, int rounds, int max) => $"{phase} · {rounds}/{max}";

	public static string GoalResumeFailed(string reason) => $"恢复目标失败：{reason}";

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

	/// 子代理卡的总耗时：从 delegate 到回合结束，含工具与等待，不是模型回复耗时。
	public static string SubagentElapsed(double milliseconds) => $"总耗时 {FormatDuration(milliseconds)}";

	public static string SubagentUsage(long tokens, double cost)
	{
		var usage = tokens > 0 ? $"{TrajectoryFormat.CompactTokens(tokens)} tk" : string.Empty;
		if (cost <= 0)
		{
			return usage;
		}

		return usage.Length == 0 ? $"${cost:0.0000}" : $"{usage} · ${cost:0.0000}";
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

	public const string SubagentStop = "停止";

	public static string SubagentCardMeta(int active, int total, int shown = -1)
	{
		var text = active > 0 ? $"{active} 个在跑 · 共 {total} 个" : $"共 {total} 个";
		return shown >= 0 && shown < total ? $"{text} · 显示 {shown} 个" : text;
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

	/// <summary>子代理在自己的标签里打开问询卡：状态行与卡片提示都用这一句。</summary>
	public const string SubagentAwaitingState = "等待回答";

	public const string SubagentAwaitingHint = "子代理在自己的标签里等你回答，点开子会话处理。";

	public const string SubagentProgress = "进度";

	public const string SubagentBrief = "任务";

	public const string SubagentResult = "最近回复";

	public static string SubagentAwaitingInput(string name) => $"子代理「{name}」在等你回答。";

	public const string ReportExpandHint = "展开子代理回报全文";

	// 注入的回报：不是用户写的气泡，单独一种行。
	public const string ReportSubagentResult = "子代理回报";

	public const string ReportSubagentMessage = "子代理消息";

	public const string ReportLinkedChild = "子会话回报";

	public const string ReportLinkedMessage = "关联会话消息";

	public const string QueuedInjected = "回报";

	public static string QueuedInjectedTip(string preview) => $"等待下一轮处理的回报：{preview}";

	public static string SubagentStopFailed(string name) => $"停不掉子代理「{name}」，它可能已经结束。";
}
