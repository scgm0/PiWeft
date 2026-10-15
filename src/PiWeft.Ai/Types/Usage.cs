namespace PiWeft.Ai;

/// <summary>一次助手回复的费用明细（美元）。</summary>
public sealed record UsageCost
{
	public double Input { get; init; }

	public double Output { get; init; }

	public double CacheRead { get; init; }

	public double CacheWrite { get; init; }

	public double Total { get; init; }
}

/// <summary>一次助手回复的令牌统计。</summary>
public sealed record Usage
{
	public long Input { get; init; }

	public long Output { get; init; }

	public long CacheRead { get; init; }

	public long CacheWrite { get; init; }

	/// <summary><see cref="CacheWrite"/> 中采用 1 小时保留期的部分（仅 Anthropic）。</summary>
	public long? CacheWrite1h { get; init; }

	/// <summary>推理/思考令牌；provider 上报时它是 <see cref="Output"/> 的子集。</summary>
	public long? Reasoning { get; init; }

	public long TotalTokens { get; init; }

	private readonly UsageCost _cost = new();

	public UsageCost Cost
	{
		get => _cost;
		init => _cost = value ?? new UsageCost();
	}

	public static Usage Empty => new();

	/// <summary>参与上下文核算的各类令牌之和。</summary>
	public long ContextTokens => Input + CacheRead + CacheWrite;
}
