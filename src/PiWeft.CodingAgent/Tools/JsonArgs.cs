using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.CodingAgent.Tools;

/// <summary>
/// 宽容的工具参数读取。模型写出的 JSON 解析成 <see cref="JsonElement"/> 支撑的值，
/// 进程内调用方则用 CLR 基础类型构建节点；两种形状都要能读。
/// </summary>
internal static class JsonArgs
{
	public static double? Number(JsonNode? node)
	{
		if (node is null || node.GetValueKind() != JsonValueKind.Number)
		{
			return null;
		}

		var value = node.AsValue();
		if (value.TryGetValue<double>(out var asDouble))
		{
			return asDouble;
		}

		if (value.TryGetValue<long>(out var asLong))
		{
			return asLong;
		}

		if (value.TryGetValue<int>(out var asInt))
		{
			return asInt;
		}

		if (value.TryGetValue<decimal>(out var asDecimal))
		{
			return (double)asDecimal;
		}

		return null;
	}

	public static int? Int(JsonNode? node) => Number(node) is { } number ? (int)number : null;

	public static bool? Bool(JsonNode? node)
	{
		if (node is null || node.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
		{
			return null;
		}

		return node.GetValue<bool>();
	}

	public static string? String(JsonNode? node)
	{
		if (node is null || node.GetValueKind() != JsonValueKind.String)
		{
			return null;
		}

		return node.GetValue<string>();
	}
}
