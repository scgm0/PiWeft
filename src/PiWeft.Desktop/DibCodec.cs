using System.Buffers.Binary;
using System.Numerics;

namespace PiWeft.Desktop;

/// <summary>
/// 剪贴板 DIB（<c>CF_DIB</c> / <c>CF_DIBV5</c>）与 BGRA 位图互转用的最小实现。只处理截图与常规程序
/// 会放上剪贴板的 24/32 位无调色板格式：调色板 DIB 从不出现在截图路径上，遇到就按失败处理。
/// 纯托管代码，移植性测试与 Windows 后端共用同一份逻辑。
/// </summary>
internal static class DibCodec
{
	/// <summary>BITMAPV5HEADER 的字节数；写出的 DIB 一律用它，保证带 alpha 掩码与色彩空间。</summary>
	internal const int HeaderSize = 124;

	/// <summary>单边像素上限；超过它的剪贴板内容按「不是图片」处理，避免异常大小的分配。</summary>
	private const int MaxDimension = 16384;

	/// <summary>总像素上限（约 64M 像素，对应 256 MiB BGRA）。</summary>
	private const long MaxPixels = 64L * 1024 * 1024;

	private const int InfoHeaderSize = 40;
	private const int CompressionRgb = 0;
	private const int CompressionBitFields = 3;
	private const uint SrgbColorSpace = 0x73524742; // 'BGRs'，即 LCS_sRGB 的小端写法
	private const uint DefaultRedMask = 0x00FF0000;
	private const uint DefaultGreenMask = 0x0000FF00;
	private const uint DefaultBlueMask = 0x000000FF;
	private const uint DefaultAlphaMask = 0xFF000000;

	public static bool TryRead(ReadOnlySpan<byte> dib, out PixelImage? image)
	{
		image = null;
		if (dib.Length < InfoHeaderSize) return false;

		var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib);
		if (headerSize < InfoHeaderSize || headerSize > dib.Length) return false;

		var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
		var height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
		var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
		var compression = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);

		// 负高度表示自上而下存储；正高度是 BMP 传统的自下而上。
		var topDown = height < 0;
		height = Math.Abs(height);
		if (width <= 0 || height <= 0) return false;
		if (width > MaxDimension || height > MaxDimension) return false;
		if ((long)width * height > MaxPixels) return false;
		if (bitCount is not (24 or 32)) return false;
		if (compression is not (CompressionRgb or CompressionBitFields)) return false;
		if (bitCount == 24 && compression != CompressionRgb) return false;

		var red = DefaultRedMask;
		var green = DefaultGreenMask;
		var blue = DefaultBlueMask;
		// BI_RGB 的 32 位里第 4 个字节规范上是保留位，实践中常被当作 alpha；先读出来再按需回退为不透明。
		var alpha = bitCount == 32 ? DefaultAlphaMask : 0u;
		var offset = headerSize;

		if (compression == CompressionBitFields)
		{
			if (headerSize >= 56)
			{
				// BITMAPV4/V5 把掩码放在头里。
				red = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
				green = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
				blue = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
				alpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
			}
			else
			{
				// 40 字节头后面紧跟三个（或四个）掩码。
				if (dib.Length < headerSize + 12) return false;
				red = BinaryPrimitives.ReadUInt32LittleEndian(dib[headerSize..]);
				green = BinaryPrimitives.ReadUInt32LittleEndian(dib[(headerSize + 4)..]);
				blue = BinaryPrimitives.ReadUInt32LittleEndian(dib[(headerSize + 8)..]);
				offset = headerSize + 12;
				if (dib.Length >= headerSize + 16)
				{
					var explicitAlpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[(headerSize + 12)..]);
					if (explicitAlpha != 0)
					{
						alpha = explicitAlpha;
						offset = headerSize + 16;
					}
				}
			}
		}

		var stride = (width * bitCount + 31) / 32 * 4;
		if ((long)offset + (long)stride * height > dib.Length) return false;

		var bgra = new byte[width * height * 4];
		var anyAlpha = false;
		for (var y = 0; y < height; y++)
		{
			var source = dib.Slice(offset + (topDown ? y : height - 1 - y) * stride, stride);
			var target = bgra.AsSpan(y * width * 4);
			for (var x = 0; x < width; x++)
			{
				var pixel = bitCount == 32
					? BinaryPrimitives.ReadUInt32LittleEndian(source[(x * 4)..])
					: (uint)(source[x * 3] << 16 | source[x * 3 + 1] << 8 | source[x * 3 + 2]);
				var a = Channel(pixel, alpha, 255);
				if (a != 0) anyAlpha = true;
				target[x * 4] = Channel(pixel, blue, 0);
				target[x * 4 + 1] = Channel(pixel, green, 0);
				target[x * 4 + 2] = Channel(pixel, red, 0);
				target[x * 4 + 3] = a;
			}
		}

		if (!anyAlpha)
			for (var index = 3; index < bgra.Length; index += 4) bgra[index] = 255;

		image = new PixelImage(width, height, bgra);
		return true;
	}

	/// <summary>写出带 alpha 掩码的 32 位 DIB（BITMAPV5HEADER + BI_BITFIELDS），即 CF_DIBV5 的内容。</summary>
	public static byte[] Write(PixelImage image)
	{
		var stride = image.Width * 4;
		var dib = new byte[HeaderSize + stride * image.Height];

		BinaryPrimitives.WriteUInt32LittleEndian(dib, HeaderSize);
		BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), image.Width);
		BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), image.Height);
		BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), CompressionBitFields);
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), (uint)(stride * image.Height));
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), DefaultRedMask);
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), DefaultGreenMask);
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), DefaultBlueMask);
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), DefaultAlphaMask);
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(56), SrgbColorSpace);
		CopyPixels(image, dib, HeaderSize);

		return dib;
	}

	/// <summary>
	/// 写出 32 位 BI_RGB DIB（仅 BITMAPINFOHEADER），即 CF_DIB 的内容。老程序（画图、Word）只认这一版，
	/// 代价是 alpha 通道在它们眼里是保留位。
	/// </summary>
	public static byte[] WriteRgb(PixelImage image)
	{
		var stride = image.Width * 4;
		var dib = new byte[InfoHeaderSize + stride * image.Height];

		BinaryPrimitives.WriteUInt32LittleEndian(dib, InfoHeaderSize);
		BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), image.Width);
		BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), image.Height);
		BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), CompressionRgb);
		BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), (uint)(stride * image.Height));
		CopyPixels(image, dib, InfoHeaderSize);

		return dib;
	}

	/// <summary>BMP 传统是自下而上，因此第 0 行放最后一条扫描线。</summary>
	private static void CopyPixels(PixelImage image, byte[] target, int offset)
	{
		var stride = image.Width * 4;
		for (var y = 0; y < image.Height; y++)
			image.Bgra.AsSpan(y * stride, stride).CopyTo(target.AsSpan(offset + (image.Height - 1 - y) * stride));
	}

	/// <summary>按掩码取出一个通道并缩放到 0–255；掩码为 0 时返回调用方给的回退值。</summary>
	private static byte Channel(uint pixel, uint mask, byte fallback)
	{
		if (mask == 0) return fallback;
		var shift = BitOperations.TrailingZeroCount(mask);
		var max = mask >> shift;
		return (byte)(((pixel & mask) >> shift) * 255u / max);
	}
}
