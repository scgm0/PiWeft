using System.Text.Json.Nodes;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Xunit;

namespace PiWeft.Imaging.Tests;

/// pi 的 processImage / resizeImageInProcess 语义：限制内原样返回、超限时按 EXIF 方向缩放、
/// PNG 与 JPEG 质量阶梯里取最小的、仍超预算按 0.75 递减、最后给尺寸映射括注。
public sealed class ImagePipelineTests
{
	private static byte[] SolidRgba(int width, int height, byte r, byte g, byte b, byte a = 255)
	{
		var pixels = new byte[width * height * 4];
		for (var index = 0; index < pixels.Length; index += 4)
		{
			pixels[index] = r;
			pixels[index + 1] = g;
			pixels[index + 2] = b;
			pixels[index + 3] = a;
		}

		return pixels;
	}

	private static long Base64Length(ReadOnlySpan<char> base64) => base64.Length;

	[Fact]
	public void ImagesWithinEveryLimitPassThroughUnchanged()
	{
		var png = PngWriter.Encode(SolidRgba(8, 8, 10, 20, 30), 8, 8);

		var result = ImagePipeline.Process(png, "image/png");

		Assert.True(result.Ok);
		Assert.False(result.WasResized);
		Assert.Equal("image/png", result.MimeType);
		Assert.Equal(Convert.ToBase64String(png), result.Data);
		Assert.Empty(result.Hints);
	}

	[Fact]
	public void LargeImagesAreResizedUnderTheBase64BudgetAndCarryTheMappingNote()
	{
		var jpeg = JpegEncoder.Encode(SolidRgba(3000, 2000, 120, 90, 60), 3000, 2000, 4, new JpegEncodeOptions { Quality = 90 });
		var options = new ImageResizeOptions { MaxWidth = 512, MaxHeight = 512, JpegQuality = 80 };

		var result = ImagePipeline.Process(jpeg, "image/jpeg", options);

		Assert.True(result.Ok);
		Assert.True(result.WasResized);
		Assert.True(Base64Length(result.Data) < options.MaxBytes);
		Assert.Contains(result.MimeType, new[] { "image/png", "image/jpeg" });
		Assert.Contains(result.Hints, hint => hint.Contains("original 3000x2000", StringComparison.Ordinal));
		Assert.Contains(result.Hints, hint => hint.Contains("displayed at 512x341", StringComparison.Ordinal));
	}

	[Fact]
	public void NonSquareImagesKeepTheirAspectRatio()
	{
		var png = PngWriter.Encode(SolidRgba(40, 10, 0, 0, 255), 40, 10);
		var options = new ImageResizeOptions { MaxWidth = 20, MaxHeight = 20 };

		var result = ImagePipeline.Process(png, "image/png", options);

		Assert.True(result.Ok);
		Assert.Contains(result.Hints, hint => hint.Contains("displayed at 20x5", StringComparison.Ordinal));
	}

	[Fact]
	public void UnsupportedFormatsBecomePngWithAConversionHint()
	{
		var bmp = BmpEncoder.Encode(SolidRgba(8, 8, 1, 2, 3), 8, 8, 4);

		var result = ImagePipeline.Process(bmp, "image/bmp");

		Assert.True(result.Ok);
		Assert.Equal("image/png", result.MimeType);
		Assert.Contains(result.Hints, hint => hint == "[Image converted from image/bmp to image/png.]");
	}

	[Fact]
	public void TheByteHeaderWinsOverAWrongExtensionOrMime()
	{
		var bmp = BmpEncoder.Encode(SolidRgba(8, 8, 4, 5, 6), 8, 8, 4);

		// 附件可能被命名成 .png（扩展名给的 mime 是 image/png），内容其实是 BMP。
		var result = ImagePipeline.Process(bmp, "image/png");

		Assert.True(result.Ok);
		Assert.Equal("image/png", result.MimeType);
		Assert.Contains(result.Hints, hint => hint == "[Image converted from image/bmp to image/png.]");
	}

	[Fact]
	public void AutoResizeOffStillNormalizesButNeverResamples()
	{
		var bmp = BmpEncoder.Encode(SolidRgba(10, 10, 9, 9, 9), 10, 10, 4);
		var options = new ImageResizeOptions { MaxWidth = 2, MaxHeight = 2 };

		var result = ImagePipeline.Process(bmp, "image/bmp", options, autoResize: false);

		Assert.True(result.Ok);
		Assert.False(result.WasResized);
		Assert.Equal("image/png", result.MimeType);
		Assert.Contains(result.Hints, hint => hint.StartsWith("[Image converted from", StringComparison.Ordinal));
	}

	[Fact]
	public void GarbageIsReportedAsAnOmissionInsteadOfThrowing()
	{
		// 可内联的格式、但字节解不开：与 pi 一样报「缩不到限制内」，不会把坏数据塞进上下文。
		var broken = ImagePipeline.Process([1, 2, 3, 4, 5], "image/png");
		Assert.False(broken.Ok);
		Assert.Equal(ImagePipeline.CannotResizeMessage, broken.Error);

		// 根本不在支持列表里的格式：报「无法转换」。
		var unsupported = ImagePipeline.Process([1, 2, 3, 4, 5], "image/tiff");
		Assert.False(unsupported.Ok);
		Assert.Equal(ImageFormats.UnsupportedMessage, unsupported.Error);
	}

