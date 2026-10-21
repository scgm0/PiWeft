using System.Buffers;

namespace PiWeft.CodingAgent.Session;

/// <summary>
/// Reads a UTF-8 file line by line without decoding each line to a string. The returned span is
/// backed by a pooled buffer and stays valid until the next <see cref="TryReadLine"/> call.
/// </summary>
public sealed class Utf8LineReader : IDisposable
{
	private const int ReadBufferSize = 64 * 1024;
	private const int InitialLineCapacity = 4 * 1024;

	private readonly FileStream _stream;
	private byte[] _readBuffer;
	private byte[] _lineBuffer;
	private int _readStart;
	private int _readEnd;
	private int _lineLength;
	private bool _firstLine = true;
	private bool _disposed;

	public Utf8LineReader(string path)
	{
		_stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 1,
			FileOptions.SequentialScan);
		_readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
		_lineBuffer = ArrayPool<byte>.Shared.Rent(InitialLineCapacity);
	}

	/// <summary>已消费到的字节位置（含尚未交付的缓冲），用于读取进度。</summary>
	public long BytesRead => _stream.Position - (_readEnd - _readStart);

	public bool TryReadLine(out ReadOnlySpan<byte> line)
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

					break;
				}
			}

			var chunk = _readBuffer.AsSpan(_readStart, _readEnd - _readStart);
			var newline = chunk.IndexOf((byte)'\n');
			if (newline >= 0)
			{
				Append(chunk[..newline]);
				_readStart += newline + 1;
				break;
			}

			Append(chunk);
			_readStart = _readEnd;
		}

		var result = _lineBuffer.AsSpan(0, _lineLength);
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

		line = result;
		return true;
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
		ArrayPool<byte>.Shared.Return(_lineBuffer);
		_readBuffer = [];
		_lineBuffer = [];
	}
}
