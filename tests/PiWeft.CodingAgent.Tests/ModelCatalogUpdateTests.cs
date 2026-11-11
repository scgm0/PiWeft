using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Models;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 在线模型目录：落到 agent 目录的覆盖层、备份/还原、两个下载源与内容比较。
[Collection("process-environment")]
public sealed class ModelCatalogUpdateTests : IDisposable
{
	private const string RepoUrl = "https://catalog.test/models.json";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-overlay-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly bool _offline = PiEnvironment.Offline;

	public ModelCatalogUpdateTests()
	{
		Directory.CreateDirectory(_root);
		PiEnvironment.Offline = false;
	}

	public void Dispose()
	{
		PiEnvironment.Offline = _offline;
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static string Catalog(string id, int contextWindow = 200_000) =>
		$$"""
		{"anthropic":[{"id":"{{id}}","name":"Claude X","api":"anthropic-messages","provider":"anthropic","baseUrl":"https://api.anthropic.com","contextWindow":{{contextWindow}},"maxTokens":8192}]}
		""";

	[Fact]
	public void OverlayAppliesOnlyWhenTheDigestMatches()
	{
		WriteOverlay(Catalog("claude-x"), version: null, digestOverride: "deadbeef");
		var status = ModelCatalogOverlay.Status(_root);
		Assert.True(status.HasOverlayFiles);
		Assert.True(status.OverlayInvalid);
		Assert.Null(ModelCatalogOverlay.CreateRegistry(_root).Get("anthropic", "claude-x"));

		WriteOverlay(Catalog("claude-x"), version: null);
		var registry = ModelCatalogOverlay.CreateRegistry(_root);
		Assert.NotNull(registry.Get("anthropic", "claude-x"));
		Assert.Equal(1, ModelCatalogOverlay.Status(_root).Overlay!.EntryCount);
	}

	[Fact]
	public void OverlayCacheDropsOldContentAfterRewrite()
	{
		WriteOverlay(Catalog("claude-v1"), version: null);
		Assert.NotNull(ModelCatalogOverlay.CreateRegistry(_root).Get("anthropic", "claude-v1"));

		// 直接改写文件（绕过写入器）也必须失效：缓存键里带目录摘要与元数据原文。
		WriteOverlay(Catalog("claude-v2"), version: null);
		var registry = ModelCatalogOverlay.CreateRegistry(_root);
		Assert.NotNull(registry.Get("anthropic", "claude-v2"));
		Assert.Null(registry.Get("anthropic", "claude-v1"));
	}

	[Fact]
	public async Task RepoUpdateBacksUpAndBothRestoresWork()
	{
		var body = Catalog("claude-x");
		var handler = new RouteHandler(_ => Json(body));
		var options = new ModelCatalogFetchOptions { IncludePiAi = false, Urls = [RepoUrl] };

		var first = await OnlineModelCatalog.UpdateAsync(_root, handler, options);
		Assert.True(first.Ok);
		Assert.True(first.Updated);
		Assert.NotNull(ModelCatalogOverlay.CreateRegistry(_root).Get("anthropic", "claude-x"));

		var status = ModelCatalogOverlay.Status(_root);
		Assert.NotNull(status.Overlay);
		Assert.Null(status.Overlay!.Version);
		Assert.False(status.HasBackup);

		// 第二版写入前，第一版必须进备份，且旧目录在备份期间仍然有效。
		body = Catalog("claude-y");
		var second = await OnlineModelCatalog.UpdateAsync(_root, handler, options);
		Assert.True(second.Ok && second.Updated);
		status = ModelCatalogOverlay.Status(_root);
		Assert.True(status.HasBackup);
		Assert.NotNull(ModelCatalogOverlay.CreateRegistry(_root).Get("anthropic", "claude-y"));

		var previous = OnlineModelCatalog.RestorePrevious(_root);
		Assert.True(previous.Ok && previous.Updated);
		var registry = ModelCatalogOverlay.CreateRegistry(_root);
		Assert.NotNull(registry.Get("anthropic", "claude-x"));
		Assert.Null(registry.Get("anthropic", "claude-y"));

		var builtin = OnlineModelCatalog.RestoreBuiltin(_root);
		Assert.True(builtin.Ok && builtin.Updated);
		var after = ModelCatalogOverlay.Status(_root);
		Assert.False(after.HasOverlayFiles);
		Assert.True(after.HasBackup);
		Assert.Null(ModelCatalogOverlay.CreateRegistry(_root).Get("anthropic", "claude-x"));
	}

	[Theory]
	[InlineData("""{"anthropic":null}""")]
	[InlineData("""{"anthropic":[null]}""")]
	public async Task MalformedRepoCatalogIsRejectedInsteadOfCrashing(string body)
	{
		var handler = new RouteHandler(_ => Json(body));
		var options = new ModelCatalogFetchOptions { IncludePiAi = false, Urls = [RepoUrl] };

		var result = await OnlineModelCatalog.UpdateAsync(_root, handler, options);

		Assert.False(result.Ok);
		Assert.Equal(ModelCatalogError.Validation, result.Error);
		Assert.False(File.Exists(ModelCatalogFiles.CatalogPath(_root)));
	}

	[Fact]
	public void RestorePreviousWithoutBackupReportsNoBackup()
	{
		var result = OnlineModelCatalog.RestorePrevious(_root);

		Assert.False(result.Ok);
		Assert.Equal(ModelCatalogError.NoBackup, result.Error);
	}

	[Fact]
	public async Task RepoCheckIgnoresFormattingAndKeyOrder()
	{
		var body = Catalog("claude-x");
		var handler = new RouteHandler(_ => Json(body));
		var options = new ModelCatalogFetchOptions { IncludePiAi = false, Urls = [RepoUrl] };
		Assert.True((await OnlineModelCatalog.UpdateAsync(_root, handler, options)).Updated);

		// 同样的模型，provider 顺序、字段顺序与空白都换了：内容摘要必须一致。
		handler = new RouteHandler(_ => Json("""
			{"anthropic":[
				{"maxTokens":8192,"contextWindow":200000,"baseUrl":"https://api.anthropic.com","provider":"anthropic","api":"anthropic-messages","name":"Claude X","id":"claude-x"}
			]}
			"""));
		var check = await OnlineModelCatalog.CheckAsync(_root, handler, options);
		Assert.True(check.Ok);
		Assert.False(check.HasUpdate);
	}

	[Fact]
	public async Task NpmSourceWritesPackageVersionAndSecondCheckSkipsDownloads()
	{
		var (handler, _) = NpmFixture();
		var options = new ModelCatalogFetchOptions { IncludeRepo = false };

		var update = await OnlineModelCatalog.UpdateAsync(_root, handler, options);
		Assert.True(update.Ok);
		Assert.True(update.Updated);
		Assert.Equal("1.5.2", update.Info!.Version);
		Assert.NotNull(ModelCatalogOverlay.CreateRegistry(_root).Get("anthropic", "claude-x"));
		Assert.Equal("1.5.2", ModelCatalogOverlay.Status(_root).Overlay!.Version);

		// 覆盖版已经写明 pi-ai 版本：下一次检查只解析一次元数据，不再下载 tarball。
		var before = handler.Requests.Count;
		var check = await OnlineModelCatalog.CheckAsync(_root, handler, options);
		Assert.True(check.Ok);
		Assert.False(check.HasUpdate);
		Assert.Equal(1, handler.Requests.Count - before);
	}

	[Fact]
	public async Task NpmRegistryFailureFallsBackToTheOfficialRegistry()
	{
		var (tarball, registry) = NpmPackage();
		var handler = new RouteHandler(request =>
		{
			if (request.RequestUri!.Host == "registry.npmmirror.com")
			{
				return Json("", HttpStatusCode.ServiceUnavailable);
			}

			return request.RequestUri.AbsolutePath switch
			{
				var path when path.EndsWith("/latest", StringComparison.Ordinal) => Json(registry),
				"/pi-ai.tgz" => Binary(tarball),
				_ => Json("", HttpStatusCode.NotFound),
			};
		});

		var result = await OnlineModelCatalog.UpdateAsync(_root, handler, new ModelCatalogFetchOptions { IncludeRepo = false });

		Assert.True(result.Ok);
		Assert.True(result.Updated);
		Assert.Contains(handler.Requests, request => request.RequestUri!.Host == "registry.npmjs.org");
	}

	[Fact]
	public async Task NpmManifestHashMismatchLeavesNoOverlay()
	{
		var tarball = Tarball(
			"""{"schemaVersion":6,"files":{"anthropic.json":"deadbeef"}}""",
			PiAiFile("claude-x"));
		var registry = RegistryJson(tarball);
		var handler = NpmHandler(registry, tarball);

		var result = await OnlineModelCatalog.UpdateAsync(_root, handler, new ModelCatalogFetchOptions { IncludeRepo = false });

		Assert.False(result.Ok);
		Assert.Equal(ModelCatalogError.Validation, result.Error);
		Assert.False(File.Exists(ModelCatalogFiles.CatalogPath(_root)));
	}

	[Fact]
	public async Task NpmTarballIntegrityMismatchIsRejected()
	{
		var modelFile = PiAiFile("claude-x");
		var tarball = Tarball(Manifest(modelFile), modelFile);
		var registry = RegistryJson(tarball, integrity: "sha512-AAAA");
		var handler = NpmHandler(registry, tarball);

		var result = await OnlineModelCatalog.UpdateAsync(_root, handler, new ModelCatalogFetchOptions { IncludeRepo = false });

		Assert.False(result.Ok);
		Assert.Equal(ModelCatalogError.Validation, result.Error);
		Assert.False(File.Exists(ModelCatalogFiles.CatalogPath(_root)));
	}

	[Fact]
	public async Task NpmFailureFallsBackToRepoUntilAnNpmOverlayExists()
	{
		var body = Catalog("claude-repo");
		var repoHits = 0;
		var handler = new RouteHandler(request =>
		{
			if (request.RequestUri!.Host.StartsWith("registry.", StringComparison.Ordinal) ||
				request.RequestUri.Host == "packages.test")
			{
				return Json("", HttpStatusCode.ServiceUnavailable);
			}

			repoHits++;
			return Json(body);
		});
		var options = new ModelCatalogFetchOptions { Urls = [RepoUrl] };

		var fallback = await OnlineModelCatalog.UpdateAsync(_root, handler, options);
		Assert.True(fallback.Ok && fallback.Updated);
		Assert.Equal(1, repoHits);
		Assert.Null(fallback.Info!.Version);

		// 已有 pi-ai 覆盖版后 npm 失败：不再用无版本号的仓库文件覆盖，避免降级。
		WriteOverlay(Catalog("claude-npm"), version: "1.5.2");
		var catalogBefore = File.ReadAllBytes(ModelCatalogFiles.CatalogPath(_root));
		repoHits = 0;
		var blocked = await OnlineModelCatalog.UpdateAsync(_root, handler, options);
		Assert.False(blocked.Ok);
		Assert.Equal(ModelCatalogError.Network, blocked.Error);
		Assert.Equal(0, repoHits);
		Assert.Equal(catalogBefore, File.ReadAllBytes(ModelCatalogFiles.CatalogPath(_root)));
	}

	[Fact]
	public async Task ConditionalRepoRequestKeepsTheCurrentOverlay()
	{
		var body = Catalog("claude-x");
		var handler = new RouteHandler(request =>
			request.Headers.IfNoneMatch.Count > 0
				? new HttpResponseMessage(HttpStatusCode.NotModified)
				: Json(body, etag: "\"catalog-v1\""));
		var options = new ModelCatalogFetchOptions { IncludePiAi = false, Urls = [RepoUrl] };

		Assert.True((await OnlineModelCatalog.UpdateAsync(_root, handler, options)).Updated);
		var meta = ModelCatalogOverlay.ReadOverlay(_root)!.Meta;
		Assert.Equal("\"catalog-v1\"", meta.ETag);

		var second = await OnlineModelCatalog.UpdateAsync(_root, handler, options);
		Assert.True(second.Ok);
		Assert.False(second.Updated);
		Assert.False(File.Exists(ModelCatalogFiles.CatalogBackupPath(_root)));
	}

	[Fact]
	public async Task InvalidCurrentOverlayDoesNotOverwriteTheLastGoodBackup()
	{
		var body = Catalog("claude-v1");
		var handler = new RouteHandler(_ => Json(body));
		var options = new ModelCatalogFetchOptions { IncludePiAi = false, Urls = [RepoUrl] };
		Assert.True((await OnlineModelCatalog.UpdateAsync(_root, handler, options)).Updated);
		body = Catalog("claude-v2");
		Assert.True((await OnlineModelCatalog.UpdateAsync(_root, handler, options)).Updated);

		// 当前目录被改成与元数据摘要不符：写下一版时不能把它当备份覆盖掉 v1。
		File.WriteAllText(ModelCatalogFiles.MetaPath(_root), "{\"digest\":\"deadbeef\"}");
		Assert.Null(ModelCatalogOverlay.ReadOverlay(_root));
		body = Catalog("claude-v3");
		var third = await OnlineModelCatalog.UpdateAsync(_root, handler, options);
		Assert.True(third.Ok && third.Updated, $"{third.Error}: {third.Message}");
		Assert.Equal("claude-v3", ModelCatalogOverlay.ReadOverlay(_root)!.Models["anthropic"][0].Id);

		var restore = OnlineModelCatalog.RestorePrevious(_root);
		Assert.True(restore.Ok);
		Assert.Equal("claude-v1", ModelCatalogOverlay.ReadOverlay(_root)!.Models["anthropic"][0].Id);
	}

	[Fact]
	public void SemverComparisonHandlesPrereleases()
	{
		Assert.True(PiAiCatalogSource.CompareSemver("1.10.0", "1.9.9") > 0);
		Assert.True(PiAiCatalogSource.CompareSemver("1.5.2", "1.5.2-beta.1") > 0);
		Assert.True(PiAiCatalogSource.CompareSemver("1.5.2-beta.2", "1.5.2-beta.10") < 0);
		Assert.Equal(0, PiAiCatalogSource.CompareSemver("1.5.2", "1.5.2"));
		Assert.Equal(0, PiAiCatalogSource.CompareSemver("1.5.2+build.7", "1.5.2"));
		Assert.True(PiAiCatalogSource.CompareSemver("1.5.3", "1.5.2-beta.1+build") > 0);
	}

	private void WriteOverlay(string catalog, string? version, string? digestOverride = null)
	{
		var bytes = Encoding.UTF8.GetBytes(catalog);
		var models = ModelCatalogOverlay.ParseCatalog(bytes);
		var summary = ModelCatalogOverlay.Summarize(models);
		var meta = new ModelCatalogMeta
		{
			Digest = digestOverride ?? ModelCatalogOverlay.Digest(bytes),
			EntryCount = summary.EntryCount,
			ProviderCount = summary.ProviderCount,
			FetchedAt = DateTimeOffset.UtcNow,
			Source = "test",
			PackageVersion = version,
		};
		File.WriteAllBytes(ModelCatalogFiles.CatalogPath(_root), bytes);
		File.WriteAllBytes(ModelCatalogFiles.MetaPath(_root),
			JsonSerializer.SerializeToUtf8Bytes(meta, ModelCatalogJsonContext.Default.ModelCatalogMeta));
	}

	private const string PiAiFileTemplate = """
		{"anthropic-messages":{"chat:__ID__":{"id":"__ID__","name":"Claude X","api":"anthropic-messages","provider":"anthropic","baseUrl":"https://api.anthropic.com","contextWindow":200000,"maxTokens":8192}}}
		""";

	private static string PiAiFile(string id) => PiAiFileTemplate.Replace("__ID__", id, StringComparison.Ordinal);

	private static string Manifest(string modelFile)
	{
		var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelFile)));
		return """{"schemaVersion":6,"generatedAt":"2026-11-10T12:05:37.628Z","files":{"anthropic.json":"__HASH__"}}""".Replace("__HASH__", hash, StringComparison.Ordinal);
	}

	private static (RouteHandler Handler, byte[] Tarball) NpmFixture()
	{
		var (tarball, registry) = NpmPackage();
		return (NpmHandler(registry, tarball), tarball);
	}

	private static (byte[] Tarball, string Registry) NpmPackage()
	{
		var modelFile = PiAiFile("claude-x");
		var tarball = Tarball(Manifest(modelFile), modelFile);
		return (tarball, RegistryJson(tarball));
	}

	private static RouteHandler NpmHandler(string registry, byte[] tarball) => new(request =>
		request.RequestUri!.AbsolutePath switch
		{
			var path when path.EndsWith("/latest", StringComparison.Ordinal) => Json(registry),
			"/pi-ai.tgz" => Binary(tarball),
			_ => Json("", HttpStatusCode.NotFound),
		});

	private static string RegistryJson(byte[] tarball, string? integrity = null)
	{
		var sri = integrity ?? "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball));
		return new JsonObject
		{
			["version"] = "1.5.2",
			["dist"] = new JsonObject
			{
				["tarball"] = "https://packages.test/pi-ai.tgz",
				["integrity"] = sri,
			},
		}.ToJsonString();
	}

	private static byte[] Tarball(string manifest, string modelFile)
	{
		using var output = new MemoryStream();
		using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
		{
			using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
			WriteTarEntry(tar, "package/dist/providers/data/.manifest.json", Encoding.UTF8.GetBytes(manifest));
			WriteTarEntry(tar, "package/dist/providers/data/anthropic.json", Encoding.UTF8.GetBytes(modelFile));
		}

		return output.ToArray();
	}

	private static void WriteTarEntry(TarWriter tar, string name, byte[] bytes)
	{
		using var data = new MemoryStream(bytes);
		tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
	}

	private static HttpResponseMessage Binary(byte[] body) =>
		new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

	private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? etag = null)
	{
		var response = new HttpResponseMessage(status)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};
		if (etag is not null)
		{
			response.Headers.ETag = new EntityTagHeaderValue(etag);
		}

		return response;
	}

	private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(route(request));
		}
	}
}
