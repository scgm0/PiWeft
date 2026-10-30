namespace PiWeft.Desktop;

/// <summary>
/// 会话进行态的文案：输入区上方那条状态栏，以及时间线里等首 token 的占位行。
///
/// 写这里的原则和 PiDeck 的 <c>agent.loading.*</c> 一样：状态栏只能说后台已经确定的事实。
/// 唯一观测不到子阶段的窗口是「消息已发出、屏幕上还没有本回合的任何输出」，
/// 只有它允许多句轮播；其余状态一律单句，否则每两秒换一句猜测，读起来像在瞎猜。
/// </summary>
public static partial class Strings
{
	/// <summary>
	/// 预热阶段（已发出、还没有任何可见输出）的轮播短语。三句都是这段时间真实会经过的事：
	/// 装配环境与会话 → 向供应商发请求 → 等第一个 token 落地。
	/// </summary>
	public static IReadOnlyList<string> RespondingStartingPhrases { get; } =
	[
		"正在准备会话",
		"正在连接模型",
		"等待首个回复",
	];

	/// <summary>回合内的空窗（工具已收口、下一次推流还没开始）：一句事实，不猜在等什么。</summary>
	public const string RespondingWaiting = "正在处理";

	public const string RespondingExecuting = "正在执行命令";
	public const string RespondingThinking = "正在思考";
	public const string RespondingGenerating = "正在生成回复";
	public const string RespondingCompacting = "正在压缩上下文";

	/// <summary>
	/// 本轮已用时长：静默期唯一能证明「没有卡死」的量。秒为单位，整分才带秒位。
	/// </summary>
	public static string RespondingElapsed(double milliseconds)
	{
		var seconds = (long)Math.Max(0, milliseconds / 1000);
		return seconds < 60
			? $"本轮已 {seconds} 秒"
			: $"本轮已 {seconds / 60} 分 {seconds % 60:00} 秒";
	}

	// 时间线里的等待占位行：首 token 到达前，在本回合该出现回复的位置上先落一个「有位子在等」。
	public const string PendingReplyTitle = "正在等第一句话";
	public const string PendingReplyHint = "首个 token 一到，回复就从这一行开始写。";

	/// <summary>状态条右侧功能提示轮播的开关名称与说明（设置 → 外观）。</summary>
	public const string RespondingTipsSetting = "等待时轮播功能提示";

	public const string RespondingTipsSettingDesc =
		"会话进行期间，在状态条右侧一行小字轮播本应用的能力入口（命令面板、排队发送、@ 引用等）。" +
		"关掉只影响这一行提示，不影响里面提到的任何功能。";

	/// <summary>提示正文里的 <c>{shortcut}</c> 占位符，替换成该动作当前生效的键位。</summary>
	public const string RespondingTipShortcutToken = "{shortcut}";
}
