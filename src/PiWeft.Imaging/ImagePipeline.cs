using System.Globalization;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;

namespace PiWeft.Imaging;

/// 字节层结果：附件路径可以复用原始 base64 字符串，不必先编码再丢弃。
public sealed record ImageProcessBytesResult
{
	public required bool Ok { get; init; }

	/// 处理后的字节。未重编码时就是传入的那个数组（调用方不要修改）。
	public byte[] Bytes { get; init; } = [];

	public string MimeType { get; init; } = string.Empty;

	public IReadOnlyList<string> Hints { get; init; } = [];

	public bool WasResized { get; init; }

	public string? Error { get; init; }
}

/// 一次图片处理的结果。<see cref="Data"/> 是 base64，<see cref="Hints"/> 是写给模型/用户的
/// 括注（转码与尺寸映射），与 pi 的 <c>processed.hints</c> 同义。
public sealed record ImageProcessResult
{
	public required bool Ok { get; init; }

	public string Data { get; init; } = string.Empty;

	public string MimeType { get; init; } = string.Empty;

	public IReadOnlyList<string> Hints { get; init; } = [];

	public bool WasResized { get; init; }

	public string? Error { get; init; }
}

/// pi 的 <c>processImage</c> + <c>resizeImageInProcess</c> 的进程内实现：
/// <list type="number">
/// <item>把不能内联的格式（BMP 等）转成 PNG；</item>
/// <item>尺寸与 base64 长度都在限制内时原样返回（保留动画与原始编码）；</item>
/// <item>否则应用 EXIF 方向、Lanczos3 缩到限制内，再在 PNG 与 JPEG 质量阶梯里挑最小的；</item>
/// <item>仍超预算就按 0.75 递减尺寸，直到 1×1；</item>
/// <item>输出尺寸映射括注，让模型能把坐标换回原图。</item>
/// </list>
public static class ImagePipeline
{
	public const string CannotResizeMessage =
		"[Image omitted: could not be resized below the inline image size limit.]";

	/// 与 pi 相同的质量阶梯（去重后第一个是模型目录的 <c>jpegQuality</c>）。
	private static readonly int[] LowerJpegQualities = [85, 70, 55, 40];

	/// <summary>
	/// 处理一张图片。<paramref name="bytes"/> 是原始文件字节，<paramref name="mimeType"/>
	/// 是嗅探或声明出来的类型（可带参数，也可为空，以字节头为准）。
	/// </summary>
	public static ImageProcessResult Process(
		ReadOnlySpan<byte> bytes,
		string mimeType,
		ImageResizeOptions? options = null,
		bool autoResize = true) => Process(bytes.ToArray(), mimeType, options, autoResize);

	/// <see cref="Process(ReadOnlySpan{byte}, string, ImageResizeOptions?, bool)"/> 的数组重载：
	/// 限制内的图片原样返回，整图拷贝都省掉。
	public static ImageProcessResult Process(
		byte[] bytes,
		string mimeType,
		ImageResizeOptions? options = null,
		bool autoResize = true)
	{
		var result = ProcessBytes(bytes, mimeType, options, autoResize);
		return new ImageProcessResult
		{
			Ok = result.Ok,
			Data = result.Ok ? Convert.ToBase64String(result.Bytes) : string.Empty,
			MimeType = result.MimeType,
			Hints = result.Hints,
			WasResized = result.WasResized,
			Error = result.Error,
		};
	}

