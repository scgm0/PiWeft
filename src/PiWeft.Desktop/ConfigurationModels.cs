using System.Text.Json.Nodes;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>Field patches preserve provider/model metadata that the form does not understand.</summary>
public static class ConfigurationModels
{
	public static void ValidateId(string id)
	{
		if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new ArgumentException(Strings.ConfigNameRequired);
	}

	public static JsonObject UpsertProvider(JsonObject source, string? oldName, string name, string baseUrl, string api, string? key)
	{
		ValidateId(name);
		if (!CliEnvironment.SupportedApis.Contains(api, StringComparer.Ordinal)) throw new ArgumentException(Strings.ConfigUnsupportedApi);
		if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
			throw new ArgumentException(Strings.ConfigUrlRequired);
		var root = source.DeepClone().AsObject();
		var providers = root["providers"] as JsonObject ?? new JsonObject();
		if (root["providers"] is null) root["providers"] = providers;
		if (oldName != name && providers.ContainsKey(name)) throw new ArgumentException(Strings.ConfigNameExists);
		var provider = oldName is not null && providers[oldName] is JsonObject existing ? existing.DeepClone().AsObject() : new JsonObject();
		provider["baseUrl"] = baseUrl.Trim();
		provider["api"] = api;
		if (!string.IsNullOrEmpty(key)) provider["apiKey"] = key;
		provider["models"] ??= new JsonArray();
		if (oldName is not null && oldName != name) providers.Remove(oldName);
		providers[name] = provider;
		return root;
	}

	public static JsonObject UpsertModel(JsonObject source, string provider, int? index, string id, string name, int context, int maxTokens, bool reasoning, bool image, ModelCostValues? cost = null)
	{
		ValidateId(id);
		if (context <= 0 || maxTokens <= 0 || maxTokens > context) throw new ArgumentException(Strings.ConfigPositiveNumber);
		var root = source.DeepClone().AsObject();
		var entry = root["providers"]?[provider] as JsonObject ?? throw new ArgumentException(Strings.ConfigNameRequired);
		var models = entry["models"] as JsonArray ?? new JsonArray();
		if (entry["models"] is null) entry["models"] = models;
		if (index is { } at && (at < 0 || at >= models.Count)) throw new ArgumentException(Strings.ConfigModelIdRequired);
		if (models.Where((_, at) => at != index).Any(model => Text(model, "id") == id)) throw new ArgumentException(Strings.ConfigNameExists);
		var model = index is { } position && models[position] is JsonObject existing ? existing.DeepClone().AsObject() : new JsonObject();
		model["id"] = id.Trim();
		model["name"] = string.IsNullOrWhiteSpace(name) ? id.Trim() : name.Trim();
		model["contextWindow"] = context;
		model["maxTokens"] = maxTokens;
		model["reasoning"] = reasoning;
		model["input"] = image ? new JsonArray("text", "image") : new JsonArray("text");
		if (cost is { } values)
		{
			if (!IsValidCost(values))
			{
				throw new ArgumentException(Strings.ConfigCostInvalid);
			}

			// 只有表单确实提供了价格才写入，避免把已有 cost 覆盖成 0。
			model["cost"] = new JsonObject
			{
				["input"] = values.Input,
				["output"] = values.Output,
				["cacheRead"] = values.CacheRead,
				["cacheWrite"] = values.CacheWrite,
			};
		}

		if (index is { } replace) models[replace] = model;
		else models.Add((JsonNode)model);
		return root;
	}

