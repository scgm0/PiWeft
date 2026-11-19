using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.Ai.Tests;

/// 请求容量预估只影响内存分配，估小了最多少分几次容；这里锁住「大图必须被计入」，
/// 否则带图的请求会按默认容量反复翻倍扩容。
public class TranscriptTests
{
	[Fact]
	public void EstimateRequestBytesCountsTheImagePayload()
	{
		var imageData = new string('A', 400_000);
		var messages = new Message[]
		{
			new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
			new ToolResultMessage
			{
				ToolCallId = "c1",
				ToolName = "read",
				Content =
				[
					new TextContent { Text = "Read image file [image/png]" },
					new ImageContent { Data = imageData, MimeType = "image/png" },
				],
				Timestamp = 2,
			},
		};

		var estimate = Transcript.EstimateRequestBytes(
			messages,
			[new Tool { Name = "read", Parameters = new JsonObject { ["type"] = "object" } }]);

		Assert.True(estimate >= imageData.Length, $"estimate {estimate} is below the image payload {imageData.Length}");
	}

	[Fact]
	public void EstimateRequestBytesStaysSmallWithoutImages()
	{
		var messages = new Message[] { new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 } };

		Assert.InRange(Transcript.EstimateRequestBytes(messages), 4096, 16_384);
	}

	[Fact]
	public void NormalizeEmitsTheSystemPromptOnce()
	{
		var system = new SystemMessage
		{
			Content = MessageContent.FromText("BASE"),
			Sections = new Dictionary<string, string?> { ["rules"] = "RULES" },
			Timestamp = 1,
		};
		var normalized = Transcript.Normalize(new Context { Messages = [system] });

		// 章节已经拼进 Content，provider 取初始系统文本时不能再追加一遍。
		Assert.Equal("BASE\n\nRULES", Transcript.GetInitialSystemText(normalized));
	}

	[Fact]
	public void CachedSystemTextDoesNotLeakThroughWith()
	{
		var system = new SystemMessage
		{
			Sections = new Dictionary<string, string?> { ["rules"] = "one" },
		};
		Assert.Equal("one", Transcript.GetText(system));

		// `with` 出来的检查点快照换了章节，不能继承旧文本。
		var changed = system with { Sections = new Dictionary<string, string?> { ["rules"] = "two" } };
		Assert.Equal("two", Transcript.GetText(changed));
		Assert.Equal("one", Transcript.GetText(system));
	}
}
