using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using Xunit;

namespace PiWeft.Ai.Tests;

public class MessageJsonTests
{
	[Fact]
	public void AssistantMessage_RoundTrips_AllBlockKinds()
	{
		var message = new AssistantMessage
		{
			Api = "anthropic-messages",
			Provider = "anthropic",
			Model = "claude-sonnet-4-5",
			Timestamp = 1_700_000_000_000,
			StopReason = StopReason.ToolUse,
			Content =
			[
				new ThinkingContent { Thinking = "let me think", ThinkingSignature = "sig" },
				new TextContent { Text = "hello" },
				new ToolCall { Id = "call_1", Name = "read", Arguments = new JsonObject { ["path"] = "a.txt" } },
			],
			Usage = new Usage
			{
				Input = 10,
				Output = 20,
				CacheRead = 3,
				CacheWrite = 4,
				TotalTokens = 37,
				Cost = new UsageCost { Input = 0.1, Output = 0.2, Total = 0.3 },
			},
		};

		var json = PiJson.SerializeMessage(message);
		var back = Assert.IsType<AssistantMessage>(PiJson.DeserializeMessage(json));

		Assert.Equal("anthropic", back.Provider);
		Assert.Equal("claude-sonnet-4-5", back.Model);
		Assert.Equal(StopReason.ToolUse, back.StopReason);
		Assert.Equal(3, back.Content.Count);
		Assert.Equal("let me think", Assert.IsType<ThinkingContent>(back.Content[0]).Thinking);
		Assert.Equal("hello", Assert.IsType<TextContent>(back.Content[1]).Text);
		var call = Assert.IsType<ToolCall>(back.Content[2]);
		Assert.Equal("read", call.Name);
		Assert.Equal("a.txt", call.Arguments["path"]!.GetValue<string>());
		Assert.Equal(37, back.Usage.TotalTokens);
		Assert.Equal(0.3, back.Usage.Cost.Total, 6);
		Assert.Equal(1_700_000_000_000, back.Timestamp);
	}

	[Fact]
	public void SystemMessage_WithStringContent_RoundTrips()
	{
		var message = new SystemMessage
		{
			Content = MessageContent.FromText("be helpful"),
			Sections = new Dictionary<string, string?> { ["tools"] = "use them", ["gone"] = null },
			Timestamp = 42,
		};

		var back = Assert.IsType<SystemMessage>(PiJson.DeserializeMessage(PiJson.SerializeMessage(message)));

		Assert.Equal("be helpful", back.Content.Text);
		Assert.Equal("use them", back.Sections!["tools"]);
		Assert.Null(back.Sections["gone"]);
	}

	[Fact]
	public void UserMessage_WithImageBlocks_RoundTrips()
	{
		var message = new UserMessage
		{
			Content = MessageContent.FromBlocks(
			[
				new TextContent { Text = "what is this" },
				new ImageContent { Data = "AAAA", MimeType = "image/png" },
			]),
			Timestamp = 7,
		};

		var back = Assert.IsType<UserMessage>(PiJson.DeserializeMessage(PiJson.SerializeMessage(message)));

		Assert.False(back.Content.IsText);
		Assert.Equal(2, back.Content.Blocks!.Count);
		Assert.Equal("image/png", Assert.IsType<ImageContent>(back.Content.Blocks[1]).MimeType);
	}

	[Fact]
	public void ToolResultMessage_RoundTrips_WithErrorAndDuration()
	{
		var message = new ToolResultMessage
		{
			ToolCallId = "c1",
			ToolName = "bash",
			IsError = true,
			Content = [new TextContent { Text = "boom" }],
			Details = new JsonObject { ["exitCode"] = 1 },
			Timestamp = 9,
			DurationMs = 12.5,
		};

		var back = Assert.IsType<ToolResultMessage>(PiJson.DeserializeMessage(PiJson.SerializeMessage(message)));

		Assert.True(back.IsError);
		Assert.Equal(12.5, back.DurationMs);
		Assert.Equal(1, back.Details!["exitCode"]!.GetValue<int>());
	}

	[Fact]
	public void UserMessage_ParsesPiWireShape()
	{
		const string json = """{"role":"user","content":"hi","timestamp":5}""";
		var back = Assert.IsType<UserMessage>(PiJson.DeserializeMessage(json));
		Assert.Equal("hi", back.Content.Text);
		Assert.Equal(5, back.Timestamp);
	}
}

/// <summary>
/// JSON 写出保留中文与必要的 JSON 引号转义，避免可读内容变成 \uXXXX。
/// </summary>
public class JsonEscapingTests
{
	private const string Command = "echo \"=== 我这条会话的启动时间 ===\"";

	[Fact]
	public void JsonNodesAreWrittenWithoutUnicodeEscapes()
	{
		var node = new System.Text.Json.Nodes.JsonObject { ["command"] = Command };

		var json = node.ToJsonString(PiWeft.Ai.Json.PiJson.NodeOptions);

		Assert.Contains("\"command\":\"echo \\\"=== 我这条会话的启动时间 ===\\\"\"", json);
		Assert.DoesNotContain("\\u0022", json);
		Assert.DoesNotContain("\\u6211", json);
	}

	[Fact]
	public void MessageJsonKeepsChineseAndQuotesReadable()
	{
		var message = new AssistantMessage
		{
			Timestamp = 1,
			Api = "openai-completions",
			Provider = "openai",
			Model = "gpt-test",
			StopReason = StopReason.ToolUse,
			Content =
			[
				new ToolCall
				{
					Id = "call_1",
					Name = "bash",
					Arguments = new System.Text.Json.Nodes.JsonObject { ["command"] = Command },
				},
			],
		};

		var json = PiJson.SerializeMessage(message);

		Assert.Contains("我这条会话的启动时间", json);
		Assert.Contains("\\\"=== ", json);
		Assert.DoesNotContain("\\u0022", json);
		Assert.DoesNotContain("\\u6211", json);
	}

	[Theory]
	[InlineData("中")]
	[InlineData("<tag> & 'quote'")]
	[InlineData("\"quoted\"")]
	public void AiSourceGeneratedContextUsesTheRelaxedEncoder(string text)
	{
		Assert.DoesNotContain("\\u", SerializeThroughContext(text));
	}

	[Theory]
	[InlineData("中")]
	[InlineData("<tag> & 'quote'")]
	[InlineData("\"quoted\"")]
	public void AuthSourceGeneratedContextUsesTheRelaxedEncoder(string text)
	{
		var credentials = new PiWeft.Ai.Auth.CodexCredentials { Access = text, Refresh = text };
		var context = PiWeft.Ai.Auth.AuthJsonContext.Default;

		var json = JsonSerializer.Serialize(credentials, context.CodexCredentials);

		Assert.DoesNotContain("\\u", json);
		Assert.Equal(text, JsonSerializer.Deserialize(json, context.CodexCredentials)!.Access);
	}

	private static string SerializeThroughContext(string text)
	{
		var model = new Model { Id = text, Provider = "test", Name = text };
		return JsonSerializer.Serialize(model, PiJsonContext.Default.Model);
	}
}
