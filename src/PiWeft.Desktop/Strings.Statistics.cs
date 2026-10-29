namespace PiWeft.Desktop;

public static partial class Strings
{
	public const string ExecutionProcess = "执行过程";
	public const string ToolStopped = "已中断";
	public const string OutlineImageMessage = "图片消息";
	public const string ExecutionExpandHint = "展开或收起本轮思考、工具调用和中间回复；最终回答始终显示。";
	public const string ExecutionGroupHint = "展开或收起这组思考与工具调用。";
	// 吸顶标题的附属入口：只显示图标，悬停/聚焦时说明用途。
	public const string StepBackToStart = "回到这一步的开头";
	public static string ExecutionTotal(double milliseconds) => $"总耗时 {FormatDuration(milliseconds)}";

	// 过程组组头（PiDeck 同款）：运行态按最新工具类别给「正在…」，纯思考组退回「正在分析请求」。
	public const string ProcessGroupAnalyzing = "正在分析请求";
	public const string ProcessGroupAnalyzed = "已完成分析";
	public const string ProcessGroupRead = "正在读取文件";
	public const string ProcessGroupSearch = "正在搜索代码";
	public const string ProcessGroupWrite = "正在写入文件";
	public const string ProcessGroupEdit = "正在编辑文件";
	public const string ProcessGroupCommands = "正在运行命令";
	public const string ProcessGroupTodo = "正在更新计划";
	public const string ProcessGroupTools = "正在调用工具";
	/// <summary>类别超过三个时组头收尾：PiDeck 的「{title} 等」。</summary>
	public static string ProcessGroupMore(string title) => $"{title} 等";

	/// <summary>工具行的动词短语：进行中加「正在」，例如「正在执行 cd …」「读取 src/App.cs」。</summary>
	public static string ToolAction(string verb, string detail, bool running) => running ? $"正在{verb} {detail}" : $"{verb} {detail}";
	public const string ToolActionExecute = "执行";
	public const string ToolActionRead = "读取";
	public const string ToolActionEdit = "修改";
	public const string ToolActionSearch = "搜索";

	/// <summary>输入区上方的会话进行态（PiDeck responding 指示条）。</summary>
	public const string BusyExecuting = "正在执行命令…";
	public const string SessionStatistics = "会话统计";
	public const string StatisticsUsage = "累计用量";
	public const string StatisticsLastReply = "上次模型回复";
	public const string StatisticsTiming = "累计计时";
	public const string StatisticsInputOutput = "输入 / 输出 token";
	public const string StatisticsCacheIO = "缓存读取 / 写入";
	public const string StatisticsCacheTotal = "缓存总量";
	public const string StatisticsLatestHit = "最新命中率";
	public const string StatisticsAverageHit = "会话平均命中率";
	public const string StatisticsWeightedHit = "累计加权命中率";
	public const string StatisticsCost = "累计费用";
	public const string StatisticsCny = "约合人民币";
	public const string StatisticsTurns = "当前分支轮次";
	public const string StatisticsMessages = "用户 / 助手消息";
	public const string StatisticsTools = "工具执行 / 失败";
	public const string StatisticsFirstToken = "首 token 延迟";
	public const string StatisticsReplyDuration = "回复总耗时";
	public const string StatisticsGenerationSpeed = "生成速度";
	public const string StatisticsEndToEndSpeed = "端到端速度";
	public const string StatisticsLlmDuration = "模型回复累计耗时";
	public const string StatisticsToolDuration = "工具累计执行耗时";
	public const string StatisticsFirstTokenAverage = "平均首 token 延迟";
	public const string StatisticsMissing = "-";
	public const string CompactContext = "压缩上下文";
	public const string StatisticsContextHint = "当前分支压缩/编辑后的上下文估算；超过窗口上限时保留实际百分比。";
	public const string StatisticsUsageHint = "整个会话文件累计，包含其它分支及摘要用量；不因压缩或导航清零。";
	public const string StatisticsHitHint = "缓存读取 /（输入 + 缓存读取 + 缓存写入）；仅对有效的助手用量采样。";
	public const string StatisticsAverageHint = "每条有效助手消息命中率的算术平均；样本不是界面轮询快照。";
	public const string StatisticsFirstTokenHint = "模型请求到正文首 delta；纯思考回复回退到首个思考 delta，旧记录缺失时不估算。";
	public const string StatisticsSpeedHint = "输出 token / 首个非空正文或思考 delta 到回复结束的时间；不含工具耗时。";
	public const string StatisticsEndToEndHint = "输出 token / 整次模型回复耗时，包含等待首 token 的时间。";
	public const string StatisticsCnyHint = "按 1 USD = 7.2 CNY 估算，与 PiDeck 一致；不是实时汇率或供应商余额。";
	public const string StatisticsToolTimingHint = "各工具执行耗时之和；并行工具会重叠，不代表墙钟耗时。";
	public static string StatisticsSamples(int count) => $"{count} 条样本";
	public static string StatisticsTurnCount(int count) => $"{count} 轮";
}
