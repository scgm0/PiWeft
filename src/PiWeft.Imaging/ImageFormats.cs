using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Gif;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Webp;

namespace PiWeft.Imaging;

/// 进程内能用到的图片格式。与 <c>read</c> 工具的 MIME 嗅探保持一致：
/// 只有可以安全内联的静态/首帧图片，动画 GIF/WebP 在超限时只会取首帧。
public enum ImageCodec
{
	Png,

	Jpeg,

	Gif,

	Webp,

	Bmp,
}

/// 只读文件头得到的尺寸与 EXIF 方向。<see cref="Width"/>/<see cref="Height"/> 是**存储**尺寸，
/// 方向为 5–8 时显示尺寸要交换宽高（见 <see cref="ImageOrientation"/>）。
public readonly record struct ImageHeader(
	int Width,
	int Height,
	ImageOrientation Orientation,
	ImageCodec Codec,
	string MimeType)
{
	/// 方向 5–8 交换显示宽高；判「是否超限」必须用显示尺寸。
	public bool SwapsDimensions =>
		Orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90
			or ImageOrientation.Transverse or ImageOrientation.Rotate270;
}

/// 格式嗅探与解码。pi 把这件事交给 Photon；这里用 Sdcb.SimdCodecs 的分格式解码器，
/// 只引用需要的五个包（门面包会把 TIFF/J2K/JBIG2 一起拖进来）。
internal static class ImageFormats
{
	private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

	/// 单张图解码后的 RGBA 上限（1 GiB）。正常的内联图片远低于它。
	private const long MaxDecodedBytes = 1L << 30;

	public const string UnsupportedMessage =
		"[Image omitted: could not be converted to a supported inline image format.]";

	public static bool TryDetect(ReadOnlySpan<byte> data, out ImageCodec codec, out string mimeType)
	{
		if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
		{
			codec = ImageCodec.Png;
			mimeType = "image/png";
			return true;
		}

		if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
		{
			codec = ImageCodec.Jpeg;
			mimeType = "image/jpeg";
			return true;
		}

		if (data.Length >= 6 &&
			(data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
		{
			codec = ImageCodec.Gif;
			mimeType = "image/gif";
			return true;
		}

		if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
		{
			codec = ImageCodec.Webp;
			mimeType = "image/webp";
			return true;
		}

		if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
		{
			codec = ImageCodec.Bmp;
			mimeType = "image/bmp";
			return true;
		}

		codec = default;
		mimeType = string.Empty;
		return false;
	}

	public static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
	{
		if (!TryDetect(data, out var codec, out var mime) || !TryReadInfo(codec, data, out var info))
		{
			header = default;
			return false;
		}

		header = new ImageHeader(info.Width, info.Height, info.Orientation, codec, mime);
		return true;
	}

	/// 解成紧密打包的 RGBA8（行优先，无行填充），尺寸是**存储**尺寸；方向由调用方应用。
	public static bool TryDecodeRgba8(
		ReadOnlySpan<byte> data,
		out byte[] rgba,
		out int width,
		out int height,
		out ImageOrientation orientation)
	{
		rgba = [];
		width = 0;
		height = 0;
		orientation = ImageOrientation.Normal;

		if (!TryDetect(data, out var codec, out _) || !TryReadInfo(codec, data, out var info))
		{
			return false;
		}

		// 解压炸弹防护：RGBA 中间图超过 1 GiB 的图（百万像素级的扫描件/伪造头部）
		// 直接按「无法处理」返回，别让一次 read 把进程撑爆。pi 侧 Photon 也是失败返回。
		var length = (long)info.Width * info.Height * 4;
		if (length <= 0 || length > MaxDecodedBytes)
		{
			return false;
		}

		var pixels = new byte[length];
		if (!TryDecodeInto(codec, data, pixels))
		{
			return false;
		}

		rgba = pixels;
		width = info.Width;
		height = info.Height;
		orientation = info.Orientation;
		return true;
	}

	private static bool TryReadInfo(ImageCodec codec, ReadOnlySpan<byte> data, out ImageInfo info)
	{
		try
		{
			info = codec switch
			{
				ImageCodec.Png => PngImageDecoder.TryReadInfo(data, out var png) ? png : default,
				ImageCodec.Jpeg => JpegImageDecoder.TryReadInfo(data, out var jpeg) ? jpeg : default,
				ImageCodec.Gif => GifImageDecoder.TryReadInfo(data, out var gif) ? gif : default,
				ImageCodec.Webp => WebpImageDecoder.TryReadInfo(data, out var webp) ? webp : default,
				ImageCodec.Bmp => BmpImageDecoder.TryReadInfo(data, out var bmp) ? bmp : default,
				_ => default,
			};
			return info.Width > 0 && info.Height > 0;
		}
		catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException)
		{
			info = default;
			return false;
		}
	}

	private static bool TryDecodeInto(ImageCodec codec, ReadOnlySpan<byte> data, byte[] pixels)
	{
		try
		{
			return codec switch
			{
				ImageCodec.Png => PngImageDecoder.TryDecodeIntoRgba8(data, pixels),
				ImageCodec.Jpeg => JpegImageDecoder.TryDecodeIntoRgba8(data, pixels),
				ImageCodec.Gif => GifImageDecoder.TryDecodeIntoRgba8(data, pixels),
				ImageCodec.Webp => WebpImageDecoder.TryDecodeIntoRgba8(data, pixels),
				ImageCodec.Bmp => BmpImageDecoder.TryDecodeIntoRgba8(data, pixels),
				_ => false,
			};
		}
		catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException)
		{
			// 损坏或截断的图片：上层改写成「无法转换」的提示，不让工具失败。
			return false;
		}
	}
}
