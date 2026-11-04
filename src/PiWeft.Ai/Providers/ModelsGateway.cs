using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PiWeft.Ai.Providers;

internal static class JsonPayload
{
	private static readonly JsonWriterOptions WriterOptions = new()
	{
		Indented = false,
		SkipValidation = true,

		// 与 JS 的 JSON.stringify 保持一致：请求体不做 HTML 相关转义。
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

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

public interface IApiProvider
{
	string Api { get; }

	AssistantMessageEventStream Stream(Model model, Context context, StreamOptions options);
}

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

	public AssistantMessageEventStream Stream(string modelId, Context context, StreamOptions? options = null)
	{
		var model = Registry.Resolve(modelId)
			?? throw new InvalidOperationException($"unknown model '{modelId}'");

		return Stream(model, context, options);
	}

	public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
		GetProvider(model.Api).Stream(model, context, options ?? new StreamOptions());
}
