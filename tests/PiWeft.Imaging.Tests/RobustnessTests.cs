using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Xunit;

namespace PiWeft.Imaging.Tests;

/// 坏图、截断图、随机字节都只能变成一句「省略」提示，不能把异常抛到工具调用上。
public sealed class RobustnessTests
{
	private static byte[] Pixels(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
	{
		var pixels = new byte[width * height * 4];
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var (r, g, b) = color(x, y);
				var offset = (y * width + x) * 4;
				pixels[offset] = r;
				pixels[offset + 1] = g;
				pixels[offset + 2] = b;
				pixels[offset + 3] = 255;
			}
		}

		return pixels;
	}

	[Fact]
	public void TruncatedFilesAreReportedAndNeverThrow()
	{
		var png = PngWriter.Encode(Pixels(16, 16, static (x, y) => ((byte)(x * 16), (byte)(y * 16), 0)), 16, 16);
		var jpeg = JpegEncoder.Encode(Pixels(16, 16, static (x, y) => (0, (byte)(x * 16), (byte)(y * 16))), 16, 16, 4);
		var bmp = BmpEncoder.Encode(Pixels(16, 16, static (x, y) => ((byte)(x * 16), 0, (byte)(y * 16))), 16, 16, 4);

		foreach (var (bytes, mime) in new[] { (png, "image/png"), (jpeg, "image/jpeg"), (bmp, "image/bmp") })
		{
			for (var length = 0; length <= bytes.Length; length += Math.Max(1, bytes.Length / 17))
			{
				var truncated = bytes.AsSpan(0, length).ToArray();
				var result = ImagePipeline.Process(truncated, mime);
				if (!result.Ok)
				{
					Assert.NotNull(result.Error);
				}
			}
		}
	}

	[Fact]
	public void RandomBytesWithEverySupportedMimeNeverThrow()
	{
		var random = new Random(20261106);
		var buffer = new byte[4096];
		foreach (var mime in new[] { "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/tiff", "" })
		{
			for (var attempt = 0; attempt < 25; attempt++)
			{
				random.NextBytes(buffer);
				var length = random.Next(0, buffer.Length + 1);
				var result = ImagePipeline.Process(buffer.AsSpan(0, length), mime);
				Assert.Equal(result.Ok, result.Error is null);
			}
		}
	}
}
