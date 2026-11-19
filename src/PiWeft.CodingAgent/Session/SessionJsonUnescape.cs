using System.Buffers;

namespace PiWeft.CodingAgent.Session;

/// 把 JSON 字符串的原始 UTF-8（含转义序列）直接解成 UTF-8 字节，不经过 UTF-16。
/// 会话里的工具输出几乎都含换行，`Utf8JsonReader.ValueIsEscaped` 恒为 true；
/// 先 `GetString()` 再编码回 UTF-8 会为每段正文多出一份等量的临时字符串。
internal static class SessionJsonUnescape
{
	/// <summary>解转义。成功时 <paramref name="utf8"/> 是最终字节，<paramref name="characterLength"/> 是码点数。</summary>
	public static bool TryUnescape(ReadOnlySpan<byte> source, out byte[] utf8, out int characterLength)
	{
		// 解转义后的长度未知，先租池化缓冲；最后只复制实际用到的那段，
		// 池数组不交给 TextContent 长期持有（它可能比正文大）。
		var rented = ArrayPool<byte>.Shared.Rent(source.Length);
		var written = 0;
		var characters = 0;

		try
		{
		for (var index = 0; index < source.Length; index++)
		{
			var value = source[index];
			if (value != (byte)'\\')
			{
				rented[written++] = value;
				// TextContent.TextLength 统一按 UTF-16 单位：非 BMP（4 字节序列）占两个。
				if ((value & 0xC0) != 0x80)
				{
					characters += value >= 0xF0 ? 2 : 1;
				}

				continue;
			}

			if (++index >= source.Length)
			{
				utf8 = [];
				characterLength = 0;
				return false;
			}

			switch (source[index])
			{
				case (byte)'"': rented[written++] = (byte)'"'; characters++; break;
				case (byte)'\\': rented[written++] = (byte)'\\'; characters++; break;
				case (byte)'/': rented[written++] = (byte)'/'; characters++; break;
				case (byte)'b': rented[written++] = (byte)'\b'; characters++; break;
				case (byte)'f': rented[written++] = (byte)'\f'; characters++; break;
				case (byte)'n': rented[written++] = (byte)'\n'; characters++; break;
				case (byte)'r': rented[written++] = (byte)'\r'; characters++; break;
				case (byte)'t': rented[written++] = (byte)'\t'; characters++; break;
				case (byte)'u':
				{
					if (index + 4 >= source.Length || !TryParseHex4(source.Slice(index + 1, 4), out var unit))
					{
						utf8 = [];
						characterLength = 0;
						return false;
					}

					index += 4;
					// 代理对：高代理后面紧跟 \uXXXX 低代理时合成一个码点。
					if (unit is >= 0xD800 and <= 0xDBFF && index + 6 < source.Length &&
						source[index + 1] == (byte)'\\' && source[index + 2] == (byte)'u' &&
						TryParseHex4(source.Slice(index + 3, 4), out var low) && low is >= 0xDC00 and <= 0xDFFF)
					{
						index += 6;
						written += EncodeRune(0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00), rented.AsSpan(written));
						// 代理对在 UTF-16 里是两个单位。
						characters += 2;
						break;
					}

					// 孤立代理项按 U+FFFD 处理，与 GetString 的行为一致。
					written += EncodeRune(unit is >= 0xD800 and <= 0xDFFF ? 0xFFFD : unit, rented.AsSpan(written));
					characters++;
					break;
				}

				default:
					utf8 = [];
					characterLength = 0;
					return false;
			}
		}

		utf8 = rented.AsSpan(0, written).ToArray();
		characterLength = characters;
		return true;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	private static bool TryParseHex4(ReadOnlySpan<byte> source, out int value)
	{
		value = 0;
		for (var index = 0; index < source.Length; index++)
		{
			var digit = source[index] switch
			{
				>= (byte)'0' and <= (byte)'9' => source[index] - '0',
				>= (byte)'a' and <= (byte)'f' => source[index] - 'a' + 10,
				>= (byte)'A' and <= (byte)'F' => source[index] - 'A' + 10,
				_ => -1,
			};
			if (digit < 0)
			{
				return false;
			}

			value = (value << 4) | digit;
		}

		return true;
	}

	private static int EncodeRune(int codePoint, Span<byte> destination)
	{
		if (codePoint < 0x80)
		{
			destination[0] = (byte)codePoint;
			return 1;
		}

		if (codePoint < 0x800)
		{
			destination[0] = (byte)(0xC0 | (codePoint >> 6));
			destination[1] = (byte)(0x80 | (codePoint & 0x3F));
			return 2;
		}

		if (codePoint < 0x10000)
		{
			destination[0] = (byte)(0xE0 | (codePoint >> 12));
			destination[1] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
			destination[2] = (byte)(0x80 | (codePoint & 0x3F));
			return 3;
		}

		destination[0] = (byte)(0xF0 | (codePoint >> 18));
		destination[1] = (byte)(0x80 | ((codePoint >> 12) & 0x3F));
		destination[2] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
		destination[3] = (byte)(0x80 | (codePoint & 0x3F));
		return 4;
	}
}
