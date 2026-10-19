using System.Net;
using System.Text;
using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.CodingAgent.Models;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public sealed class ModelCatalogStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-catalog-" + Guid.NewGuid().ToString("N")[..8]);

	public ModelCatalogStoreTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}


	[Fact]
	public async Task RefreshAllWritesACacheThatCanBeLoadedOffline()
	{
		var handler = new FakeCatalogHandler("""{"data":[{"id":"remote-only","name":"Remote Only","api":"openai-completions","contextWindow":128000,"maxTokens":4096}]}""");

		var (updated, failed) = await ModelCatalogStore.RefreshAllAsync(_root, handler: handler);

		Assert.True(updated > 0);
		Assert.Equal(0, failed);
		Assert.True(File.Exists(Path.Combine(_root, "models-store.json")));

		var registry = ModelRegistry.LoadDefault();
		Assert.True(ModelCatalogStore.LoadCached(registry, _root) > 0);
		Assert.NotNull(registry.Resolve("anthropic", "remote-only"));
	}

	[Fact]
	public async Task OpenRouterAccountCatalogUsesKeySpecificModels()
	{
		OAuthStore.Save("openrouter", new OAuthCredential { Access = "key", Refresh = "", Expires = long.MaxValue }, _root);
		var handler = new FakeCatalogHandler(
			"""{"data":[{"id":"openrouter/auto","name":"Auto","context_length":200000,"top_provider":{"max_completion_tokens":8192},"pricing":{"prompt":"0.000001","completion":"0.000002","input_cache_read":"0.0000001"},"architecture":{"input_modalities":["text","image"]},"supported_parameters":["reasoning"]}]}""");

		Assert.True(await ModelCatalogStore.RefreshAccountCatalogAsync("openrouter", _root, handler));

		var registry = ModelRegistry.LoadDefault();
		ModelCatalogStore.LoadCached(registry, _root);
		var model = registry.Resolve("openrouter", "openrouter/auto")!;
		Assert.Equal(200000, model.ContextWindow);
		Assert.Equal(8192, model.MaxTokens);
		Assert.Contains("image", model.Input);
		Assert.Equal(1, model.Cost.Input);
		Assert.Equal(2, model.Cost.Output);
		Assert.True(model.Reasoning);
	}

	[Fact]
	public async Task RadiusAccountCatalogReplacesProviderModels()
	{
		OAuthStore.Save("radius", new OAuthCredential
		{
			Access = "token",
			Refresh = "refresh",
			Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
		}, _root);
		var handler = new FakeCatalogHandler(
			"""{"baseUrl":"https://gateway.example/v1","models":[{"id":"custom","name":"Custom","reasoning":true,"input":["text"],"cost":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0},"contextWindow":64000,"maxTokens":4096}]}""");

		Assert.True(await ModelCatalogStore.RefreshAccountCatalogAsync("radius", _root, handler));

		var registry = ModelRegistry.LoadDefault();
		ModelCatalogStore.LoadCached(registry, _root);
		var model = registry.Resolve("radius", "custom")!;
		Assert.Equal("pi-messages", model.Api);
		Assert.Equal("https://gateway.example/v1", model.BaseUrl);
		Assert.Equal(64000, model.ContextWindow);
	}

	private sealed class FakeCatalogHandler(string body) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			});
	}
	[Fact]
	public void CachedRemoteModelsOverrideBuiltInsAndSurviveOffline()
	{
		File.WriteAllText(
			Path.Combine(_root, "models-store.json"),
			"""
			{"providers":{"anthropic":{"models":[{"id":"claude-sonnet-4-5","name":"Remote Sonnet","provider":"anthropic","api":"anthropic-messages","contextWindow":123456,"maxTokens":4096}]}}}
			""");

		var registry = ModelRegistry.LoadDefault();
		var loaded = ModelCatalogStore.LoadCached(registry, _root);
		var model = registry.Resolve("anthropic", "claude-sonnet-4-5")!;

		Assert.True(loaded > 0);
		Assert.Equal("Remote Sonnet", model.Name);
		Assert.Equal(123456, model.ContextWindow);
	}
}
