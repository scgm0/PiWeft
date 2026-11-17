namespace PiWeft.Desktop;

public static partial class Strings
{
	// ── 使用统计：应用用量 ────────────────────────────────────────────────
	public const string SettingsUsageStats = "使用统计";
	public const string SettingsGroupData = "数据与统计";
	public const string UsageStatsAppUsage = "应用用量";
	public const string UsageStatsSourceHint = "来自本机会话历史。";
	public const string UsageStatsTruncatedHint = "扫描达到上限，统计只含部分会话。";
	public const string UsageStatsLifetimeTotal = "累计 Token 数";
	public const string UsageStatsLifetimePeak = "峰值 Token 数";
	public const string UsageStatsLongestChat = "最长聊天时长";
	public const string UsageStatsCurrentStreak = "当前连续天数";
	public const string UsageStatsLongestStreak = "最长连续天数";
	public const string UsageStatsRangeTitle = "时间范围";
	public const string UsageStatsRange7d = "近 7 日";
	public const string UsageStatsRange30d = "近 30 日";
	public const string UsageStatsRangeAll = "全部";
	public const string UsageStatsAllTime = "全部时间";
	public const string UsageStatsEmptyTitle = "还没有可展示的数据";
	public const string UsageStatsEmptyDescription = "当前区间暂无可展示的用量数据。";
	public const string UsageStatsLoadingTitle = "正在统计中";
	public const string UsageStatsLoadingDescription = "正在读取本地会话历史，可能需要一点时间。";
	public const string UsageStatsError = "无法读取使用统计。请稍后重试。";
	public const string UsageStatsDailyChart = "每日 Token 趋势";
	public const string UsageStatsMonthlyChart = "每月 Token 趋势";
	public const string UsageStatsModelChart = "模型用量";
	public const string UsageStatsDetails = "用量明细";
	public const string UsageStatsTotal = "合计 Token";
	public const string UsageStatsUnknownModel = "未知模型";
	public const string UsageStatsNoModels = "区间内没有模型用量。";
	public const string UsageStatsInput = "输入 Token";
	public const string UsageStatsOutput = "输出 Token";
	public const string UsageStatsCacheRead = "缓存读取";
	public const string UsageStatsCacheWrite = "缓存写入";
	public const string UsageStatsCacheHit = "缓存命中率";
	public const string UsageStatsReplies = "模型回复";
	public const string UsageStatsActiveDays = "活跃天数";
	public const string UsageStatsTotalHint = "整个本地会话历史累计；达到扫描上限时只含部分会话。";
	public const string UsageStatsPeakHint = "按自然日统计的最高 Token 用量。";
	public const string UsageStatsLongestChatHint = "单个会话内助手回复时长之和；没有时长记录时退回首末时间跨度。";
	public const string UsageStatsStreakHint = "今天还没使用不算中断，从昨天继续算。";
	public const string UsageStatsCacheHitHint = "缓存读取 ÷（输入 + 缓存读取 + 缓存写入）。";
	public const string UsageStatsRangeTotalHint = "供应商回报的总量；与输入、输出、缓存之和不一致时取较大值，避免少算。";
	public const string UsageStatsCostHint = "会话记录的 cost；缺失时按当前模型价格估算。";
	public const string UsageStatsTokenUnit = "tokens";
	public const string UsageStatsTooltip = "统计口径：助手回复里的 usage 加上会话级用量条目；压缩/分支摘要与工具结果的 usage 不计，和会话统计一致。轮次只计用户亲手消息；按本机时区归到自然日。";

	public static string UsageStatsSessionSummary(int sessions, int turns) => $"共 {sessions} 个会话，{turns} 轮。";

	public static string UsageStatsLastRefresh(string time) => $"最近刷新时间：{time}";

	public static string UsageStatsDailyChartHint(int days) => $"按天展示 Token 用量变化，共 {days} 天。";

	public static string UsageStatsMonthlyChartHint(int months) => $"按月展示 Token 用量变化，共 {months} 个月。";

	public static string UsageStatsModelUsage(string tokens, long replies) => $"{tokens} · {replies} 条回复";

	public static string UsageStatsHeatmapCell(string date, string tokens) => $"{date} · {tokens} tokens";
}
