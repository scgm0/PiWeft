using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

// ── 探测规格（纯数据，可序列化；旧版 probes 数组与内置候选共用） ─────────────

internal enum UsageParseKind { Periods, Balance, Credits, Custom }

internal sealed record UsageProbeWindow(
	string Key,
	string TotalPath = "",
	string UsedPath = "",
	string RemainingPath = "",
	string PercentPath = "",
	string ListPath = "",
	IReadOnlyList<UsageProbeWhere>? Where = null);

internal sealed record UsageProbeWhere(string Path, JsonNode? Value);

internal sealed record UsageProbeBooster(
	string BalancePath,
	string TotalPath = "",
	string CurrencyPath = "",
	string MonthlyUsedCentsPath = "",
	string MonthlyChargeLimitCentsPath = "",
	string MonthlyChargeLimitEnabledPath = "",
	double FixedPointPerCent = 1_000_000);

internal sealed record UsageProbeParse(
	UsageParseKind Kind = UsageParseKind.Periods,
	string ValuePath = "",
	string CurrencyPath = "",
	string Currency = "",
	string TotalPath = "",
	string UsedPath = "",
	string RemainingPath = "",
	double Scale = 1,
	IReadOnlyList<UsageProbeWindow>? Windows = null,
	UsageProbeBooster? Booster = null,
	string Resolver = "");

internal sealed record UsageProbePreflight(
	string Path,
	string AbsoluteUrl = "",
	IReadOnlyDictionary<string, string>? Headers = null,
	string CapturePath = "",
	string CaptureHeader = "");

internal sealed record UsageProbeCandidate(
	string Path,
	string AbsoluteUrl = "",
	string Method = "GET",
	JsonNode? Body = null,
	IReadOnlyDictionary<string, string>? Headers = null,
	IReadOnlyList<string>? BaseUrlContains = null,
	IReadOnlyList<string>? ApiTypes = null,
	bool RootPath = false,
	bool NoVersionPath = false,
	bool NoBearer = false,
	string TrimSuffix = "",
	UsageProbePreflight? Preflight = null,
	UsageProbeParse? Parse = null,
	string TemplateId = "");

internal sealed record UsageProbeResponse(
	bool Matched,
	ProviderUsageKind? Kind = null,
	UsagePeriods? Periods = null,
	double? Balance = null,
	string? Currency = null,
	UsageCredits? Credits = null,
	UsageBooster? Booster = null);

/// 内置候选表与探测规则（对齐 PiDeck 的 providerUsageProbe）。新增供应商只在这里补
/// 「端点 + 解析」，不散到 UI 与 IPC。用户维护的旧版 probes 数组合并在本表之后。
internal static class UsageProbes
{
	public const int MaxResponseBytes = 1024 * 1024;

