using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiWeft.Ai.Json;

public sealed record ModelCatalogFile
{
	public string? SchemaVersion { get; init; }

	public string? GeneratedAt { get; init; }

	public IReadOnlyDictionary<string, IReadOnlyList<Model>> Providers { get; init; } =
		new Dictionary<string, IReadOnlyList<Model>>();
}

internal sealed class ModelTypeJsonConverter : JsonConverter<ModelType>
{
	public override ModelType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
		reader.GetString() switch
		{
			"image" => ModelType.Image,
			"classifier" => ModelType.Classifier,
			_ => ModelType.Chat,
		};

	public override void Write(Utf8JsonWriter writer, ModelType value, JsonSerializerOptions options) =>
		writer.WriteStringValue(value switch
		{
			ModelType.Image => "image",
			ModelType.Classifier => "classifier",
			_ => "chat",
		});
}

/// 源生成的序列化上下文。静态构造函数把生成的选项换成一份使用
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> 的副本，使经由本上下文的序列化结果与
/// JavaScript 的 <c>JSON.stringify</c> 一致（不对 <c>&lt;</c>、<c>&gt;</c>、<c>&amp;</c>、<c>'</c> 做 HTML 转义）。
[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	WriteIndented = false)]
[JsonSerializable(typeof(ModelCatalogFile))]
[JsonSerializable(typeof(Dictionary<string, List<Model>>))]
[JsonSerializable(typeof(Model))]
[JsonSerializable(typeof(Message))]
internal partial class PiJsonContext : JsonSerializerContext
{
	static PiJsonContext()
	{
		Default = new PiJsonContext(CreateOptions(Default));
	}

	private static JsonSerializerOptions CreateOptions(PiJsonContext defaultContext) =>
		new(defaultContext.GeneratedSerializerOptions!)
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};
}
