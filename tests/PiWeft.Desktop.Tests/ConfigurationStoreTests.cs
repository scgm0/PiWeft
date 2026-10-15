using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-config-" + Guid.NewGuid().ToString("N"));
	private ConfigurationStore Store => new(Path.Combine(_root, "agent"), Path.Combine(_root, "project"));

	[Fact]
	public void ProviderEditPreservesUnknownFieldsAndExistingKey()
	{
		var store = Store;
		var file = store.Read(ConfigFile.Models);
		file = store.Save(file, """{"extra":true,"providers":{"local":{"apiKey":"secret","compat":{"custom":true},"headers":{"X-Test":"yes"},"models":[{"id":"old","extra":42}]}}}""");
		var next = ConfigurationModels.UpsertProvider(file.Parse(), "local", "local", "https://example.com/v1", "openai-completions", null);
		var saved = store.Save(file, next.ToJsonString());
		Assert.True(saved.Parse()["extra"]!.GetValue<bool>());
		Assert.Equal("secret", saved.Parse()["providers"]!["local"]!["apiKey"]!.GetValue<string>());
		Assert.Equal(42, saved.Parse()["providers"]!["local"]!["models"]![0]!["extra"]!.GetValue<int>());
		Assert.True(saved.Parse()["providers"]!["local"]!["compat"]!["custom"]!.GetValue<bool>());
	}

	[Fact]
	public void ExternalChangeIsRejectedWithoutOverwriting()
	{
		var store = Store;
		var file = store.Save(store.Read(ConfigFile.Settings), "{\"unknown\":1}");
		File.WriteAllText(file.Path, "{\"unknown\":2}");
		Assert.Throws<ConfigurationConflictException>(() => store.Save(file, "{}"));
		Assert.Equal("{\"unknown\":2}", File.ReadAllText(file.Path));
	}

	[Fact]
	public void MissingToExistingIsAConflict()
	{
		var store = Store;
		var file = store.Read(ConfigFile.Models);
		Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
		File.WriteAllText(file.Path, "{}");
		Assert.Throws<ConfigurationConflictException>(() => store.Save(file, "{}"));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("{broken")]
	[InlineData("{\"providers\":[]}")]
	[InlineData("{\"providers\":{\"local\":{\"models\":[{}]}}}")]
	[InlineData("{\"providers\":{\"local\":{\"models\":[{\"id\":\"x\"},{\"id\":\"x\"}]}}}")]
	public void InvalidModelsNeverTouchDisk(string raw)
	{
		var store = Store;
		var file = store.Read(ConfigFile.Models);
		Assert.ThrowsAny<ArgumentException>(() => store.Save(file, raw));
		Assert.False(File.Exists(file.Path));
	}

	[Fact]
	public void ProjectScopeAndCredentialScopeAreSeparated()
	{
		var store = Store;
		Assert.Contains(Path.Combine("project", ".pi", "models.json"), store.Read(ConfigFile.Models, project: true).Path);
		Assert.Throws<ArgumentException>(() => store.Read(ConfigFile.Auth, project: true));
	}

	[Fact]
	public void RedactedExportDoesNotLeakNestedCredentials()
	{
		var store = Store;
		var file = store.Save(store.Read(ConfigFile.Models), """{"providers":{"local":{"apiKey":"secret","headers":{"Authorization":"Bearer hidden","X-Api-Key":"hidden","User-Agent":"test"},"models":[]}}}""");
		var exported = ConfigurationModels.Export(file.Parse());
		Assert.DoesNotContain("secret", exported);
		Assert.DoesNotContain("hidden", exported);
		Assert.DoesNotContain("User-Agent", exported);
		Assert.DoesNotContain("headers", exported);
	}

	[Fact]
	public void ModelPatchPreservesAdvancedProperties()
	{
		var root = JsonNode.Parse("""{"providers":{"local":{"models":[{"id":"old","headers":{"X":"y"},"cost":{"input":1,"output":2,"cacheRead":3,"cacheWrite":4},"compat":{"extra":true}}]}}}""")!.AsObject();
		var next = ConfigurationModels.UpsertModel(root, "local", 0, "new", "New", 32768, 4096, true, true);
		Assert.Equal("y", next["providers"]!["local"]!["models"]![0]!["headers"]!["X"]!.GetValue<string>());
		Assert.Equal(3, next["providers"]!["local"]!["models"]![0]!["cost"]!["cacheRead"]!.GetValue<int>());
	}

	[Fact]
	public void ModelCostIsWrittenOnlyWhenProvided()
	{
		var root = JsonNode.Parse("""{"providers":{"local":{"models":[{"id":"m","cost":{"input":1,"output":2,"cacheRead":3,"cacheWrite":4},"headers":{"X":"y"}}]}}}""")!.AsObject();

		// 不带 cost：保留原有价格与未建模字段。
		var preserved = ConfigurationModels.UpsertModel(root, "local", 0, "m", "M", 1000, 100, false, false);
		var kept = preserved["providers"]!["local"]!["models"]![0]!;
		Assert.Equal(4, ConfigurationModels.Decimal(kept["cost"], "cacheWrite"));
		Assert.Equal("y", kept["headers"]!["X"]!.GetValue<string>());

		// 带 cost：四个字段被写入。
		var replaced = ConfigurationModels.UpsertModel(root, "local", 0, "m", "M", 1000, 100, false, false, new ModelCostValues(10, 20, 30, 40));
		var cost = replaced["providers"]!["local"]!["models"]![0]!["cost"]!;
		Assert.Equal(10, ConfigurationModels.Decimal(cost, "input"));
		Assert.Equal(40, ConfigurationModels.Decimal(cost, "cacheWrite"));

		// 负价格或非有限值被拒绝。
		Assert.Throws<ArgumentException>(() => ConfigurationModels.UpsertModel(root, "local", 0, "m", "M", 1000, 100, false, false, new ModelCostValues(-1, 0, 0, 0)));
		Assert.Throws<ArgumentException>(() => ConfigurationModels.UpsertModel(root, "local", 0, "m", "M", 1000, 100, false, false, new ModelCostValues(double.NaN, 0, 0, 0)));
	}

	[Fact]
	public void AddMissingModelsKeepsExistingEntriesAndDefaultsNewOnes()
	{
		var root = JsonNode.Parse("""{"providers":{"local":{"models":[{"id":"keep","headers":{"X":"y"}}]}}}""")!.AsObject();

		var (next, added) = ConfigurationModels.AddMissingModels(root, "local", ["keep", "fresh", "fresh"]);

		Assert.Equal(1, added);
		var models = next["providers"]!["local"]!["models"]!.AsArray();
		Assert.Equal(2, models.Count);
		// 已存在的条目保留未建模字段。
		Assert.Equal("y", models[0]!["headers"]!["X"]!.GetValue<string>());
		Assert.Equal("fresh", models[1]!["id"]!.GetValue<string>());
		Assert.Equal(128000, models[1]!["contextWindow"]!.GetValue<int>());
		Assert.Equal("text", models[1]!["input"]![0]!.GetValue<string>());

		// 原对象不被修改。
		Assert.Single(root["providers"]!["local"]!["models"]!.AsArray());
	}

	[Fact]
	public void CredentialChangePreservesOtherProvidersAndRejectsOAuthReplacement()
	{
		var root = JsonNode.Parse("""{"other":{"type":"oauth","access":"hidden"},"local":{"type":"api_key","key":"old","env":{"X":"y"}}}""")!.AsObject();
		var next = ConfigurationModels.SetApiKey(root, "local", "new");
		Assert.Equal("hidden", next["other"]!["access"]!.GetValue<string>());
		Assert.Equal("y", next["local"]!["env"]!["X"]!.GetValue<string>());
		Assert.Throws<ArgumentException>(() => ConfigurationModels.SetApiKey(root, "other", "replacement"));
	}

	[Fact]
	public void BadFileCanBeRepairedButNotSilentlyReplacedByAForm()
	{
		var store = Store;
		var file = store.Read(ConfigFile.Settings);
		Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
		File.WriteAllText(file.Path, "bad");
		file = store.Read(ConfigFile.Settings);
		Assert.NotNull(file.Error);
		Assert.Throws<ArgumentException>(() => file.Parse());
		Assert.Null(store.Save(file, "{}").Error);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}
}