	[Fact]
	public void ExifOrientationIsAppliedBeforeResizingAndTheNoteUsesDisplayedDimensions()
	{
		// 40×20 的存储图 + 方向 6（Rotate90）：显示尺寸是 20×40。
		var jpeg = JpegEncoder.Encode(SolidRgba(40, 20, 200, 100, 50), 40, 20, 4);
		var oriented = WithOrientation(jpeg, 6);

		Assert.True(ImageFormats.TryReadHeader(oriented, out var header));
		Assert.Equal(ImageOrientation.Rotate90, header.Orientation);
		Assert.True(header.SwapsDimensions);

		var result = ImagePipeline.Process(oriented, "image/jpeg", new ImageResizeOptions { MaxWidth = 10, MaxHeight = 100 });

		Assert.True(result.Ok);
		Assert.Contains(result.Hints, hint => hint.Contains("original 20x40", StringComparison.Ordinal));
		Assert.Contains(result.Hints, hint => hint.Contains("displayed at 10x20", StringComparison.Ordinal));
	}

	[Fact]
	public void BudgetThatCannotBeMetShrinksTowardsOnePixelAndThenFailsCleanly()
	{
		var png = PngWriter.Encode(SolidRgba(64, 64, 0, 0, 0), 64, 64);
		// 1×1 的 PNG 也不止 8 字节 base64：必然走到 1×1 后放弃。
		var result = ImagePipeline.Process(png, "image/png", new ImageResizeOptions { MaxBytes = 8 });

		Assert.False(result.Ok);
		Assert.Equal(ImagePipeline.CannotResizeMessage, result.Error);
	}

	[Fact]
	public void InputLimitsAreReadFromTheModelEntry()
	{
		var limits = new JsonObject
		{
			["images"] = new JsonObject
			{
				["resize"] = new JsonObject
				{
					["maxWidth"] = 1600,
					["maxHeight"] = 1200,
					["maxBytes"] = 3_000_000,
					["jpegQuality"] = 70,
				},
			},
		};

		var options = ImageResizeOptions.FromInputLimits(limits);

		Assert.Equal(1600, options.MaxWidth);
		Assert.Equal(1200, options.MaxHeight);
		Assert.Equal(3_000_000, options.MaxBytes);
		Assert.Equal(70, options.JpegQuality);
	}

	[Fact]
	public void MissingOrInvalidResizeFieldsFallBackToTheDefaults()
	{
		var defaults = ImageResizeOptions.Default;

		Assert.Equal(defaults, ImageResizeOptions.FromInputLimits(null));
		Assert.Equal(defaults, ImageResizeOptions.FromInputLimits(new JsonObject()));

		var invalid = ImageResizeOptions.FromInputLimits(new JsonObject
		{
			["images"] = new JsonObject
			{
				["resize"] = new JsonObject { ["maxWidth"] = 0, ["maxBytes"] = -1, ["jpegQuality"] = 500 },
			},
		});
		Assert.Equal(defaults.MaxWidth, invalid.MaxWidth);
		Assert.Equal(defaults.MaxHeight, invalid.MaxHeight);
		Assert.Equal(defaults.MaxBytes, invalid.MaxBytes);
		// 质量只有「夹到 1..100」这一条：500 变成 100，不回落默认值。
		Assert.Equal(100, invalid.JpegQuality);
	}

	[Fact]
	public void Base64LengthMatchesTheEncoder()
	{
		Assert.Equal(0, ImagePipeline.Base64Length(0));
		Assert.Equal(4, ImagePipeline.Base64Length(1));
		Assert.Equal(4, ImagePipeline.Base64Length(3));
		Assert.Equal(8, ImagePipeline.Base64Length(4));

		var bytes = new byte[1000];
		Assert.Equal(Convert.ToBase64String(bytes).Length, ImagePipeline.Base64Length(bytes.Length));
	}

	/// 在 SOI 之后插入一个只带 Orientation 标签的 EXIF APP1 段。
	private static byte[] WithOrientation(byte[] jpeg, ushort orientation)
	{
		var tiff = new byte[]
		{
			(byte)'I', (byte)'I', 0x2A, 0x00, // little-endian TIFF 头
			0x08, 0x00, 0x00, 0x00,       // IFD0 偏移
			0x01, 0x00,                   // 1 个条目
			0x12, 0x01,                   // tag 0x0112 (Orientation)
			0x03, 0x00,                   // type SHORT
			0x01, 0x00, 0x00, 0x00,       // count = 1
			(byte)(orientation & 0xFF), (byte)(orientation >> 8), 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00,       // 下一个 IFD = 0
		};

		var payload = new byte[6 + tiff.Length];
		"Exif\0\0"u8.CopyTo(payload);
		tiff.CopyTo(payload, 6);

		var segment = new List<byte>(payload.Length + 4) { 0xFF, 0xE1 };
		var length = payload.Length + 2;
		segment.Add((byte)(length >> 8));
		segment.Add((byte)length);
		segment.AddRange(payload);

		var result = new byte[jpeg.Length + segment.Count];
		jpeg.AsSpan(0, 2).CopyTo(result);
		segment.CopyTo(result, 2);
		jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + segment.Count));
		return result;
	}
}