	public static readonly IReadOnlyList<UsageProbeCandidate> Candidates =
	[
		// OpenCode Go：origin 下的 /zen/go/v1/usage 给出滚动、周、月三档占用百分比与重置时间。
		// baseUrl 在 /inference/go/...（或旧 /zen/go/...）之下，端点不在推理路径里，故 rootPath。
		new("/zen/go/v1/usage", RootPath: true, BaseUrlContains: ["opencode.ai/zen/go", "opencode.ai/inference/go"],
			TemplateId: "opencode-usage"),

		// DeepSeek：/user/balance。total_balance 在部分部署里是字符串，解析器已兼容。
		new("/user/balance", BaseUrlContains: ["api.deepseek.com"], TemplateId: "deepseek-balance",
			Parse: new(UsageParseKind.Balance, ValuePath: "balance_infos[0].total_balance", CurrencyPath: "balance_infos[0].currency")),

		// OpenRouter：/api/v1/key 直接给出 per-key 额度与已用（普通 inference key 即可）。
		new("/key", BaseUrlContains: ["openrouter.ai"], TemplateId: "openrouter-credits",
			Parse: new(UsageParseKind.Credits, TotalPath: "data.limit", UsedPath: "data.usage", RemainingPath: "data.limit_remaining")),

		// Kimi For Coding：/usages 含 5h 滚动窗、周额度、月度会员限额与独立 Boost 点数。
		new("/usages", BaseUrlContains: ["api.kimi.com"], TemplateId: "kimi-credits",
			Parse: new(UsageParseKind.Custom, Resolver: "kimi-credits")),

		// Moonshot / Kimi 官方余额：.ai 计 USD、.cn 计 CNY，响应本身没有币种字段。
		new("/users/me/balance", BaseUrlContains: ["api.moonshot.ai", "api.moonshot.cn"], TemplateId: "moonshot-balance",
			Parse: new(UsageParseKind.Balance, ValuePath: "data.available_balance")),

		// SiliconFlow：保留既有内置支持；.cn 与 .com 币种不同。
		new("/user/info", BaseUrlContains: ["api.siliconflow.cn"], TemplateId: "siliconflow-balance",
			Parse: new(UsageParseKind.Balance, ValuePath: "data.totalBalance", Currency: "CNY")),
		new("/user/info", BaseUrlContains: ["api.siliconflow.com"], TemplateId: "siliconflow-balance",
			Parse: new(UsageParseKind.Balance, ValuePath: "data.totalBalance", Currency: "USD")),

		// 智谱 GLM Coding Plan：监控 API 挂在 host 根（/api/monitor/…），与 OpenAI 兼容端点
		// /api/paas/v4 不在同一 base 下，故 rootPath 只取 origin。认证要求裸 apiKey（不加 Bearer）。
		// limits 是数组，按 unit/type 分类而非硬编码下标：
		//   unit:3 → 5h 滚动窗、unit:6 → 周窗、type:TIME_LIMIT → MCP 月度额度。
		// type 有两套写法（新套餐 CREDIT_LIMIT / 老套餐 TOKENS_LIMIT），用 where 的数组匹配兼容。
		new("/api/monitor/usage/quota/limit", RootPath: true, TemplateId: "zhipu-quota",
			BaseUrlContains: ["open.bigmodel.cn", "api.z.ai"],
			Headers: new Dictionary<string, string> { ["Authorization"] = "{{apiKey}}" },
			Parse: new(UsageParseKind.Credits,
				TotalPath: "data.limits[0].usage", UsedPath: "data.limits[0].currentValue",
				Windows:
				[
					new("fiveHour", "usage", "currentValue", PercentPath: "percentage", ListPath: "data.limits",
						Where: [new("type", new JsonArray("CREDIT_LIMIT", "TOKENS_LIMIT")), new("unit", JsonValue.Create(3))]),
					new("weekly", "usage", "currentValue", PercentPath: "percentage", ListPath: "data.limits",
						Where: [new("type", new JsonArray("CREDIT_LIMIT", "TOKENS_LIMIT")), new("unit", JsonValue.Create(6))]),
					new("mcpMonthly", "usage", "currentValue", PercentPath: "percentage", ListPath: "data.limits",
						Where: [new("type", JsonValue.Create("TIME_LIMIT"))]),
				])),

		// OpenAI Codex（ChatGPT 订阅）：/wham/usage 与 pi 内置 openai-codex provider 的
		// baseUrl（https://chatgpt.com/backend-api）同路径；用户可能写成 …/backend-api/codex，
		// 故对 baseUrl 末尾的 /codex 做一次剥离。凭据是 ChatGPT OAuth token。
		new("/wham/usage", BaseUrlContains: ["chatgpt.com"], ApiTypes: ["openai-codex-responses"],
			TrimSuffix: "/codex", TemplateId: "codex-usage",
			Parse: new(UsageParseKind.Custom, Resolver: "codex-usage")),

		// xAI（Grok 消费者订阅）：用量端点在 cli-chat-proxy.grok.com，与 api.x.ai 不同 host，
		// 走 absoluteUrl 固定官方域（仅代码内置，不接受用户输入）。链路两步：先查 identity
		// 拿 userId，再带 x-userid 查 billing；鉴权用 OAuth Bearer + Grok 客户端头。
		new("/v1/billing?format=credits",
			AbsoluteUrl: "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
			BaseUrlContains: ["api.x.ai"], TemplateId: "xai-billing",
			Headers: new Dictionary<string, string>
			{
				["X-XAI-Token-Auth"] = "xai-grok-cli",
				["x-grok-client-version"] = "1.0.10",
				["x-grok-client-mode"] = "interactive",
			},
			Preflight: new("/v1/user?include=subscription",
				"https://cli-chat-proxy.grok.com/v1/user?include=subscription",
				new Dictionary<string, string>
				{
					["X-XAI-Token-Auth"] = "xai-grok-cli",
					["x-grok-client-version"] = "1.0.10",
					["x-grok-client-mode"] = "interactive",
				},
				"userId", "x-userid"),
			Parse: new(UsageParseKind.Custom, Resolver: "xai-billing")),

		// Command Code：用量端点在 host 根 /alpha/…（与 /provider/v1 不同 base），rootPath 取 origin。
		new("/alpha/billing/credits", RootPath: true, BaseUrlContains: ["api.commandcode.ai"],
			TemplateId: "commandcode-credits", Parse: new(UsageParseKind.Custom, Resolver: "commandcode-credits")),

		// TokenDance：余额端点挂在 host 根 /portal/…；三个字段均为微元（1 元 = 1,000,000）。
		new("/portal/api/v1/user/balance", RootPath: true, BaseUrlContains: ["tokendance.space"],
			TemplateId: "tokendance-balance",
			Parse: new(UsageParseKind.Credits, TotalPath: "balance.credits", UsedPath: "balance.credits_used",
				RemainingPath: "balance.balance", Scale: 1_000_000)),

		// MiniMax Coding Plan：官方 /v1/token_plan/remains 与旧版 /v1/api/openplatform/…
		// 路径在 host 根，而 provider baseUrl 普遍带 /anthropic 或 /v1，故 rootPath 取 origin。
		// 响应给的是「剩余百分比」，解析器翻转为已用。
		new("/v1/token_plan/remains", RootPath: true, BaseUrlContains: ["minimaxi.com", "minimax.io"],
			TemplateId: "minimax-plan", Parse: new(UsageParseKind.Custom, Resolver: "minimax-plan")),
		new("/v1/api/openplatform/coding_plan/remains", RootPath: true, BaseUrlContains: ["minimaxi.com", "minimax.io"],
			TemplateId: "minimax-plan", Parse: new(UsageParseKind.Custom, Resolver: "minimax-plan")),

		// 通用 OpenAI 兼容网关：多数中转站实现官方 /v1/usage（{ balance, unit }）。
		// 不限定 baseUrl，只靠 apiTypes 收窄到 OpenAI 协议，放在末尾；前面带域名的专有候选优先。
		new("/usage", ApiTypes: ["openai-completions", "openai-responses", "openai-codex-responses"],
			Parse: new(UsageParseKind.Balance, ValuePath: "balance", CurrencyPath: "unit")),
	];

