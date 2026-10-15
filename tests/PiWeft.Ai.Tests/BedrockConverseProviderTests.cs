using System.Net;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Providers.Bedrock;
using Xunit;

namespace PiWeft.Ai.Tests;

[Collection("process-environment")]
public class BedrockConverseProviderTests
{
	private static Model Model(string id = "anthropic.claude-sonnet-4-5-v1:0") => new()
	{
		Id = id,
		Provider = "amazon-bedrock",
		Api = BedrockConverseProvider.ApiId,
		BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
		Reasoning = true,
		ContextWindow = 200_000,
		MaxTokens = 8192,
		Cost = new ModelCost { Input = 3, Output = 15, CacheRead = 0.3, CacheWrite = 3.75 },
	};

	private static byte[] Event(string eventType, string payload) =>
		AwsEventStream.Encode(
			new Dictionary<string, string> { [":event-type"] = eventType, [":message-type"] = "event" },
			Encoding.UTF8.GetBytes(payload));

	private static byte[] Exception(string code, string message) =>
		AwsEventStream.Encode(
			new Dictionary<string, string>
			{
				[":message-type"] = "exception",
				[":exception-type"] = code,
				[":error-code"] = code,
				[":error-message"] = message,
				[":content-type"] = "application/json",
			},
			Encoding.UTF8.GetBytes($"{{\"message\":\"{message}\"}}"));

	private static HttpResponseMessage BinaryStream(byte[] frame) => new(HttpStatusCode.OK)
	{
		Content = new ByteArrayContent(frame),
	};

