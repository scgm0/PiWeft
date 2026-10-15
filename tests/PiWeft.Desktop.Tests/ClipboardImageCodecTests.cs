using System.Buffers.Binary;
using Aprillz.MewUI.Resources;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 剪贴板互转用的编解码器：PNG 编码与 DIB 读写都必须原样往返。
/// 解码一侧用框架的 <see cref="ImageDecoders"/> 交叉验证 —— 那是另一套实现，也正是运行时真正用到的那条路径。
/// </summary>
[Collection("mewui-controls")]
public sealed class ClipboardImageCodecTests
{
	private static PixelImage Sample()
	{
		var image = PixelImage.Create(3, 2);
		var pixels = new (byte B, byte G, byte R, byte A)[]
		{
			(0x00, 0x00, 0xFF, 0xFF), (0x00, 0xFF, 0x00, 0x80), (0xFF, 0x00, 0x00, 0x00),
			(0x10, 0x20, 0x30, 0x40), (0xFF, 0xFF, 0xFF, 0xFF), (0x7F, 0x80, 0x81, 0xFF),
		};
		for (var index = 0; index < pixels.Length; index++)
		{
			image.Bgra[index * 4] = pixels[index].B;
			image.Bgra[index * 4 + 1] = pixels[index].G;
			image.Bgra[index * 4 + 2] = pixels[index].R;
			image.Bgra[index * 4 + 3] = pixels[index].A;
		}
		return image;
	}

	[Fact]
	public void EncodedPngDecodesBackToTheSamePixels()
	{
		var image = Sample();
		var png = PngCodec.Encode(image);
		Assert.True(PngCodec.IsPng(png));

		Assert.True(ImageDecoders.TryDecode(png, out var decoded));
		Assert.Equal(image.Width, decoded.WidthPx);
		Assert.Equal(image.Height, decoded.HeightPx);
		Assert.Equal(image.Bgra, decoded.Data);
	}

	[Fact]
	public void EncodedPngRoundTripsGradientsAndRepetition()
	{
		// 逐行滤波器按行选择，因此同时覆盖「各行相同」与「逐像素变化」两种极端。
		var image = PixelImage.Create(64, 48);
		for (var y = 0; y < image.Height; y++)
			for (var x = 0; x < image.Width; x++)
			{
				var offset = (y * image.Width + x) * 4;
				var flat = y % 2 == 0;
				image.Bgra[offset] = (byte)(flat ? 0x40 : x * 4);
				image.Bgra[offset + 1] = (byte)(flat ? 0x40 : y * 5);
				image.Bgra[offset + 2] = (byte)(flat ? 0x40 : x + y);
				image.Bgra[offset + 3] = flat ? (byte)0x80 : (byte)0xFF;
			}

		var png = PngCodec.Encode(image);
		Assert.True(ImageDecoders.TryDecode(png, out var decoded));
		Assert.Equal(image.Bgra, decoded.Data);
	}

	[Fact]
	public void ForeignOrTruncatedPayloadsAreNotPng()
	{
		Assert.False(PngCodec.IsPng([]));
		Assert.False(PngCodec.IsPng([0x89, 0x50, 0x4E, 0x47]));
		Assert.False(PngCodec.IsPng("not an image at all"u8));
	}

	[Fact]
	public void BothWrittenDibsRoundTripIncludingAlpha()
	{
		var image = Sample();

		// CF_DIBV5 版：V5 头 + BI_BITFIELDS + 显式 alpha 掩码。
		Assert.True(DibCodec.TryRead(DibCodec.Write(image), out var deep));
		Assert.Equal(image.Width, deep!.Width);
		Assert.Equal(image.Height, deep.Height);
		Assert.Equal(image.Bgra, deep.Bgra);

		// CF_DIB 版：40 字节 BITMAPINFOHEADER + BI_RGB，供只认这一版的老程序使用。
		Assert.True(DibCodec.TryRead(DibCodec.WriteRgb(image), out var plain));
		Assert.Equal(image.Width, plain!.Width);
		Assert.Equal(image.Bgra, plain.Bgra);
	}

