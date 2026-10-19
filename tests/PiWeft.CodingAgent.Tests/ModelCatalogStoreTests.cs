using System.Net;
using System.Text;
using PiWeft.Ai;
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