	/// 与 <see cref="Process"/> 相同，但不做 base64：附件归一化在未重编码时
	/// 直接复用调用方的 base64 字符串，省一次大字符串分配。
	public static ImageProcessBytesResult ProcessBytes(
		byte[] bytes,
		string mimeType,
		ImageResizeOptions? options = null,
		bool autoResize = true)
	{
		var resize = options ?? ImageResizeOptions.Default;
		if (!TryNormalize(bytes, mimeType, out var normalized, out var normalizedMime, out var convertedFrom))
		{
			return BytesFailure(ImageFormats.UnsupportedMessage);
		}

		var hints = new List<string>(3);
		if (ConversionHint(convertedFrom, normalizedMime) is { } conversion)
		{
			hints.Add(conversion);
		}

		if (!autoResize)
		{
			return BytesSuccess(normalized, normalizedMime, hints, wasResized: false);
		}

		// 与 pi 的 resizeImageInProcess 一样先完整解码：坏图在这一步就会失败，
		// 不等 provider 收到一段解不开的字节。解码后才知道显示尺寸（方向 5–8 宽高互换）。
		if (!ImageFormats.TryDecodeRgba8(normalized, out var rgba, out var width, out var height, out var orientation))
		{
			return BytesFailure(CannotResizeMessage);
		}

		var swaps = orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90
			or ImageOrientation.Transverse or ImageOrientation.Rotate270;
		var displayWidth = swaps ? height : width;
		var displayHeight = swaps ? width : height;
		if (displayWidth <= resize.MaxWidth &&
			displayHeight <= resize.MaxHeight &&
			Base64Length(normalized.Length) < resize.MaxBytes)
		{
			return BytesSuccess(normalized, normalizedMime, hints, wasResized: false);
		}

		if (orientation != ImageOrientation.Normal)
		{
			(rgba, width, height) = OrientationTransform.Apply(rgba, width, height, orientation);
		}

		var (targetWidth, targetHeight) = TargetSize(width, height, resize.MaxWidth, resize.MaxHeight);
		var qualities = Qualities(resize.JpegQuality);

		for (int currentWidth = targetWidth, currentHeight = targetHeight; ; )
		{
			// 只有字节预算超标（尺寸没超）时目标尺寸就是原尺寸：不必再重采样一遍。
			var resized = currentWidth == width && currentHeight == height
				? rgba
				: LanczosResampler.Resize(rgba, width, height, currentWidth, currentHeight);
			var png = PngWriter.Encode(resized, currentWidth, currentHeight);
			var jpeg = JpegEncoder.Encode(
				resized,
				currentWidth,
				currentHeight,
				channels: 4,
				new JpegEncodeOptions { Quality = qualities[0] });

			if (TryPick(png, jpeg, resized, currentWidth, currentHeight, qualities, resize.MaxBytes, out var data, out var mime))
			{
				hints.Add(DimensionNote(width, height, currentWidth, currentHeight));
				return BytesSuccess(data, mime, hints, wasResized: true);
			}

			if (currentWidth == 1 && currentHeight == 1)
			{
				break;
			}

			var nextWidth = currentWidth == 1 ? 1 : Math.Max(1, (int)MathF.Floor(currentWidth * 0.75f));
			var nextHeight = currentHeight == 1 ? 1 : Math.Max(1, (int)MathF.Floor(currentHeight * 0.75f));
			if (nextWidth == currentWidth && nextHeight == currentHeight)
			{
				break;
			}

			currentWidth = nextWidth;
			currentHeight = nextHeight;
		}

		return BytesFailure(CannotResizeMessage);
	}

	/// base64 载荷的字符数（ASCII 字节数）。pi 的 <c>maxBytes</c> 就是这个口径。
	public static long Base64Length(long byteCount) => (byteCount + 2) / 3 * 4;

	/// MIME 规范化（去参数、小写、<c>image/jpg</c> → <c>image/jpeg</c>）。
	/// 调用方用它判断「处理前后是不是同一种编码」，同编码且未重编码时可直接复用原始 base64。
	public static string NormalizeMimeType(string mimeType)
	{
		var value = mimeType;
		var separator = value.IndexOf(';');
		if (separator >= 0)
		{
			value = value[..separator];
		}

		value = value.Trim().ToLowerInvariant();
		return value == "image/jpg" ? "image/jpeg" : value;
	}

	private static bool TryNormalize(
		byte[] bytes,
		string mimeType,
		out byte[] normalized,
		out string normalizedMime,
		out string? convertedFrom)
	{
		// 以字节头为准：扩展名/附件 mime 可能和内容不符（把 BMP 存成 .png 也会发生）。
		// pi 的 normalize 同样以 sniff 出来的类型为准。
		normalizedMime = ImageFormats.TryDetect(bytes, out _, out var detectedMime)
			? detectedMime
			: NormalizeMimeType(mimeType);
		convertedFrom = null;

		if (normalizedMime is "image/png" or "image/jpeg" or "image/gif" or "image/webp")
		{
			// 原样返回同一个数组：调用方只读，省一次整图拷贝。
			normalized = bytes;
			return true;
		}

		// 其余可解码格式（BMP）：解成 RGBA 再写成 PNG。EXIF 方向在这里也一并应用，
		// 保证转出的 PNG 是「显示方向」。
		if (!ImageFormats.TryDecodeRgba8(bytes, out var rgba, out var width, out var height, out var orientation))
		{
			normalized = [];
			normalizedMime = string.Empty;
			return false;
		}

		convertedFrom = normalizedMime.Length > 0 ? normalizedMime : null;

		(rgba, width, height) = OrientationTransform.Apply(rgba, width, height, orientation);
		normalized = PngWriter.Encode(rgba, width, height);
		normalizedMime = "image/png";
		return true;
	}

