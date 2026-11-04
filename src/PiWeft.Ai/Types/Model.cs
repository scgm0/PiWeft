using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

public record ModelCostRates
{
	public double Input { get; init; }

	public double Output { get; init; }

	public double CacheRead { get; init; }

	public double CacheWrite { get; init; }
}

public sealed record ModelCostTier : ModelCostRates
{
	public double InputTokensAbove { get; init; }
}

public sealed record ModelCost : ModelCostRates
{
	public IReadOnlyList<ModelCostTier>? Tiers { get; init; }
}

[JsonConverter(typeof(ModelTypeJsonConverter))]
public enum ModelType
{
	Chat,
	Image,
	Classifier,
}

/// provider 模型目录中的一个条目。默认类型是 chat，因此 pi 目录里没有显式
/// <c>type</c> 的条目都按对话模型处理。
public sealed record Model
{
	public string Id { get; init; } = string.Empty;

	public string Name { get; init; } = string.Empty;

	public string Api { get; init; } = string.Empty;

	public string Provider { get; init; } = string.Empty;

	public string BaseUrl { get; init; } = string.Empty;

	public ModelType Type { get; init; } = ModelType.Chat;

	private readonly IReadOnlyList<string> _input = ["text"];

	public IReadOnlyList<string> Input
	{
		get => _input;
		init => _input = value is { Count: > 0 } ? value : ["text"];
	}

	public IReadOnlyList<string>? Output { get; init; }

	private readonly ModelCost _cost = new();

	public ModelCost Cost
	{
		get => _cost;
		init => _cost = value ?? new ModelCost();
	}

	public Dictionary<string, string>? Headers { get; init; }

	/// pi 的 provider 级 <c>authHeader</c>：为 true 时额外发送 <c>Authorization: Bearer &lt;key&gt;</c>。
	/// 不是模型目录字段，provider 配置在加载时写入。
	[JsonIgnore]
	public bool AuthHeader { get; init; }

	public bool Reasoning { get; init; }

	public Dictionary<string, string?>? ThinkingLevelMap { get; init; }

	public JsonObject? Compat { get; init; }

	public Dictionary<string, int>? PromptCache { get; init; }

	public JsonObject? SamplingParams { get; init; }

	public JsonObject? InputLimits { get; init; }

	public int ContextWindow { get; init; }

	public int MaxTokens { get; init; }

	public bool SupportsImages => Input.Contains("image", StringComparer.Ordinal);
}

public static class ThinkingLevels
{
	public const string Off = "off";
	public const string Minimal = "minimal";
	public const string Low = "low";
	public const string Medium = "medium";
	public const string High = "high";
	public const string XHigh = "xhigh";
	public const string Max = "max";

	public static readonly string[] Requestable = [Minimal, Low, Medium, High, XHigh, Max];

	public static readonly string[] Model = [Off, Minimal, Low, Medium, High, XHigh, Max];
}
