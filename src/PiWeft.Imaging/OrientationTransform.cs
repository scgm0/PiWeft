using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace PiWeft.Imaging;

/// EXIF 方向（TIFF Orientation 0x0112）的像素变换。解码器只报告方向、不应用，
/// 缩放前必须先转成「显示坐标」，否则尺寸提示与缩放比例都会算错。
/// 变换后方向即为 <see cref="ImageOrientation.Normal"/>，输出不再带方向。
/// <para>
/// 每个方向一条专用循环，像素按 <c>uint</c>（RGBA 四字节）整体搬：手机照片常见 6/8，
/// 逐像素 switch + 四次字节写在这个尺寸下要多花几十毫秒，而且没有向量化的余地。
/// </para>
internal static class OrientationTransform
{
	/// 返回显示方向的 RGBA 缓冲与其尺寸；<paramref name="orientation"/> 为 Normal 时复制一份。
	public static (byte[] Pixels, int Width, int Height) Apply(
		ReadOnlySpan<byte> rgba,
		int width,
		int height,
		ImageOrientation orientation)
	{
		if (orientation == ImageOrientation.Normal)
		{
			return (rgba.ToArray(), width, height);
		}

		var swaps = orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90
			or ImageOrientation.Transverse or ImageOrientation.Rotate270;
		var targetWidth = swaps ? height : width;
		var targetHeight = swaps ? width : height;
		var target = new byte[targetWidth * targetHeight * 4];

		var source = MemoryMarshal.Cast<byte, uint>(rgba);
		var destination = MemoryMarshal.Cast<byte, uint>(target.AsSpan());

		switch (orientation)
		{
			case ImageOrientation.FlipHorizontal:
				for (var y = 0; y < height; y++)
				{
					var sourceRow = y * width;
					var targetRow = y * targetWidth;
					for (var x = 0; x < width; x++)
					{
						destination[targetRow + x] = source[sourceRow + (width - 1 - x)];
					}
				}

				break;

			case ImageOrientation.Rotate180:
				var count = width * height;
				for (var index = 0; index < count; index++)
				{
					destination[index] = source[count - 1 - index];
				}

				break;

			case ImageOrientation.FlipVertical:
				var rowLength = width;
				for (var y = 0; y < height; y++)
				{
					source.Slice((height - 1 - y) * rowLength, rowLength)
						.CopyTo(destination.Slice(y * targetWidth, rowLength));
				}

				break;

			case ImageOrientation.Transpose:
				// dest(x,y) = src(y,x)
				for (var y = 0; y < targetHeight; y++)
				{
					for (var x = 0; x < targetWidth; x++)
					{
						destination[y * targetWidth + x] = source[x * width + y];
					}
				}

				break;

			case ImageOrientation.Rotate90:
				// dest(x,y) = src(row: height-1-x, col: y)：手机竖拍最常见的方向。
				for (var y = 0; y < targetHeight; y++)
				{
					for (var x = 0; x < targetWidth; x++)
					{
						destination[y * targetWidth + x] = source[(height - 1 - x) * width + y];
					}
				}

				break;

			case ImageOrientation.Transverse:
				// dest(x,y) = src(row: height-1-x, col: width-1-y)
				for (var y = 0; y < targetHeight; y++)
				{
					for (var x = 0; x < targetWidth; x++)
					{
						destination[y * targetWidth + x] = source[(height - 1 - x) * width + (width - 1 - y)];
					}
				}

				break;

			case ImageOrientation.Rotate270:
				// dest(x,y) = src(row: x, col: width-1-y)
				for (var y = 0; y < targetHeight; y++)
				{
					for (var x = 0; x < targetWidth; x++)
					{
						destination[y * targetWidth + x] = source[x * width + (width - 1 - y)];
					}
				}

				break;
		}

		return (target, targetWidth, targetHeight);
	}
}
