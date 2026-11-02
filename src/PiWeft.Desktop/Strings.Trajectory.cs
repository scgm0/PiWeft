namespace PiWeft.Desktop;

public static partial class Strings
{
	// 轨迹页（右侧面板的会话复盘）
	public const string Trajectory = "轨迹";
	public const string TrajectoryFoldAll = "全部折叠";
	public const string TrajectoryUnfoldAll = "全部展开";
	public const string TrajectoryLoadOlder = "加载更早记录";
	public const string TrajectoryEmpty = "当前会话还没有可展示的记录。";
	public const string TrajectoryInspectHint = "点选一条记录查看输入、输出和耗时";
	public const string TrajectoryInFlight = "进行中";
	public const string TrajectoryUnknownDuration = "—";
	public const string TrajectoryCopy = "复制";
	public const string TrajectoryTextTruncated = "…（已截断，复制可获取全文）";

	// 泳道
	public const string TrajectoryLaneInput = "输入";
	public const string TrajectoryLaneModel = "模型";
	public const string TrajectoryLaneTools = "工具";
	public const string TrajectoryLaneProcess = "过程";

	// 记录种类
	public const string TrajectoryKindUser = "用户";
	public const string TrajectoryKindInitialPrompt = "初始提示";
	public const string TrajectoryKindAssistant = "助手";
	public const string TrajectoryKindThinking = "思考";
	public const string TrajectoryKindTool = "工具";
	public const string TrajectoryKindError = "错误";
	public const string TrajectoryKindSystemPrompt = "系统提示词";
	public const string TrajectoryKindSystemPromptInitial = "初始提示词";
	public const string TrajectoryKindSystemPromptUpdate = "提示词更新";
	public const string TrajectoryKindModelChange = "模型切换";
	public const string TrajectoryKindThinkingChange = "思考档位";
	public const string TrajectoryKindCompaction = "压缩";
	public const string TrajectoryKindBranchSummary = "分支摘要";
	public const string TrajectoryKindUsage = "用量记录";
	public const string TrajectoryKindCustom = "扩展过程";
	public const string TrajectoryKindSessionInfo = "会话信息";
	public const string TrajectoryKindContextEdit = "上下文编辑";
	public const string TrajectoryKindInternalPrompt = "内部注入";
	public const string TrajectoryKindLabel = "书签";
	public const string TrajectoryKindUnknown = "未知记录";

	// 工具与助手状态
	public const string TrajectoryStatusDone = "完成";
	public const string TrajectoryStatusRunning = "运行中";
	public const string TrajectoryStatusError = "失败";
	public const string TrajectoryStatusPending = "进行中";
	public const string TrajectoryStatusLength = "达到长度上限";
	public const string TrajectoryStatusToolUse = "调用工具";
	public const string TrajectoryStatusAborted = "已中止";
	public const string TrajectoryStatusDeferred = "延后";
	public const string TrajectoryStatusStopped = "已中断";

	// 检查器字段
	public const string TrajectoryFieldTime = "时间";
	public const string TrajectoryFieldDuration = "耗时";
	public const string TrajectoryFieldStatus = "状态";
	public const string TrajectoryFieldCallId = "调用 ID";
	public const string TrajectoryFieldTool = "工具";
	public const string TrajectoryFieldModel = "模型";
	public const string TrajectoryFieldThinkingLevel = "思考档位";
	public const string TrajectoryFieldCustomType = "过程类型";
	public const string TrajectoryFieldInputTokens = "输入 tokens";
	public const string TrajectoryFieldOutputTokens = "输出 tokens";
	public const string TrajectoryFieldCacheRead = "缓存读取";
	public const string TrajectoryFieldCacheWrite = "缓存写入";
	public const string TrajectoryFieldReasoningTokens = "推理 tokens";
	public const string TrajectoryFieldTokensBefore = "压缩前 tokens";
	public const string TrajectoryFieldInput = "入参";
	public const string TrajectoryFieldOutput = "输出";
	public const string TrajectoryFieldThinking = "思考";
	public const string TrajectoryFieldResult = "结果";
	public const string TrajectoryFieldPrompt = "提示词";
	public const string TrajectoryFieldDetail = "详情";
	public static string TrajectoryImages(int count) => $"[{count} 张图片]";
	public static string TrajectoryTurns(int count) => $"{count} 轮";
	public static string TrajectoryRecords(int count) => $"{count} 条记录";
	public static string TrajectoryTurn(int index) => $"第 {index} 轮";
	public static string TrajectoryTurnStats(int steps, int tools) => $"{steps} 条 · {tools} 工具";
	public static string TrajectoryTokenPair(long input, long output) =>
		$"{TrajectoryFormat.CompactTokens(input)}→{TrajectoryFormat.CompactTokens(output)}";
	public static string TrajectoryGroup(int steps, int tools) => $"{steps} 个步骤 · {tools} 个工具调用，点击展开";
}
