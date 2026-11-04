using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Desktop;

internal sealed record UsageProbeProviderConfig(
	bool Enabled = false,
	string Template = "",
	string ApiKey = "",
	string BaseUrl = "",
	string AccessToken = "",
	string UserId = "",
	string Cookie = "",
	string CookiePath = "",
	string ValuePath = "",
	string CurrencyPath = "",
	int TimeoutSecs = UsageProbeStore.DefaultTimeoutSeconds,
	int IntervalMinutes = UsageProbeStore.DefaultIntervalMinutes);

internal sealed record UsageProbeRecognition(string TemplateId, string Category);

internal sealed record UsageProbeDocument(
	IReadOnlyDictionary<string, UsageProbeProviderConfig> Providers,
	IReadOnlyList<UsageProbeCandidate> Probes,
	IReadOnlyList<string> Errors,
	string Path);

/// 用量查询配置文件（与 PiDeck 同名同结构）：
/// <c>~/.pi/agent/usage-probes.json</c>，顶层 providers 由弹窗维护、probes 数组供 AI/高级用户手写。
/// 旧版 <c>usage-queries.json</c> 只读兼容：自动识别模式下优先使用，显式选择模板后让位。
internal static class UsageProbeStore
{
	public const string FileName = "usage-probes.json";
	public const string LegacyFileName = "usage-queries.json";
	public const int DefaultTimeoutSeconds = 10;
	public const int DefaultIntervalMinutes = 5;
	public const int MaxBytes = 2 * 1024 * 1024;

	private static readonly JsonDocumentOptions ReadOptions = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	public static readonly IReadOnlyList<string> DeclarativeTemplates = ["general", "newapi", "cookie", "sub2api"];

	public static bool IsDeclarativeTemplate(string id) => DeclarativeTemplates.Contains(id, StringComparer.Ordinal);

	public static bool IsKnownTemplate(string id) =>
		IsDeclarativeTemplate(id) || UsageProbes.Candidates.Any(candidate => candidate.TemplateId == id);

