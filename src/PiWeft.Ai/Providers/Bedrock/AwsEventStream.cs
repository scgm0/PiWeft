using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PiWeft.Ai.Providers.Bedrock;

/// <summary>一条解码后的 AWS 事件流消息。</summary>
public sealed record AwsEventStreamMessage
{
	/// <summary>消息头，例如 <c>:event-type</c> 与 <c>:message-type</c>。</summary>
	public required IReadOnlyDictionary<string, string> Headers { get; init; }

	/// <summary>原始 payload 字节。</summary>
	public required byte[] Payload { get; init; }

	public string? EventType => Headers.GetValueOrDefault(":event-type");

	public string? MessageType => Headers.GetValueOrDefault(":message-type");

	public string? ErrorCode => Headers.GetValueOrDefault(":error-code");

	public string? ErrorMessage => Headers.GetValueOrDefault(":error-message");

	public bool IsEvent => MessageType is null or "event";

	public bool IsError => MessageType is "error" or "exception";

	/// <summary>把 payload 当作 JSON 解析。</summary>
	public JsonDocument? ParseJson()
	{
		if (Payload.Length == 0)
		{
			return null;
		}

		return JsonDocument.Parse(Payload);
	}
}

/// <summary>
/// <c>application/vnd.amazon.eventstream</c> 分帧的解码器，Bedrock 等 AWS 流式 API 都用这种格式。
/// 每条消息依次为：总长度、头部长度、前导 CRC、头部、payload、消息 CRC —— 全部大端序。
/// </summary>
public static class AwsEventStream
{
	/// <summary>每条消息都以三个 4 字节字开头。</summary>
	public const int PreludeLength = 12;

	/// <summary>前导部分长度、头部、payload 与结尾的 CRC。</summary>
	public const int TrailerLength = 4;

