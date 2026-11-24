using System.Buffers.Binary;
using System.IO.Compression;
using Aprillz.MewUI.Resources;

namespace PiWeft.Desktop;

/// 交给 MewUI 之前的 PNG 兼容处理。
/// 框架的 <c>PngDecoder</c> 只解 8 位（以及 1/2/4 位灰度、索引色），不支持 16 位与 Adam7。
/// 解码失败后 <c>Image</c> 每帧重试并抛异常，UI 会被拖死；这里把 16 位降成 8 位重编码，
/// 其余解不了的组合在入口拒绝，由调用方显示占位。
internal static class PngCompat
{
	// 与框架解码器一致的上限：伪造头部的图不能先吃掉内存。
	private const int MaxDimension = 32768;
	private const long MaxPixels = 64L * 1024 * 1024;
	// 16 位转换要同时持有解压后的行数据与重编码结果，聊天截图远低于这个规模。
	private const long MaxConversionPixels = 16L * 1024 * 1024;
	private const long MaxIntermediateBytes = 1L << 30;

	private readonly record struct Header(int Width, int Height, byte BitDepth, byte ColorType, byte Interlace);

	/// 返回 false 表示框架解不了、这里也转换不了。<paramref name="prepared"/> 总是可交给
	/// <c>ImageSource.FromBytes</c>；非 PNG 与已支持的 PNG 原样返回。
	public static bool TryPrepare(byte[] data, out byte[] prepared)
	{
		prepared = data;
		if (!PngCodec.IsPng(data))
		{
			// 未知格式交给框架会在渲染时逐帧抛「Unsupported image format」，入口就拒掉。
			return ImageDecoders.DetectFormatId(data) is not null;
		}

		if (!TryRead(data, out var header, out var idat) || (long)header.Width * header.Height > MaxPixels)
		{
			return false;
		}

		if (IsSupported(header))
		{
			return true;
		}

		if (header.BitDepth != 16 || header.Interlace != 0
			|| (long)header.Width * header.Height > MaxConversionPixels)
		{
			return false;
		}

		return TryDownscale16(header, idat, out prepared);
	}

	private static bool IsSupported(Header header) =>
		header.Interlace == 0 &&
		header.ColorType is 0 or 2 or 3 or 4 or 6 &&
		(header.BitDepth == 8 || (header.BitDepth is 1 or 2 or 4 && header.ColorType is 0 or 3));

	/// 走完整份 chunk 链：长度必须落在 buffer 内，必须有 IHDR、IDAT 与 IEND。
	/// 只要这段结构成立，8 位 PNG 就交给框架去解。
	private static bool TryRead(ReadOnlySpan<byte> data, out Header header, out byte[] idat)
	{
		header = default;
		idat = [];
		var sawHeader = false;
		var sawIdat = false;
		using var compressed = new MemoryStream();
		var offset = 8;
		while (offset + 12 <= data.Length)
		{
			int length = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
			offset += 4;
			if (length < 0 || offset + 4L + length + 4 > data.Length)
			{
				return false;
			}

			var type = data.Slice(offset, 4);
			offset += 4;
			var chunk = data.Slice(offset, length);
			offset += length + 4;

			if (type.SequenceEqual("IHDR"u8))
			{
				if (sawHeader || length != 13)
				{
					return false;
				}

				int width = BinaryPrimitives.ReadInt32BigEndian(chunk);
				int height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
				byte bitDepth = chunk[8];
				byte colorType = chunk[9];
				if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
				{
					return false;
				}
				if (chunk[10] != 0 || chunk[11] != 0)
				{
					return false;
				}

				header = new Header(width, height, bitDepth, colorType, chunk[12]);
				sawHeader = true;
			}
			else if (type.SequenceEqual("IDAT"u8))
			{
				if (!sawHeader)
				{
					return false;
				}

				sawIdat = true;
				// 8 位 PNG 只校验结构，不复制压缩数据。
				if (header.BitDepth == 16)
				{
					compressed.Write(chunk);
				}
			}
			else if (type.SequenceEqual("IEND"u8))
			{
				if (!sawHeader || !sawIdat)
				{
					return false;
				}

				idat = compressed.ToArray();
				return true;
			}
		}

		return false;
	}

