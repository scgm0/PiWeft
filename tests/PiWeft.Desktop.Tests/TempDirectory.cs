namespace PiWeft.Desktop.Tests;

/// 临时目录的清理。摘要缓存现在由后台线程落盘：删除进行到一半时缓存文件可能刚好写进来，
/// `Directory.Delete` 会抛「目录非空」。这种失败与被测行为无关，重试几次即可。
internal static class TempDirectory
{
	public static void Delete(string path, int attempts = 10)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}

				return;
			}
			catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
			{
				Thread.Sleep(20);
			}
		}
	}
}
