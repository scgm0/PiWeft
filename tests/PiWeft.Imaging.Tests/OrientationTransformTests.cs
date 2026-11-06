using Sdcb.SimdCodecs.Abstractions;
using Xunit;

namespace PiWeft.Imaging.Tests;

/// EXIF 方向 1–8 的像素映射。用 3×2 的 A–F 图案逐点断言，避免「看起来对」的实现
/// （5/7 这类转置方向最容易把宽高和坐标一起弄反）。
public sealed class OrientationTransformTests
{
	// 3×2：A B C / D E F，R 通道存编号。
	private static readonly byte[] Source =
	[
		0, 0, 0, 255, 1, 0, 0, 255, 2, 0, 0, 255,
		3, 0, 0, 255, 4, 0, 0, 255, 5, 0, 0, 255,
	];

	private static byte[] Apply(ImageOrientation orientation) =>
		OrientationTransform.Apply(Source, 3, 2, orientation).Pixels;

	private static byte Rows(byte[] pixels, int index) => pixels[index * 4];

	[Fact]
	public void NormalKeepsTheStoredOrder()
	{
		var (pixels, width, height) = OrientationTransform.Apply(Source, 3, 2, ImageOrientation.Normal);

		Assert.Equal((3, 2), (width, height));
		Assert.Equal([0, 1, 2, 3, 4, 5], Enumerable.Range(0, 6).Select(index => Rows(pixels, index)));
	}

	[Theory]
	// FlipHorizontal: C B A / F E D
	[InlineData(ImageOrientation.FlipHorizontal, 3, 2, new byte[] { 2, 1, 0, 5, 4, 3 })]
	// Rotate180: F E D / C B A
	[InlineData(ImageOrientation.Rotate180, 3, 2, new byte[] { 5, 4, 3, 2, 1, 0 })]
	// FlipVertical: D E F / A B C
	[InlineData(ImageOrientation.FlipVertical, 3, 2, new byte[] { 3, 4, 5, 0, 1, 2 })]
	// Transpose: A D / B E / C F
	[InlineData(ImageOrientation.Transpose, 2, 3, new byte[] { 0, 3, 1, 4, 2, 5 })]
	// Rotate90 (CW): D A / E B / F C
	[InlineData(ImageOrientation.Rotate90, 2, 3, new byte[] { 3, 0, 4, 1, 5, 2 })]
	// Transverse: F C / E B / D A
	[InlineData(ImageOrientation.Transverse, 2, 3, new byte[] { 5, 2, 4, 1, 3, 0 })]
	// Rotate270 (CCW): C F / B E / A D
	[InlineData(ImageOrientation.Rotate270, 2, 3, new byte[] { 2, 5, 1, 4, 0, 3 })]
	public void EveryOrientationMapsPixelsAndSwapsAxisWhenNeeded(
		ImageOrientation orientation,
		int expectedWidth,
		int expectedHeight,
		byte[] expected)
	{
		var (pixels, width, height) = OrientationTransform.Apply(Source, 3, 2, orientation);

		Assert.Equal((expectedWidth, expectedHeight), (width, height));
		Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(index => Rows(pixels, index)));
	}

	[Fact]
	public void AlphaIsCarriedThroughTheTransform()
	{
		var source = Source.ToArray();
		source[3] = 7;

		var (pixels, _, _) = OrientationTransform.Apply(source, 3, 2, ImageOrientation.Rotate180);

		// F E D / C B A：A 的 alpha 落到最后一个像素。
		Assert.Equal(7, pixels[5 * 4 + 3]);
	}
}