	/// <summary>
	/// 把远程目录里的模型 id 合并进供应商（已存在的跳过），新条目用保守默认值，
	/// 用户可在表单里继续修改。返回新对象与新增数量。
	/// </summary>
	public static (JsonObject Root, int Added) AddMissingModels(
		JsonObject source,
		string provider,
		IReadOnlyList<string> ids,
		int contextWindow = 128000,
		int maxTokens = 8192)
	{
		ValidateId(provider);
		var root = source.DeepClone().AsObject();
		var entry = root["providers"]?[provider] as JsonObject ?? throw new ArgumentException(Strings.ConfigNameRequired);

		var nodes = new List<JsonNode?>(entry["models"] is JsonArray existing ? existing.Count : 0);
		var known = new HashSet<string>(StringComparer.Ordinal);
		if (entry["models"] is JsonArray current)
		{
			foreach (var model in current)
			{
				nodes.Add(model?.DeepClone());
				if (Text(model, "id") is { Length: > 0 } id)
				{
					known.Add(id);
				}
			}
		}

		var added = 0;
		foreach (var id in ids)
		{
			if (id.Length == 0 || !known.Add(id))
			{
				continue;
			}

			nodes.Add(new JsonObject
			{
				["id"] = id,
				["name"] = id,
				["contextWindow"] = contextWindow,
				["maxTokens"] = maxTokens,
				["reasoning"] = false,
				["input"] = new JsonArray("text"),
			});
			added++;
		}

		entry["models"] = new JsonArray([.. nodes]);
		return (root, added);
	}

	public static JsonObject SetApiKey(JsonObject source, string provider, string key)
	{
		ValidateId(provider);
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException(Strings.ConfigKeyRequired);
		var root = source.DeepClone().AsObject();
		if (root[provider] is JsonObject oauth && Text(oauth, "type") != "api_key") throw new ArgumentException(Strings.ConfigOAuthProtected);
		var credential = root[provider] as JsonObject ?? new JsonObject();
		if (root[provider] is null) root[provider] = credential;
		credential["type"] = "api_key";
		credential["key"] = key;
		return root;
	}

	public static string Export(JsonObject source)
	{
		var root = source.DeepClone();
		Redact(root);
		return root.ToJsonString(ConfigurationStore.WriteOptions);
	}

	private static void Redact(JsonNode node)
	{
		if (node is JsonObject obj)
		{
			foreach (var (key, value) in obj.ToList())
			{
				if (IsSecret(key) || key.Equals("headers", StringComparison.OrdinalIgnoreCase) ||
					key.Equals("env", StringComparison.OrdinalIgnoreCase) || key.Equals("oauth", StringComparison.OrdinalIgnoreCase)) obj.Remove(key);
				else if (key.Equals("baseUrl", StringComparison.OrdinalIgnoreCase) && value is JsonValue urlValue &&
					urlValue.TryGetValue<string>(out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
					obj[key] = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');
				else if (value is not null) Redact(value);
			}
		}
		else if (node is JsonArray array)
		{
			foreach (var value in array) if (value is not null) Redact(value);
		}
	}

	private static bool IsSecret(string key) => key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("token", StringComparison.OrdinalIgnoreCase) && !key.Equals("maxTokens", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("authorization", StringComparison.OrdinalIgnoreCase) || key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
		key.Equals("access", StringComparison.OrdinalIgnoreCase) || key.Equals("refresh", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("password", StringComparison.OrdinalIgnoreCase);

	public static string Text(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
	public static int Number(JsonNode? node, string key, int fallback) => node?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
	public static double Decimal(JsonNode? node, string key, double fallback = 0) => node?[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : fallback;
	public static bool Flag(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

	/// <summary>价格必须非负且有限；NaN/ Infinity 会污染序列化，直接拒绝。</summary>
	private static bool IsValidCost(ModelCostValues cost) =>
		IsRate(cost.Input) && IsRate(cost.Output) && IsRate(cost.CacheRead) && IsRate(cost.CacheWrite);

	private static bool IsRate(double value) => double.IsFinite(value) && value >= 0;
}

/// <summary>模型价格（每百万 token 或供应商单位，原样写入 <c>cost</c>）。</summary>
public readonly record struct ModelCostValues(double Input, double Output, double CacheRead, double CacheWrite);
