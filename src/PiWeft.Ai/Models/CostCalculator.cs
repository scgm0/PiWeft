namespace PiWeft.Ai;

/// <summary>
/// 对应 pi 的 <c>calculateCost</c>：先套用命中输入阈值中最高的整请求价格档位，
/// 再对 Anthropic 的 1 小时缓存写入按基础输入价的 2 倍计费。会就地写入并返回 <paramref name="usage"/> 的费用。
/// </summary>
public static class CostCalculator
{
	public static Usage Calculate(Model model, Usage usage)
	{
		var inputTokens = usage.Input + usage.CacheRead + usage.CacheWrite;

		var cost = model.Cost;
		ModelCostRates rates = cost;
		var matchedThreshold = double.NegativeInfinity;
		foreach (var tier in cost.Tiers ?? [])
		{
			if (inputTokens > tier.InputTokensAbove && tier.InputTokensAbove > matchedThreshold)
			{
				rates = tier;
				matchedThreshold = tier.InputTokensAbove;
			}
		}

		var longWrite = usage.CacheWrite1h ?? 0;
		var shortWrite = usage.CacheWrite - longWrite;

		var input = rates.Input / 1_000_000.0 * usage.Input;
		var output = rates.Output / 1_000_000.0 * usage.Output;
		var cacheRead = rates.CacheRead / 1_000_000.0 * usage.CacheRead;
		var cacheWrite = (rates.CacheWrite * shortWrite + rates.Input * 2 * longWrite) / 1_000_000.0;

		return usage with
		{
			Cost = new UsageCost
			{
				Input = input,
				Output = output,
				CacheRead = cacheRead,
				CacheWrite = cacheWrite,
				Total = input + output + cacheRead + cacheWrite,
			},
		};
	}
}

/// <summary>对应 pi 的思考级别解析辅助方法。</summary>
public static class ThinkingLevelResolver
{
	/// <summary>模型支持的级别。非推理模型只支持 <c>off</c>。</summary>
	public static IReadOnlyList<string> GetSupported(Model model)
	{
		if (!model.Reasoning)
		{
			return [ThinkingLevels.Off];
		}

		var supported = new List<string>();
		foreach (var level in ThinkingLevels.Model)
		{
			string? mapped = null;
			var hasMapping = model.ThinkingLevelMap?.TryGetValue(level, out mapped) == true;

			if (hasMapping && mapped is null)
			{
				continue;
			}

			// xhigh/max 需要显式开启：必须有映射值才会启用。
			if ((level == ThinkingLevels.XHigh || level == ThinkingLevels.Max) && !hasMapping)
			{
				continue;
			}

			supported.Add(level);
		}

		return supported;
	}

	/// <summary>把请求的级别收敛到最近的支持项。</summary>
	public static string Clamp(Model model, string level)
	{
		var available = GetSupported(model);
		if (available.Contains(level, StringComparer.Ordinal))
		{
			return level;
		}

		var requestedIndex = Array.IndexOf(ThinkingLevels.Model, level);
		if (requestedIndex == -1)
		{
			return available.Count > 0 ? available[0] : ThinkingLevels.Off;
		}

		for (var i = requestedIndex; i < ThinkingLevels.Model.Length; i++)
		{
			if (available.Contains(ThinkingLevels.Model[i], StringComparer.Ordinal))
			{
				return ThinkingLevels.Model[i];
			}
		}

		for (var i = requestedIndex - 1; i >= 0; i--)
		{
			if (available.Contains(ThinkingLevels.Model[i], StringComparer.Ordinal))
			{
				return ThinkingLevels.Model[i];
			}
		}

		return available.Count > 0 ? available[0] : ThinkingLevels.Off;
	}
}
