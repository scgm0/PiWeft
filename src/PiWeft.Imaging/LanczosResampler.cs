using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PiWeft.Imaging;

/// 可分离的 Lanczos3 重采样，带预乘 alpha。核与 pi 的 Photon <c>Lanczos3</c> 相同；
/// 缩小时按比例放宽核半径，否则大比例缩小会走样。
/// <para>
/// 预乘是为透明像素的边缘：直接对未预乘 RGBA 加权，透明区域里隐藏的颜色会渗进边缘；
/// 预乘后滤波、最后除回去，透明像素才不会被染色。Photon 走未预乘路径，这里选了视觉上更正确的一侧。
/// </para>
/// <para>
/// 三处性能安排：核的源下标与权重在构造轴时一次算完，内层循环没有 clamp 与除法；
/// 纵向加权用 <see cref="Vector{T}"/> 一次累加 4–8 个像素；分带缓冲从
/// <see cref="ArrayPool{T}"/> 取并跨带复用，没有整幅 float 中间图，也不制造 LOH 垃圾。
/// 不透明图写出时走 alpha≈1 快路径，省掉三次除法。
/// </para>
internal static class LanczosResampler
{
	private const float KernelRadius = 3f;

	private const int MaxBandRows = 64;

	/// 一条水平重采样缓存的行数上限；分带高度 = 该值 / 纵向比例，避免缩小 10 倍时一次缓存上千行。
	private const int MaxBufferedRows = 192;

