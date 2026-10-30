using PiWeft.Ai;
using Xunit;

namespace PiWeft.Ai.Tests;

/// <summary>
/// 响应体空闲超时：供应商把连接挂着不发也不关时，这一轮必须失败（并进入自动重试），
/// 但「慢而仍在产出」的长流不能被切断。
/// </summary>
public class IdleTimeoutStreamTests
{
	/// <summary>永远不返回数据的流：只有取消能让 ReadAsync 结束。</summary>
	private sealed class SilentStream : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => 0; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
			return 0;
		}
	}

	/// <summary>每隔一段时间产出一个字节的流：总时长可以超过空闲超时，但相邻两次读的间隔不超过它。</summary>
	private sealed class TrickleStream(int bytes, TimeSpan interval) : Stream
	{
		private int _remaining = bytes;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => 0; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_remaining == 0)
			{
				return 0;
			}

			await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
			_remaining--;
			buffer.Span[0] = (byte)'x';
			return 1;
		}
	}

	[Fact]
	public async Task SilentStreamFailsWithRetryableTimeoutMessage()
	{
		await using var stream = new IdleTimeoutStream(new SilentStream(), TimeSpan.FromMilliseconds(400), CancellationToken.None);
		var buffer = new byte[16];

		var exception = await Assert.ThrowsAsync<IOException>(
			async () => { _ = await stream.ReadAsync(buffer, CancellationToken.None); });

		Assert.Contains("idle timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
		Assert.True(RetryClassifier.IsRetryable(new AssistantMessage
		{
			Api = "a",
			Provider = "p",
			Model = "m",
			Content = [],
			StopReason = StopReason.Error,
			ErrorMessage = exception.Message,
		}));
	}

	[Fact]
	public async Task ProgressKeepsTheStreamAlivePastTheTimeout()
	{
		using var request = new CancellationTokenSource();
		await using var stream = new IdleTimeoutStream(
			new TrickleStream(bytes: 8, interval: TimeSpan.FromMilliseconds(80)),
			TimeSpan.FromMilliseconds(300),
			request.Token);

		var buffer = new byte[16];
		var total = 0;
		while (true)
		{
			var read = await stream.ReadAsync(buffer, request.Token);
			if (read == 0)
			{
				break;
			}

			total += read;
		}

		Assert.Equal(8, total);
	}

	[Fact]
	public async Task RequestCancellationStaysAnOperationCanceledException()
	{
		using var request = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
		await using var stream = new IdleTimeoutStream(new SilentStream(), TimeSpan.FromSeconds(5), request.Token);
		var buffer = new byte[16];

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () => { _ = await stream.ReadAsync(buffer, request.Token); });
	}
}
