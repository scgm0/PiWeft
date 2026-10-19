using Xunit;
using PiWeft.Ai;

namespace PiWeft.Ai.Tests;

public class VirtualModelsTests
{
	[Fact]
	public void CatalogModelExposesOnlyConfiguredThinkingLevels()
	{
		var definition = new VirtualModelDefinition
		{
			Provider = "router",
			Id = "auto",
			Name = "Auto",
			ThinkingLevels = [ThinkingLevels.Off, ThinkingLevels.High],
			ContextWindow = 64_000,
			MaxTokens = 4_096,
			Route = (_, _) => throw new InvalidOperationException(),
		};

		var model = VirtualModels.CreateCatalogModel(definition);

		Assert.True(VirtualModels.IsVirtual(model));
		Assert.Equal(ModelType.Chat, model.Type);
		Assert.True(model.Reasoning);
		Assert.Equal(["off", "high"], ThinkingLevelResolver.GetSupported(model));
		Assert.Equal(64_000, model.ContextWindow);
		Assert.Equal(4_096, model.MaxTokens);
	}

	[Fact]
	public async Task RouteResolvesPhysicalModelAndClampsThinkingLevel()
	{
		var physical = new Model
		{
			Provider = "provider",
			Id = "model",
			Api = "openai-completions",
			Reasoning = true,
			ContextWindow = 128_000,
		};
		var registry = new ModelRegistry();
		registry.Add(physical);
		var definition = new VirtualModelDefinition
		{
			Provider = "router",
			Id = "auto",
			Name = "Auto",
			ThinkingLevels = [ThinkingLevels.Off, ThinkingLevels.High],
			Route = (_, _) => Task.FromResult(new VirtualModelRoute
			{
				Model = physical,
				ThinkingLevel = ThinkingLevels.High,
			}),
		};
		registry.AddVirtual(definition);

		var route = await VirtualModels.RouteAsync(definition, new VirtualModelRequest
		{
			Model = registry.Get("router", "auto")!,
			ThinkingLevel = ThinkingLevels.Low,
			Reason = VirtualRouteReason.User,
			Messages = [],
			Registry = registry,
		});

		Assert.Same(physical, route.Model);
		Assert.Equal(ThinkingLevels.High, route.ThinkingLevel);
	}

	[Fact]
	public async Task StaticRouteRejectsUnknownTarget()
	{
		var registry = new ModelRegistry();
		var definition = new VirtualModelDefinition
		{
			Provider = "router",
			Id = "auto",
			Name = "Auto",
			Route = VirtualModels.StaticRoute("missing/model"),
		};
		registry.AddVirtual(definition);

		await Assert.ThrowsAsync<InvalidOperationException>(() => VirtualModels.RouteAsync(definition, new VirtualModelRequest
		{
			Model = registry.Get("router", "auto")!,
			ThinkingLevel = ThinkingLevels.Off,
			Reason = VirtualRouteReason.User,
			Messages = [],
			Registry = registry,
		}));
	}
}
