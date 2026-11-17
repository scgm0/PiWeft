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
}
