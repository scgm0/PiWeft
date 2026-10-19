using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Utils;
using Xunit;

namespace PiWeft.Ai.Tests;

public sealed class MessageTransformTests
{
	private static Model TextModel() => new()
	{
		Id = "target",
		Provider = "p",
		Api = "anthropic-messages",
		Input = ["text"],
	};

	[Fact]
	public void ThinkingFromAnotherModelIsDropped()
	{
		var messages = new List<Message>
		{
			new AssistantMessage
			{
				Provider = "old",
				Api = "old-api",
				Model = "old-model",
				Content =
				[
					new ThinkingContent { Thinking = "old reasoning", ThinkingSignature = "sig" },
					new TextContent { Text = "answer" },
				],
			},
		};

		var transformed = MessageTransform.Transform(messages, TextModel());

		var assistant = Assert.IsType<AssistantMessage>(Assert.Single(transformed));
		Assert.DoesNotContain(assistant.Content, static block => block is ThinkingContent);
		Assert.Contains(assistant.Content, static block => block is TextContent { Text: "answer" });
	}

	[Fact]
	public void FailedAssistantMessagesAreNotReplayed()
	{
		var messages = new List<Message>
		{
			new UserMessage { Content = MessageContent.FromText("q") },
			new AssistantMessage { StopReason = StopReason.Error, ErrorMessage = "boom" },
			new AssistantMessage { StopReason = StopReason.Aborted },
			new UserMessage { Content = MessageContent.FromText("again") },
		};

		var transformed = MessageTransform.Transform(messages, TextModel());

		Assert.Equal(2, transformed.Count);
		Assert.All(transformed, static message => Assert.IsType<UserMessage>(message));
	}

	[Fact]
	public void OrphanedToolCallsGetSyntheticResultsBeforeTheNextUserTurn()
	{
		var messages = new List<Message>
		{
			new AssistantMessage
			{
				Content = [new ToolCall { Id = "call-1", Name = "read", Arguments = new JsonObject() }],
			},
			new UserMessage { Content = MessageContent.FromText("next") },
		};

		var transformed = MessageTransform.Transform(messages, TextModel());

		Assert.Equal(3, transformed.Count);
		var result = Assert.IsType<ToolResultMessage>(transformed[1]);
		Assert.Equal("call-1", result.ToolCallId);
		Assert.True(result.IsError);
		Assert.IsType<UserMessage>(transformed[2]);
	}

	[Fact]
	public void ImagesAreDowngradedForTextOnlyModels()
	{
		var messages = new List<Message>
		{
			new UserMessage
			{
				Content = MessageContent.FromBlocks([new ImageContent { Data = "x", MimeType = "image/png" }]),
			},
			new ToolResultMessage
			{
				ToolCallId = "c",
				ToolName = "read",
				Content = [new ImageContent { Data = "x", MimeType = "image/png" }],
			},
		};

		var transformed = MessageTransform.Transform(messages, TextModel());

		var user = Assert.IsType<UserMessage>(transformed[0]);
		Assert.All(user.Content.Blocks!, static block => Assert.IsType<TextContent>(block));
		var result = Assert.IsType<ToolResultMessage>(transformed[1]);
		Assert.All(result.Content, static block => Assert.IsType<TextContent>(block));
	}

	[Fact]
	public void LongToolCallIdsAreNormalizedTogetherWithTheirResults()
	{
		var longId = new string('x', 120);
		var messages = new List<Message>
		{
			new AssistantMessage
			{
				Provider = "old",
				Api = "old-api",
				Model = "old-model",
				Content = [new ToolCall { Id = longId, Name = "read", Arguments = new JsonObject() }],
			},
			new ToolResultMessage
			{
				ToolCallId = longId,
				ToolName = "read",
				Content = [new TextContent { Text = "ok" }],
			},
		};

		var transformed = MessageTransform.Transform(messages, TextModel());
		var assistant = Assert.IsType<AssistantMessage>(transformed[0]);
		var call = Assert.IsType<ToolCall>(Assert.Single(assistant.Content));
		var result = Assert.IsType<ToolResultMessage>(transformed[1]);
		Assert.Equal(call.Id, result.ToolCallId);
		Assert.True(call.Id.Length <= 64);
	}

	[Fact]
	public void JsonRepairFixesInvalidEscapesAndTrailingCommas()
	{
		var (value, error) = JsonRepair.ParseObject("""{"path":"C:\\usepackage","n":1,}""");

		Assert.Null(error);
		Assert.Equal("C:\\usepackage", value!["path"]!.GetValue<string>());
	}

	[Fact]
	public void InvalidArgumentsReturnAnErrorWithTheOriginalText()
	{
		var (value, error) = JsonRepair.ParseObject("""{"path": """);

		Assert.Null(value);
		Assert.NotNull(error);
		Assert.Contains("Received arguments", error);
	}
}
