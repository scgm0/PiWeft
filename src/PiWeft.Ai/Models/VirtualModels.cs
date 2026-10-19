using System.Text.Json.Nodes;

namespace PiWeft.Ai;

/// <summary>
/// 请求为什么需要路由：用户消息后的首个请求、同一回合的续写、自动重试，
/// 或代理循环之外的一次调用（摘要、扩展调用）。与 pi 的 <c>ModelRouteReason</c> 对齐。
/// </summary>
public enum VirtualRouteReason
{
	User,
	Continuation,
	Retry,
	Direct,
}

/// <summary>一次虚拟模型路由的输入。</summary>
public sealed record VirtualModelRequest
{
	public required Model Model { get; init; }

	public required string ThinkingLevel { get; init; }

	public required VirtualRouteReason Reason { get; init; }

	public required IReadOnlyList<Message> Messages { get; init; }

	/// <summary>目录，路由函数用它把目标解析成真正的物理模型。</summary>
	public required ModelRegistry Registry { get; init; }

	/// <summary>最近一次成功响应使用的物理模型；没有时为 null。</summary>
	public Model? PreviousModel { get; init; }

	public string? PreviousThinkingLevel { get; init; }

	/// <summary>重试时失败的那条响应；它已经不在 <see cref="Messages"/> 里。</summary>
	public AssistantMessage? Failed { get; init; }

	/// <summary>会话分支上保存的上一次路由状态。</summary>
	public JsonNode? State { get; init; }
}

/// <summary>虚拟模型为某一次请求选出的物理模型与思考级别。</summary>
public sealed record VirtualModelRoute
{
	public required Model Model { get; init; }

	/// <summary>为 null 时沿用请求的思考级别。</summary>
	public string? ThinkingLevel { get; init; }

	/// <summary>新的路由状态；由宿主保存到会话分支。</summary>
	public JsonNode? State { get; init; }
}

/// <summary>
/// 虚拟模型目录项：本身不直接发请求，而是由 <see cref="Route"/> 为每次请求选择
/// 一个物理模型。对应 pi 的 <c>VirtualModelDefinition</c>。
/// </summary>
public sealed record VirtualModelDefinition
{
	public required string Provider { get; init; }

	public required string Id { get; init; }

	public required string Name { get; init; }

	/// <summary>选择器里允许的思考级别；默认只有 <c>off</c>。</summary>
	public IReadOnlyList<string> ThinkingLevels { get; init; } = [PiWeft.Ai.ThinkingLevels.Off];

	/// <summary>首次响应前展示的上下文窗口；0 表示未知。</summary>
	public int ContextWindow { get; init; }

	/// <summary>首次响应前展示的最大输出；0 表示未知。</summary>
	public int MaxTokens { get; init; }

	/// <summary>选择器接受的输入模态；默认文本与图片。</summary>
	public IReadOnlyList<string> Input { get; init; } = ["text", "image"];

	public required Func<VirtualModelRequest, CancellationToken, Task<VirtualModelRoute>> Route { get; init; }
}

/// <summary>虚拟模型目录项与路由校验。</summary>
public static class VirtualModels
{
	/// <summary>虚拟目录项使用的 api id；请求到达 provider 前必须已经被路由。</summary>
	public const string Api = "pi-virtual";

	/// <summary>保存路由状态的会话自定义条目类型，与 pi 相同。</summary>
	public const string StateEntryType = "pi.virtual-model-state";

	public static bool IsVirtual(Model model) =>
		string.Equals(model.Api, Api, StringComparison.Ordinal);

	/// <summary>构造供模型选择器展示的虚拟目录项。</summary>
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

	/// <summary>
	/// 把路由结果收敛到物理模型，并校验目标确实是目录里的非虚拟模型。
	/// 物理模型缺失或目标本身还是虚拟模型时抛错，避免请求落到 <c>pi-virtual</c> API。
	/// </summary>
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

	/// <summary>创建「固定目标」路由，供 <c>models.json</c> 的静态 <c>virtual</c> 条目使用。</summary>
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