	public static readonly IReadOnlyDictionary<string, string> TemplateCategories = new Dictionary<string, string>
	{
		["opencode-usage"] = Strings.UsageCategoryPlan,
		["deepseek-balance"] = Strings.UsageCategoryBalance,
		["openrouter-credits"] = Strings.UsageCategoryBalance,
		["kimi-credits"] = Strings.UsageCategoryPlan,
		["moonshot-balance"] = Strings.UsageCategoryBalance,
		["siliconflow-balance"] = Strings.UsageCategoryBalance,
		["zhipu-quota"] = Strings.UsageCategoryPlan,
		["codex-usage"] = Strings.UsageCategorySubscription,
		["xai-billing"] = Strings.UsageCategorySubscription,
		["commandcode-credits"] = Strings.UsageCategoryPlan,
		["tokendance-balance"] = Strings.UsageCategoryBalance,
		["minimax-plan"] = Strings.UsageCategoryPlan,
	};

	public static bool Applies(UsageProbeCandidate candidate, string baseUrl, string apiType)
	{
		if (candidate.BaseUrlContains is { Count: > 0 } needles)
		{
			// 内置候选只信任 host（可带路径前缀）。子串匹配会让 api.deepseek.com.evil.test
			// 之类的地址拿到真实 API Key；用户探针保持原有的子串语义，兼容中转站路径。
			if (candidate.TemplateId.Length > 0)
			{
				if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return false;
				if (!needles.Any(needle => BuiltInMatches(baseUri, needle))) return false;
			}
			else if (!needles.Any(needle => baseUrl.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase)))
			{
				return false;
			}
		}
		if (candidate.ApiTypes is { Count: > 0 } types &&
			!types.Contains(apiType.Trim(), StringComparer.OrdinalIgnoreCase)) return false;
		return true;
	}

	private static bool BuiltInMatches(Uri baseUri, string needle)
	{
		var value = needle.Trim();
		if (value.Length == 0) return false;
		var slash = value.IndexOf('/');
		var host = slash < 0 ? value : value[..slash];
		var actualHost = baseUri.Host.TrimEnd('.');
		if (!actualHost.Equals(host, StringComparison.OrdinalIgnoreCase) &&
			!actualHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)) return false;
		return slash < 0 || baseUri.AbsolutePath.StartsWith(value[slash..], StringComparison.OrdinalIgnoreCase);
	}

	public static IReadOnlyList<string> Urls(UsageProbeCandidate candidate, string baseUrl)
	{
		if (candidate.AbsoluteUrl.Length > 0) return [candidate.AbsoluteUrl];
		var trimmed = baseUrl.TrimEnd('/');
		if (candidate.TrimSuffix.Length > 0 && trimmed.EndsWith(candidate.TrimSuffix, StringComparison.OrdinalIgnoreCase))
			trimmed = trimmed[..^candidate.TrimSuffix.Length].TrimEnd('/');
		if (candidate.RootPath)
		{
			return Uri.TryCreate(trimmed, UriKind.Absolute, out var root)
				? [root.GetLeftPart(UriPartial.Authority) + candidate.Path]
				: [trimmed + candidate.Path];
		}
		if (candidate.NoVersionPath) return [trimmed + candidate.Path];
		var versioned = EnsureVersionPath(trimmed);
		var urls = new List<string> { versioned + candidate.Path, trimmed + candidate.Path };
		// 站点根兜底只给内置候选：官方余额端点（/user/balance、/users/me/balance）常挂在
		// host 根，而 models.json 的 baseUrl 可能已带 /v1；前两条都会 404，第三条才是真端点。
		// 用户探针按 skill 约定就是「相对 baseUrl 的路径」，不擅自换根。
		if (candidate.TemplateId.Length > 0 && Uri.TryCreate(trimmed, UriKind.Absolute, out var origin))
			urls.Add(origin.GetLeftPart(UriPartial.Authority) + candidate.Path);
		return urls.Distinct(StringComparer.Ordinal).ToArray();
	}

	public static string EnsureVersionPath(string baseUrl)
	{
		var trimmed = baseUrl.TrimEnd('/');
		return HasVersionPath(trimmed) ? trimmed : trimmed + "/v1";
	}

	public static string StripVersionPath(string baseUrl)
	{
		var trimmed = baseUrl.TrimEnd('/');
		foreach (var suffix in new[] { "/v1beta", "/v1alpha", "/v1", "/api" })
			if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return trimmed[..^suffix.Length].TrimEnd('/');
		return trimmed;
	}

	public static bool HasVersionPath(string baseUrl)
	{
		var trimmed = baseUrl.TrimEnd('/');
		return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
			trimmed.EndsWith("/v2", StringComparison.OrdinalIgnoreCase) ||
			trimmed.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) ||
			trimmed.EndsWith("/v1alpha", StringComparison.OrdinalIgnoreCase) ||
			trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase);
	}

	/// 组装请求头：显式 Authorization 或 noBearer 时不自动补 Bearer；headers 里的
	/// {{apiKey}} 替换成真实 key。模型自定义头只在同源请求里携带（跨域不泄露）。
	public static Dictionary<string, string> BuildHeaders(UsageProbeCandidate candidate, string token,
		IReadOnlyDictionary<string, string>? modelHeaders, bool sameOrigin)
	{
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (sameOrigin && modelHeaders is { Count: > 0 })
			foreach (var (name, value) in modelHeaders) headers[name] = value;
		if (candidate.Headers is { Count: > 0 })
			foreach (var (name, value) in candidate.Headers)
			{
				// 没有凭据时不要把 {{apiKey}} 替换成空值发出去：交给上层的缺凭据门控处理。
				if (token.Length == 0 && value.Contains("{{apiKey}}", StringComparison.OrdinalIgnoreCase)) continue;
				headers[name] = value.Replace("{{apiKey}}", token, StringComparison.OrdinalIgnoreCase);
			}
		var hasAuthorization = headers.ContainsKey("Authorization");
		if (!candidate.NoBearer && !hasAuthorization && token.Length > 0) headers["Authorization"] = "Bearer " + token;
		return headers;
	}

	public static UsageProbeResponse Parse(JsonNode? body, UsageProbeParse? parse)
	{
		parse ??= new UsageProbeParse();
		if (body is not JsonObject) return new(false);
		switch (parse.Kind)
		{
			case UsageParseKind.Balance:
			{
				var value = UsageJson.NumberAt(body, parse.ValuePath);
				if (value is null) return new(false);
				var currency = UsageJson.TextAt(body, parse.CurrencyPath) ?? (parse.Currency.Length > 0 ? parse.Currency : null);
				if (currency is { Length: > 12 } || currency?.Any(char.IsControl) == true) currency = null;
				return new(true, ProviderUsageKind.Balance, Balance: value, Currency: currency);
			}
			case UsageParseKind.Credits:
				return ParseCredits(body, parse);
			case UsageParseKind.Custom:
				return UsageProbeResolvers.Parse(parse.Resolver, body);
			default:
				return ParsePeriods(body);
		}
	}

	private static UsageProbeResponse ParsePeriods(JsonNode? body)
	{
		if (body?["usage"] is not JsonObject usage) return new(false);
		UsagePeriod? Read(string key)
		{
			if (usage[key] is not JsonObject item) return null;
			var percent = UsageJson.Number(item["percent"]);
			var resetsAt = ParseResetTime(item["resetsAt"] ?? item["resetAt"] ?? item["reset_at"]);
			var status = UsageJson.TextAt(item, "status");
			return percent is null && resetsAt is null && status is null ? null : new UsagePeriod(percent, resetsAt, status);
		}
		var periods = new UsagePeriods(Read("rolling"), Read("weekly"), Read("monthly"));
		return periods.IsEmpty ? new(false) : new(true, ProviderUsageKind.Periods, Periods: periods);
	}

	private static UsageProbeResponse ParseCredits(JsonNode body, UsageProbeParse parse)
	{
		var scale = parse.Scale > 0 && double.IsFinite(parse.Scale) ? parse.Scale : 1;
		double? Scaled(string path)
		{
			if (path.Length == 0) return null;
			var value = UsageJson.NumberAt(body, path);
			return value is null ? null : value / scale;
		}

		var total = Scaled(parse.TotalPath);
		var used = Scaled(parse.UsedPath);
		var remaining = Scaled(parse.RemainingPath);
		if (remaining is null && total is not null && used is not null) remaining = total - used;

		var windows = new List<UsageWindow>();
		foreach (var window in parse.Windows ?? [])
		{
			var scope = body;
			if (window.ListPath.Length > 0)
			{
				scope = ResolveWindowByList(body, window);
				if (scope is null) continue;
			}
			var wTotal = UsageJson.NumberAt(scope, window.TotalPath);
			var wUsed = UsageJson.NumberAt(scope, window.UsedPath);
			var wRemaining = UsageJson.NumberAt(scope, window.RemainingPath);
			if (wRemaining is null && wTotal is not null && wUsed is not null) wRemaining = wTotal - wUsed;
			var wPercent = UsageJson.NumberAt(scope, window.PercentPath);
			if (wPercent is { } percent) wPercent = Math.Clamp(percent, 0, 100);
			if (wTotal is null && wUsed is null && wRemaining is null && wPercent is null) continue;
			windows.Add(new UsageWindow(window.Key, wTotal, wUsed, wRemaining, wPercent));
		}

		var booster = parse.Booster is null ? null : ParseBooster(body, parse.Booster);
		if (total is null && used is null && remaining is null && windows.Count == 0 && booster is null) return new(false);
		return new(true, ProviderUsageKind.Credits, Credits: new UsageCredits(total, used, remaining, windows.Count > 0 ? windows : null), Booster: booster);
	}

	private static JsonNode? ResolveWindowByList(JsonNode body, UsageProbeWindow window)
	{
		if (UsageJson.At(body, window.ListPath) is not JsonArray list) return null;
		foreach (var item in list)
		{
			if (item is not JsonObject obj) continue;
			var matches = true;
			foreach (var where in window.Where ?? [])
			{
				var actual = UsageJson.At(obj, where.Path);
				if (!JsonEquals(where.Value, actual)) { matches = false; break; }
			}
			if (matches) return obj;
		}
		return null;
	}

	private static bool JsonEquals(JsonNode? expected, JsonNode? actual)
	{
		if (expected is JsonArray set) return set.Any(item => JsonEquals(item, actual));
		if (expected is null || actual is null) return expected is null && actual is null;
		return string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal);
	}

	private static UsageBooster? ParseBooster(JsonNode body, UsageProbeBooster spec)
	{
		var fixedPoint = spec.FixedPointPerCent > 0 ? spec.FixedPointPerCent : 1_000_000;
		double Major(double value) => value / fixedPoint / 100;
		var balance = UsageJson.NumberAt(body, spec.BalancePath);
		if (balance is null) return null;
		var total = UsageJson.NumberAt(body, spec.TotalPath);
		var usedCents = UsageJson.NumberAt(body, spec.MonthlyUsedCentsPath);
		var limitCents = UsageJson.NumberAt(body, spec.MonthlyChargeLimitCentsPath);
		var enabled = spec.MonthlyChargeLimitEnabledPath.Length > 0 ? UsageJson.At(body, spec.MonthlyChargeLimitEnabledPath) : null;
		var unlimited = enabled is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag;
		return new UsageBooster(Major(balance.Value),
			total is null ? null : Major(total.Value),
			UsageJson.TextAt(body, spec.CurrencyPath),
			usedCents is null ? null : usedCents / 100,
			limitCents is null || unlimited ? null : limitCents / 100,
			unlimited);
	}

	public static DateTimeOffset? ParseResetTime(JsonNode? node)
	{
		if (node is not JsonValue value) return null;
		if (value.TryGetValue<string>(out var text))
		{
			if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
			return null;
		}
		if (!value.TryGetValue<double>(out var number) || number <= 0) return null;
		try
		{
			var milliseconds = number > 1e12 ? number : number * 1000;
			return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
		}
		catch (ArgumentOutOfRangeException) { return null; }
	}
}
