namespace PiWeft.Ai;

/// Responses 风格的用量口径：<c>input_tokens</c> 已经包含缓存读与缓存写，
/// 计价前必须拆出来，否则缓存 token 会按输入价重复收费。
internal static class UsageMath
{
	/// <summary>把缓存读写从总输入里拆出来，并夹取到 <paramref name="totalInput"/> 以内。</summary>
	public static (long Input, long CacheRead, long CacheWrite) SplitCacheTokens(
		long totalInput,
		long cachedTokens,
		long cacheWriteTokens)
	{
		totalInput = Math.Max(0, totalInput);
		var cacheRead = Math.Clamp(cachedTokens, 0, totalInput);
		var cacheWrite = Math.Clamp(cacheWriteTokens, 0, totalInput - cacheRead);
		return (totalInput - cacheRead - cacheWrite, cacheRead, cacheWrite);
	}
}
