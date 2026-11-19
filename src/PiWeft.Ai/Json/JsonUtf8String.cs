using System.Buffers;
using System.Text.Json;

namespace PiWeft.Ai.Json;

/// 把延迟存储的 UTF-8 正文直接写进 <see cref="Utf8JsonWriter"/>，不经过 UTF-16。
/// 大工具结果读进来只留 UTF-8，写回时再解码成字符串等于把省下的分配原样付回去：
/// 50 KiB 正文每行多出 100 KiB 临时字符串，600 行就是 60 MB。
internal static class JsonUtf8String
{
	/// <summary>写一个 JSON 字符串值（含引号），转义规则与 <c>Utf8JsonWriter.WriteStringValue</c> 一致。</summary>
	public static void Write(Utf8JsonWriter writer, ReadOnlySpan<byte> utf8)
	{
		if (!NeedsEscape(utf8))
		{
			var plain = ArrayPool<byte>.Shared.Rent(utf8.Length + 2);
			try
			{
				plain[0] = (byte)'"';
				utf8.CopyTo(plain.AsSpan(1));
				plain[utf8.Length + 1] = (byte)'"';
				writer.WriteRawValue(plain.AsSpan(0, utf8.Length + 2), skipInputValidation: true);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(plain);
			}

			return;
		}

		// 最坏 6 倍：控制字符写成 \u00XX。
		var escaped = ArrayPool<byte>.Shared.Rent((utf8.Length * 6) + 2);
		try
		{
			var written = 0;
			escaped[written++] = (byte)'"';
			var index = 0;
			while (index < utf8.Length)
			{
				// 用 SearchValues 找下一个要转义的字节，中间整段复制，不再逐字节分支。
				var relative = utf8[index..].IndexOfAny(Escapable);
				if (relative < 0)
				{
					utf8[index..].CopyTo(escaped.AsSpan(written));
					written += utf8.Length - index;
					break;
				}

				var next = index + relative;
				if (next > index)
				{
					utf8[index..next].CopyTo(escaped.AsSpan(written));
					written += next - index;
				}

				var value = utf8[next];
				switch (value)
				{
					case (byte)'"':
						escaped[written++] = (byte)'\\';
						escaped[written++] = (byte)'"';
						break;
					case (byte)'\\':
						escaped[written++] = (byte)'\\';
						escaped[written++] = (byte)'\\';
						break;
					case (byte)'\b':
						escaped[written++] = (byte)'\\';
						escaped[written++] = (byte)'b';
						break;
					case (byte)'\f':
						escaped[written++] = (byte)'\\';
						escaped[written++] = (byte)'f';
						break;
					case (byte)'\n':
						escaped[written++] = (byte)'\\';
						escaped[written++] = (byte)'n';
						break;
					case (byte)'\r':
						escaped[written++] = (byte)'\\';
						escaped[written++] = (byte)'r';
						break;
					case (byte)'\t':
						escaped[written++] = (byte)'\\';
						escaped[written++] = (byte)'t';
						break;
					default:
						escaped[written++] = (byte)'\\';
						escaped[written++] = (byte)'u';
						escaped[written++] = (byte)'0';
						escaped[written++] = (byte)'0';
						escaped[written++] = Hex(value >> 4);
						escaped[written++] = Hex(value & 0xF);
						break;
				}

				index = next + 1;
			}

			escaped[written++] = (byte)'"';
			writer.WriteRawValue(escaped.AsSpan(0, written), skipInputValidation: true);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(escaped);
		}
	}

	private static bool NeedsEscape(ReadOnlySpan<byte> utf8) => utf8.IndexOfAny(Escapable) >= 0;

	/// <summary>
	/// 这些多字节码点在 <see cref="Utf8JsonWriter"/> 里会被转义：C1 控制符（U+0080–U+009F）
	/// 与 U+2028 / U+2029。本类只处理单字节转义，调用方遇到它们要走字符串路径。
	/// </summary>
	public static bool NeedsStringPath(ReadOnlySpan<byte> utf8)
	{
		for (var index = 0; index < utf8.Length; index++)
		{
			var value = utf8[index];
			// 非 BMP（emoji 与扩展 CJK）：encoder 会写成一对 \uXXXX。
			if (value is >= 0xF0 and <= 0xF4)
			{
				return true;
			}

			if (value == 0xC2 && index + 1 < utf8.Length && utf8[index + 1] is >= 0x80 and <= 0x9F)
			{
				return true;
			}

			if (value == 0xE2 && index + 2 < utf8.Length && utf8[index + 1] == 0x80 && utf8[index + 2] is 0xA8 or 0xA9)
			{
				return true;
			}
		}

		return false;
	}

	private static readonly SearchValues<byte> Escapable = SearchValues.Create(BuildEscapable());

	private static byte[] BuildEscapable()
	{
		var values = new byte[35];
		for (var index = 0; index < 0x20; index++)
		{
			values[index] = (byte)index;
		}

		values[0x20] = (byte)'"';
		values[0x21] = (byte)'\\';
		values[0x22] = 0x7F;
		return values;
	}

	private static byte Hex(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
}