	private static readonly byte[] StreamFrame = Combine(
		Event("messageStart", """{"role":"assistant"}"""),
		Event("contentBlockStart", """{"contentBlockIndex":0,"start":{}}"""),
		Event("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"Hello"}}"""),
		Event("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":" world"}}"""),
		Event("contentBlockStop", """{"contentBlockIndex":0}"""),
		Event("contentBlockStart", """{"contentBlockIndex":1,"start":{"toolUse":{"toolUseId":"toolu_1","name":"read"}}}"""),
		Event("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"toolUse":{"input":"{\"path\":"}}}"""),
		Event("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"toolUse":{"input":"\"a.txt\"}"}}}"""),
		Event("contentBlockStop", """{"contentBlockIndex":1}"""),
		Event("messageStop", """{"stopReason":"tool_use"}"""),
		Event(
			"metadata",
			"""{"usage":{"inputTokens":12,"outputTokens":7,"cacheReadInputTokens":4,"cacheWriteInputTokens":2}}"""));

	private static byte[] Combine(params byte[][] parts) => [.. parts.SelectMany(static part => part)];

	private static void WithCredentials(Action body)
	{
		var previousId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
		var previousSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
		Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIA_TEST");
		Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "secret");
		try
		{
			body();
		}
		finally
		{
			Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousId);
			Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecret);
		}
	}

	[Fact]
	public void UrlAndRegionComeFromTheModel()
	{
		var model = Model();

		Assert.Equal(
			"https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude-sonnet-4-5-v1%3A0/converse-stream",
			BedrockConverseProvider.BuildUrl(model));
		Assert.Equal("us-east-1", BedrockConverseProvider.ResolveRegion(model));

		var european = model with { BaseUrl = "https://bedrock-runtime.eu-central-1.amazonaws.com" };
		Assert.Equal("eu-central-1", BedrockConverseProvider.ResolveRegion(european));
	}

	[Fact]
	public async Task StreamsTextAndToolCallFromTheBinaryEventStream()
	{
		var handler = new FakeHttpHandler { Responder = _ => BinaryStream(StreamFrame) };
		var provider = new BedrockConverseProvider(new HttpClient(handler));

		await WithCredentialsAsync(async () =>
		{
			var stream = provider.Stream(
				Model(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions());

			var events = new List<string>();
			await foreach (var @event in stream)
			{
				events.Add(@event switch
				{
					StartEvent => "start",
					TextStartEvent => "text_start",
					TextDeltaEvent d => $"text_delta:{d.Delta}",
					TextEndEvent e => $"text_end:{e.Content}",
					ToolCallStartEvent => "toolcall_start",
					ToolCallDeltaEvent d => $"toolcall_delta:{d.Delta}",
					ToolCallEndEvent e => $"toolcall_end:{e.ToolCall.Name}",
					DoneEvent d => $"done:{d.Reason}",
					ErrorEvent e => $"error:{e.Error.ErrorMessage}",
					_ => "other",
				});
			}

			Assert.Equal("start", events[0]);
			Assert.Contains("text_delta:Hello", events);
			Assert.Contains("text_end:Hello world", events);
			Assert.Contains("toolcall_end:read", events);
			Assert.Equal("done:ToolUse", events[^1]);

			var message = await stream.Result;
			Assert.Equal("anthropic.claude-sonnet-4-5-v1:0", message.Model);
			Assert.Equal(StopReason.ToolUse, message.StopReason);
			Assert.Equal("tool_use", message.RawStopReason);

			var call = Assert.IsType<ToolCall>(message.Content[1]);
			Assert.Equal("toolu_1", call.Id);
			Assert.Equal("a.txt", call.Arguments["path"]!.GetValue<string>());

			Assert.Equal(12, message.Usage.Input);
			Assert.Equal(7, message.Usage.Output);
			Assert.Equal(4, message.Usage.CacheRead);
			Assert.Equal(2, message.Usage.CacheWrite);
			Assert.Equal(25, message.Usage.TotalTokens);
			Assert.True(message.Usage.Cost.Total > 0);
		});
	}

	[Fact]
	public async Task SignsTheRequestWithSigV4()
	{
		var handler = new FakeHttpHandler { Responder = _ => BinaryStream(StreamFrame) };
		var provider = new BedrockConverseProvider(new HttpClient(handler));

		await WithCredentialsAsync(async () =>
		{
			var stream = provider.Stream(
				Model(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions());

			await foreach (var _ in stream)
			{
			}

			var request = handler.LastRequest!;
			var authorization = request.Headers.GetValues("authorization").Single();

			Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIA_TEST/", authorization);
			Assert.Contains("/us-east-1/bedrock/aws4_request", authorization);
			Assert.Contains("SignedHeaders=", authorization);
			Assert.Contains("Signature=", authorization);
			Assert.Equal("application/vnd.amazon.eventstream", request.Headers.GetValues("accept").Single());
			Assert.NotNull(request.Headers.GetValues("x-amz-date").Single());
			Assert.Equal(
				AwsSignatureV4.Hex(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(handler.LastBody!))),
				request.Headers.GetValues("x-amz-content-sha256").Single());
		});
	}

	[Fact]
	public async Task SendsTheConverseRequestShape()
	{
		var handler = new FakeHttpHandler { Responder = _ => BinaryStream(StreamFrame) };
		var provider = new BedrockConverseProvider(new HttpClient(handler));

		await WithCredentialsAsync(async () =>
		{
			var context = new Context
			{
				SystemPrompt = "be terse",
				Messages =
				[
					new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
					new AssistantMessage
					{
						Content = [new ToolCall { Id = "toolu_9", Name = "read", Arguments = new() { ["path"] = "x" } }],
						Timestamp = 2,
					},
					new ToolResultMessage
					{
						ToolCallId = "toolu_9",
						ToolName = "read",
						Content = [new TextContent { Text = "contents" }],
						Timestamp = 3,
					},
				],
				Tools = [new Tool { Name = "read", Description = "read a file", Parameters = new() { ["type"] = "object" } }],
			};

			var stream = provider.Stream(Model(), context, new StreamOptions { MaxTokens = 512, ThinkingLevel = ThinkingLevels.High });
			await foreach (var _ in stream)
			{
			}

			using var document = JsonDocument.Parse(handler.LastBody!);
			var root = document.RootElement;

			Assert.Equal("be terse", root.GetProperty("system")[0].GetProperty("text").GetString());
			Assert.Equal(512, root.GetProperty("inferenceConfig").GetProperty("maxTokens").GetInt32());

			var messages = root.GetProperty("messages");
			Assert.Equal(3, messages.GetArrayLength());
			Assert.Equal("user", messages[0].GetProperty("role").GetString());
			Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
			Assert.Equal("toolu_9", messages[1].GetProperty("content")[0].GetProperty("toolUse").GetProperty("toolUseId").GetString());

			var toolResult = messages[2].GetProperty("content")[0].GetProperty("toolResult");
			Assert.Equal("toolu_9", toolResult.GetProperty("toolUseId").GetString());
			Assert.Equal("success", toolResult.GetProperty("status").GetString());
			Assert.Equal("contents", toolResult.GetProperty("content")[0].GetProperty("text").GetString());

			var tool = root.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec");
			Assert.Equal("read", tool.GetProperty("name").GetString());
			Assert.True(tool.GetProperty("inputSchema").TryGetProperty("json", out _));

			// Anthropic models on Bedrock take thinking through the model-specific extension fields.
			Assert.Equal(
				16384,
				root.GetProperty("additionalModelRequestFields").GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
		});
	}

	[Fact]
	public async Task ServiceExceptionsBecomeErrorEvents()
	{
		var handler = new FakeHttpHandler { Responder = _ => BinaryStream(Combine(
			Event("messageStart", """{"role":"assistant"}"""),
			Exception("ValidationException", "bad request"))) };
		var provider = new BedrockConverseProvider(new HttpClient(handler));

		await WithCredentialsAsync(async () =>
		{
			var stream = provider.Stream(
				Model(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions());

			var events = new List<AssistantMessageEvent>();
			await foreach (var @event in stream)
			{
				events.Add(@event);
			}

			var error = Assert.IsType<ErrorEvent>(events[^1]);
			Assert.Equal(StopReason.Error, error.Reason);
			Assert.Contains("bad request", error.Error.ErrorMessage);
		});
	}

	[Fact]
	public async Task MissingCredentialsFailBeforeAnyRequest()
	{
		var handler = new FakeHttpHandler();
		var provider = new BedrockConverseProvider(new HttpClient(handler));

		var previousId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
		var previousSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
		Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", null);
		Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", null);
		try
		{
			var stream = provider.Stream(
				Model(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions());

			var events = new List<AssistantMessageEvent>();
			await foreach (var @event in stream)
			{
				events.Add(@event);
			}

			var error = Assert.IsType<ErrorEvent>(events[^1]);
			Assert.Contains("AWS_ACCESS_KEY_ID", error.Error.ErrorMessage);
			Assert.Null(handler.LastRequest);
		}
		finally
		{
			Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousId);
			Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecret);
		}
	}

	[Fact]
	public void ThinkingBudgetOnlyAppliesToAnthropicModels()
	{
		Assert.Equal(16384, BedrockConverseProvider.ResolveThinkingBudget(Model(), new StreamOptions { ThinkingLevel = ThinkingLevels.High }));
		Assert.Null(BedrockConverseProvider.ResolveThinkingBudget(Model(), new StreamOptions { ThinkingLevel = ThinkingLevels.Off }));
		Assert.Null(BedrockConverseProvider.ResolveThinkingBudget(
			Model("amazon.nova-2-lite-v1:0"),
			new StreamOptions { ThinkingLevel = ThinkingLevels.High }));
	}

	private static async Task WithCredentialsAsync(Func<Task> body)
	{
		var previousId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
		var previousSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
		Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIA_TEST");
		Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "secret");
		try
		{
			await body();
		}
		finally
		{
			Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousId);
			Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecret);
		}
	}
}
