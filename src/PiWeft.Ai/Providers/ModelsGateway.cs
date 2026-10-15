using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PiWeft.Ai.Providers;

/// <summary>用 <see cref="Utf8JsonWriter"/> 构建 provider 请求 payload，不使用反射。</summary>
internal static class JsonPayload
{
	private static readonly JsonWriterOptions WriterOptions = new()
	{
		Indented = false,
		SkipValidation = true,

		// 与 JS 的 JSON.stringify 保持一致：请求体不做 HTML 相关转义。
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>把 payload 写进缓冲区并返回其 UTF-8 字节。</summary>
	public static byte[] Write(Action<Utf8JsonWriter> write)
	{
		var buffer = new ArrayBufferWriter<byte>(4096);
		using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
		{
			write(writer);
			writer.Flush();
		}

		return buffer.WrittenSpan.ToArray();
	}
}

/// <summary>一种 provider API 的实现，按 <see cref="Api"/> 索引。</summary>
public interface IApiProvider
{
	/// <summary>pi 的 api id，例如 <c>anthropic-messages</c>。</summary>
	string Api { get; }

	/// <summary>发起一次流式请求；返回的流由该 provider 负责完成。</summary>
	AssistantMessageEventStream Stream(Model model, Context context, StreamOptions options);
}

/// <summary>为模型解析出 provider，并对外暴露流式入口。</summary>
public sealed class ModelsGateway
{
	private readonly Dictionary<string, IApiProvider> _providers = new(StringComparer.Ordinal);

	public ModelsGateway(ModelRegistry registry)
	{
		Registry = registry;
	}

	public ModelRegistry Registry { get; }

	public void Register(IApiProvider provider)
	{
		ArgumentNullException.ThrowIfNull(provider);
		_providers[provider.Api] = provider;
	}

	public bool TryGetProvider(string api, [NotNullWhen(true)] out IApiProvider? provider) =>
		_providers.TryGetValue(api, out provider);

	public IApiProvider GetProvider(string api) =>
		_providers.TryGetValue(api, out var provider)
			? provider
			: throw new InvalidOperationException($"no provider registered for api '{api}'");

	/// <summary>为目录中的模型 id 流式返回响应（<c>provider/model</c> 或唯一的模型 id）。</summary>
	public AssistantMessageEventStream Stream(string modelId, Context context, StreamOptions? options = null)
	{
		var model = Registry.Resolve(modelId)
			?? throw new InvalidOperationException($"unknown model '{modelId}'");

		return Stream(model, context, options);
	}

	public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
		GetProvider(model.Api).Stream(model, context, options ?? new StreamOptions());
}
