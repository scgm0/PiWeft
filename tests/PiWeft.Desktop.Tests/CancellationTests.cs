using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class CancellationTests
{
	[Fact]
	public void CancelQuietlyIgnoresADisposedSource()
	{
		// 完成回调可能在另一线程先 Dispose；Hide/Close 里的取消不能因此冒异常。
		var source = new CancellationTokenSource();
		source.Dispose();

		Cancellation.CancelQuietly(source);
	}
}
