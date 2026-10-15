using System.Buffers.Binary;
using System.IO.Compression;

namespace PiWeft.Desktop;

/// <summary>8 位 BGRA 位图：每行紧密排列，跨度为 <c>Width * 4</c>。</summary>
internal sealed record PixelImage(int Width, int Height, byte[] Bgra)
{
	public static PixelImage Create(int width, int height) => new(width, height, new byte[width * height * 4]);
}

/// <summary>
/// PNG **编码**（8 位 RGBA）。解码不需要自己写：MewUI 的 <c>ImageDecoders</c> 公开提供 PNG/JPEG/BMP/ICO
/// 解码与 <c>ImageSource.CopyPixels</c> 像素读取，只有「把像素编回 PNG」这一侧框架没有，
/// 而 Windows 的 CF_DIB 剪贴板需要它。整份实现只用 BCL，NativeAOT 下可用。
/// </summary>
internal static class PngCodec
{
	private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

	private static ReadOnlySpan<byte> Ihdr => "IHDR"u8;
	private static ReadOnlySpan<byte> Idat => "IDAT"u8;
	private static ReadOnlySpan<byte> Iend => "IEND"u8;

	public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

	/// <summary>把 BGRA 位图编码为 8 位 RGBA PNG；逐行按最小绝对差启发式选滤波器。</summary>
	public static byte[] Encode(PixelImage image)
	{
		var width = image.Width;
		var height = image.Height;
		var stride = width * 4;
		var raw = new byte[(stride + 1) * height];
		var current = new byte[stride];
		var previous = new byte[stride];
		var filtered = new byte[stride];
		var best = new byte[stride];

		for (var y = 0; y < height; y++)
		{
			var row = image.Bgra.AsSpan(y * stride, stride);
			for (var x = 0; x < width; x++)
			{
				current[x * 4] = row[x * 4 + 2];
				current[x * 4 + 1] = row[x * 4 + 1];
				current[x * 4 + 2] = row[x * 4];
				current[x * 4 + 3] = row[x * 4 + 3];
			}

			byte chosen = 0;
			var bestScore = long.MaxValue;
			for (byte filter = 0; filter <= 4; filter++)
			{
				ApplyFilter(filter, current, previous, filtered);
				long score = 0;
				foreach (var value in filtered) score += value < 128 ? value : 256 - value;
				if (score >= bestScore) continue;
				bestScore = score;
				chosen = filter;
				filtered.CopyTo(best, 0);
			}

			raw[y * (stride + 1)] = chosen;
			best.CopyTo(raw, y * (stride + 1) + 1);
			current.CopyTo(previous, 0);
		}

		var header = new byte[13];
		BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
		BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
		header[8] = 8;
		header[9] = 6;

		using var output = new MemoryStream();
		output.Write(Signature);
		WriteChunk(output, Ihdr, header);
		using (var compressed = new MemoryStream())
		{
			using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
			WriteChunk(output, Idat, compressed.ToArray());
		}

		WriteChunk(output, Iend, []);
		return output.ToArray();
	}

	private static void ApplyFilter(byte filter, ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, Span<byte> target)
	{
		const int bytesPerPixel = 4;
		for (var i = 0; i < target.Length; i++)
		{
			var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
			var up = previous[i];
			var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
			target[i] = filter switch
			{
				1 => (byte)(current[i] - left),
				2 => (byte)(current[i] - up),
				3 => (byte)(current[i] - (left + up) / 2),
				4 => (byte)(current[i] - Paeth(left, up, upLeft)),
				_ => current[i],
			};
		}
	}

	private static int Paeth(int left, int up, int upLeft)
	{
		var estimate = left + up - upLeft;
		var toLeft = Math.Abs(estimate - left);
		var toUp = Math.Abs(estimate - up);
		var toUpLeft = Math.Abs(estimate - upLeft);
		if (toLeft <= toUp && toLeft <= toUpLeft) return left;
		return toUp <= toUpLeft ? up : upLeft;
	}

	private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
	{
		Span<byte> header = stackalloc byte[8];
		BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
		type.CopyTo(header[4..]);
		output.Write(header);
		output.Write(data);
		Span<byte> crc = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(type, data));
		output.Write(crc);
	}

	/// <summary>PNG 分块校验用的 CRC-32（IEEE）。</summary>
	private static class Crc32
	{
		private static readonly uint[] Table = Build();

		public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
		{
			var crc = 0xFFFFFFFFu;
			crc = Accumulate(crc, first);
			crc = Accumulate(crc, second);
			return crc ^ 0xFFFFFFFFu;
		}

		private static uint Accumulate(uint crc, ReadOnlySpan<byte> data)
		{
			foreach (var value in data) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
			return crc;
		}

		private static uint[] Build()
		{
			var table = new uint[256];
			for (var index = 0; index < table.Length; index++)
			{
				var value = (uint)index;
				for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
				table[index] = value;
			}
			return table;
		}
	}
}
