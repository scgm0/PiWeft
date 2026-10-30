using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Ai;

/// <summary>可供模型调用的工具声明。</summary>
public sealed record Tool
{
	public string Name { get; init; } = string.Empty;

	public string Description { get; init; } = string.Empty;

	/// <summary>工具参数的 JSON Schema。</summary>
	public JsonObject Parameters { get; init; } = new();

	/// <summary>provider 侧的受限采样配置（<c>false</c> 或一个对象），原样保留。</summary>
	public JsonNode? ConstrainedSampling { get; init; }
}

public sealed record ToolReference
{
	public string Name { get; init; } = string.Empty;
}

/// <summary><c>Models.StreamAsync</c> 接受的原始请求输入，使用前会归一化成会话记录。</summary>
public sealed record Context
{
	public string? SystemPrompt { get; init; }

	public IReadOnlyList<Message> Messages { get; init; } = [];

	public IReadOnlyList<Tool>? Tools { get; init; }
}

/// <summary>发出 provider 请求后，调用方观察到的 HTTP 响应。</summary>
public sealed record ProviderResponse
{
	public int Status { get; init; }

	public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>各 provider 请求共用的鉴权、传输与生命周期回调。</summary>
public record ProviderRequestOptions
{
	public CancellationToken CancellationToken { get; init; }

	/// <summary>显式 API key；不提供时 provider 会从环境变量解析。</summary>
	public string? ApiKey { get; init; }

	/// <summary>HTTP 请求超时，单位毫秒。</summary>
	public int? TimeoutMs { get; init; }

	/// <summary>调用方请求头，覆盖在 provider 默认值之上；值为 <c>null</c> 表示删除该默认头。</summary>
	public IReadOnlyDictionary<string, string?>? Headers { get; init; }

	/// <summary>在请求发出前检查或替换 payload；返回 <c>null</c> 表示不改动。</summary>
	public Func<JsonNode, Model, JsonNode?>? OnPayload { get; init; }

	/// <summary>收到 HTTP 响应、尚未读取响应体时调用。</summary>
	public Action<ProviderResponse, Model>? OnResponse { get; init; }

	/// <summary>归一化之前，每个解析出的 provider 流事件都会回调一次。</summary>
	public Action<JsonNode, Model>? OnProviderStreamEvent { get; init; }

	/// <summary>覆盖 HTTP 处理器（用于测试或代理）。</summary>
	public HttpMessageHandler? HttpHandler { get; init; }

	/// <summary>
	/// Raises <see cref="OnProviderStreamEvent"/> only when someone listens. The callback argument
	/// must not be evaluated unconditionally: parsing a JsonNode for every streamed delta is one of
	/// the largest avoidable allocations in a normal request.
	/// </summary>
	internal void NotifyStreamEvent(string payloadJson, Model model)
	{
		if (OnProviderStreamEvent is { } callback)
		{
			callback(JsonNode.Parse(payloadJson)!, model);
		}
	}

	/// <inheritdoc cref="NotifyStreamEvent(string, Model)"/>
	internal void NotifyStreamEvent(JsonElement element, Model model)
	{
		if (OnProviderStreamEvent is { } callback)
		{
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

	/// <summary>任意采样参数，在具名字段之后合并进请求体。</summary>
	public JsonObject? SamplingParams { get; init; }

	/// <summary>取值 <c>none</c>、<c>short</c> 或 <c>long</c>，默认 <c>short</c>。</summary>
	public string? CacheRetention { get; init; }

	/// <summary>会话标识，供支持会话级缓存的 provider 使用。</summary>
	public string? SessionId { get; init; }

	/// <summary>偏好的传输方式（<c>sse</c>、<c>websocket</c>、<c>websocket-cached</c>、<c>auto</c>）。</summary>
	public string? Transport { get; init; }

	/// <summary>
	/// 响应体空闲超时（毫秒）：连续这么久没有收到任何字节就失败。
	/// <see langword="null"/> 或 <c>0</c> 表示不设限。长思考由这个计时器而不是总时长兜底 ——
	/// 总时长会把「慢但仍在产出」的合法长流切断。
	/// </summary>
	public int? IdleTimeoutMs { get; init; }

	/// <summary>请求的 pi 思考级别（<c>off</c>…<c>max</c>），provider 会按模型支持范围收敛。</summary>
	public string? ThinkingLevel { get; init; }

	public JsonObject? Metadata { get; init; }
}

public static class CacheRetentions
{
	public const string None = "none";
	public const string Short = "short";
	public const string Long = "long";
}
