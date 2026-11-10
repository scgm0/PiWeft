namespace PiWeft.Desktop;

/// 取消 CTS 的统一入口。完成回调可能在另一线程释放 CTS，直接 Cancel 会抛 ObjectDisposedException；
/// 这类竞态只发生在收口与取消交错的一瞬，丢掉这次取消即可，异常不该冒到 UI。
internal static class Cancellation
{
	internal static void CancelQuietly(CancellationTokenSource? source)
	{
		if (source is null)
		{
			return;
		}

		try
		{
			source.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}
}
