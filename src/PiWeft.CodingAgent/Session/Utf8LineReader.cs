using System.Buffers;

namespace PiWeft.CodingAgent.Session;

/// Reads a UTF-8 file line by line without decoding each line to a string. The returned span is
/// backed by a pooled buffer and stays valid until the next <see cref="TryReadLine"/> call.
public sealed class Utf8LineReader : IDisposable
{
	private const int ReadBufferSize = 64 * 1024;
	private const int LineFeed = 10;      // '\n'
	private const int CarriageReturn = 13; // '\r'
	private const int InitialLineCapacity = 4 * 1024;

	private const int MaxPooledLineBuffer = 256 * 1024;

	private readonly FileStream _stream;
	private readonly int _maxLineBytes;
	private byte[] _readBuffer;
	private byte[] _lineBuffer;
	private int _readStart;
	private int _readEnd;
	private int _lineLength;
	private bool _discarding;
	private ReadOnlyMemory<byte> _currentLine;
	private bool _firstLine = true;
	private bool _skipPartialLine;
	private bool _disposed;

	/// <paramref name="fromBytes"/> 用于续读或分段并行扫：偏移落在行中间时，那条被切开的半行
	/// 会被自动跳过（归上一段），因此调用方总是按整行消费。
	/// <paramref name="maxLineBytes"/> 大于 0 时，超长行不再复制进缓冲，而是丢弃并返回空行；
	/// 扫描只需要行内少数字段时这能省掉几十 MB 的搬运。
	public Utf8LineReader(string path, long fromBytes = 0, int maxLineBytes = 0)
	{
		_maxLineBytes = Math.Max(0, maxLineBytes);
		_stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 1,
			FileOptions.SequentialScan);
		if (fromBytes > 0)
		{
			// 续读不是文件开头：BOM 只在真正的首行处理。
			_firstLine = false;
			_stream.Seek(fromBytes - 1, SeekOrigin.Begin);
			// 前一个字节不是换行说明这里切在行中间：那条整行归上一段。
			_skipPartialLine = _stream.ReadByte() is not (LineFeed or CarriageReturn);
			_stream.Seek(fromBytes, SeekOrigin.Begin);
		}
		_readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
		_lineBuffer = ArrayPool<byte>.Shared.Rent(InitialLineCapacity);
	}

	public long BytesRead => _stream.Position - (_readEnd - _readStart);

	/// <summary>最近一次 <see cref="TryReadLine"/> 返回的整行字节，不含换行与 BOM；下次读取前有效。</summary>
	public ReadOnlyMemory<byte> CurrentLineMemory => _currentLine;

	public bool TryReadLine(out ReadOnlySpan<byte> line)
	{
		while (TryReadRawLine(out line))
		{
			if (!_skipPartialLine)
			{
				return true;
			}

			_skipPartialLine = false;
		}

		return false;
	}

	private bool TryReadRawLine(out ReadOnlySpan<byte> line)
	{
		_lineLength = 0;
		_discarding = false;
		while (true)
		{
			if (_readStart == _readEnd)
			{
				_readEnd = _stream.Read(_readBuffer, 0, _readBuffer.Length);
				_readStart = 0;
				if (_readEnd == 0)
				{
					if (_lineLength == 0)
					{
						if (_discarding)
						{
							// 末尾的超长行没有换行收尾，也当作一行空内容交出去，行数不变。
							_discarding = false;
							line = default;
							_currentLine = default;
							return true;
						}

						line = default;
						_currentLine = default;
						return false;
					}

					line = Finish(_lineBuffer.AsMemory(0, _lineLength));
					return true;
				}
			}

			var chunk = _readBuffer.AsSpan(_readStart, _readEnd - _readStart);
			var newline = chunk.IndexOf((byte)'\n');
			if (newline >= 0)
			{
				// 上限按去掉 \r 后的行长算，与调用方看到的 line.Length 一致（CRLF 的 \r 不计入）。
				var lineBytes = _lineLength + newline;
				if (lineBytes > 0 &&
					((newline > 0 && chunk[newline - 1] == (byte)'\r') ||
					(newline == 0 && _lineLength > 0 && _lineBuffer[_lineLength - 1] == (byte)'\r')))
				{
					lineBytes--;
				}

				if (_discarding || (_maxLineBytes > 0 && lineBytes > _maxLineBytes))
				{
					// 超长行直接丢弃：调用方拿到的是一行空内容，行边界的消费位置仍然准确。
					_readStart += newline + 1;
					_lineLength = 0;
					_discarding = false;
					line = default;
					_currentLine = default;
					return true;
				}

				// 零拷贝快路径：整行本来就在读缓冲里（会话文件的行几乎都远小于一块），
				// 再抄进行缓冲等于给几十 MB 多走一遍内存。返回的跨度只有效到下一次调用为止，
				// 这正是本类型的契约，调用方都当场消费完。
				if (_lineLength == 0)
				{
					var raw = _readBuffer.AsMemory(_readStart, newline);
					_readStart += newline + 1;
					line = Finish(raw);
					return true;
				}

				Append(chunk[..newline]);
				_readStart += newline + 1;
				line = Finish(_lineBuffer.AsMemory(0, _lineLength));
				return true;
			}

			if (_maxLineBytes > 0 && !_discarding && _lineLength + chunk.Length > _maxLineBytes + 1)
			{
				// 后续块不再复制，等换行到来后整行丢弃。+1 给可能落在末尾的 \r 留余量。
				_discarding = true;
				_lineLength = 0;
			}
			else if (!_discarding)
			{
				Append(chunk);
			}

			_readStart = _readEnd;
		}
	}

	private ReadOnlySpan<byte> Finish(ReadOnlyMemory<byte> raw)
	{
		var result = raw;
		if (result.Length > 0 && result.Span[^1] == (byte)'\r')
		{
			result = result[..^1];
		}

		if (_firstLine)
		{
			_firstLine = false;
			if (result.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
			{
				result = result[3..];
			}
		}

		_currentLine = result;
		return result.Span;
	}

	private void Append(ReadOnlySpan<byte> chunk)
	{
		if (chunk.Length == 0)
		{
			return;
		}

		EnsureCapacity(_lineLength + chunk.Length);
		chunk.CopyTo(_lineBuffer.AsSpan(_lineLength));
		_lineLength += chunk.Length;
	}

	private void EnsureCapacity(int required)
	{
		if (required <= _lineBuffer.Length)
		{
			return;
		}

		var next = ArrayPool<byte>.Shared.Rent(Math.Max(required, _lineBuffer.Length * 2));
		_lineBuffer.AsSpan(0, _lineLength).CopyTo(next);
		// 与 Dispose 同一门槛：几 MB 的正文行不给共享池留常驻块。
		if (_lineBuffer.Length <= MaxPooledLineBuffer)
		{
			ArrayPool<byte>.Shared.Return(_lineBuffer);
		}
		_lineBuffer = next;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_stream.Dispose();
		ArrayPool<byte>.Shared.Return(_readBuffer);
		// 行缓冲会跟着最大的一行长膨胀（几 MB 的会话正文很常见）。
		// 这么大的数组还给共享池，等于进程永久留着它；超过阈值直接交给 GC，
		// 池里只留正常尺寸的分桶，下一次读还能复用。
		if (_lineBuffer.Length <= MaxPooledLineBuffer)
		{
			ArrayPool<byte>.Shared.Return(_lineBuffer);
		}

		_currentLine = default;
		_readBuffer = [];
		_lineBuffer = [];
	}
}