	public static UsageProbeDocument Load(string agentDir)
	{
		var path = System.IO.Path.Combine(agentDir, FileName);
		var errors = new List<string>();
		if (!File.Exists(path)) return new UsageProbeDocument(new Dictionary<string, UsageProbeProviderConfig>(), [], errors, path);
		JsonObject root;
		try
		{
			var info = new FileInfo(path);
			if (info.Length > MaxBytes) throw new InvalidDataException(Strings.ConfigTooLarge);
			root = JsonNode.Parse(File.ReadAllText(path).TrimStart('\uFEFF'), documentOptions: ReadOptions) as JsonObject
				?? throw new ArgumentException(Strings.ConfigObjectRequired);
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
		{
			errors.Add(Strings.UsageConfigReadFailed(ex is JsonException ? Strings.ConfigInvalidJson : ex.Message));
			return new UsageProbeDocument(new Dictionary<string, UsageProbeProviderConfig>(), [], errors, path);
		}
		return ReadRoot(root, path);
	}

	private static UsageProbeDocument ReadRoot(JsonObject root, string path)
	{
		var providers = new Dictionary<string, UsageProbeProviderConfig>(StringComparer.Ordinal);
		var errors = new List<string>();
		if (root["providers"] is JsonObject map)
		{
			foreach (var (name, node) in map)
			{
				if (node is not JsonObject entry)
				{
					errors.Add(Strings.UsageConfigProviderInvalid(name));
					continue;
				}
				try { providers[name] = ReadProvider(entry); }
				catch (ArgumentException ex) { errors.Add(Strings.UsageConfigProviderInvalid(name, ex.Message)); }
			}
		}
		else if (root["providers"] is not null) errors.Add(Strings.UsageConfigProvidersRequired);

		var probes = ReadProbes(root["probes"], errors);
		return new UsageProbeDocument(providers, probes, errors, path);
	}

	public static UsageProbeProviderConfig? LoadProvider(string agentDir, string provider) =>
		Load(agentDir).Providers.GetValueOrDefault(provider);

	public static UsageProbeProviderConfig ReadProvider(JsonObject entry)
	{
		var config = new UsageProbeProviderConfig(
			Enabled: Flag(entry, "enabled"),
			Template: Text(entry, "template"),
			ApiKey: Text(entry, "apiKey"),
			BaseUrl: Text(entry, "baseUrl"),
			AccessToken: Text(entry, "accessToken"),
			UserId: Text(entry, "userId"),
			Cookie: Text(entry, "cookie"),
			CookiePath: Text(entry, "cookiePath"),
			ValuePath: Text(entry, "valuePath"),
			CurrencyPath: Text(entry, "currencyPath"),
			TimeoutSecs: Number(entry, "timeoutSecs") ?? DefaultTimeoutSeconds,
			IntervalMinutes: Number(entry, "intervalMinutes") ?? DefaultIntervalMinutes);
		Validate(config);
		return config;
	}

	public static JsonObject ToJson(UsageProbeProviderConfig config)
	{
		var entry = new JsonObject { ["enabled"] = config.Enabled };
		void Add(string key, string value)
		{
			if (!string.IsNullOrWhiteSpace(value)) entry[key] = value;
		}
		Add("template", config.Template);
		Add("apiKey", config.ApiKey);
		Add("baseUrl", config.BaseUrl);
		Add("accessToken", config.AccessToken);
		Add("userId", config.UserId);
		Add("cookie", config.Cookie);
		Add("cookiePath", config.CookiePath);
		Add("valuePath", config.ValuePath);
		Add("currencyPath", config.CurrencyPath);
		entry["timeoutSecs"] = config.TimeoutSecs;
		entry["intervalMinutes"] = config.IntervalMinutes;
		return entry;
	}

	public static void Validate(UsageProbeProviderConfig config)
	{
		if (config.TimeoutSecs is < 1 or > 300 || config.IntervalMinutes is < 0 or > 1440)
			throw new ArgumentException(Strings.UsageInvalidConfig);
		if (config.Template.Length > 0 && !IsKnownTemplate(config.Template)) throw new ArgumentException(Strings.UsageInvalidConfig);
		if (config.BaseUrl.Length > 0 &&
			(!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http") ||
			baseUri.Scheme == "http" && !baseUri.IsLoopback || baseUri.UserInfo.Length > 0))
			throw new ArgumentException(Strings.UsageInvalidConfig);
		if (config.CookiePath.Length > 0 && (!config.CookiePath.StartsWith('/') || config.CookiePath.Any(char.IsControl)))
			throw new ArgumentException(Strings.UsageInvalidConfig);
		foreach (var value in new[] { config.ApiKey, config.AccessToken, config.UserId, config.Cookie, config.ValuePath, config.CurrencyPath })
			if (value.Any(char.IsControl)) throw new ArgumentException(Strings.UsageInvalidConfig);
	}

	public static string? TemplateError(UsageProbeProviderConfig config) => config.Template switch
	{
		"newapi" when config.AccessToken.Trim().Length == 0 || config.UserId.Trim().Length == 0 => Strings.UsageNewApiNeeds,
		"cookie" when config.Cookie.Trim().Length == 0 || config.CookiePath.Trim().Length == 0 || config.ValuePath.Trim().Length == 0 => Strings.UsageCookieNeeds,
		_ => null,
	};

	public static UsageProbeRecognition? Recognize(Model model)
	{
		var api = NormalizeApi(model.Api);
		foreach (var candidate in UsageProbes.Candidates)
		{
			if (candidate.TemplateId.Length == 0) continue;
			if (!UsageProbes.Applies(candidate, model.BaseUrl, api)) continue;
			return new UsageProbeRecognition(candidate.TemplateId,
				UsageProbes.TemplateCategories.GetValueOrDefault(candidate.TemplateId, Strings.UsageCategoryBalance));
		}
		return null;
	}

	public static UsageProbeCandidate? LoadLegacyCandidate(string agentDir, string provider, ICollection<string>? errors = null)
	{
		var path = System.IO.Path.Combine(agentDir, LegacyFileName);
		if (!File.Exists(path)) return null;
		try
		{
			if (new FileInfo(path).Length > MaxBytes) return null;
			if (JsonNode.Parse(File.ReadAllText(path).TrimStart('\uFEFF'), documentOptions: ReadOptions) is not JsonObject root ||
				root["providers"] is not JsonObject providers || providers[provider] is not JsonObject entry) return null;
			var endpoint = Text(entry, "path");
			if (endpoint.Length == 0) return null;
			var token = Text(entry, "token");
			var cookie = Text(entry, "cookie");
			var userId = Text(entry, "userId");
			var currency = Text(entry, "currency");
			var currencyPath = Text(entry, "currencyPath");
			var valuePath = Text(entry, "valuePath");
			var totalPath = Text(entry, "totalPath");
			var usedPath = Text(entry, "usedPath");
			var scale = UsageJson.Number(entry["scale"]) ?? 1;
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (cookie.Length > 0) headers["Cookie"] = cookie;
			if (token.Length > 0) headers["Authorization"] = "Bearer " + token;
			if (userId.Length > 0) headers["New-Api-User"] = userId;
			UsageProbeParse parse = totalPath.Length > 0 || usedPath.Length > 0
				? new UsageProbeParse(UsageParseKind.Credits, TotalPath: totalPath, UsedPath: usedPath, RemainingPath: valuePath, Scale: scale)
				: new UsageProbeParse(UsageParseKind.Balance, valuePath, currencyPath, currency);
			var absolute = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
			// 旧版异域地址：必须有显式凭据，且不接受查询/片段（明细里会回显 URL，避免泄密）。
			if (absolute && token.Length == 0 && cookie.Length == 0)
			{
				errors?.Add(Strings.UsageConfigLegacyCrossOrigin(provider));
				return null;
			}
			if (absolute && (uri!.Query.Length > 0 || uri.Fragment.Length > 0))
			{
				errors?.Add(Strings.UsageConfigLegacyCrossOrigin(provider));
				return null;
			}
			return new UsageProbeCandidate(endpoint,
				AbsoluteUrl: absolute ? endpoint : "",
				Headers: headers.Count > 0 ? headers : null,
				RootPath: Flag(entry, "rootPath") && !absolute,
				NoVersionPath: true,
				NoBearer: cookie.Length > 0,
				Parse: parse);
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
		{
			errors?.Add(Strings.UsageConfigLegacyReadFailed(ex.Message));
			return null;
		}
	}

	public static IReadOnlyList<UsageProbeCandidate> ReadProbes(JsonNode? node, ICollection<string> errors)
	{
		if (node is null) return [];
		if (node is not JsonArray array)
		{
			errors.Add(Strings.UsageConfigProbesRequired);
			return [];
		}
		var candidates = new List<UsageProbeCandidate>();
		for (var index = 0; index < array.Count; index++)
		{
			var position = index + 1;
			try
			{
				if (array[index] is not JsonObject probe) throw new ArgumentException(Strings.UsageConfigProbeNotObject(position));
				var match = probe["match"] as JsonObject;
				var keywords = StringList(match?["baseUrlContains"]);
				if (keywords.Count == 0) throw new ArgumentException(Strings.UsageConfigProbeMatch(position));
				var request = probe["request"] as JsonObject;
				var path = Text(request, "path");
				if (!path.StartsWith('/') || path.Any(char.IsControl)) throw new ArgumentException(Strings.UsageConfigProbePath(position));
				var method = Text(request, "method");
				if (method.Length > 0 && method is not ("GET" or "POST")) throw new ArgumentException(Strings.UsageConfigProbeMethod(position));
				var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				if (request?["headers"] is JsonObject headerMap)
					foreach (var (name, value) in headerMap)
						if (value is JsonValue text && text.TryGetValue<string>(out var headerValue) &&
							name.Length > 0 && !name.Any(char.IsControl) && !headerValue.Any(char.IsControl))
							headers[name] = headerValue;
				var parse = ReadProbeParse(probe["parse"], position);
				candidates.Add(new UsageProbeCandidate(path,
					Method: method.Length > 0 ? method : "GET",
					Body: request?["body"]?.DeepClone(),
					Headers: headers.Count > 0 ? headers : null,
					BaseUrlContains: keywords,
					ApiTypes: StringList(match?["apiTypes"]) is { Count: > 0 } apiTypes ? apiTypes : null,
					RootPath: Flag(request, "rootPath"),
					NoBearer: Flag(request, "skipBearer"),
					Parse: parse));
			}
			catch (ArgumentException ex) { errors.Add(ex.Message); }
		}
		return candidates;
	}

	private static UsageProbeParse ReadProbeParse(JsonNode? node, int position)
	{
		if (node is null) return new UsageProbeParse();
		if (node is not JsonObject parse) throw new ArgumentException(Strings.UsageConfigProbeParse(position));
		var kind = Text(parse, "kind");
		if (kind.Length == 0 || kind == "periods") return new UsageProbeParse();
		if (kind == "custom") throw new ArgumentException(Strings.UsageConfigProbeCustom(position));
		if (kind == "balance")
		{
			var valuePath = Text(parse, "valuePath");
			if (valuePath.Length == 0) throw new ArgumentException(Strings.UsageConfigProbeParse(position));
			return new UsageProbeParse(UsageParseKind.Balance, valuePath, Text(parse, "currencyPath"));
		}
		if (kind != "credits") throw new ArgumentException(Strings.UsageConfigProbeParse(position));

		var totalPath = Text(parse, "totalPath");
		var usedPath = Text(parse, "usedPath");
		var remainingPath = Text(parse, "remainingPath");
		var scaleRaw = UsageJson.Number(parse["scale"]);
		var scale = scaleRaw is > 0 ? scaleRaw.Value : 1;
		var windows = new List<UsageProbeWindow>();
		if (parse["windows"] is JsonArray windowNodes)
		{
			foreach (var item in windowNodes)
			{
				if (item is not JsonObject window) continue;
				var key = Text(window, "key");
				var wTotal = Text(window, "totalPath");
				var wUsed = Text(window, "usedPath");
				var wRemaining = Text(window, "remainingPath");
				var wPercent = Text(window, "percentPath");
				var listPath = Text(window, "listPath");
				if (key.Length == 0) continue;
				if (listPath.Length > 0)
				{
					var where = new List<UsageProbeWhere>();
					if (window["where"] is JsonArray conditions)
						foreach (var condition in conditions.OfType<JsonObject>())
						{
							var wherePath = Text(condition, "path");
							if (wherePath.Length > 0 && condition.ContainsKey("eq")) where.Add(new UsageProbeWhere(wherePath, condition["eq"]?.DeepClone()));
						}
					if (where.Count == 0) continue;
					if (wTotal.Length == 0 && wUsed.Length == 0 && wRemaining.Length == 0 && wPercent.Length == 0) continue;
					windows.Add(new UsageProbeWindow(key, wTotal, wUsed, wRemaining, wPercent, listPath, where));
				}
				else
				{
					if (wTotal.Length == 0 && wUsed.Length == 0 && wRemaining.Length == 0 && wPercent.Length == 0) continue;
					windows.Add(new UsageProbeWindow(key, wTotal, wUsed, wRemaining, wPercent));
				}
			}
		}
		UsageProbeBooster? booster = null;
		if (parse["booster"] is JsonObject boosterNode)
		{
			var balancePath = Text(boosterNode, "balancePath");
			if (balancePath.Length > 0)
				booster = new UsageProbeBooster(balancePath, Text(boosterNode, "totalPath"), Text(boosterNode, "currencyPath"),
					Text(boosterNode, "monthlyUsedCentsPath"), Text(boosterNode, "monthlyChargeLimitCentsPath"),
					Text(boosterNode, "monthlyChargeLimitEnabledPath"),
					FixedPoint(boosterNode["fixedPointPerCent"]));
		}
		if (totalPath.Length == 0 && usedPath.Length == 0 && remainingPath.Length == 0 && windows.Count == 0 && booster is null)
			throw new ArgumentException(Strings.UsageConfigProbeParse(position));
		return new UsageProbeParse(UsageParseKind.Credits, TotalPath: totalPath, UsedPath: usedPath, RemainingPath: remainingPath,
			Scale: scale, Windows: windows.Count > 0 ? windows : null, Booster: booster);
	}

	private static double FixedPoint(JsonNode? node)
	{
		var value = UsageJson.Number(node);
		return value is > 0 ? value.Value : 1_000_000;
	}

	public static string Text(JsonNode? node, string key) =>
		node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;

	public static bool Flag(JsonNode? node, string key) =>
		node?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

	public static int? Number(JsonNode? node, string key) =>
		UsageJson.Number(node?[key]) is { } number && number >= int.MinValue && number <= int.MaxValue ? (int)number : null;

	public static string NormalizeApi(string api) => api.Trim().ToLowerInvariant() switch
	{
		"" => "openai-completions",
		"openai-chat-completions" => "openai-completions",
		"google" => "google-generative-ai",
		var value => value,
	};

	private static List<string> StringList(JsonNode? node) =>
		node is JsonArray array
			? array.OfType<JsonValue>()
				.Select(value => value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty)
				.Where(text => text.Length > 0)
				.ToList()
			: [];
}
