using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiWeft.Ai.Json;

/// pi 各类值使用的标准 <see cref="JsonSerializerOptions"/>，基于源生成的
/// <see cref="PiJsonContext"/> 构建，因此永远不会退回反射路径。
public static class PiJson
{
	public static JsonSerializerOptions Options { get; } = CreateOptions();

	/// 写 <see cref="System.Text.Json.Nodes.JsonNode"/> 用的选项。无参的
	/// <c>ToJsonString()</c> 使用默认编码器；这份选项让中文、引号与 HTML 字符保持可读。
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

	/// Windows PowerShell 5.1 等工具会在文件开头写 UTF-8 BOM；JSON 解析器不把它当空白。
	public static string StripBom(string text) => text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

	public static string SerializeMessage(Message message) =>
		JsonSerializer.Serialize(message, PiJsonContext.Default.Message);

	public static JsonNode? SerializeModelNode(Model model) =>
		JsonSerializer.SerializeToNode(model, PiJsonContext.Default.Model);

	public static Message DeserializeMessage(string json) =>
		JsonSerializer.Deserialize(json, PiJsonContext.Default.Message)
		?? throw new JsonException("message payload was null");

	public static Message DeserializeMessage(ReadOnlySpan<byte> json) =>
		JsonSerializer.Deserialize(json, PiJsonContext.Default.Message)
		?? throw new JsonException("message payload was null");

	/// 内容块列表的源生成序列化；扩展事件与工具结果都走这里，避免 AOT 反射警告。
	public static JsonNode? SerializeBlocks(IReadOnlyList<ContentBlock> blocks) =>
		JsonSerializer.SerializeToNode(blocks.ToList(), PiJsonContext.Default.ListContentBlock);

	/// 读回扩展返回的内容块列表；不是数组时调用方应视为未修改。
	public static List<ContentBlock>? DeserializeBlocks(string json) =>
		JsonSerializer.Deserialize(json, PiJsonContext.Default.ListContentBlock);
}
