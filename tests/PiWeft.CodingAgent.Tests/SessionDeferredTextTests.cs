using System.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 延迟正文不能改变会话文件的字节与后续操作的结果：打开、序列化、fork、context_edit 都按同一段文字走。
public sealed class SessionDeferredTextTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-deferred-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _sessions;

	public SessionDeferredTextTests()
	{
		_sessions = Path.Combine(_root, "sessions");
		Directory.CreateDirectory(_sessions);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	[Fact]
	public void LargeToolResultSurvivesOpenAndSerializesByteForByte()
	{
		var text = LargeText("原始工具输出", 600);
		var manager = SessionManager.Create(_root, _sessions);
		var entryId = manager.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "call-1",
			ToolName = "bash",
			Content = [TextContent.FromUtf8(Encoding.UTF8.GetBytes(text), text.Length)],
			Timestamp = 1,
		});

		var path = manager.SessionFile!;
		var expected = File.ReadAllLines(path);

		var opened = SessionManager.Open(path);
		var actual = new List<string>();
		if (opened.Header is { } header)
		{
			actual.Add(SessionJson.Serialize(header));
		}

		foreach (var entry in opened.Entries)
		{
			actual.Add(SessionJson.Serialize(entry));
		}

		Assert.Equal(expected, actual);

		var result = Assert.IsType<ToolResultMessage>(
			Assert.IsType<SessionMessageEntry>(opened.Entries.Single(entry => entry.Id == entryId)).Message);
		var block = Assert.IsType<TextContent>(Assert.Single(result.Content));
		Assert.True(block.IsTextDeferred);
		Assert.Equal(text, block.Text);
	}

	[Fact]
	public void ForkKeepsTheFullDeferredToolResult()
	{
		var text = LargeText("fork 前的正文", 400);
		var manager = SessionManager.Create(_root, _sessions);
		manager.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "call-1",
			ToolName = "bash",
			Content = [TextContent.FromUtf8(Encoding.UTF8.GetBytes(text), text.Length)],
			Timestamp = 1,
		});

		var fork = SessionManager.ForkFrom(manager.SessionFile!, Path.Combine(_root, "fork"), _sessions, id: "fork-1");
		var result = Assert.IsType<ToolResultMessage>(fork.BuildProjectedMessages().Single(message => message is ToolResultMessage));
		Assert.Equal(text, Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);

		// 派生会话也按 v3 落盘：再打开一次正文仍完整。
		var reopened = SessionManager.Open(fork.SessionFile!);
		var reopenedResult = Assert.IsType<ToolResultMessage>(reopened.BuildProjectedMessages().Single(message => message is ToolResultMessage));
		Assert.Equal(text, Assert.IsType<TextContent>(Assert.Single(reopenedResult.Content)).Text);
	}

	[Fact]
	public void ContextEditReplacementUsesDeferredTextAndSurvivesReopen()
	{
		var original = LargeText("被替换的正文", 200);
		var replacement = LargeText("替换后的正文", 300);
		var manager = SessionManager.Create(_root, _sessions);
		var targetId = manager.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "call-1",
			ToolName = "bash",
			Content = [TextContent.FromUtf8(Encoding.UTF8.GetBytes(original), original.Length)],
			Timestamp = 1,
		});

		manager.AppendContextEdit(targetId, MessageContent.FromBlocks(
		[
			TextContent.FromUtf8(Encoding.UTF8.GetBytes(replacement), replacement.Length),
		]));

		var path = manager.SessionFile!;
		var reopened = SessionManager.Open(path);
		var projected = Assert.IsType<ToolResultMessage>(
			reopened.BuildProjectedMessages().Single(message => message is ToolResultMessage));
		var block = Assert.IsType<TextContent>(Assert.Single(projected.Content));
		Assert.True(block.IsTextDeferred);
		Assert.Equal(replacement, block.Text);

		// 原条目仍在文件里，context_edit 只改投影。
		Assert.Contains("被替换的正文 0", File.ReadAllText(path));
	}

	private static string LargeText(string label, int lines)
	{
		var builder = new StringBuilder();
		for (var index = 0; index < lines; index++)
		{
			builder.Append(label).Append(' ').Append(index)
				.Append(" 中文与 emoji 😀 混排，用来跨过 8 KiB 阈值并带上代理对。\n");
		}

		return builder.ToString();
	}
}
