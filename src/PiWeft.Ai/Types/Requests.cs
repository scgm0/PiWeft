using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Ai;

public sealed record Tool
{
	public string Name { get; init; } = string.Empty;

	public string Description { get; init; } = string.Empty;

	public JsonObject Parameters { get; init; } = new();

	public JsonNode? ConstrainedSampling { get; init; }
}

public sealed record ToolReference
{
	public string Name { get; init; } = string.Empty;
}

public sealed record Context
{
	public string? SystemPrompt { get; init; }

	public IReadOnlyList<Message> Messages { get; init; } = [];

	public IReadOnlyList<Tool>? Tools { get; init; }
}

public sealed record ProviderResponse
{
	public int Status { get; init; }

	public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

public record ProviderRequestOptions
{
	public CancellationToken CancellationToken { get; init; }

	public string? ApiKey { get; init; }

	public int? TimeoutMs { get; init; }

	public IReadOnlyDictionary<string, string?>? Headers { get; init; }

	public Func<JsonNode, Model, JsonNode?>? OnPayload { get; init; }

	public Action<ProviderResponse, Model>? OnResponse { get; init; }

	public Action<JsonNode, Model>? OnProviderStreamEvent { get; init; }

	public HttpMessageHandler? HttpHandler { get; init; }

	/// Raises <see cref="OnProviderStreamEvent"/> only when someone listens. The callback argument
	/// must not be evaluated unconditionally: parsing a JsonNode for every streamed delta is one of
	/// the largest avoidable allocations in a normal request.
	/// 用请求体字节直接回调，避免为观察者再分配一份 UTF-16 字符串。
	internal void NotifyStreamEvent(ReadOnlySpan<byte> payload, Model model)
	{
		if (OnProviderStreamEvent is { } callback)
		{
			callback(JsonNode.Parse(payload)!, model);
		}
	}

	/// <inheritdoc cref="NotifyStreamEvent(ReadOnlySpan{byte}, Model)"/>
	internal void NotifyStreamEvent(JsonElement element, Model model)
	{
		if (OnProviderStreamEvent is { } callback)
		{
			// 只有扩展观察时才付出这次分配。
			callback(JsonNode.Parse(element.GetRawText())!, model);
		}
	}
}

public sealed record StreamOptions : ProviderRequestOptions
{
	public StreamOptions()
	{
	}
	public double? Temperature { get; init; }

	public int? MaxTokens { get; init; }

	public JsonObject? SamplingParams { get; init; }

	public string? CacheRetention { get; init; }

	public string? SessionId { get; init; }

	public string? Transport { get; init; }

	/// 响应体空闲超时（毫秒）：连续这么久收不到字节就失败。
	/// <see langword="null"/> 或 <c>0</c> 表示不设限。长思考靠这个计时器兜底，不用总时长，
	/// 否则「慢但仍在产出」的合法长流会被切断。
	public int? IdleTimeoutMs { get; init; }

	public string? ThinkingLevel { get; init; }

	public JsonObject? Metadata { get; init; }

	/// settings.thinkingBudgets：minimal / low / medium / high 的自定义 token 预算。
	public IReadOnlyDictionary<string, int>? ThinkingBudgets { get; init; }
}

public static class CacheRetentions
{
	public const string None = "none";
	public const string Short = "short";
	public const string Long = "long";
}
