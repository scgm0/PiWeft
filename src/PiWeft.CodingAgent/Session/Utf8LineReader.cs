using System.Buffers;

namespace PiWeft.CodingAgent.Session;

/// <summary>
/// Reads a UTF-8 file line by line without decoding each line to a string. The returned span is
/// backed by a pooled buffer and stays valid until the next <see cref="TryReadLine"/> call.
/// </summary>
public sealed class Utf8LineReader : IDisposable
{
	private const int ReadBufferSize = 64 * 1024;
	private const int LineFeed = 10;      // '\n'
	private const int CarriageReturn = 13; // '\r'
	private const int InitialLineCapacity = 4 * 1024;

	/// <summary>行缓冲还回共享池的上限；再大就宁可让 GC 回收，不给进程留常驻块。</summary>
	private const int MaxPooledLineBuffer = 256 * 1024;

	private readonly FileStream _stream;
	private byte[] _readBuffer;
	private byte[] _lineBuffer;
	private int _readStart;
	private int _readEnd;
	private int _lineLength;
	private bool _firstLine = true;
	private bool _skipPartialLine;
	private bool _disposed;

	/// <summary>
	/// <paramref name="fromBytes"/> 用于续读或分段并行扫：偏移落在行中间时，那条被切开的半行
	/// 会被自动跳过（归上一段），因此调用方总是按整行消费。
	/// </summary>
	public Utf8LineReader(string path, long fromBytes = 0)
	{
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

	/// <summary>已消费到的字节位置（含尚未交付的缓冲），用于读取进度。</summary>
	public long BytesRead => _stream.Position - (_readEnd - _readStart);

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
						line = default;
						return false;
					}

					line = Finish(_lineBuffer.AsSpan(0, _lineLength));
					return true;
				}
			}

			var chunk = _readBuffer.AsSpan(_readStart, _readEnd - _readStart);
			var newline = chunk.IndexOf((byte)'\n');
			if (newline >= 0)
			{
				// 零拷贝快路径：整行本来就在读缓冲里（会话文件的行几乎都远小于一块），
				// 再抄进行缓冲等于给几十 MB 多走一遍内存。返回的跨度只有效到下一次调用为止，
				// 这正是本类型的契约，两个调用方都当场消费完。
				if (_lineLength == 0)
				{
					_readStart += newline + 1;
					line = Finish(chunk[..newline]);
					return true;
				}

				Append(chunk[..newline]);
				_readStart += newline + 1;
				line = Finish(_lineBuffer.AsSpan(0, _lineLength));
				return true;
			}

			Append(chunk);
			_readStart = _readEnd;
		}
	}

	/// <summary>统一收尾：去掉行尾 CR，并在真正的首行剥掉 BOM。</summary>
	private ReadOnlySpan<byte> Finish(ReadOnlySpan<byte> raw)
	{
		var result = raw;
		if (result.Length > 0 && result[^1] == (byte)'\r')
		{
			result = result[..^1];
		}

		if (_firstLine)
		{
			_firstLine = false;
			if (result.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
			{
				result = result[3..];
			}
		}

		return result;
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
		ArrayPool<byte>.Shared.Return(_lineBuffer);
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
		// 这么大的数组还给共享池就等于进程永久留着它 —— 超过阈值直接交给 GC，
		// 池里只留正常尺寸的分桶，下一次读还能复用。
		if (_lineBuffer.Length <= MaxPooledLineBuffer)
		{
			ArrayPool<byte>.Shared.Return(_lineBuffer);
		}

		_readBuffer = [];
		_lineBuffer = [];
	}
}
