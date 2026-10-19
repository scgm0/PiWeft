using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>Custom endpoints and metadata overrides from <c>models.json</c>.</summary>
public class ModelsFileTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-models-" + Guid.NewGuid().ToString("N")[..8]);

	public ModelsFileTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string AgentDir => Path.Combine(_root, "agent");

	private string ProjectCwd => Path.Combine(_root, "project");

	private void WriteUser(string json)
	{
		Directory.CreateDirectory(AgentDir);
		File.WriteAllText(Path.Combine(AgentDir, "models.json"), json);
	}

	private void WriteProject(string json)
	{
		Directory.CreateDirectory(Path.Combine(ProjectCwd, ".pi"));
		File.WriteAllText(Path.Combine(ProjectCwd, ".pi", "models.json"), json);
	}

	[Fact]
	public void Load_ParsesVirtualModelRoutes()
	{
		WriteUser(
			"""
			{
			  "providers": {
			    "router": {
			      "models": [
			        {
			          "id": "auto",
			          "name": "Auto",
			          "contextWindow": 64000,
			          "virtual": {
			            "model": "ollama/qwen2.5-coder:7b",
			            "thinkingLevel": "high",
			            "thinkingLevels": ["off", "high"],
			            "input": ["text"]
			          }
			        }
			      ]
			    }
			  }
			}
			""");

		var applied = ModelsFileLoader.Load(ProjectCwd, AgentDir);

		Assert.Empty(applied.Models);
		var definition = Assert.Single(applied.VirtualModels);
		Assert.Equal("router", definition.Provider);
		Assert.Equal("auto", definition.Id);
		Assert.Equal("Auto", definition.Name);
		Assert.Equal(64000, definition.ContextWindow);
		Assert.Equal(["off", "high"], definition.ThinkingLevels);
		Assert.Equal(["text"], definition.Input);
	}

	[Fact]
	public void Load_AddsACompatibleEndpoint()
	{
		WriteUser(
			"""
			{
			  "$schema": "https://pi.dev/schemas/models.schema.json",
			  "providers": {
			    "ollama": {
			      "baseUrl": "http://localhost:11434/v1",
			      "api": "openai-completions",
			      "apiKey": "ollama",
			      "models": [
			        { "id": "qwen2.5-coder:7b", "name": "Qwen 2.5 Coder", "reasoning": false, "contextWindow": 32768, "maxTokens": 8192 }
			      ]
			    }
			  }
			}
			""");

		var applied = ModelsFileLoader.Load(ProjectCwd, AgentDir);

		var model = Assert.Single(applied.Models);
		Assert.Equal("ollama", model.Provider);
		Assert.Equal("qwen2.5-coder:7b", model.Id);
		Assert.Equal("Qwen 2.5 Coder", model.Name);
		Assert.Equal("openai-completions", model.Api);
		Assert.Equal("http://localhost:11434/v1", model.BaseUrl);
		Assert.Equal(32768, model.ContextWindow);
		Assert.Equal(8192, model.MaxTokens);
		Assert.Equal("ollama", applied.ApiKeys["ollama"]);
	}

	[Fact]
	public void Load_ProviderApiAndBaseUrlAreDefaultsForTheModels()
	{
		WriteUser(
			"""
			{
			  "providers": {
			    "local": {
			      "baseUrl": "http://localhost:8080/v1",
			      "api": "openai-completions",
			      "headers": { "X-Proxy": "yes" },
			      "models": [
			        { "id": "a" },
			        { "id": "b", "api": "anthropic-messages", "baseUrl": "http://localhost:8081" }
			      ]
			    }
			  }
			}
			""");

		var applied = ModelsFileLoader.Load(ProjectCwd, AgentDir);

		var a = applied.Models.Single(static m => m.Id == "a");
		Assert.Equal("openai-completions", a.Api);
		Assert.Equal("http://localhost:8080/v1", a.BaseUrl);
		Assert.Equal("yes", a.Headers!["X-Proxy"]);
		// A model without a name falls back to its id.
		Assert.Equal("a", a.Name);

		// A model may override the provider defaults.
		var b = applied.Models.Single(static m => m.Id == "b");
		Assert.Equal("anthropic-messages", b.Api);
		Assert.Equal("http://localhost:8081", b.BaseUrl);
	}

	[Fact]
	public void Load_InterpolatesEnvironmentVariablesInKeysAndHeaders()
	{
		Environment.SetEnvironmentVariable("MODELS_TEST_KEY", "secret-key");
		try
		{
			WriteUser(
				"""
				{
				  "providers": {
				    "proxy": {
				      "baseUrl": "https://proxy.example.test/v1",
				      "api": "openai-completions",
				      "apiKey": "${MODELS_TEST_KEY}",
				      "models": [ { "id": "gpt-x" } ]
				    }
				  }
				}
				""");

			var applied = ModelsFileLoader.Load(ProjectCwd, AgentDir);
			Assert.Equal("secret-key", applied.ApiKeys["proxy"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable("MODELS_TEST_KEY", null);
		}
	}

	[Fact]
	public void Load_ProjectProvidersReplaceUserProviders()
	{
		WriteUser("""{"providers":{"shared":{"baseUrl":"https://user.example/v1","api":"openai-completions","models":[{"id":"user-model"}]}}}""");
		WriteProject("""{"providers":{"shared":{"baseUrl":"https://project.example/v1","api":"openai-completions","models":[{"id":"project-model"}]}}}""");

		var applied = ModelsFileLoader.Load(ProjectCwd, AgentDir);

		var model = Assert.Single(applied.Models);
		Assert.Equal("project-model", model.Id);
		Assert.Equal("https://project.example/v1", model.BaseUrl);
	}

	[Fact]
	public void Load_WithoutFiles_ReturnsNothing()
	{
		var applied = ModelsFileLoader.Load(ProjectCwd, Path.Combine(_root, "missing"));

		Assert.Empty(applied.Models);
		Assert.Empty(applied.ApiKeys);
	}

	[Fact]
	public void Load_ModelOverrideChangesMetadataOfACatalogModel()
	{
		WriteUser(
			"""
			{
			  "modelOverrides": {
			    "claude-sonnet-4-5": { "name": "Sonnet (proxied)", "maxTokens": 1234 }
			  }
			}
			""");

		var registry = ModelRegistry.LoadDefault();
		var before = registry.Resolve("anthropic", "claude-sonnet-4-5");
		Assert.NotNull(before);

		var applied = ModelsFileLoader.ApplyOverrides(
			registry,
			ModelsFileLoader.Read(ModelsFileLoader.UserPath(AgentDir)),
			ModelsFileLoader.Read(ModelsFileLoader.ProjectPath(ProjectCwd)));

		// The id may exist on several providers; each match is overridden.
		Assert.True(applied >= 1);

		var after = registry.Resolve("anthropic", "claude-sonnet-4-5")!;
		Assert.Equal("Sonnet (proxied)", after.Name);
		Assert.Equal(1234, after.MaxTokens);
		// Fields the override does not mention are untouched.
		Assert.Equal(before!.ContextWindow, after.ContextWindow);
		Assert.Equal(before.Api, after.Api);
	}

	[Fact]
	public void ApplyOverrides_IgnoresUnknownIds()
	{
		var registry = ModelRegistry.LoadDefault();
		var file = new ModelsFile
		{
			ModelOverrides = new Dictionary<string, ModelsOverrideEntry>
			{
				["not-a-real-model"] = new() { Name = "x" },
			},
		};

		Assert.Equal(0, ModelsFileLoader.ApplyOverrides(registry, file, null));
	}


	[Fact]
	public void ProviderLevelModelOverridesApplyToCatalogModels()
	{
		WriteUser(
			"""
			{
			  "providers": {
			    "anthropic": {
			      "modelOverrides": {
			        "claude-sonnet-4-5": { "name": "Provider override", "maxTokens": 777 }
			      }
			    }
			  }
			}
			""");

		var registry = ModelRegistry.LoadDefault();
		var file = ModelsFileLoader.Read(ModelsFileLoader.UserPath(AgentDir));

		Assert.True(ModelsFileLoader.ApplyOverrides(registry, file, null) >= 1);
		var model = registry.Resolve("anthropic", "claude-sonnet-4-5")!;
		Assert.Equal("Provider override", model.Name);
		Assert.Equal(777, model.MaxTokens);
	}

	[Fact]
	public void ProviderBaseUrlAppliesToBuiltInModels()
	{
		WriteUser("""{"providers":{"anthropic":{"baseUrl":"https://proxy.example.test/v1"}}}""");

		var registry = ModelRegistry.LoadDefault();
		var file = ModelsFileLoader.Read(ModelsFileLoader.UserPath(AgentDir));
		Assert.True(ModelsFileLoader.ApplyProviderSettings(registry, file, null) > 0);

		Assert.Equal("https://proxy.example.test/v1", registry.Resolve("anthropic", "claude-sonnet-4-5")!.BaseUrl);
	}

	[Fact]
	public void PartialCostOverrideKeepsUnmentionedRates()
	{
		var registry = ModelRegistry.LoadDefault();
		var before = registry.Resolve("anthropic", "claude-sonnet-4-5")!;
		var file = new ModelsFile
		{
			Providers = new Dictionary<string, ModelsProviderEntry>
			{
				["anthropic"] = new()
				{
					ModelOverrides = new Dictionary<string, ModelsOverrideEntry>
					{
						["claude-sonnet-4-5"] = new() { Cost = new ModelCostPatch { Input = 99 } },
					},
				},
			},
		};

		ModelsFileLoader.ApplyOverrides(registry, file, null);
		var after = registry.Resolve("anthropic", "claude-sonnet-4-5")!;

		Assert.Equal(99, after.Cost.Input);
		Assert.Equal(before.Cost.Output, after.Cost.Output);
		Assert.Equal(before.Cost.CacheRead, after.Cost.CacheRead);
	}

	[Fact]
	public void Load_CustomModelsJoinTheRegistryAlongsideTheCatalog()
	{
		WriteUser("""{"providers":{"local":{"baseUrl":"http://localhost:1234/v1","api":"openai-completions","models":[{"id":"my-local"}]}}}""");

		var registry = ModelRegistry.LoadDefault();
		var catalogCount = registry.Count;

		foreach (var model in ModelsFileLoader.Load(ProjectCwd, AgentDir).Models)
		{
			registry.Add(model);
		}

		Assert.Equal(catalogCount + 1, registry.Count);
		Assert.Equal("my-local", registry.Resolve("local", "my-local")!.Id);
		Assert.Equal("http://localhost:1234/v1", registry.Resolve("local/my-local")!.BaseUrl);
		Assert.Equal("my-local", registry.Resolve("local/my-local")!.Id);
	}

	[Fact]
	public void Load_AModelWithoutPricingStillPricesAtZero()
	{
		// A models.json entry normally omits cost; pricing must not throw for it.
		WriteUser("""{"providers":{"local":{"baseUrl":"http://localhost:1234/v1","api":"openai-completions","models":[{"id":"unpriced"}]}}}""");

		var model = ModelsFileLoader.Load(ProjectCwd, AgentDir).Models.Single();

		Assert.NotNull(model.Cost);
		Assert.NotNull(model.Input);
		Assert.Contains("text", model.Input);

		var usage = CostCalculator.Calculate(model, new Usage { Input = 1000, Output = 1000 });
		Assert.Equal(0, usage.Cost.Total);
	}

	[Fact]
	public void Interpolate_LeavesPlainValuesAlone()
	{
		Assert.Equal("literal", ModelsFileLoader.Interpolate("literal"));
		Assert.Null(ModelsFileLoader.Interpolate(null));
	}
}
