namespace PiWeft.Ai;

public sealed record UsageCost
{
	public double Input { get; init; }

	public double Output { get; init; }

	public double CacheRead { get; init; }

	public double CacheWrite { get; init; }

	public double Total { get; init; }

	/// 未建模的顶层字段，重写会话文件时原样输出（pi 会往里加东西，例如
	/// <c>totalFromProvider</c>）。丢弃它等于在 <c>fork</c>/导入/编辑重发后改写用户的历史。
	public System.Text.Json.Nodes.JsonObject? Extra { get; init; }
}

public sealed record Usage
{
	public long Input { get; init; }

	public long Output { get; init; }

	public long CacheRead { get; init; }

	public long CacheWrite { get; init; }

	public long? CacheWrite1h { get; init; }

	public long? Reasoning { get; init; }

	public long TotalTokens { get; init; }

	private readonly UsageCost _cost = new();

	public UsageCost Cost
	{
		get => _cost;
		init => _cost = value ?? new UsageCost();
	}

	public System.Text.Json.Nodes.JsonObject? Extra { get; init; }

	public static Usage Empty => new();

	public long ContextTokens => Input + CacheRead + CacheWrite;
}
