using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiWeft.CodingAgent.Settings;

/// <summary>
/// pi 的 <c>httpIdleTimeoutMs</c> 既收毫秒数也收字符串 <c>"disabled"</c>。
/// 源生成的 <see cref="int"/> 转换器只认数字，这里把两种写法都收下：
/// <c>"disabled"</c> 落成 <c>0</c>（关闭），无法识别的值按未设置处理。
/// </summary>
public sealed class TimeoutMsJsonConverter : JsonConverter<int?>
{
	public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		switch (reader.TokenType)
		{
			case JsonTokenType.Null:
				return null;
			case JsonTokenType.Number when reader.TryGetInt32(out var value):
				return value < 0 ? 0 : value;
			case JsonTokenType.String when reader.GetString() is { } text:
				if (text.Equals("disabled", StringComparison.OrdinalIgnoreCase))
				{
					return 0;
				}

				return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
					? Math.Max(parsed, 0)
					: null;
			default:
				// 对象/数组/布尔等形状不认识：跳过去，别让一次坏配置让整个设置文件读不出来。
				reader.Skip();
				return null;
		}
	}

	public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
	{
		if (value is null)
		{
			writer.WriteNullValue();
		}
		else if (value.Value == 0)
		{
			writer.WriteStringValue("disabled");
		}
		else
		{
			writer.WriteNumberValue(value.Value);
		}
	}
}
