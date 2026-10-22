using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 分段并行读必须和逐行读一模一样：每行恰好解析一次、顺序不变、行边界切在巨型行中间也不能丢内容。
/// 单测里的常规会话都是小文件（走单段路径），所以这里刻意造超过并行阈值的大文件。
/// </summary>
public sealed class SessionParallelReadTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-parallel-read-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionParallelReadTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionDir => Path.Combine(_root, "sessions");

	/// <summary>大约 12 MiB 的会话：足够切成多段并行扫。</summary>
	private SessionManager BuildLarge(int turns, int paddingChars)
	{
		var manager = SessionManager.Create(_root, SessionDir);
		var padding = new string('x', paddingChars);
		for (var i = 0; i < turns; i++)
		{
			manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"question {i} {padding}"), Timestamp = i });
			manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = $"answer {i} {padding}" }] });
		}

		return manager;
	}

	/// <summary>取一条消息的正文文本（用户消息与助手消息的内容容器不同）。</summary>
	private static string? TextOf(Message message) => message switch
	{
		UserMessage user => user.Content.Text,
		AssistantMessage assistant => assistant.Content.OfType<TextContent>().FirstOrDefault()?.Text,
		_ => null,
	};

	private static List<string> Ids(IEnumerable<object> entries) =>
		entries.OfType<SessionEntry>().Select(static entry => entry.Id).ToList();

	[Fact]
	public void ParallelReadKeepsEveryLineOnceInFileOrder()
	{
		var manager = BuildLarge(4000, 1500);
		var path = manager.SessionFile!;
		var size = new FileInfo(path).Length;
		Assert.True(size > 8 * 1024 * 1024, $"基准文件要超过并行阈值才有意义（当前 {size} 字节）");

		// 基线是「逐行单段读」自己写一遍：不能拿另一个并行结果当基准。
		var sequential = new List<string>();
		using (var reader = new Utf8LineReader(path))
		{
			while (reader.TryReadLine(out var line))
			{
				if (SessionJson.ParseLine(line) is SessionEntry entry)
				{
					sequential.Add(entry.Id);
				}
			}
		}

		var expectedCount = manager.Entries.Count;
		var reopened = SessionManager.Open(path);
		var actual = Ids(reopened.Entries);

		// 4000 轮 = 8000 条消息条目（头部不在 Entries 里），并行与单段必须一模一样。
		Assert.Equal(8000, actual.Count);
		Assert.Equal(expectedCount, reopened.Entries.Count);
		Assert.Equal(sequential, actual);
		// 头部被正确识别（不在 Entries 里但参与建树），且没有重复 id —— 重复意味着边界行被两段都读了。
		Assert.Equal(manager.SessionId, reopened.SessionId);
		Assert.Equal(actual.Count, actual.Distinct().Count());
	}

	[Fact]
	public void ParallelReadPreservesContentAcrossBoundaries()
	{
		var manager = BuildLarge(3000, 2000);
		var path = manager.SessionFile!;

		var reopened = SessionManager.Open(path);
		var messages = reopened.Entries.OfType<SessionMessageEntry>().ToList();
		Assert.NotEmpty(messages);
		foreach (var entry in messages.Take(50))
		{
			var text = TextOf(entry.Message);
			Assert.False(string.IsNullOrEmpty(text), "边界附近的内容被截断了");
			Assert.Contains("x", text!);
		}
	}

	[Fact]
	public void SingleGiantLineSurvivesTheChunking()
	{
		// 一条超过段宽的巨型行：段首落在它中间时必须整条让给上一段，否则内容会被腰斩。
		var manager = SessionManager.Create(_root, SessionDir);
		var huge = new string('y', 6 * 1024 * 1024);
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText(huge), Timestamp = 1 });
		for (var i = 0; i < 3000; i++)
		{
			manager.AppendMessage(new AssistantMessage { Content = [new TextContent { Text = $"tail {i} {new string('z', 1200)}" }] });
		}

		var path = manager.SessionFile!;
		var reopened = SessionManager.Open(path);
		var first = reopened.Entries.OfType<SessionMessageEntry>().First(entry => entry.Message is UserMessage);
		Assert.Equal(huge, TextOf(first.Message));
	}

	[Fact]
	public void ParallelReadReportsMonotonicProgressToTheFullFraction()
	{
		var manager = BuildLarge(4000, 1600);
		var reports = new List<SessionReadProgress>();
		var reopened = SessionManager.Open(manager.SessionFile!, null, null, null, new Collector(reports), CancellationToken.None);

		Assert.NotEmpty(reports);
		for (var i = 1; i < reports.Count; i++)
		{
			Assert.True(reports[i].BytesRead >= reports[i - 1].BytesRead, "进度不能倒退");
			Assert.True(reports[i].Lines >= reports[i - 1].Lines, "行数不能倒退");
		}

		Assert.Equal(1.0, reports[^1].Fraction, 3);
		Assert.True(reports[^1].Lines >= manager.Entries.Count, "最后一帧必须覆盖全部行");
		Assert.Equal(manager.Entries.Count, reopened.Entries.Count);
	}

	private sealed class Collector(List<SessionReadProgress> sink) : IProgress<SessionReadProgress>
	{
		public void Report(SessionReadProgress value) => sink.Add(value);
	}
}
