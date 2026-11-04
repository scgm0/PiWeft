using System.Text.Json.Nodes;

namespace PiWeft.Ai;

/// 请求为什么需要路由：用户消息后的首个请求、同一回合的续写、自动重试，
/// 或代理循环之外的一次调用（摘要、扩展调用）。与 pi 的 <c>ModelRouteReason</c> 对齐。
public enum VirtualRouteReason
{
	User,
	Continuation,
	Retry,
	Direct,
}

public sealed record VirtualModelRequest
{
	public required Model Model { get; init; }

	public required string ThinkingLevel { get; init; }

	public required VirtualRouteReason Reason { get; init; }

	public required IReadOnlyList<Message> Messages { get; init; }

	public required ModelRegistry Registry { get; init; }

	public Model? PreviousModel { get; init; }

	public string? PreviousThinkingLevel { get; init; }

	public AssistantMessage? Failed { get; init; }

	public JsonNode? State { get; init; }
}

public sealed record VirtualModelRoute
{
	public required Model Model { get; init; }

	public string? ThinkingLevel { get; init; }

	public JsonNode? State { get; init; }
}

/// 虚拟模型目录项：本身不直接发请求，而是由 <see cref="Route"/> 为每次请求选择
/// 一个物理模型。对应 pi 的 <c>VirtualModelDefinition</c>。
public sealed record VirtualModelDefinition
{
	public required string Provider { get; init; }

	public required string Id { get; init; }

	public required string Name { get; init; }

	public IReadOnlyList<string> ThinkingLevels { get; init; } = [PiWeft.Ai.ThinkingLevels.Off];

	public int ContextWindow { get; init; }

	public int MaxTokens { get; init; }

	public IReadOnlyList<string> Input { get; init; } = ["text", "image"];

	public required Func<VirtualModelRequest, CancellationToken, Task<VirtualModelRoute>> Route { get; init; }
}

public static class VirtualModels
{
	public const string Api = "pi-virtual";

	public const string StateEntryType = "pi.virtual-model-state";

	public static bool IsVirtual(Model model) =>
		string.Equals(model.Api, Api, StringComparison.Ordinal);

	public static Model CreateCatalogModel(VirtualModelDefinition definition)
	{
		var levels = definition.ThinkingLevels.Count > 0 ? definition.ThinkingLevels : [ThinkingLevels.Off];
		var map = new Dictionary<string, string?>(StringComparer.Ordinal);
		foreach (var level in ThinkingLevels.Model)
		{
			map[level] = levels.Contains(level, StringComparer.Ordinal) ? level : null;
		}

		return new Model
		{
			Id = definition.Id,
			Name = definition.Name,
			Provider = definition.Provider,
			Api = Api,
			BaseUrl = string.Empty,
			Type = ModelType.Chat,
			Input = definition.Input.Count > 0 ? definition.Input : ["text", "image"],
			Cost = new ModelCost(),
			ContextWindow = definition.ContextWindow,
			MaxTokens = definition.MaxTokens,
			Reasoning = levels.Any(level => !string.Equals(level, ThinkingLevels.Off, StringComparison.Ordinal)),
			ThinkingLevelMap = map,
		};
	}

	/// 把路由结果收敛到物理模型，并校验目标确实是目录里的非虚拟模型。
	/// 物理模型缺失或目标本身还是虚拟模型时抛错，避免请求落到 <c>pi-virtual</c> API。
	public static async Task<VirtualModelRoute> RouteAsync(
		VirtualModelDefinition definition,
		VirtualModelRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(request);

		var name = $"Virtual model {definition.Provider}/{definition.Id}";
		var selected = await definition.Route(request, cancellationToken).ConfigureAwait(false);
		var target = request.Registry.Get(selected.Model.Provider, selected.Model.Id);
		if (target is null || IsVirtual(target))
		{
			throw new InvalidOperationException(
				$"{name} routed to {selected.Model.Provider}/{selected.Model.Id}, which is not a physical catalog model.");
		}

		return selected with
		{
			Model = target,
			ThinkingLevel = ThinkingLevelResolver.Clamp(
				target,
				selected.ThinkingLevel ?? request.ThinkingLevel),
		};
	}

	public static Func<VirtualModelRequest, CancellationToken, Task<VirtualModelRoute>> StaticRoute(
		string target,
		string? thinkingLevel = null) =>
		(request, _) =>
		{
			var model = request.Registry.Resolve(target)
				?? throw new InvalidOperationException($"Virtual model target '{target}' is not in the catalog.");
			return Task.FromResult(new VirtualModelRoute
			{
				Model = model,
				ThinkingLevel = thinkingLevel,
			});
		};
}
