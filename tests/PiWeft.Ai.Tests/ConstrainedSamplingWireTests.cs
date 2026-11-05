using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Providers;
using PiWeft.Ai.Providers.Anthropic;
using PiWeft.Ai.Providers.Bedrock;
using PiWeft.Ai.Providers.Google;
using PiWeft.Ai.Providers.Mistral;
using PiWeft.Ai.Providers.OpenAi;
using Xunit;

namespace PiWeft.Ai.Tests;

/// 受约束采样在各 provider 的线上形状：工具声明了 <c>strict: "prefer"</c> 之后，
/// 支持严格的端点必须收到严格 schema + strict 标志，不支持的老模型必须收到普通 schema
/// 且完全不出现 strict 字段（否则有些端点会整请求拒绝）。
/// 这些用例只看请求体，因此用 401 结束流：请求已经发出，解析阶段不必成功。
[Collection("process-environment")]
public class ConstrainedSamplingWireTests
{
	private static HttpResponseMessage Unauthorized() => new(HttpStatusCode.Unauthorized)
	{
		Content = new StringContent("""{"error":{"message":"nope"}}""", Encoding.UTF8, "application/json"),
	};

	private static Tool StrictReadTool() => new()
	{
		Name = "read",
		Description = "read a file",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string" },
				["offset"] = new JsonObject { ["type"] = "number" },
			},
			["required"] = new JsonArray("path"),
		},
		ConstrainedSampling = ConstrainedSampling.JsonSchema(),
	};

	private static Context ContextWithTool() => new()
	{
		Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
		Tools = [StrictReadTool()],
	};

	private static async Task<JsonDocument> CaptureAsync(
		Func<FakeHttpHandler, IApiProvider> provider,
		Model model)
	{
		var handler = new FakeHttpHandler { Responder = _ => Unauthorized() };
		var stream = provider(handler).Stream(model, ContextWithTool(), new StreamOptions { ApiKey = "test-key" });
		await foreach (var _ in stream)
		{
		}

		Assert.NotNull(handler.LastBody);
		return JsonDocument.Parse(handler.LastBody!);
	}

	[Fact]
	public async Task OpenAiCompletionsSendsStrictUnlessTheModelOptsOut()
	{
		static Model Model(bool? supportsStrictMode) => new()
		{
			Id = "gpt-x",
			Provider = "openai-compatible",
			Api = OpenAiCompletionsProvider.ApiId,
			BaseUrl = "https://example.test/v1",
			ContextWindow = 8_000,
			MaxTokens = 1_000,
			Compat = supportsStrictMode is { } flag ? new JsonObject { ["supportsStrictMode"] = flag } : null,
		};

		using (var document = await CaptureAsync(h => new OpenAiCompletionsProvider(new HttpClient(h)), Model(null)))
		{
			var function = document.RootElement.GetProperty("tools")[0].GetProperty("function");
			Assert.True(function.GetProperty("strict").GetBoolean());
			Assert.Equal(2, function.GetProperty("parameters").GetProperty("required").GetArrayLength());
		}

		using (var document = await CaptureAsync(
			h => new OpenAiCompletionsProvider(new HttpClient(h)),
			Model(supportsStrictMode: false)))
		{
			var function = document.RootElement.GetProperty("tools")[0].GetProperty("function");
			Assert.False(function.TryGetProperty("strict", out _));
			Assert.Equal(1, function.GetProperty("parameters").GetProperty("required").GetArrayLength());
		}
	}

	[Fact]
	public async Task OpenAiResponsesOnlySendsStrictWhenTheModelSupportsIt()
	{
		static Model Model(bool strict) => new()
		{
			Id = "gpt-5",
			Provider = "openai",
			Api = OpenAiResponsesProvider.ApiId,
			BaseUrl = "https://api.openai.test/v1",
			ContextWindow = 8_000,
			MaxTokens = 1_000,
			Compat = new JsonObject { ["supportsStrictMode"] = strict },
		};

		using (var document = await CaptureAsync(h => new OpenAiResponsesProvider(new HttpClient(h)), Model(true)))
		{
			var tool = document.RootElement.GetProperty("tools")[0];
			Assert.True(tool.GetProperty("strict").GetBoolean());
		}

		using (var document = await CaptureAsync(h => new OpenAiResponsesProvider(new HttpClient(h)), Model(false)))
		{
			var tool = document.RootElement.GetProperty("tools")[0];
			Assert.False(tool.TryGetProperty("strict", out _));
			Assert.False(tool.GetProperty("parameters").TryGetProperty("additionalProperties", out _));
		}
	}

	[Fact]
	public async Task GoogleOnlyGoesStrictOnGeminiThreeAndUp()
	{
		static Model Model(string id) => new()
		{
			Id = id,
			Provider = "google",
			Api = GoogleGenerativeAiProvider.ApiId,
			BaseUrl = "https://generativelanguage.test/v1beta",
			ContextWindow = 8_000,
			MaxTokens = 1_000,
		};

		using (var document = await CaptureAsync(
			h => new GoogleGenerativeAiProvider(new HttpClient(h)),
			Model("gemini-3-pro")))
		{
			var declaration = document.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
			Assert.Equal(2, declaration.GetProperty("parametersJsonSchema").GetProperty("required").GetArrayLength());
			// 有严格工具时 functionCallingConfig 必须切到 VALIDATED。
			Assert.Equal("VALIDATED",
				document.RootElement.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
		}

		using (var document = await CaptureAsync(
			h => new GoogleGenerativeAiProvider(new HttpClient(h)),
			Model("gemini-2.5-flash")))
		{
			var declaration = document.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
			Assert.Equal(1, declaration.GetProperty("parametersJsonSchema").GetProperty("required").GetArrayLength());
			Assert.False(document.RootElement.TryGetProperty("toolConfig", out _));
		}
	}

	[Fact]
	public async Task MistralAlwaysSendsStrictBecauseItsDecoderSupportsIt()
	{
		var model = new Model
		{
			Id = "mistral-large-latest",
			Provider = "mistral",
			Api = MistralConversationsProvider.ApiId,
			BaseUrl = "https://api.mistral.test/v1",
			ContextWindow = 8_000,
			MaxTokens = 1_000,
		};

		using var document = await CaptureAsync(h => new MistralConversationsProvider(new HttpClient(h)), model);
		var function = document.RootElement.GetProperty("tools")[0].GetProperty("function");

		Assert.True(function.GetProperty("strict").GetBoolean());
		Assert.Equal(2, function.GetProperty("parameters").GetProperty("required").GetArrayLength());
	}

	[Fact]
	public async Task BedrockSendsStrictOnlyWithTheCompatFlag()
	{
		// Bedrock 的请求要 SigV4 签名，必须先有凭据；用例结束恢复环境。
		var previousId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
		var previousSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
		Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIA_TEST");
		Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "secret");
		try
		{
			await BedrockStrictFlagAsync();
		}
		finally
		{
			Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousId);
			Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecret);
		}
	}

	private static async Task BedrockStrictFlagAsync()
	{
		static Model Model(bool strict) => new()
		{
			Id = "anthropic.claude-sonnet-4-5-v1:0",
			Provider = "amazon-bedrock",
			Api = BedrockConverseProvider.ApiId,
			BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
			ContextWindow = 200_000,
			MaxTokens = 8_000,
			Compat = new JsonObject { ["supportsStrictMode"] = strict },
		};

		using (var document = await CaptureAsync(h => new BedrockConverseProvider(new HttpClient(h)), Model(true)))
		{
			var spec = document.RootElement.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec");
			Assert.True(spec.GetProperty("strict").GetBoolean());
		}

		using (var document = await CaptureAsync(h => new BedrockConverseProvider(new HttpClient(h)), Model(false)))
		{
			var spec = document.RootElement.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec");
			Assert.False(spec.TryGetProperty("strict", out _));
		}
	}
}