	public static byte[] Resize(
		byte[] rgba,
		int width,
		int height,
		int targetWidth,
		int targetHeight)
	{
		if (targetWidth == width && targetHeight == height)
		{
			return [.. rgba];
		}

		var hasAlpha = HasTransparency(rgba);
		var xAxis = BuildAxis(width, targetWidth, reverse: true);
		var yAxis = BuildAxis(height, targetHeight, reverse: false);

		var output = new byte[targetWidth * targetHeight * 4];
		var rowLength = targetWidth * 4;
		var band = Math.Clamp((int)(MaxBufferedRows / MathF.Max(1f, yAxis.Scale)), 1, MaxBandRows);
		var plan = BuildPlan(targetHeight, band, yAxis);

		// 分带之间写的是不同的目标行、读的只是只读源像素：彼此独立，可并行。
		// 每个分带的浮点运算顺序固定，输出与单线程逐带计算一致。
		var options = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
		};
		Parallel.For(
			0,
			plan.Count,
			options,
			index => ProcessBand(rgba, width, height, hasAlpha, xAxis, yAxis, plan[index], rowLength, output));
		return output;
	}

	private static List<Band> BuildPlan(int targetHeight, int bandSize, Axis yAxis)
	{
		var plan = new List<Band>((targetHeight + bandSize - 1) / bandSize);
		for (var targetY = 0; targetY < targetHeight; targetY += bandSize)
		{
			var bandEnd = Math.Min(targetHeight, targetY + bandSize);
			var sourceStart = int.MaxValue;
			var sourceEnd = 0;
			for (var y = targetY; y < bandEnd; y++)
			{
				sourceStart = Math.Min(sourceStart, yAxis.Starts[y]);
				sourceEnd = Math.Max(sourceEnd, yAxis.Starts[y] + yAxis.Counts[y]);
			}

			plan.Add(new Band(targetY, bandEnd, sourceStart, sourceEnd));
		}

		return plan;
	}

	private static void ProcessBand(
		byte[] rgba,
		int width,
		int height,
		bool hasAlpha,
		Axis xAxis,
		Axis yAxis,
		Band band,
		int rowLength,
		byte[] output)
	{
		var rowCount = band.SourceEnd - band.SourceStart;
		var rows = ArrayPool<float>.Shared.Rent(rowCount * rowLength);
		var accumulator = ArrayPool<float>.Shared.Rent(rowLength);
		try
		{
			var bandSpan = rows.AsSpan(0, rowCount * rowLength);
			for (var logicalY = band.SourceStart; logicalY < band.SourceEnd; logicalY++)
			{
				// 核在边缘会伸出 [0, height)：逻辑下标保留（纵向加权还要按它取行），
				// 真正读像素时夹到边界，等价于复制边缘像素。
				var sourceY = Math.Clamp(logicalY, 0, height - 1);
				ResampleRowInto(
					rgba,
					width,
					sourceY,
					hasAlpha,
					xAxis,
					bandSpan.Slice((logicalY - band.SourceStart) * rowLength, rowLength));
			}

			for (var y = band.TargetStart; y < band.TargetEnd; y++)
			{
				var acc = accumulator.AsSpan(0, rowLength);
				acc.Clear();
				var start = yAxis.Starts[y];
				var count = yAxis.Counts[y];
				var weightOffset = yAxis.Offsets[y];
				for (var k = 0; k < count; k++)
				{
					var weight = yAxis.Weights[weightOffset + k];
					if (weight == 0f)
					{
						continue;
					}

					var source = bandSpan.Slice((start + k - band.SourceStart) * rowLength, rowLength);
					Accumulate(acc, source, weight);
				}

				WriteRow(acc, output, y * rowLength);
			}
		}
		finally
		{
			ArrayPool<float>.Shared.Return(rows);
			ArrayPool<float>.Shared.Return(accumulator);
		}
	}

	private readonly record struct Band(int TargetStart, int TargetEnd, int SourceStart, int SourceEnd);

	private static void Accumulate(Span<float> accumulator, ReadOnlySpan<float> source, float weight)
	{
		var vectorWeight = new Vector<float>(weight);
		var index = 0;
		for (; index <= accumulator.Length - Vector<float>.Count; index += Vector<float>.Count)
		{
			var sum = new Vector<float>(accumulator[index..]) + new Vector<float>(source[index..]) * vectorWeight;
			sum.CopyTo(accumulator[index..]);
		}

		for (; index < accumulator.Length; index++)
		{
			accumulator[index] += source[index] * weight;
		}
	}

	/// 水平重采样一整行源像素（源驱动散射，输出预乘后的 RGBA）：
	/// 每个源像素只做一次「取字节 → 预乘」，再按预计算的反向权重加到它覆盖的每个输出像素上。
	/// 比「每个输出像素反向收集十几个源像素」少一半以上的乘法与字节加载，而且源行顺序访问。
	/// 四个通道用 ref 局部变量写，省掉每次写入的边界检查；不透明图连预乘都省掉。
	private static void ResampleRowInto(
		byte[] source,
		int width,
		int sourceY,
		bool hasAlpha,
		Axis axis,
		Span<float> target)
	{
		target.Clear();
		var rowOffset = sourceY * width * 4;
		var outputs = axis.ReverseOutputs;
		var weights = axis.ReverseWeights;
		var offsets = axis.ReverseOffsets;
		var counts = axis.ReverseCounts;
		ref var targetStart = ref MemoryMarshal.GetReference(target);

		for (var sourceX = 0; sourceX < width; sourceX++)
		{
			var count = counts[sourceX];
			if (count == 0)
			{
				continue;
			}

			var pixel = rowOffset + sourceX * 4;
			float red, green, blue, alpha;
			if (hasAlpha)
			{
				alpha = source[pixel + 3] * (1f / 255f);
				red = source[pixel] * alpha;
				green = source[pixel + 1] * alpha;
				blue = source[pixel + 2] * alpha;
			}
			else
			{
				alpha = 1f;
				red = source[pixel];
				green = source[pixel + 1];
				blue = source[pixel + 2];
			}

			var offset = offsets[sourceX];
			for (var pair = offset; pair < offset + count; pair++)
			{
				var weight = weights[pair];
				if (weight == 0f)
				{
					continue;
				}

				ref var destination = ref Unsafe.Add(ref targetStart, outputs[pair] * 4);
				destination += red * weight;
				Unsafe.Add(ref destination, 1) += green * weight;
				Unsafe.Add(ref destination, 2) += blue * weight;
				if (hasAlpha)
				{
					Unsafe.Add(ref destination, 3) += alpha * weight;
				}
			}
		}

		if (!hasAlpha)
		{
			// 权重已归一化：不透明图的 alpha 恒为 1，直接写，不必逐 tap 累加。
			for (var index = 3; index < target.Length; index += 4)
			{
				target[index] = 1f;
			}
		}
	}

	private static void WriteRow(ReadOnlySpan<float> accumulator, byte[] output, int offset)
	{
		for (var i = 0; i < accumulator.Length; i += 4)
		{
			var alpha = accumulator[i + 3];
			if (alpha <= 1e-6f)
			{
				output[offset + i] = 0;
				output[offset + i + 1] = 0;
				output[offset + i + 2] = 0;
				output[offset + i + 3] = 0;
				continue;
			}

			// 不透明图（权重和为 1，alpha 刚好 ≈1）走乘 255 的快路径，省掉三次除法。
			var inverse = alpha >= 0.999f ? 255f : 255f / alpha;
			output[offset + i] = ToByte(accumulator[i] * inverse);
			output[offset + i + 1] = ToByte(accumulator[i + 1] * inverse);
			output[offset + i + 2] = ToByte(accumulator[i + 2] * inverse);
			output[offset + i + 3] = ToByte(alpha * 255f);
		}
	}

	private static byte ToByte(float value) =>
		(byte)Math.Clamp((int)(value + 0.5f), 0, 255);

	private static Axis BuildAxis(int sourceLength, int targetLength, bool reverse)
	{
		var scale = (float)sourceLength / targetLength;
		// 缩小时按比例放宽核半径，让每个输出像素覆盖足够多的源像素（抗混叠）。
		var filterScale = MathF.Max(1f, scale);
		var support = KernelRadius * filterScale;

		var starts = new int[targetLength];
		var counts = new int[targetLength];
		var offsets = new int[targetLength];
		var indices = new List<int>(targetLength * 7);
		var weights = new List<float>(targetLength * 7);

		for (var i = 0; i < targetLength; i++)
		{
			var center = (i + 0.5f) * scale - 0.5f;
			var left = (int)MathF.Ceiling(center - support);
			var right = (int)MathF.Floor(center + support);
			if (right < left)
			{
				left = (int)MathF.Floor(center);
				right = left + 1;
			}

			var count = right - left + 1;
			starts[i] = left;
			counts[i] = count;
			offsets[i] = weights.Count;

			var total = 0f;
			for (var source = left; source <= right; source++)
			{
				var weight = Lanczos3((source - center) / filterScale);
				indices.Add(Math.Clamp(source, 0, sourceLength - 1));
				weights.Add(weight);
				total += weight;
			}

			if (MathF.Abs(total) > 1e-6f)
			{
				var inverse = 1f / total;
				for (var k = 0; k < count; k++)
				{
					weights[offsets[i] + k] *= inverse;
				}
			}
		}

		var axis = new Axis
		{
			Starts = starts,
			Counts = counts,
			Offsets = offsets,
			Indices = [.. indices],
			Weights = [.. weights],
			Scale = scale,
			ReverseOffsets = [],
			ReverseCounts = [],
			ReverseOutputs = [],
			ReverseWeights = [],
		};

		if (reverse)
		{
			BuildReverseMap(axis, indices, weights, starts, counts, offsets, sourceLength);
		}

		return axis;
	}

	/// 反向索引：每个**源**像素贡献到哪些输出像素、权重多少（CSR，源下标连续）。
	/// 水平重采样据此顺序扫源行、散射累加到目标行，避免每个输出像素重复读源字节。
	private static void BuildReverseMap(
		Axis axis,
		List<int> indices,
		List<float> weights,
		int[] starts,
		int[] counts,
		int[] offsets,
		int sourceLength)
	{
		var reverseCounts = new int[sourceLength];
		for (var x = 0; x < counts.Length; x++)
		{
			for (var k = 0; k < counts[x]; k++)
			{
				reverseCounts[indices[offsets[x] + k]]++;
			}
		}

		var reverseStarts = new int[sourceLength];
		var total = 0;
		for (var index = 0; index < sourceLength; index++)
		{
			reverseStarts[index] = total;
			total += reverseCounts[index];
		}

		var reverseOutputs = new int[total];
		var reverseWeights = new float[total];
		var cursor = new int[sourceLength];
		Array.Copy(reverseStarts, cursor, sourceLength);
		for (var x = 0; x < counts.Length; x++)
		{
			for (var k = 0; k < counts[x]; k++)
			{
				var source = indices[offsets[x] + k];
				var position = cursor[source]++;
				reverseOutputs[position] = x;
				reverseWeights[position] = weights[offsets[x] + k];
			}
		}

		axis.ReverseOffsets = reverseStarts;
		axis.ReverseCounts = reverseCounts;
		axis.ReverseOutputs = reverseOutputs;
		axis.ReverseWeights = reverseWeights;
	}

	private static float Lanczos3(float x)
	{
		if (x == 0f)
		{
			return 1f;
		}

		if (x <= -KernelRadius || x >= KernelRadius)
		{
			return 0f;
		}

		var piX = MathF.PI * x;
		return KernelRadius * MathF.Sin(piX) * MathF.Sin(piX / KernelRadius) / (piX * piX);
	}

	private sealed class Axis
	{
		public required int[] Starts { get; init; }

		public required int[] Counts { get; init; }

		public required int[] Offsets { get; init; }

		/// 每个 tap 的源下标（已夹到 [0, 源长度)），内层循环不再需要边界判断。
		public required int[] Indices { get; init; }

		public required float[] Weights { get; init; }

		/// 源/目标长度比；用于决定分带高度。
		public required float Scale { get; init; }

		/// 反向索引（仅水平轴需要）。
		public required int[] ReverseOffsets { get; set; }

		public required int[] ReverseCounts { get; set; }

		public required int[] ReverseOutputs { get; set; }

		public required float[] ReverseWeights { get; set; }
	}

	/// 整幅扫描 alpha 通道，判断是否需要预乘。绝大多数照片/截图都不透明，
	/// 省掉预乘能让水平重采样少一半乘法。
	private static bool HasTransparency(byte[] rgba)
	{
		var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
		var alphaMask = new Vector<uint>(BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu);
		var opaque = new Vector<uint>(BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu);
		var index = 0;
		for (; index <= words.Length - Vector<uint>.Count; index += Vector<uint>.Count)
		{
			var masked = new Vector<uint>(words[index..]) & alphaMask;
			if (!Vector.EqualsAll(masked, opaque))
			{
				return true;
			}
		}

		for (; index < words.Length; index++)
		{
			if ((words[index] & (BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu)) !=
				(BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu))
			{
				return true;
			}
		}

		return false;
	}
}