	private static bool TryDownscale16(Header header, byte[] idat, out byte[] prepared)
	{
		prepared = [];
		int channels = header.ColorType switch
		{
			0 => 1,
			2 => 3,
			4 => 2,
			6 => 4,
			_ => 0,
		};
		if (channels == 0)
		{
			return false;
		}

		int stride = header.Width * channels * 2;
		long expected = (long)(stride + 1) * header.Height;
		if (expected > MaxIntermediateBytes)
		{
			return false;
		}

		var raw = new byte[expected];
		try
		{
			using var source = new MemoryStream(idat, writable: false);
			using var zlib = new ZLibStream(source, CompressionMode.Decompress);
			zlib.ReadExactly(raw);
		}
		catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
		{
			return false;
		}

		var pixels = PixelImage.Create(header.Width, header.Height);
		var previous = new byte[stride];
		var offset = 0;
		for (var y = 0; y < header.Height; y++)
		{
			var filter = raw[offset++];
			var row = raw.AsSpan(offset, stride);
			if (!Unfilter(filter, row, previous, channels * 2))
			{
				return false;
			}

			DecodeRow(row, pixels, y, header.ColorType, channels);
			row.CopyTo(previous);
			offset += stride;
		}

		prepared = PngCodec.Encode(pixels);
		return true;
	}

	/// 每像素只取高字节：16 位 PNG 降 8 位是标准做法，低字节只会带来肉眼看不到的量化噪声。
	private static void DecodeRow(ReadOnlySpan<byte> row, PixelImage image, int y, byte colorType, int channels)
	{
		var target = image.Bgra.AsSpan(y * image.Width * 4, image.Width * 4);
		for (var x = 0; x < image.Width; x++)
		{
			var sample = row.Slice(x * channels * 2, channels * 2);
			byte red, green, blue, alpha;
			switch (colorType)
			{
				case 0:
					red = green = blue = sample[0];
					alpha = byte.MaxValue;
					break;
				case 4:
					red = green = blue = sample[0];
					alpha = sample[2];
					break;
				case 2:
					red = sample[0];
					green = sample[2];
					blue = sample[4];
					alpha = byte.MaxValue;
					break;
				default:
					red = sample[0];
					green = sample[2];
					blue = sample[4];
					alpha = sample[6];
					break;
			}

			var offset = x * 4;
			target[offset] = blue;
			target[offset + 1] = green;
			target[offset + 2] = red;
			target[offset + 3] = alpha;
		}
	}

	private static bool Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int bytesPerPixel)
	{
		switch (filter)
		{
			case 0:
				return true;
			case 1:
				for (var i = bytesPerPixel; i < row.Length; i++) row[i] += row[i - bytesPerPixel];
				return true;
			case 2:
				for (var i = 0; i < row.Length; i++) row[i] += previous[i];
				return true;
			case 3:
				for (var i = 0; i < row.Length; i++)
				{
					var left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
					row[i] += (byte)((left + previous[i]) / 2);
				}
				return true;
			case 4:
				for (var i = 0; i < row.Length; i++)
				{
					var left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
					var upperLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
					row[i] += (byte)Paeth(left, previous[i], upperLeft);
				}
				return true;
			default:
				return false;
		}
	}

	private static int Paeth(int left, int up, int upperLeft)
	{
		var estimate = left + up - upperLeft;
		var toLeft = Math.Abs(estimate - left);
		var toUp = Math.Abs(estimate - up);
		var toUpperLeft = Math.Abs(estimate - upperLeft);
		if (toLeft <= toUp && toLeft <= toUpperLeft) return left;
		return toUp <= toUpperLeft ? up : upperLeft;
	}
}
