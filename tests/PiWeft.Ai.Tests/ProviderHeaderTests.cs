using System.Net.Http;
using System.Text.Json.Nodes;
using PiWeft.Ai.Providers;
using Xunit;

namespace PiWeft.Ai.Tests;

public sealed class ProviderHeaderTests
{
	private static Model Model(string provider, string baseUrl, JsonObject? compat = null) => new()
	{
		Id = "model",
		Provider = provider,
		Api = "openai-completions",
		BaseUrl = baseUrl,
		Compat = compat,
	};

	private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://example.test/chat/completions");

	[Fact]
	public void OpenRouterCompletionsUseXSessionIdOnly()
	{
		var request = Request();
		ProviderSessionHeaders.ApplyCompletions(Model("openrouter", "https://openrouter.ai/api/v1"),
			new StreamOptions { SessionId = "session-42" }, request);
		Assert.Equal("session-42", request.Headers.GetValues("x-session-id").Single());
		Assert.False(request.Headers.Contains("session_id"));
		Assert.False(request.Headers.Contains("x-client-request-id"));
		Assert.False(request.Headers.Contains("x-session-affinity"));
	}

	[Fact]
	public void OptedInOpenAiCompatibilitySendsAffinityHeaders()
	{
		var compat = new JsonObject { ["sendSessionAffinityHeaders"] = true };
		var request = Request();
		ProviderSessionHeaders.ApplyCompletions(Model("custom", "https://gateway.test/v1", compat),
			new StreamOptions { SessionId = "session-42" }, request);
		Assert.Equal("session-42", request.Headers.GetValues("session_id").Single());
		Assert.Equal("session-42", request.Headers.GetValues("x-client-request-id").Single());
		Assert.Equal("session-42", request.Headers.GetValues("x-session-affinity").Single());
	}

	[Fact]
	public void OpenAiNoSessionFormatOmitsSessionId()
	{
		var compat = new JsonObject { ["sendSessionAffinityHeaders"] = true, ["sessionAffinityFormat"] = "openai-nosession" };
		var request = Request();
		ProviderSessionHeaders.ApplyCompletions(Model("custom", "https://gateway.test/v1", compat),
			new StreamOptions { SessionId = "session-42" }, request);
		Assert.False(request.Headers.Contains("session_id"));
		Assert.Equal("session-42", request.Headers.GetValues("x-client-request-id").Single());
		Assert.Equal("session-42", request.Headers.GetValues("x-session-affinity").Single());
	}

	[Fact]
	public void ResponsesAndAnthropicUseTheirOwnAffinitySets()
	{
		var responses = Request();
		ProviderSessionHeaders.ApplyResponses(Model("openrouter", "https://openrouter.ai/api/v1"),
			new StreamOptions { SessionId = "s" }, responses);
		Assert.Equal("s", responses.Headers.GetValues("x-session-id").Single());

		var openAiResponses = Request();
		ProviderSessionHeaders.ApplyResponses(Model("openai", "https://api.openai.com/v1"),
			new StreamOptions { SessionId = "s" }, openAiResponses);
		Assert.Equal("s", openAiResponses.Headers.GetValues("session_id").Single());
		Assert.Equal("s", openAiResponses.Headers.GetValues("x-client-request-id").Single());
		Assert.False(openAiResponses.Headers.Contains("x-session-affinity"));

		var anthropic = Request();
		ProviderSessionHeaders.ApplyAnthropic(Model("anthropic", "https://api.anthropic.com"),
			new StreamOptions { SessionId = "s" }, anthropic);
		Assert.False(anthropic.Headers.Contains("x-session-affinity"));

		var anthropicGateway = Request();
		ProviderSessionHeaders.ApplyAnthropic(Model("custom", "https://gateway.test",
			new JsonObject { ["sendSessionAffinityHeaders"] = true }), new StreamOptions { SessionId = "s" }, anthropicGateway);
		Assert.Equal("s", anthropicGateway.Headers.GetValues("x-session-affinity").Single());
	}

	[Fact]
	public void ExplicitSessionHeadersWin()
	{
		var request = Request();
		request.Headers.TryAddWithoutValidation("session_id", "manual");
		ProviderSessionHeaders.ApplyCompletions(Model("custom", "https://gateway.test/v1",
			new JsonObject { ["sendSessionAffinityHeaders"] = true }), new StreamOptions { SessionId = "auto" }, request);
		Assert.Equal("manual", request.Headers.GetValues("session_id").Single());
	}

	[Fact]
	public void CopilotDynamicHeadersFollowTheLastMessageAndImages()
	{
		var model = Model("github-copilot", "https://api.individual.githubcopilot.com");
		var user = new UserMessage { Content = MessageContent.FromText("hello") };
		var request = Request();
		CopilotHeaders.Apply(model, [user], request);
		Assert.Equal("user", request.Headers.GetValues("X-Initiator").Single());
		Assert.Equal("conversation-edits", request.Headers.GetValues("Openai-Intent").Single());
		Assert.False(request.Headers.Contains("Copilot-Vision-Request"));

		var afterTool = new ToolResultMessage { Content = [new TextContent { Text = "ok" }] };
		var agentRequest = Request();
		CopilotHeaders.Apply(model, [user, afterTool], agentRequest);
		Assert.Equal("agent", agentRequest.Headers.GetValues("X-Initiator").Single());

		var image = new UserMessage
		{
			Content = MessageContent.FromBlocks([new ImageContent { Data = "abc", MimeType = "image/png" }]),
		};
		var visionRequest = Request();
		CopilotHeaders.Apply(model, [image], visionRequest);
		Assert.Equal("true", visionRequest.Headers.GetValues("Copilot-Vision-Request").Single());
	}

	[Fact]
	public void AuthHeaderAddsBearerLikePiWithConfiguredAuth()
	{
		var model = Model("custom", "https://gateway.test/v1") with { AuthHeader = true };
		var request = new HttpRequestMessage(HttpMethod.Post, "https://gateway.test/v1/messages");
		request.Headers.TryAddWithoutValidation("x-api-key", "key");
		request.Headers.TryAddWithoutValidation("Authorization", "Bearer manual");
		ProviderAuthHeader.Apply(model, "key", request);
		Assert.Equal("Bearer key", request.Headers.Authorization!.ToString());
	}
}
