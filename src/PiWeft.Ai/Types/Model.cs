using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

/// <summary>每百万令牌的价格。</summary>
public record ModelCostRates
{
	public double Input { get; init; }

	public double Output { get; init; }

	public double CacheRead { get; init; }

	public double CacheWrite { get; init; }
}

/// <summary>整次请求适用的价格档位：命中输入阈值中最高的一档作用于整个请求。</summary>
public sealed record ModelCostTier : ModelCostRates
{
	public double InputTokensAbove { get; init; }
}

public sealed record ModelCost : ModelCostRates
{
	public IReadOnlyList<ModelCostTier>? Tiers { get; init; }
}

/// <summary>目录条目的用途，决定哪种操作可以接受它。</summary>
[JsonConverter(typeof(ModelTypeJsonConverter))]
public enum ModelType
{
	Chat,
	Image,
	Classifier,
}

/// <summary>
/// provider 模型目录中的一个条目。默认类型是 chat，因此 pi 目录里没有显式
/// <c>type</c> 的条目都按对话模型处理。
/// </summary>
public sealed record Model
{
	public string Id { get; init; } = string.Empty;

	public string Name { get; init; } = string.Empty;

	public string Api { get; init; } = string.Empty;

	public string Provider { get; init; } = string.Empty;

	public string BaseUrl { get; init; } = string.Empty;

	public ModelType Type { get; init; } = ModelType.Chat;

	private readonly IReadOnlyList<string> _input = ["text"];

	/// <summary>输入模态，例如 <c>text</c> 与 <c>image</c>。</summary>
	public IReadOnlyList<string> Input
	{
		get => _input;
		init => _input = value is { Count: > 0 } ? value : ["text"];
	}

	/// <summary>图片模型的输出模态，始终包含 <c>image</c>。</summary>
	public IReadOnlyList<string>? Output { get; init; }

	private readonly ModelCost _cost = new();

	/// <summary>每百万令牌的价格。永不为 null，因此费用计算无需判空。</summary>
	public ModelCost Cost
	{
		get => _cost;
		init => _cost = value ?? new ModelCost();
	}

	public Dictionary<string, string>? Headers { get; init; }

	public bool Reasoning { get; init; }

	/// <summary>把 pi 的思考级别映射到 provider 或模型私有的取值；<c>null</c> 表示该级别不受支持。</summary>
	public Dictionary<string, string?>? ThinkingLevelMap { get; init; }

	/// <summary>provider 兼容性标志，原样保留，便于客户端读取目录中的声明。</summary>
	public JsonObject? Compat { get; init; }

	/// <summary>提示缓存生命周期（秒），键为 <c>short</c> / <c>long</c>。</summary>
	public Dictionary<string, int>? PromptCache { get; init; }

	/// <summary>按思考级别覆盖的采样参数。</summary>
	public JsonObject? SamplingParams { get; init; }

	/// <summary>目录中的输入约束（图片数量与缩放限制），原样保留。</summary>
	public JsonObject? InputLimits { get; init; }

	public int ContextWindow { get; init; }

	public int MaxTokens { get; init; }

	public bool SupportsImages => Input.Contains("image", StringComparer.Ordinal);
}

/// <summary>pi 的思考级别。注意 <c>off</c> 是模型侧的级别，不是请求侧的级别。</summary>
public static class ThinkingLevels
{
	public const string Off = "off";
	public const string Minimal = "minimal";
	public const string Low = "low";
	public const string Medium = "medium";
	public const string High = "high";
	public const string XHigh = "xhigh";
	public const string Max = "max";

	/// <summary>请求可以要求的级别，从最省到最贵。</summary>
	public static readonly string[] Requestable = [Minimal, Low, Medium, High, XHigh, Max];

	/// <summary>模型可能上报的级别，包含 <see cref="Off"/>。</summary>
	public static readonly string[] Model = [Off, Minimal, Low, Medium, High, XHigh, Max];
}
