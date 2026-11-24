using System.Buffers.Binary;
using System.IO.Compression;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Resources;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 16 位 PNG 框架解不了，交给渲染前必须降成 8 位；不支持的交错版本要拒绝，
/// 否则 <c>Image</c> 每帧重试解码并抛异常，UI 直接卡死。
[Collection("mewui-controls")]
public sealed class PngCompatTests
{
	[Fact]
	public void SixteenBitGrayscaleIsDownscaled()
	{
		ushort[] samples = [0x12FF, 0x8000, 0xFFFF, 0x0001, 0x7F7F, 0xAB00];
		var png = Png16(3, 2, colorType: 0, channels: 1, samples);
		Assert.False(ImageDecoders.TryDecode(png, out _));

		Assert.True(PngCompat.TryPrepare(png, out var prepared));
		Assert.NotSame(png, prepared);

		Assert.True(ImageDecoders.TryDecode(prepared, out var decoded));
		Assert.Equal(3, decoded.WidthPx);
		Assert.Equal(2, decoded.HeightPx);
		for (var i = 0; i < samples.Length; i++)
		{
			var gray = (byte)(samples[i] >> 8);
			Assert.Equal(gray, decoded.Data[i * 4]);
			Assert.Equal(gray, decoded.Data[i * 4 + 1]);
			Assert.Equal(gray, decoded.Data[i * 4 + 2]);
			Assert.Equal(byte.MaxValue, decoded.Data[i * 4 + 3]);
		}
	}

	[Fact]
	public void SixteenBitRgbaKeepsHighBytesAndAlpha()
	{
		ushort[] samples = [0x1234, 0x5678, 0x9ABC, 0xDEF0];
		var png = Png16(1, 1, colorType: 6, channels: 4, samples);

		Assert.True(PngCompat.TryPrepare(png, out var prepared));
		Assert.True(ImageDecoders.TryDecode(prepared, out var decoded));
		Assert.Equal(new byte[] { 0x9A, 0x56, 0x12, 0xDE }, decoded.Data);
	}

	[Fact]
	public void SixteenBitGrayAlphaKeepsAlpha()
	{
		ushort[] samples = [0x8040, 0x20FF];
		var png = Png16(1, 1, colorType: 4, channels: 2, samples);

		Assert.True(PngCompat.TryPrepare(png, out var prepared));
		Assert.True(ImageDecoders.TryDecode(prepared, out var decoded));
		Assert.Equal(new byte[] { 0x80, 0x80, 0x80, 0x20 }, decoded.Data);
	}

	[Fact]
	public void FilteredSixteenBitRowsAreReconstructed()
	{
		// 第二行用 Sub、第三行用 Up、第四行用 Paeth：逆滤波错了像素就会串位。
		ushort[] samples =
		[
			0x1000, 0x2000, 0x3000,
			0x4000, 0x5000, 0x6000,
			0x7000, 0x8000, 0x9000,
			0xA000, 0xB000, 0xC000,
		];
		var png = Png16(3, 4, colorType: 0, channels: 1, samples, filters: [1, 2, 4, 0]);

		Assert.True(PngCompat.TryPrepare(png, out var prepared));
		Assert.True(ImageDecoders.TryDecode(prepared, out var decoded));
		for (var i = 0; i < samples.Length; i++)
		{
			Assert.Equal((byte)(samples[i] >> 8), decoded.Data[i * 4]);
		}
	}

	[Fact]
	public void SupportedPngIsPassedThrough()
	{
		var png = PngCodec.Encode(PixelImage.Create(2, 2));
		Assert.True(PngCompat.TryPrepare(png, out var prepared));
		Assert.Same(png, prepared);
	}

	[Fact]
	public void NonPngIsPassedThrough()
	{
		byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46];
		Assert.True(PngCompat.TryPrepare(jpeg, out var prepared));
		Assert.Same(jpeg, prepared);
	}

	[Fact]
	public void InterlacedPngIsRejected()
	{
		var png = PngCodec.Encode(PixelImage.Create(2, 2));
		png[28] = 1; // IHDR 的 interlace 字节
		Assert.False(PngCompat.TryPrepare(png, out _));
	}

	[Fact]
	public void TruncatedPngIsRejected()
	{
		var png = PngCodec.Encode(PixelImage.Create(2, 2));
		Assert.False(PngCompat.TryPrepare(png[..^12], out _));
	}

	[Fact]
	public void EmptyAndUnknownDataAreRejected()
	{
		Assert.False(PngCompat.TryPrepare([], out _));
		Assert.False(PngCompat.TryPrepare("not an image"u8.ToArray(), out _));
	}

	[Fact]
	public void ChatImageWithSixteenBitPngRendersAnImage()
	{
		var png = Png16(2, 1, colorType: 0, channels: 1, [0x1200, 0x3400]);
		var view = SessionTab.ImageView(new ChatImage(Convert.ToBase64String(png), "image/png"));
		Assert.IsType<Image>(view);
	}

	[Fact]
	public void ChatImageWithBrokenDataRendersThePlaceholder()
	{
		var view = SessionTab.ImageView(new ChatImage(Convert.ToBase64String("not an image"u8), "image/png"));
		Assert.IsType<TextBlock>(view);
	}

	private static byte[] Png16(int width, int height, byte colorType, int channels, ushort[] samples,
		byte[]? filters = null)
	{
		int stride = width * channels * 2;
		var raw = new byte[(stride + 1) * height];
		var current = new byte[stride];
		var previous = new byte[stride];
		var offset = 0;
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				for (var c = 0; c < channels; c++)
				{
					var value = samples[(y * width + x) * channels + c];
					current[(x * channels + c) * 2] = (byte)(value >> 8);
					current[(x * channels + c) * 2 + 1] = (byte)value;
				}
			}

			var filter = filters is null ? (byte)0 : filters[y];
			raw[offset++] = filter;
			for (var i = 0; i < stride; i++)
			{
				var left = i >= 2 ? current[i - 2] : 0;
				var up = previous[i];
				var upperLeft = i >= 2 ? previous[i - 2] : 0;
				raw[offset++] = filter switch
				{
					1 => (byte)(current[i] - left),
					2 => (byte)(current[i] - up),
					3 => (byte)(current[i] - (left + up) / 2),
					4 => (byte)(current[i] - Paeth(left, up, upperLeft)),
					_ => current[i],
				};
			}

			current.CopyTo(previous, 0);
		}

		var header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(header, width);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
		header[8] = 16;
		header[9] = colorType;

		using var output = new MemoryStream();
		output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
		WriteChunk(output, "IHDR"u8, header);
		using var compressed = new MemoryStream();
		using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
		{
			zlib.Write(raw);
		}
		WriteChunk(output, "IDAT"u8, compressed.ToArray());
		WriteChunk(output, "IEND"u8, []);
		return output.ToArray();
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

	private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
	{
		Span<byte> prefix = stackalloc byte[8];
		BinaryPrimitives.WriteInt32BigEndian(prefix, data.Length);
		type.CopyTo(prefix[4..]);
		output.Write(prefix);
		output.Write(data);
		Span<byte> crc = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(type, data));
		output.Write(crc);
	}

	private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
	{
		var crc = 0xFFFFFFFFu;
		foreach (var value in type) crc = Update(crc, value);
		foreach (var value in data) crc = Update(crc, value);
		return crc ^ 0xFFFFFFFFu;

		static uint Update(uint crc, byte value)
		{
			crc ^= value;
			for (var bit = 0; bit < 8; bit++)
			{
				crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
			}
			return crc;
		}
	}
}
