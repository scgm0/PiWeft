using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using PiWeft.JavaScript;

namespace PiWeft.CodingAgent.Settings;

/// <summary>
/// 把 <c>models.json</c> 里虚拟模型的 <c>route</c> 源码包装成 <see cref="VirtualModelDefinition.Route"/>。
/// 脚本收到 <c>request</c> 与 <c>context</c> 两个 JSON 值；<c>context.modelRegistry</c> 提供
/// <c>find</c>/<c>findOfType</c>/<c>getModelsOfType</c>/<c>getAvailableOfType</c>。
/// </summary>
internal static class VirtualModelScript
{
	private const string ContextPrelude =
		"""
		(function (context) {
			const models = context.modelRegistry && Array.isArray(context.modelRegistry.models)
				? context.modelRegistry.models
				: [];
			const matchesType = function (model, type) { return !type || model.type === type; };
			const find = function (provider, id) {
				return models.find(function (model) {
					return model.provider === provider && model.id === id;
				}) || null;
			};
			const ctx = context;
			context.modelRegistry = {
				models,
				all: models,
				find,
				findOfType: function (type, provider, id) {
					return models.find(function (model) {
						return matchesType(model, type) && model.provider === provider && model.id === id;
					}) || null;
				},
				getModelsOfType: function (type) {
					return models.filter(function (model) { return matchesType(model, type); });
				},
				getAvailableOfType: function (type) {
					return models.filter(function (model) { return matchesType(model, type); });
				},
			};
			return context;
		})(context)
		""";

	public static Func<VirtualModelRequest, CancellationToken, Task<VirtualModelRoute>> Create(
		string provider,
		string id,
		string source,
		int? timeoutMs = null)
	{
		var timeout = timeoutMs is > 0 ? TimeSpan.FromMilliseconds(timeoutMs.Value) : TimeSpan.FromSeconds(5);
		return async (request, cancellationToken) =>
		{
			var result = await ScriptFunction.InvokeAsync(
				ContextPrelude + ";\n" + source,
				BuildRequest(request),
				BuildContext(request.Registry),
				new ScriptFunctionOptions { Timeout = timeout },
				cancellationToken).ConfigureAwait(false);

			return ParseRoute(provider, id, result);
		};
	}

	private static JsonObject BuildRequest(VirtualModelRequest request)
	{
		var model = ModelNode(request.Model);
		var payload = new JsonObject
		{
			["model"] = model,
			["thinkingLevel"] = request.ThinkingLevel,
			["reason"] = request.Reason switch
			{
				VirtualRouteReason.User => "user",
				VirtualRouteReason.Retry => "retry",
				VirtualRouteReason.Direct => "direct",
				_ => "continuation",
			},
			["messages"] = new JsonArray([.. request.Messages.Select(static message =>
				(JsonNode?)JsonNode.Parse(PiJson.SerializeMessage(message)))]),
		};

		if (request.PreviousModel is { } previous)
		{
			var node = new JsonObject { ["model"] = ModelNode(previous) };
			if (request.PreviousThinkingLevel is { Length: > 0 } level)
			{
				node["thinkingLevel"] = level;
			}

			payload["previous"] = node;
		}

		if (request.Failed is { } failed)
		{
			var node = new JsonObject
			{
				["message"] = JsonNode.Parse(PiJson.SerializeMessage(failed)),
			};
			if (request.Registry.Get(failed.Provider, failed.Model) is { } failedModel && !VirtualModels.IsVirtual(failedModel))
			{
				node["model"] = ModelNode(failedModel);
			}

			if (failed.ThinkingLevel is { Length: > 0 } level)
			{
				node["thinkingLevel"] = level;
			}

			payload["failed"] = node;
		}

		if (request.State is { } state)
		{
			payload["state"] = state.DeepClone();
		}

		return payload;
	}

	private static JsonObject BuildContext(ModelRegistry registry)
	{
		var models = new JsonArray();
		foreach (var model in registry.List())
		{
			if (VirtualModels.IsVirtual(model))
			{
				continue;
			}

			models.Add(ModelNode(model));
		}

		return new JsonObject
		{
			["modelRegistry"] = new JsonObject { ["models"] = models },
		};
	}

	private static JsonNode ModelNode(Model model) =>
		PiJson.SerializeModelNode(model)
		?? throw new InvalidOperationException($"Could not serialize model {model.Provider}/{model.Id}");

	private static VirtualModelRoute ParseRoute(string provider, string id, JsonNode? result)
	{
		var name = $"Virtual model {provider}/{id}";
		if (result is not JsonObject selected)
		{
			throw new InvalidOperationException($"{name} route() must return an object with a model");
		}

		var target = selected["model"] switch
		{
			JsonObject model => ParseModel(model),
			JsonValue value when value.TryGetValue<string>(out var text) => SplitModel(text),
			_ => null,
		} ?? throw new InvalidOperationException($"{name} route() must return {{ model: {{ provider, id }} }}");

		var thinking = selected["thinkingLevel"]?.GetValue<string>();
		return new VirtualModelRoute
		{
			Model = new Model { Provider = target.Provider, Id = target.Id },
			ThinkingLevel = string.IsNullOrWhiteSpace(thinking) ? null : thinking,
			State = selected["state"]?.DeepClone(),
		};
	}

	private static (string Provider, string Id)? ParseModel(JsonObject model) =>
		model["provider"]?.GetValue<string>() is { Length: > 0 } provider
		&& model["id"]?.GetValue<string>() is { Length: > 0 } id
			? (provider, id)
			: null;

	private static (string Provider, string Id)? SplitModel(string text)
	{
		var slash = text.IndexOf('/');
		return slash <= 0 || slash == text.Length - 1
			? null
			: (text[..slash], text[(slash + 1)..]);
	}
}
