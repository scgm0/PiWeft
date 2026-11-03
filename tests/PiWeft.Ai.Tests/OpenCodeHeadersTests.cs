using System.Net.Http;
using PiWeft.Ai.Providers;
using Xunit;

namespace PiWeft.Ai.Tests;

public sealed class OpenCodeHeadersTests
{
	private static Model Model(string provider = "opencode", string baseUrl = "https://opencode.ai/zen/v1") => new()
	{
		Id = "gpt-5",
		Provider = provider,
		Api = "openai-completions",
		BaseUrl = baseUrl,
	};

	private static HttpRequestMessage Request() =>
		new(HttpMethod.Post, "https://opencode.ai/zen/v1/chat/completions");

	[Theory]
	[InlineData("opencode", "https://opencode.ai/zen/v1")]
	[InlineData("opencode-go", "https://opencode.ai/zen/go/v1")]
	[InlineData("custom-name", "https://opencode.ai/custom/v1")]
	public void OpenCodeRoutesReceiveSessionAndClientHeaders(string provider, string baseUrl)
	{
		var request = Request();
		OpenCodeHeaders.Apply(Model(provider, baseUrl), new StreamOptions { SessionId = "session-42" }, request);
		Assert.Equal("session-42", request.Headers.GetValues(OpenCodeHeaders.SessionHeader).Single());
		Assert.Equal(OpenCodeHeaders.ClientName, request.Headers.GetValues(OpenCodeHeaders.ClientHeader).Single());
	}

	[Fact]
	public void ExplicitHeadersWinIncludingEmptyValues()
	{
		var explicitRequest = Request();
		explicitRequest.Headers.TryAddWithoutValidation(OpenCodeHeaders.SessionHeader, "manual");
		explicitRequest.Headers.TryAddWithoutValidation(OpenCodeHeaders.ClientHeader, "other-client");
		OpenCodeHeaders.Apply(Model(), new StreamOptions { SessionId = "auto" }, explicitRequest);
		Assert.Equal("manual", explicitRequest.Headers.GetValues(OpenCodeHeaders.SessionHeader).Single());
		Assert.Equal("other-client", explicitRequest.Headers.GetValues(OpenCodeHeaders.ClientHeader).Single());

		var emptyRequest = Request();
		emptyRequest.Headers.TryAddWithoutValidation(OpenCodeHeaders.SessionHeader, string.Empty);
		OpenCodeHeaders.Apply(Model(), new StreamOptions { SessionId = "auto" }, emptyRequest);
		Assert.Equal(string.Empty, emptyRequest.Headers.GetValues(OpenCodeHeaders.SessionHeader).Single());
	}

	[Fact]
	public void MissingSessionUsesTheStableHostFallback()
	{
		var first = Request();
		var second = Request();
		OpenCodeHeaders.Apply(Model(), new StreamOptions(), first);
		OpenCodeHeaders.Apply(Model(), new StreamOptions(), second);
		var firstSession = first.Headers.GetValues(OpenCodeHeaders.SessionHeader).Single();
		Assert.Equal(firstSession, second.Headers.GetValues(OpenCodeHeaders.SessionHeader).Single());
		Assert.StartsWith("piweft-", firstSession, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("deepseek", "https://api.deepseek.com/v1")]
	[InlineData("custom", "https://opencode.ai.evil.example/v1")]
	[InlineData("custom", "file://opencode.ai/v1")]
	public void OtherRoutesAreNotDecorated(string provider, string baseUrl)
	{
		Assert.False(OpenCodeHeaders.IsRoute(Model(provider, baseUrl)));
		var request = Request();
		OpenCodeHeaders.Apply(Model(provider, baseUrl), new StreamOptions { SessionId = "session-42" }, request);
		Assert.False(request.Headers.Contains(OpenCodeHeaders.SessionHeader));
		Assert.False(request.Headers.Contains(OpenCodeHeaders.ClientHeader));
	}
}
