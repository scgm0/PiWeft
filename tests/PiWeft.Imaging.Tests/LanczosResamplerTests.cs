using Xunit;

namespace PiWeft.Imaging.Tests;

public sealed class LanczosResamplerTests
{
	private static byte[] Quadrants(int width, int height)
	{
		var pixels = new byte[width * height * 4];
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var offset = (y * width + x) * 4;
				var left = x < width / 2;
				var top = y < height / 2;
				pixels[offset] = (byte)(left ? 255 : 0);
				pixels[offset + 1] = (byte)(top ? 255 : 0);
				pixels[offset + 2] = (byte)(left && top ? 255 : 0);
				pixels[offset + 3] = 255;
			}
		}

		return pixels;
	}

	[Fact]
	public void ResizingToTheSameSizeReturnsACopy()
	{
		var source = Quadrants(4, 4);
		var resized = LanczosResampler.Resize(source, 4, 4, 4, 4);

		Assert.NotSame(source, resized);
		Assert.Equal(source, resized);
	}

	[Fact]
	public void DownscalingKeepsTheLeftHalfLightAndTheRightHalfDark()
	{
		// 16×16 左半白、右半黑 → 8×8。比例 2 时核半径 6，边界只会轻微过渡。
		const int size = 16;
		var source = new byte[size * size * 4];
		for (var index = 0; index < source.Length; index += 4)
		{
			var x = index / 4 % size;
			var value = (byte)(x < size / 2 ? 255 : 0);
			source[index] = value;
			source[index + 1] = value;
			source[index + 2] = value;
			source[index + 3] = 255;
		}

		var resized = LanczosResampler.Resize(source, size, size, 8, 8);

		for (var y = 0; y < 8; y++)
		{
			Assert.True(resized[(y * 8 + 0) * 4] > 200, $"y={y} x=0");
			Assert.True(resized[(y * 8 + 2) * 4] > 200, $"y={y} x=2");
			Assert.True(resized[(y * 8 + 5) * 4] < 55, $"y={y} x=5");
			Assert.True(resized[(y * 8 + 7) * 4] < 55, $"y={y} x=7");
		}

		Assert.All(Enumerable.Range(0, 64), index => Assert.Equal(255, resized[index * 4 + 3]));
	}

	[Fact]
	public void TransparentPixelsDoNotBleedTheirHiddenColour()
	{
		// 三个不透明白 + 一个透明红；缩成 1×1 时颜色应仍是白，alpha 约 75%。
		var source = new byte[]
		{
			255, 255, 255, 255, 255, 255, 255, 255,
			255, 255, 255, 255, 255, 0, 0, 0,
		};

		var resized = LanczosResampler.Resize(source, 2, 2, 1, 1);

		Assert.True(resized[0] > 240, $"r={resized[0]}");
		Assert.True(resized[1] > 240, $"g={resized[1]}");
		Assert.True(resized[2] > 240, $"b={resized[2]}");
		Assert.InRange(resized[3], 180, 200);
	}

	[Fact]
	public void UpscalingAndNonSquareTargetsProduceTheRequestedBuffer()
	{
		var resized = LanczosResampler.Resize(Quadrants(4, 4), 4, 4, 6, 3);

		Assert.Equal(6 * 3 * 4, resized.Length);
	}
}
