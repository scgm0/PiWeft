using System.Net;
using System.Text;
using PiWeft.CodingAgent;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>D-06：远程模型目录拉取的端点、鉴权与解析。</summary>
public class RemoteModelCatalogTests
{
	private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
	{
		public HttpRequestMessage? LastRequest { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(new HttpResponseMessage(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			});
		}
	}

	[Fact]
	public async Task OpenAICompatibleUsesModelsPathAndBearerToken()
	{
		var handler = new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"gpt-5"},{"id":"o4-mini"}]}""");

		var result = await RemoteModelCatalog.FetchAsync("openai-completions", "https://api.example.com/v1", "sk-test", handler);

		Assert.True(result.Ok);
		Assert.Equal(["gpt-5", "o4-mini"], result.Models);
		Assert.Equal("https://api.example.com/v1/models", handler.LastRequest!.RequestUri!.ToString());
		Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
	}

	[Fact]
	public async Task AnthropicUsesApiKeyHeaderAndVersionedPath()
	{
		var handler = new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"claude-sonnet-4-5"}]}""");

		var result = await RemoteModelCatalog.FetchAsync("anthropic-messages", "https://api.anthropic.com", "sk-ant-abc", handler);

		Assert.True(result.Ok);
		Assert.Equal(["claude-sonnet-4-5"], result.Models);
		Assert.Equal("sk-ant-abc", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
		Assert.Contains("/v1/models", handler.LastRequest.RequestUri!.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GoogleStripsTheModelsPrefix()
	{
		var handler = new StubHandler(HttpStatusCode.OK, """{"models":[{"name":"models/gemini-3-pro"},{"name":"models/gemini-3-flash"}]}""");

		var result = await RemoteModelCatalog.FetchAsync("google-generative-ai", "https://generativelanguage.googleapis.com/v1beta", "key", handler);

		Assert.True(result.Ok);
		Assert.Equal(["gemini-3-pro", "gemini-3-flash"], result.Models);
		Assert.Equal("key", handler.LastRequest!.Headers.GetValues("x-goog-api-key").Single());
	}

	[Fact]
	public async Task HttpErrorAndUnsupportedApiReportFailure()
	{
		var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"error":"bad key"}""");

		var failed = await RemoteModelCatalog.FetchAsync("openai-completions", "https://api.example.com/v1", "sk", handler);
		Assert.False(failed.Ok);
		Assert.Contains("401", failed.Error);

		var unsupported = await RemoteModelCatalog.FetchAsync("bedrock-converse-stream", "https://example.com", "k", handler);
		Assert.False(unsupported.Ok);
		Assert.Contains("不支持", unsupported.Error);
	}

	[Fact]
	public void ParseIgnoresGarbageAndDeduplicates()
	{
		Assert.Empty(RemoteModelCatalog.Parse("not json"));
		Assert.Equal(["a", "b"], RemoteModelCatalog.Parse("""{"data":[{"id":"a"},{"id":"a"},{"id":"b"},{"noid":1}]}"""));
	}
}
