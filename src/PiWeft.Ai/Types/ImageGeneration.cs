using System.Text.Json.Nodes;

namespace PiWeft.Ai;

/// <summary>生图请求的输入，对应 pi 的 <c>ImagesContext</c>。</summary>
public sealed record ImageGenerationRequest
{
	public IReadOnlyList<ContentBlock> Input { get; init; } = [];
}

/// <summary>一次生图的结果。失败也通过结果返回，而不是抛异常。</summary>
public sealed record ImageGenerationResult
{
	public string Api { get; init; } = string.Empty;

	public string Provider { get; init; } = string.Empty;

	public string Model { get; init; } = string.Empty;

	public IReadOnlyList<ContentBlock> Output { get; init; } = [];

	public Usage Usage { get; init; } = new();

	public StopReason StopReason { get; init; } = StopReason.Stop;

	public string? ErrorMessage { get; init; }

	public string? ResponseId { get; init; }

	public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>一种生图 API 的实现，按 <see cref="Api"/> 索引。</summary>
public interface IImageApiProvider
{
	string Api { get; }

	Task<ImageGenerationResult> GenerateAsync(
		Model model,
		ImageGenerationRequest request,
		ProviderRequestOptions options,
		CancellationToken cancellationToken = default);
}

/// <summary>按模型声明的 <c>api</c> 分发到生图实现；与聊天网关分开，避免污染流式接口。</summary>
public sealed class ImagesGateway
{
	private readonly Dictionary<string, IImageApiProvider> _providers = new(StringComparer.Ordinal);

	public void Register(IImageApiProvider provider)
	{
		ArgumentNullException.ThrowIfNull(provider);
		_providers[provider.Api] = provider;
	}

	public Task<ImageGenerationResult> GenerateAsync(
		Model model,
		ImageGenerationRequest request,
		ProviderRequestOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(model);
		if (!_providers.TryGetValue(model.Api, out var provider))
		{
			return Task.FromResult(new ImageGenerationResult
			{
				Api = model.Api,
				Provider = model.Provider,
				Model = model.Id,
				StopReason = StopReason.Error,
				ErrorMessage = $"No image provider registered for api: {model.Api}",
			});
		}

		return provider.GenerateAsync(model, request, options ?? new ProviderRequestOptions(), cancellationToken);
	}
}

/// <summary>把 JSON 节点安全地转成文本/数字/数组，供各 provider 复用。</summary>
internal static class ImageJson
{
	public static string? String(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	public static long Long(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

	public static string? ImageDataUrl(JsonNode? image)
	{
		var url = String(image) ?? String(image?["image_url"]) ?? String(image?["image_url"]?["url"]);
		if (url is null || !url.StartsWith("data:", StringComparison.Ordinal))
		{
			return null;
		}

		var comma = url.IndexOf(',');
		return comma > 0 ? url[(comma + 1)..] : null;
	}

	public static string? ImageMimeType(JsonNode? image)
	{
		var url = String(image) ?? String(image?["image_url"]) ?? String(image?["image_url"]?["url"]);
		if (url is null || !url.StartsWith("data:", StringComparison.Ordinal))
		{
			return null;
		}

		var semicolon = url.IndexOf(';', StringComparison.Ordinal);
		return semicolon > 5 ? url[5..semicolon] : null;
	}
}
