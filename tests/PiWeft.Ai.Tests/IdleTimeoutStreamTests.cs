using PiWeft.Ai;
using PiWeft.Ai.Providers;
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
	public async Task ErrorBodiesAreAlsoBoundedByTheIdleTimeout()
	{
		using var response = new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
		{
			Content = new StreamContent(new SilentStream()),
		};

		await Assert.ThrowsAsync<IOException>(() => ProviderHttp.ReadBodyTextAsync(
			response,
			new StreamOptions { IdleTimeoutMs = 400 },
			CancellationToken.None));
	}

	[Fact]
	public async Task HugeErrorBodiesAreCapped()
	{
		// 网关可能返回几十 MB 的错误页；调用方只用前 2000 字符，读满整份纯属浪费内存。
		await using var body = new MemoryStream(new byte[3 * 1024 * 1024]);
		using var response = new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
		{
			Content = new StreamContent(body),
		};

		var text = await ProviderHttp.ReadBodyTextAsync(response, new StreamOptions(), CancellationToken.None);

		Assert.True(text.Length <= 1024 * 1024 + 32, $"actual={text.Length}");
		Assert.EndsWith("…（响应体已截断）", text);
	}

	[Fact]
	public async Task ArrayReadsAreForwardedToTheAsyncPath()
	{
		// 基组的数组 ReadAsync 会退回 BeginRead→同步 Read：对只实现异步读的内层流
		// 直接抛 NotSupportedException，等于让所有 provider 的正文读取崩掉。
		await using var stream = new IdleTimeoutStream(new SilentStream(), TimeSpan.FromMilliseconds(400), CancellationToken.None);
		var buffer = new byte[16];

		// 期望 IOException（空闲超时），而不是 NotSupportedException（退回同步读）。
		await Assert.ThrowsAsync<IOException>(
			async () => { _ = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None); });
	}

	[Fact]
	public async Task SseParserSurfacesTheIdleTimeoutInsteadOfEndingTheStream()
	{
		// 计时器只负责取消读取；SseParser 必须把取消翻译成错误，而不是当成正常流结束 ——
		// 后者会让模型收到一条「成功的空回复」，比报错难查得多。
		await using var stream = new IdleTimeoutStream(new SilentStream(), TimeSpan.FromMilliseconds(400), CancellationToken.None);
		var parser = System.Net.ServerSentEvents.SseParser.Create<bool>(stream, static (_, _) => true);

		await Assert.ThrowsAsync<IOException>(async () =>
		{
			await foreach (var _ in parser.EnumerateAsync(CancellationToken.None))
			{
			}
		});
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
