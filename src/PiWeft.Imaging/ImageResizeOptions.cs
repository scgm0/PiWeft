using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Imaging;

/// 图片进入模型上下文的缩放/转码参数，与 pi 的 <c>ModelImageResizeOptions</c> 同义。
/// <para>
/// <see cref="MaxBytes"/> 是 **base64 编码之后**的长度，不是原始字节数：pi 的默认 4.5 MiB
/// 就是按「内联图片 base64 载荷」算的（Anthropic 的图片上限是 5 MB）。
/// </para>
public sealed record ImageResizeOptions
{
	public const int DefaultMaxWidth = 2000;

	public const int DefaultMaxHeight = 2000;

	/// 4.5 MiB of base64 payload. 留在 Anthropic 5 MB 上限之下。
	public const long DefaultMaxBytes = 4_718_592;

	public const int DefaultJpegQuality = 80;

	/// 模型目录没有声明 <c>inputLimits.images.resize</c> 时使用的档位。
	public static ImageResizeOptions Default { get; } = new();

	public int MaxWidth { get; init; } = DefaultMaxWidth;

	public int MaxHeight { get; init; } = DefaultMaxHeight;

	public long MaxBytes { get; init; } = DefaultMaxBytes;

	public int JpegQuality { get; init; } = DefaultJpegQuality;

	/// 读模型条目的 <c>inputLimits.images.resize</c>；缺字段或类型不对的字段回落到默认值
	/// （与 pi 的 <c>??</c> 链同义，模型目录不因为一个坏字段就整张图不处理）。
	public static ImageResizeOptions FromInputLimits(JsonObject? inputLimits)
	{
		if (inputLimits?["images"] is not JsonObject images || images["resize"] is not JsonObject resize)
		{
			return Default;
		}

		return new ImageResizeOptions
		{
			MaxWidth = (int)(PositiveInt(resize["maxWidth"]) ?? DefaultMaxWidth),
			MaxHeight = (int)(PositiveInt(resize["maxHeight"]) ?? DefaultMaxHeight),
			MaxBytes = PositiveInt(resize["maxBytes"]) ?? DefaultMaxBytes,
			JpegQuality = (int)Math.Clamp(PositiveInt(resize["jpegQuality"]) ?? DefaultJpegQuality, 1, 100),
		};
	}

	/// JSON 数字既可能是解析出来的 <see cref="JsonElement"/>，也可能是代码里建的 int/long 盒子。
	private static long? PositiveInt(JsonNode? node)
	{
		if (node is not JsonValue value)
		{
			return null;
		}

		long number;
		if (value.TryGetValue<JsonElement>(out var element))
		{
			if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out number))
			{
				return null;
			}
		}
		else if (value.TryGetValue<long>(out var int64))
		{
			number = int64;
		}
		else if (value.TryGetValue<int>(out var int32))
		{
			number = int32;
		}
		else if (value.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) < 9e15)
		{
			number = (long)real;
		}
		else
		{
			return null;
		}

		return number > 0 ? number : null;
	}
}
