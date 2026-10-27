using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
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

		Assert.True(ModelCatalogStore.ShouldRefresh(_root), "没有缓存时必须该刷");

		var outcome = await ModelCatalogStore.RefreshAllAsync(_root, handler: handler);

		Assert.True(outcome.Updated > 0);
		Assert.Equal(0, outcome.Failed);
		Assert.True(File.Exists(Path.Combine(_root, "models-store.json")));

		// 刷新窗口内不再重复全量拉取；显式 force 才绕过节流。
		Assert.False(ModelCatalogStore.ShouldRefresh(_root));
		var throttled = await ModelCatalogStore.RefreshAllAsync(_root, handler: handler);
		Assert.True(throttled.NothingToDo, "节流窗口内不该发任何请求");
		Assert.True((await ModelCatalogStore.RefreshAllAsync(_root, handler: handler, force: true)).Updated > 0);

		// 时间戳缺失 = 没有节流依据，必须放行而不是永远不刷。
		var undated = Path.Combine(_root, "undated");
		Directory.CreateDirectory(undated);
		File.WriteAllText(Path.Combine(undated, "models-store.json"),
			"""{"providers":{"anthropic":{"models":[{"id":"x"}],"checkedAt":0,"etag":null,"lastModified":0}}}""");
		Assert.True(ModelCatalogStore.ShouldRefresh(undated));

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
		public List<string?> IfNoneMatch { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			IfNoneMatch.Add(request.Headers.IfNoneMatch.ToString());
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
				Headers = { ETag = new EntityTagHeaderValue("\"etag-1\"") },
			});
		}
	}

	/// <summary>对上次的 ETag 回 304，其余回 200。</summary>
	private sealed class ConditionalCatalogHandler(string body) : HttpMessageHandler
	{
		public List<string?> IfNoneMatch { get; } = [];

		public int NotModifiedCount { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var sent = request.Headers.IfNoneMatch.ToString();
			IfNoneMatch.Add(sent);
			if (sent.Contains("\"etag-1\"", StringComparison.Ordinal))
			{
				NotModifiedCount++;
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
			}

			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
				Headers = { ETag = new EntityTagHeaderValue("\"etag-1\"") },
			});
		}
	}
	/// <summary>
	/// 条件请求：第二次刷新必须把上次拿到的 ETag 带回去；服务端 304 时不重新传输、
	/// 不覆盖缓存里的模型，但 checkedAt 要推进（否则节流窗口永远推不动，每次启动都全量拉一遍）。
	/// </summary>
	[Fact]
	public async Task ConditionalRequestAvoidsReDownloadingUnchangedCatalog()
	{
		const string body = """{"data":[{"id":"remote-only","name":"Remote Only","api":"openai-completions","contextWindow":128000,"maxTokens":4096}]}""";
		var first = new ConditionalCatalogHandler(body);
		await ModelCatalogStore.RefreshAllAsync(_root, handler: first, force: true);
		Assert.All(first.IfNoneMatch, sent => Assert.True(string.IsNullOrEmpty(sent)));

		var cached = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "models-store.json")))!;
		var anthropic = cached["providers"]!["anthropic"]!.AsObject();
		Assert.Equal("\"etag-1\"", (string?)anthropic["etag"]);
		var before = (long)anthropic["checkedAt"]!;

		var second = new ConditionalCatalogHandler(body);
		var outcome = await ModelCatalogStore.RefreshAllAsync(_root, handler: second, force: true);

		Assert.Contains("\"etag-1\"", second.IfNoneMatch);
		Assert.True(second.NotModifiedCount > 0, "带 ETag 的请求应当命中 304");
		Assert.Equal(0, outcome.Updated);
		Assert.True(outcome.NotModified > 0);

		var after = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "models-store.json")))!
			["providers"]!["anthropic"]!.AsObject();
		Assert.True((long)after["checkedAt"]! > before, "304 也要推进 checkedAt，否则节流永不生效");
		Assert.Equal("remote-only", (string?)after["models"]!.AsArray()[0]!["id"]);
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
