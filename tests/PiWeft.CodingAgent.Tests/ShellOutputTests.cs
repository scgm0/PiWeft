using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// shell 输出可以任意大：内存里只能留尾部窗口，完整内容必须落到临时文件。
/// 历史缺陷是把整条命令的输出堆在 StringBuilder 里，`yes`、`cat 大文件`、`grep -r` 会吃掉几 GB 内存。
public sealed class ShellOutputTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-shell-" + Guid.NewGuid().ToString("N")[..8]);

	public ShellOutputTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task LongSingleLineOutputKeepsATailWindowAndPreservesTheFullOutput()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // 用 POSIX 工具生成 3 MB 单行输出
		}

		var result = await ShellRunner.RunAsync(
			"head -c 3000000 /dev/zero | tr '\\0' a",
			_root,
			timeoutSeconds: 60,
			onOutput: null,
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.True(result.OutputTruncated);
		Assert.Equal(1, result.TotalLines);
		Assert.InRange(result.TotalBytes, 3_000_001, 3_000_100);
		Assert.NotNull(result.FullOutputPath);
		Assert.False(string.IsNullOrEmpty(result.Output));

		// 窗口只保留尾部一段，不再是整条 3 MB 字符串。
		Assert.True(result.Output.Length <= 1_200_000, $"window was {result.Output.Length} chars");
		Assert.EndsWith("a", result.Output.TrimEnd('\n'));

		var file = new FileInfo(result.FullOutputPath!);
		Assert.Equal(result.TotalBytes, file.Length);

		// 模型可见的尾部截断仍有内容，并且能指出这是第 1 行的末尾。
		var truncation = Truncate.Tail(result.Output) with
		{
			TotalLines = result.TotalLines,
			TotalBytes = result.TotalBytes,
		};
		Assert.True(truncation.Truncated);
		Assert.True(truncation.LastLinePartial);
		Assert.Equal(1, truncation.TotalLines);

		File.Delete(result.FullOutputPath!);
	}

	[Fact]
	public async Task ManyLinesKeepTheLastLineRangeAndTheFullFile()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await ShellRunner.RunAsync(
			"seq 1 300000",
			_root,
			timeoutSeconds: 60,
			onOutput: null,
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.True(result.OutputTruncated);
		Assert.Equal(300_000, result.TotalLines);
		Assert.NotNull(result.FullOutputPath);
		Assert.True(result.Output.Length <= 1_200_000);

		// 尾部窗口的末行就是命令的最后一行。
		var lines = result.Output.TrimEnd('\n').Split('\n');
		Assert.Equal("300000", lines[^1]);

		// 完整文件末尾也是最后一行。
		var fullLines = File.ReadLines(result.FullOutputPath!).ToArray();
		Assert.Equal(300_000, fullLines.Length);
		Assert.Equal("300000", fullLines[^1]);

		File.Delete(result.FullOutputPath!);
	}

	[Fact]
	public async Task SmallOutputStaysInMemoryWithoutATempFile()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await ShellRunner.RunAsync("printf 'hello\\nworld\\n'", _root, null, null, CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.False(result.OutputTruncated);
		Assert.Null(result.FullOutputPath);
		Assert.Equal("hello\nworld\n", result.Output);
		Assert.Equal(2, result.TotalLines);
	}
}