	[Fact]
	public void RgbDibIsReadableByTheFrameworkDecoder()
	{
		// 给 CF_DIB 的内容补一个 BITMAPFILEHEADER，交给框架的 BMP 解码器：另一套实现核一遍头部、行序与 32 位 alpha。
		var image = Sample();
		var dib = DibCodec.WriteRgb(image);
		var bmp = new byte[dib.Length + 14];
		bmp[0] = (byte)'B';
		bmp[1] = (byte)'M';
		BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
		// bfOffBits 要指向像素数据，即文件头 14 字节加上 40 字节的 DIB 头。
		BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 14 + 40);
		dib.CopyTo(bmp, 14);

		Assert.True(ImageDecoders.TryDecode(bmp, out var decoded));
		Assert.Equal(image.Width, decoded.WidthPx);
		Assert.Equal(image.Height, decoded.HeightPx);
		Assert.Equal(image.Bgra, decoded.Data);
	}

	[Fact]
	public void ReadsTopDownTwentyFourBitDib()
	{
		// 24 位、负高度（自上而下）、每行按 4 字节对齐。
		var dib = BuildDib(width: 2, height: 2, bitCount: 24, topDown: true, rows:
		[
			[0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x00, 0x00],
			[0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x00, 0x00],
		]);
		Assert.True(DibCodec.TryRead(dib, out var image));
		Assert.Equal(2, image!.Width);
		Assert.Equal(new byte[] { 0x03, 0x02, 0x01, 0xFF, 0x06, 0x05, 0x04, 0xFF }, image.Bgra.AsSpan(0, 8).ToArray());
		Assert.Equal(new byte[] { 0x09, 0x08, 0x07, 0xFF, 0x0C, 0x0B, 0x0A, 0xFF }, image.Bgra.AsSpan(8, 8).ToArray());
	}

	[Fact]
	public void ThirtyTwoBitRgbDibWithReservedZeroBytesIsTreatedAsOpaque()
	{
		var dib = BuildDib(width: 1, height: 1, bitCount: 32, topDown: true, rows: [[0x10, 0x20, 0x30, 0x00]]);
		Assert.True(DibCodec.TryRead(dib, out var image));
		Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0xFF }, image!.Bgra);
	}

	[Fact]
	public void UnsupportedOrTruncatedDibsAreRejected()
	{
		Assert.False(DibCodec.TryRead([], out _));
		// 8 位调色板 DIB 不在支持范围，但必须是「失败」而不是解出垃圾。
		Assert.False(DibCodec.TryRead(BuildDib(2, 2, bitCount: 8, topDown: false, rows: [[0, 0]]), out _));
		Assert.False(DibCodec.TryRead(BuildDib(4, 4, bitCount: 24, topDown: false, rows: [[1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 0]]), out _));
	}

	[Theory]
	[InlineData("image/png", true)]
	[InlineData("image/jpeg", false)]
	[InlineData("image/gif", false)]
	[InlineData("image/webp", false)]
	public void SignatureCheckRejectsMismatchedDeclarations(string mimeType, bool expected)
	{
		var png = PngCodec.Encode(Sample());
		Assert.Equal(expected, ClipboardImages.LooksLike(mimeType, png));
	}

	[Fact]
	public void SignatureCheckRecognisesEveryAcceptedContainer()
	{
		Assert.True(ClipboardImages.LooksLike("image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]));
		Assert.True(ClipboardImages.LooksLike("image/gif", "GIF89a"u8));
		Assert.True(ClipboardImages.LooksLike("image/webp", [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8]));
		Assert.False(ClipboardImages.LooksLike("image/png", [0xFF, 0xD8, 0xFF, 0xE0]));
		Assert.False(ClipboardImages.LooksLike("image/png", []));
	}

	[Fact]
	public void PastedNamesCarryTheMatchingExtension()
	{
		Assert.EndsWith(".png", ClipboardImages.PastedName("image/png"));
		Assert.EndsWith(".jpg", ClipboardImages.PastedName("image/jpeg"));
		Assert.StartsWith("pasted-", ClipboardImages.PastedName("image/webp"));
	}

	private static byte[] BuildDib(int width, int height, int bitCount, bool topDown, byte[][] rows)
	{
		using var output = new MemoryStream();
		var header = new byte[40];
		BinaryPrimitives.WriteUInt32LittleEndian(header, 40);
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), topDown ? -height : height);
		BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)bitCount);
		output.Write(header);
		foreach (var row in rows) output.Write(row);
		return output.ToArray();
	}
}
