using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Cli;

/// <summary>
/// CLI 写出的 JSONL 使用的序列化设置。默认写入深度（1000）不足以支撑长会话上的
/// <c>get_tree</c>：会话树每条条目嵌套一层，千条会话就是上千层。pi 没有这种限制，
/// 而客户端必须能读回它发来的内容。
/// </summary>
internal static class CliJson
{
	public static readonly JsonSerializerOptions Options = new()
	{
		MaxDepth = 65_536,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static string Serialize(JsonNode node) => node.ToJsonString(Options);
}
