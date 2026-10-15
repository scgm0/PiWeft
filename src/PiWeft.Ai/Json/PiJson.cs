using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiWeft.Ai.Json;

/// <summary>
/// pi 各类值使用的标准 <see cref="JsonSerializerOptions"/>，基于源生成的
/// <see cref="PiJsonContext"/> 构建，因此永远不会退回反射路径。
/// </summary>
public static class PiJson
{
	public static JsonSerializerOptions Options { get; } = CreateOptions();

	/// <summary>
	/// 写 <see cref="System.Text.Json.Nodes.JsonNode"/> 用的选项。无参的
	/// <c>ToJsonString()</c> 使用默认编码器；这份选项让中文、引号与 HTML 字符保持可读。
	/// </summary>
	public static JsonSerializerOptions NodeOptions { get; } = new()
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private static JsonSerializerOptions CreateOptions()
	{
		var options = new JsonSerializerOptions(PiJsonContext.Default.Options)
		{
			TypeInfoResolver = PiJsonContext.Default,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		};
		return options;
	}

	public static string SerializeMessage(Message message) =>
		JsonSerializer.Serialize(message, PiJsonContext.Default.Message);

	public static Message DeserializeMessage(string json) =>
		JsonSerializer.Deserialize(json, PiJsonContext.Default.Message)
		?? throw new JsonException("message payload was null");

	public static Message DeserializeMessage(ReadOnlySpan<byte> json) =>
		JsonSerializer.Deserialize(json, PiJsonContext.Default.Message)
		?? throw new JsonException("message payload was null");
}
