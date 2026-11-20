using System.Globalization;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

/// 缓存未命中检测：与 pi 的 <c>cache-stats.ts</c> 同义。
/// 只看最后一条助手消息：上一条请求的提示词本应全部命中缓存，读不回来的部分就是多付的钱。
public static class CacheMissDetector
{
	/// 少于这些 token 当作缓存断点粒度噪声，不值得提示。
	private const long NoiseFloorTokens = 1024;

	/// 提示阈值：token 数或金额任一超过。
	private const long NoticeTokens = 20_000;

	private const double NoticeCost = 0.1;

	/// Anthropic 默认缓存 TTL，用来解释长时间空闲导致的未命中。
	private const long CacheTtlMs = 5 * 60 * 1000;

	/// <param name="MissedTokens">上一条请求里本该命中却按输入价重算的 token 数。</param>
	/// <param name="MissedCost">按实际付费价与缓存读取价之差估算的多付金额。</param>
	/// <param name="IdleMs">距上一条请求的空闲时长。</param>
	/// <param name="ModelChanged">两条回复的模型是否不同。</param>
	public sealed record Miss(long MissedTokens, double MissedCost, long IdleMs, bool ModelChanged)
	{
		/// 是否大到值得在会话里提一句。
		public bool IsNoteworthy => MissedTokens >= NoticeTokens || MissedCost >= NoticeCost;

		/// 与 pi 相同的措辞：<c>Cache miss: 31k tokens re-billed (~$0.12)</c>。
		public string Notice
		{
			get
			{
				var label = ModelChanged
					? "Cache miss after model switch"
					: IdleMs >= CacheTtlMs
						? $"Cache miss after {Math.Max(1, (long)Math.Floor((IdleMs / 60_000.0) + 0.5))}m idle"
						: "Cache miss";
				var cost = MissedCost >= 0.01 ? $" (~${MissedCost.ToString("0.00", CultureInfo.InvariantCulture)})" : string.Empty;
				return $"{label}: {FormatTokens(MissedTokens)} tokens re-billed{cost}";
			}
		}
	}

	/// 取会话里最后一条助手消息相对上一条的未命中。会话太短、供应商从不报缓存、或低于噪声下限时返回 <c>null</c>。
	/// <param name="registry">模型目录：这一轮没读缓存时用它取缓存读取单价，避免把多付的差额算成全额。</param>
	public static Miss? Detect(IReadOnlyList<Message> messages, ModelRegistry? registry = null)
	{
		AssistantMessage? last = null;
		AssistantMessage? previous = null;
		// 供应商是否报过缓存：读到 previous 为止，任意一条请求有过 cacheRead/cacheWrite 就算。
		// 只看 previous 会漏掉「上一次恰好也没报缓存」的连续全未命中。
		var reportedCache = false;
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			if (last is null)
			{
				if (messages[index] is AssistantMessage found)
				{
					last = found;
					reportedCache = HasCacheActivity(found);
				}

				continue;
			}

			// 压缩或分支摘要换了上下文：压缩后的提示词是新内容，不算重算旧内容。
			if (IsContextReset(messages[index]))
			{
				return null;
			}

			if (messages[index] is not AssistantMessage earlier)
			{
				continue;
			}

			if (previous is null)
			{
				previous = earlier;
				reportedCache |= HasCacheActivity(earlier);
				if (reportedCache)
				{
					break;
				}

				continue;
			}

			// previous 自己没报缓存时继续往前找证据：供应商可能只是这一轮刚好没报。
			if (HasCacheActivity(earlier))
			{
				reportedCache = true;
				break;
			}
		}

		return Detect(previous, last, reportedCache, registry);
	}

	/// 两条相邻请求之间的未命中。上一条没有用量、或供应商从未报过缓存活动时返回 <c>null</c>。
	/// <param name="reportedCache">截至上一条请求为止是否见过缓存活动；省略时只看上一条。</param>
	internal static Miss? Detect(
		AssistantMessage? previous,
		AssistantMessage? current,
		bool? reportedCache = null,
		ModelRegistry? registry = null)
	{
		if (previous is null || current is null)
		{
			return null;
		}

		var prompt = PromptTokens(current.Usage);
		if (prompt <= 0)
		{
			return null;
		}

		// 供应商从不报缓存：这里看到的「未命中」不是真的。
		if (current.Usage.CacheRead + current.Usage.CacheWrite == 0 &&
			!(reportedCache ?? HasCacheActivity(previous)))
		{
			return null;
		}

		var previousPrompt = PromptTokens(previous.Usage);
		if (previousPrompt <= 0)
		{
			return null;
		}

		var missed = Math.Min(previousPrompt, prompt) - current.Usage.CacheRead;
		if (missed <= NoiseFloorTokens)
		{
			return null;
		}

		// 多付的钱 = 未命中 token 按实际付费价（输入 / 缓存写）与缓存读价之间的差。
		var paidTokens = current.Usage.Input + current.Usage.CacheWrite;
		var paidPerToken = paidTokens > 0 ? (current.Usage.Cost.Input + current.Usage.Cost.CacheWrite) / paidTokens : 0;
		var readPerToken = current.Usage.CacheRead > 0
			? current.Usage.Cost.CacheRead / current.Usage.CacheRead
			// 全未命中时消息里没有缓存读价，回落目录价；没有目录才按 0 计。
			: (registry?.Get(current.Provider, current.Model)?.Cost.CacheRead ?? 0) / 1_000_000.0;

		return new Miss(
			missed,
			missed * Math.Max(0, paidPerToken - readPerToken),
			Math.Max(0, current.Timestamp - previous.Timestamp),
			!string.Equals(current.Provider, previous.Provider, StringComparison.Ordinal) ||
				!string.Equals(current.Model, previous.Model, StringComparison.Ordinal));
	}

	/// 压缩/分支摘要在投影里是带固定前缀的 user 消息；旧形状的 CustomMessage 一并认。
	private static bool IsContextReset(Message message) => message switch
	{
		CustomMessage custom => custom.CustomRole is "compactionSummary" or "branchSummary",
		UserMessage user => user.Content.Text is { } text &&
			(text.StartsWith(CodingMessages.CompactionSummaryPrefix, StringComparison.Ordinal) ||
				text.StartsWith(CodingMessages.BranchSummaryPrefix, StringComparison.Ordinal)),
		_ => false,
	};

	private static bool HasCacheActivity(AssistantMessage message) =>
		message.Usage.CacheRead + message.Usage.CacheWrite > 0;

	private static long PromptTokens(Usage usage) => usage.Input + usage.CacheRead + usage.CacheWrite;

	/// 千分位缩写：<c>31k</c> / <c>1.2M</c>，与 pi 的 <c>formatTokens</c> 同口径
	/// （小写 k；10000 以上按整数四舍五入，不用银行家舍入）。
	internal static string FormatTokens(long tokens) => tokens switch
	{
		< 1_000 => tokens.ToString(CultureInfo.InvariantCulture),
		< 10_000 => (tokens / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k",
		< 1_000_000 => ((long)Math.Floor((tokens / 1_000.0) + 0.5)).ToString(CultureInfo.InvariantCulture) + "k",
		< 10_000_000 => (tokens / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M",
		_ => ((long)Math.Floor((tokens / 1_000_000.0) + 0.5)).ToString(CultureInfo.InvariantCulture) + "M",
	};
}
