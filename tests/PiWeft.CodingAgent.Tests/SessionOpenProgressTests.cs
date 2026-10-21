using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 打开长会话的后台读取契约：进度能汇报、能取消，UTF-8 逐行解析与旧的字符串路径结果一致。
/// </summary>
public class SessionOpenProgressTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-open-progress-" + Guid.NewGuid().ToString("N")[..8]);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionDir => Path.Combine(_root, "sessions");

	private SessionManager Build(int messages)
	{
		var manager = SessionManager.Create(_root, SessionDir);
		for (var i = 0; i < messages; i++)
		{
			manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"message {i} padding padding"), Timestamp = i });
		}

		return manager;
	}

	[Fact]
	public void Open_ReportsProgressAndFinishesAtFullFraction()
	{
		var manager = Build(600);
		var path = manager.SessionFile!;
		var total = new FileInfo(path).Length;

		var reports = new List<SessionReadProgress>();
		var reopened = SessionManager.Open(path, null, null, null, new Collector(reports), CancellationToken.None);

		Assert.Equal(manager.Entries.Count, reopened.Entries.Count);
		Assert.NotEmpty(reports);
		// 进度必须单调递增，最后一帧落满。
		for (var i = 1; i < reports.Count; i++)
		{
			Assert.True(reports[i].BytesRead >= reports[i - 1].BytesRead, "progress went backwards");
			Assert.True(reports[i].Lines >= reports[i - 1].Lines, "line count went backwards");
		}

		var last = reports[^1];
		Assert.Equal(total, last.TotalBytes);
		Assert.Equal(1.0, last.Fraction, 3);
		Assert.True(last.Lines >= manager.Entries.Count, "final report must cover every line");
	}

	[Fact]
	public void Open_ReportsOnceEvenWhenTheFileIsSmall()
	{
		var manager = Build(3);
		var reports = new List<SessionReadProgress>();
		SessionManager.Open(manager.SessionFile!, null, null, null, new Collector(reports), CancellationToken.None);

		// 小文件不到阈值，也要在结束时汇报一次，进度条才不会停在半截。
		Assert.Single(reports);
		Assert.Equal(1.0, reports[0].Fraction, 3);
	}

	[Fact]
	public void Open_AlreadyCancelledTokenStopsBeforeParsing()
	{
		var manager = Build(50);
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		Assert.Throws<OperationCanceledException>(() => SessionManager.Open(
			manager.SessionFile!, null, null, null, progress: null, cts.Token));
	}

	[Fact]
	public void Open_CancelledMidReadStopsTheRead()
	{
		var manager = Build(2000);
		using var cts = new CancellationTokenSource();
		// 读取线程上汇报进度，达到阈值就请求取消：验证解析真的会中断，而不是跑完才检查。
		var progress = new CancelAfter(cts, threshold: 500);

		Assert.Throws<OperationCanceledException>(() => SessionManager.Open(
			manager.SessionFile!, null, null, null, progress, cts.Token));
	}

	[Fact]
	public void Utf8ParseLine_MatchesTheStringOverload()
	{
		var manager = Build(20);
		var bytes = new List<object>();
		var texts = new List<object>();

		using (var reader = new Utf8LineReader(manager.SessionFile!))
		{
			while (reader.TryReadLine(out var line))
			{
				if (SessionJson.ParseLine(line) is { } fromBytes)
				{
					bytes.Add(fromBytes);
				}
			}
		}

		foreach (var line in File.ReadLines(manager.SessionFile!))
		{
			if (SessionJson.ParseLine(line) is { } fromText)
			{
				texts.Add(fromText);
			}
		}

		Assert.NotEmpty(bytes);
		Assert.Equal(texts.Count, bytes.Count);
		Assert.IsType<SessionHeader>(bytes[0]);
		Assert.Equal(
			texts.OfType<SessionEntry>().Select(static entry => entry.Id),
			bytes.OfType<SessionEntry>().Select(static entry => entry.Id));
	}

	[Fact]
	public void Utf8ParseLine_IgnoresBlankAndWhitespaceLines()
	{
		Assert.Null(SessionJson.ParseLine("   "));
		Assert.Null(SessionJson.ParseLine(""u8));
		Assert.Null(SessionJson.ParseLine(" \t\r"u8));
	}

	private sealed class Collector(List<SessionReadProgress> sink) : IProgress<SessionReadProgress>
	{
		public void Report(SessionReadProgress value) => sink.Add(value);
	}

	private sealed class CancelAfter(CancellationTokenSource source, int threshold) : IProgress<SessionReadProgress>
	{
		public void Report(SessionReadProgress value)
		{
			if (value.Lines >= threshold)
			{
				source.Cancel();
			}
		}
	}
}