	/// <summary>从 <paramref name="stream"/> 中读取消息直到流结束。</summary>
	public static async IAsyncEnumerable<AwsEventStreamMessage> ReadAsync(
		Stream stream,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var prelude = new byte[PreludeLength];

		while (true)
		{
			if (!await ReadExactlyAsync(stream, prelude, cancellationToken).ConfigureAwait(false))
			{
				yield break;
			}

			var totalLength = (int)BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(0, 4));
			var headersLength = (int)BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(4, 4));
			var preludeCrc = BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(8, 4));

			if (totalLength < PreludeLength + TrailerLength || headersLength < 0 || headersLength > totalLength - PreludeLength - TrailerLength)
			{
				throw new InvalidDataException($"Malformed event stream frame: total={totalLength} headers={headersLength}");
			}

			// 前导 CRC 只覆盖两个长度字，不含 CRC 字段自身。
			if (Crc32.Compute(prelude.AsSpan(0, 8)) != preludeCrc)
			{
				throw new InvalidDataException("Event stream prelude CRC mismatch");
			}

			var rest = new byte[totalLength - PreludeLength];
			if (!await ReadExactlyAsync(stream, rest, cancellationToken).ConfigureAwait(false))
			{
				yield break; // 流在一帧中间结束，等同于连接被关闭
			}

			// 消息 CRC 覆盖前导、头部与 payload，不含 CRC 自身。
			var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(rest.AsSpan(rest.Length - TrailerLength, TrailerLength));
			var coveredLength = PreludeLength + rest.Length - TrailerLength;
			var covered = System.Buffers.ArrayPool<byte>.Shared.Rent(coveredLength);
			uint actualCrc;
			try
			{
				prelude.CopyTo(covered, 0);
				rest.AsSpan(0, rest.Length - TrailerLength).CopyTo(covered.AsSpan(PreludeLength));
				actualCrc = Crc32.Compute(covered.AsSpan(0, coveredLength));
			}
			finally
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(covered);
			}

			if (actualCrc != expectedCrc)
			{
				throw new InvalidDataException("Event stream message CRC mismatch");
			}

			var headers = ParseHeaders(rest.AsSpan(0, headersLength));
			var payload = rest.AsSpan(headersLength, rest.Length - headersLength - TrailerLength).ToArray();

			yield return new AwsEventStreamMessage { Headers = headers, Payload = payload };
		}
	}

	/// <summary>解析一条消息的头部区块。</summary>
	public static Dictionary<string, string> ParseHeaders(ReadOnlySpan<byte> headers)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		var index = 0;

		while (index < headers.Length)
		{
			var nameLength = headers[index++];
			if (nameLength == 0 || index + nameLength > headers.Length)
			{
				break;
			}

			var name = Encoding.UTF8.GetString(headers.Slice(index, nameLength));
			index += nameLength;

			if (index >= headers.Length)
			{
				break;
			}

			var type = headers[index++];
			switch (type)
			{
				case 0: // 布尔真
					result[name] = "true";
					break;
				case 1: // 布尔假
					result[name] = "false";
					break;
				case 2: // 字节
					result[name] = headers[index].ToString();
					index += 1;
					break;
				case 3: // 短整型
					result[name] = BinaryPrimitives.ReadInt16BigEndian(headers[index..]).ToString();
					index += 2;
					break;
				case 4: // 整型
					result[name] = BinaryPrimitives.ReadInt32BigEndian(headers[index..]).ToString();
					index += 4;
					break;
				case 5: // 长整型
					result[name] = BinaryPrimitives.ReadInt64BigEndian(headers[index..]).ToString();
					index += 8;
					break;
				case 6: // 字节数组
				{
					var length = BinaryPrimitives.ReadUInt16BigEndian(headers[index..]);
					index += 2;
					result[name] = Convert.ToBase64String(headers.Slice(index, length));
					index += length;
					break;
				}
				case 7: // 字符串
				{
					var length = BinaryPrimitives.ReadUInt16BigEndian(headers[index..]);
					index += 2;
					result[name] = Encoding.UTF8.GetString(headers.Slice(index, length));
					index += length;
					break;
				}
				case 8: // 时间戳
					result[name] = BinaryPrimitives.ReadInt64BigEndian(headers[index..]).ToString();
					index += 8;
					break;
				case 9: // UUID
				{
					var bytes = headers.Slice(index, 16);
					result[name] = new Guid(bytes, bigEndian: true).ToString();
					index += 16;
					break;
				}
				default:
					// 出现未知的头类型时，后面的内容无法可靠解析。
					return result;
			}
		}

		return result;
	}

	/// <summary>编码一条消息。测试以及需要提供该分帧的调用方会用到。</summary>
	public static byte[] Encode(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> payload)
	{
		var headerBlock = EncodeHeaders(headers);
		var totalLength = PreludeLength + headerBlock.Length + payload.Length + TrailerLength;

		var message = new byte[totalLength];
		BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(0, 4), (uint)totalLength);
		BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4, 4), (uint)headerBlock.Length);
		BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8, 4), Crc32.Compute(message.AsSpan(0, 8)));
		headerBlock.CopyTo(message.AsSpan(PreludeLength));
		payload.CopyTo(message.AsSpan(PreludeLength + headerBlock.Length));
		BinaryPrimitives.WriteUInt32BigEndian(
			message.AsSpan(totalLength - TrailerLength, 4),
			Crc32.Compute(message.AsSpan(0, totalLength - TrailerLength)));

		return message;
	}

	private static byte[] EncodeHeaders(IReadOnlyDictionary<string, string> headers)
	{
		var buffer = new List<byte>(64);
		foreach (var (name, value) in headers)
		{
			var nameBytes = Encoding.UTF8.GetBytes(name);
			buffer.Add((byte)nameBytes.Length);
			buffer.AddRange(nameBytes);
			buffer.Add(7); // 字符串类型

			var valueBytes = Encoding.UTF8.GetBytes(value);
			buffer.Add((byte)(valueBytes.Length >> 8));
			buffer.Add((byte)(valueBytes.Length & 0xFF));
			buffer.AddRange(valueBytes);
		}

		return [.. buffer];
	}

	private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
	{
		var offset = 0;
		while (offset < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				return offset == 0 ? false : throw new InvalidDataException("Event stream ended mid-frame");
			}

			offset += read;
		}

		return true;
	}
}

/// <summary>AWS 使用的 CRC32 变体（IEEE 802.3，反射实现，与 zlib 相同）。</summary>
internal static class Crc32
{
	private static readonly uint[] Table = BuildTable();

	public static uint Compute(ReadOnlySpan<byte> data)
	{
		var crc = 0xFFFFFFFFu;
		foreach (var b in data)
		{
			crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
		}

		return crc ^ 0xFFFFFFFFu;
	}

	private static uint[] BuildTable()
	{
		var table = new uint[256];
		for (uint i = 0; i < 256; i++)
		{
			var value = i;
			for (var bit = 0; bit < 8; bit++)
			{
				value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
			}

			table[i] = value;
		}

		return table;
	}
}