	/// 先挑 PNG 与 JPEG（配置质量）里较小的，再依次试更低质量；都不行就交给调用方缩尺寸。
	private static bool TryPick(
		byte[] png,
		byte[] jpeg,
		byte[] rgba,
		int width,
		int height,
		IReadOnlyList<int> qualities,
		long maxBytes,
		out byte[] data,
		out string mimeType)
	{
		if (jpeg.Length < png.Length)
		{
			if (Base64Length(jpeg.Length) < maxBytes)
			{
				data = jpeg;
				mimeType = "image/jpeg";
				return true;
			}

			if (Base64Length(png.Length) < maxBytes)
			{
				data = png;
				mimeType = "image/png";
				return true;
			}
		}
		else
		{
			if (Base64Length(png.Length) < maxBytes)
			{
				data = png;
				mimeType = "image/png";
				return true;
			}

			if (Base64Length(jpeg.Length) < maxBytes)
			{
				data = jpeg;
				mimeType = "image/jpeg";
				return true;
			}
		}

		for (var index = 1; index < qualities.Count; index++)
		{
			var candidate = JpegEncoder.Encode(
				rgba,
				width,
				height,
				channels: 4,
				new JpegEncodeOptions { Quality = qualities[index] });
			if (Base64Length(candidate.Length) < maxBytes)
			{
				data = candidate;
				mimeType = "image/jpeg";
				return true;
			}
		}

		data = [];
		mimeType = string.Empty;
		return false;
	}

	private static IReadOnlyList<int> Qualities(int configured)
	{
		var values = new List<int>(LowerJpegQualities.Length + 1);
		var seen = new HashSet<int>();
		var candidates = new int[LowerJpegQualities.Length + 1];
		candidates[0] = configured;
		LowerJpegQualities.CopyTo(candidates, 1);
		foreach (var quality in candidates)
		{
			if (quality is >= 1 and <= 100 && seen.Add(quality))
			{
				values.Add(quality);
			}
		}

		if (values.Count == 0)
		{
			values.Add(ImageResizeOptions.DefaultJpegQuality);
		}

		return values;
	}

	/// pi 的尺寸算法：先压宽、再压高，四舍五入，最小 1 像素。
	private static (int Width, int Height) TargetSize(int width, int height, int maxWidth, int maxHeight)
	{
		var targetWidth = width;
		var targetHeight = height;
		if (targetWidth > maxWidth)
		{
			targetHeight = (int)MathF.Floor(targetHeight * (float)maxWidth / targetWidth + 0.5f);
			targetWidth = maxWidth;
		}

		if (targetHeight > maxHeight)
		{
			targetWidth = (int)MathF.Floor(targetWidth * (float)maxHeight / targetHeight + 0.5f);
			targetHeight = maxHeight;
		}

		return (Math.Max(1, targetWidth), Math.Max(1, targetHeight));
	}

	private static string? ConversionHint(string? from, string to)
	{
		if (from is null || from.Length == 0 || string.Equals(from, to, StringComparison.Ordinal))
		{
			return null;
		}

		return $"[Image converted from {from} to {to}.]";
	}

	private static string DimensionNote(int originalWidth, int originalHeight, int width, int height)
	{
		var scale = (double)originalWidth / width;
		return string.Create(
			CultureInfo.InvariantCulture,
			$"[Image: original {originalWidth}x{originalHeight}, displayed at {width}x{height}. Multiply coordinates by {scale:0.00} to map to original image.]");
	}

	private static ImageProcessResult Failure(string message) => new()
	{
		Ok = false,
		Error = message,
	};

	private static ImageProcessBytesResult BytesFailure(string message) => new()
	{
		Ok = false,
		Error = message,
	};

	private static ImageProcessBytesResult BytesSuccess(
		byte[] bytes,
		string mimeType,
		IReadOnlyList<string> hints,
		bool wasResized) => new()
		{
			Ok = true,
			Bytes = bytes,
			MimeType = mimeType,
			Hints = hints,
			WasResized = wasResized,
		};

	private static ImageProcessResult Success(
		string data,
		string mimeType,
		IReadOnlyList<string> hints,
		bool wasResized) => new()
		{
			Ok = true,
			Data = data,
			MimeType = mimeType,
			Hints = hints,
			WasResized = wasResized,
		};
}
