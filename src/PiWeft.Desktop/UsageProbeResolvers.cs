using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

/// <summary>
/// 专用解析器（结构特殊、声明式路径表达不了的响应）：
/// 与 PiDeck 的 providerUsageCustom / providerUsageMinimax 一一对应，
/// 新增此类供应商时在此注册，候选表写 <c>UsageParseKind.Custom</c>。
/// </summary>
internal static class UsageProbeResolvers
{
	public static UsageProbeResponse Parse(string resolver, JsonNode? body)
	{
		if (body is not JsonObject) return new(false);
		return resolver switch
		{
			"xai-billing" => ParseXaiBilling((JsonObject)body),
			"codex-usage" => ParseCodexUsage((JsonObject)body),
			"commandcode-credits" => ParseCommandCode((JsonObject)body),
			"kimi-credits" => ParseKimiCredits((JsonObject)body),
			"minimax-plan" => ParseMinimaxPlan((JsonObject)body),
			"sub2api-usage" => ParseSub2ApiUsage((JsonObject)body),
			_ => new(false),
		};
	}

	private static JsonObject? Box(JsonNode? node) => node as JsonObject;

	private static double? Number(JsonNode? node, string path) => UsageJson.NumberAt(node, path);

	// ── xAI ────────────────────────────────────────────────────────────────

	/// <summary>xAI 金额字段是 { val: cents } 包装：取 val 除 100 得美元。</summary>
	private static double? CentValue(JsonNode? node)
	{
		var cents = Number(node, "val");
		return cents is null ? null : cents / 100;
	}

	/// <summary>
	/// xAI consumer billing：config.creditUsagePercent 是套餐内额度占用百分比；
	/// 没有百分比时回退 monthlyLimit/used（cent wrapper）成美元桶；
	/// onDemandCap/onDemandUsed 是超出套餐的按需用量（美元）。
	/// </summary>
	private static UsageProbeResponse ParseXaiBilling(JsonObject body)
	{
		if (Box(body["config"]) is not { } config) return new(false);
		var windows = new List<UsageWindow>();
		if (Number(config, "creditUsagePercent") is { } percent && percent is >= 0 and <= 100)
		{
			windows.Add(new UsageWindow("included", 100, percent));
		}
		else
		{
			var limit = CentValue(config["monthlyLimit"]);
			var used = CentValue(config["used"]);
			if (limit is not null || used is not null)
				windows.Add(new UsageWindow("included", limit ?? used ?? 0, used ?? 0));
		}
		var cap = CentValue(config["onDemandCap"]);
		var onDemandUsed = CentValue(config["onDemandUsed"]);
		if (cap is not null || onDemandUsed is not null)
			windows.Add(new UsageWindow("onDemand", cap ?? onDemandUsed ?? 0, onDemandUsed ?? 0));
		return windows.Count == 0
			? new(false)
			: new(true, ProviderUsageKind.Credits, Credits: new UsageCredits(Windows: windows));
	}

	// ── OpenAI Codex ───────────────────────────────────────────────────────

	/// <summary>
	/// Codex wham/usage：rate_limit.primary/secondary_window 是 percent 桶，
	/// credits.balance 是剩余点数（has_credits 时），unlimited 时没有具体余额。
	/// </summary>
	private static UsageProbeResponse ParseCodexUsage(JsonObject body)
	{
		var windows = new List<UsageWindow>();
		foreach (var key in new[] { "primary", "secondary" })
		{
			var used = Number(body, $"rate_limit.{key}_window.used_percent");
			if (used is null) continue;
			windows.Add(new UsageWindow(key, 100, Math.Clamp(used.Value, 0, 100)));
		}
		var credits = Box(body["credits"]);
		var balance = credits is null ? null : Number(credits, "balance");
		var unlimited = credits is not null &&
			credits["has_credits"] is JsonValue has && has.TryGetValue<bool>(out var hasCredits) && hasCredits &&
			credits["unlimited"] is JsonValue limit && limit.TryGetValue<bool>(out var isUnlimited) && isUnlimited;
		if (windows.Count == 0 && balance is null) return new(false);
		return new(true, ProviderUsageKind.Credits,
			Credits: new UsageCredits(Remaining: unlimited ? null : balance, Windows: windows.Count > 0 ? windows : null));
	}

	// ── Command Code ───────────────────────────────────────────────────────

	/// <summary>Command Code 官方套餐定价：仅用于月度分母推算（5h/周 cap 组合全表唯一）。</summary>
	private static readonly (double Monthly, double FiveHour, double Weekly)[] CommandCodePlans =
	[
		(10, 3, 6), (70, 14, 35), (80, 16, 40), (150, 45, 90), (300, 90, 180),
	];

	/// <summary>
	/// Command Code /alpha/billing/credits：月度只有剩余额度（上限不在接口里），
	/// 5h/周窗口的 used/cap 来自响应。月度分母按套餐表反查 + 双重校验，校验不过
	/// 降级为「只显剩余不给百分比」（fail-closed）。
	/// </summary>
	private static UsageProbeResponse ParseCommandCode(JsonObject body)
	{
		var credits = Box(body["credits"]);
		var monthlyRemaining = Number(credits, "monthlyCredits") ?? Number(credits, "monthly_credits") ??
			Number(body, "monthlyCredits") ?? Number(body, "monthly_credits");
		if (monthlyRemaining is null) return new(false);

		JsonObject? windowBox = Box(credits?["windowLimits"]) ?? Box(credits?["window_limits"]) ??
			Box(body["windowLimits"]) ?? Box(body["window_limits"]);
		JsonObject? Window(string key) => windowBox is null ? null :
			Box(windowBox[key]) ?? Box(windowBox[key == "fiveHour" ? "five_hour" : key]);

		double? Cap(JsonObject? window)
		{
			if (window is null) return null;
			var cap = Number(window, "cap") ?? Number(window, "limit");
			return cap is > 0 ? cap : null;
		}
		double? Used(JsonObject? window) => window is null ? null : Math.Max(0, Number(window, "used") ?? 0);

		var fiveHourWindow = Window("fiveHour");
		var weekWindow = Window("weekly");
		var fiveHourCap = Cap(fiveHourWindow);
		var fiveHourUsed = Used(fiveHourWindow);
		var weekCap = Cap(weekWindow);
		var weekUsed = Used(weekWindow);

		var windows = new List<UsageWindow>();
		if (fiveHourCap is not null && fiveHourUsed is not null) windows.Add(new UsageWindow("fiveHour", fiveHourCap, fiveHourUsed));
		if (weekCap is not null && weekUsed is not null) windows.Add(new UsageWindow("weekly", weekCap, weekUsed));

		// 月度窗口：有可信额度出「已用/总额」，否则只显剩余（不臆造百分比）。
		double? allowance = null;
		if (fiveHourCap is not null && weekCap is not null)
		{
			var plan = CommandCodePlans.FirstOrDefault(entry => entry.FiveHour == fiveHourCap && entry.Weekly == weekCap);
			if (plan.Monthly > 0 && monthlyRemaining <= plan.Monthly) allowance = plan.Monthly;
		}
		windows.Add(allowance is null
			? new UsageWindow("monthly", Remaining: monthlyRemaining)
			: new UsageWindow("monthly", allowance, Math.Clamp(allowance.Value - monthlyRemaining.Value, 0, allowance.Value), monthlyRemaining));
		return new(true, ProviderUsageKind.Credits,
			Credits: new UsageCredits(Remaining: monthlyRemaining, Windows: windows));
	}

	// ── Kimi For Coding ────────────────────────────────────────────────────

	/// <summary>
	/// Kimi /usages：5h 滚动限制在 limits[]（window.duration/timeUnit + detail），
	/// 周额度在 usage，月度会员限额在 totalQuota；独立 Boost 钱包不走这里，
	/// 由声明式 booster 规格之外的固定路径解析（与 PiDeck 同一组路径）。
	/// </summary>
	private static UsageProbeResponse ParseKimiCredits(JsonObject body)
	{
		var windows = new List<UsageWindow>();
		if (body["limits"] is JsonArray limits)
		{
			foreach (var item in limits)
			{
				if (Box(item) is not { } limit || Box(limit["detail"]) is not { } detail) continue;
				var duration = Number(limit, "window.duration") ?? 0;
				var unit = (UsageJson.TextAt(limit, "window.timeUnit") ?? string.Empty).ToUpperInvariant().Replace("TIME_UNIT_", "");
				var key = duration == 300 && unit == "MINUTE" ? "fiveHour"
					: duration > 0 && unit.Length > 0 ? $"{duration:0}{UnitChar(unit)}"
					: UsageJson.TextAt(detail, "name") ?? "window";
				var total = Number(detail, "limit");
				var used = Number(detail, "used");
				var remaining = Number(detail, "remaining");
				if (remaining is null && total is not null && used is not null) remaining = Math.Max(0, total.Value - used.Value);
				if (total is null && used is null && remaining is null) continue;
				windows.Add(new UsageWindow(key, total, used, remaining));
			}
		}

		double? weeklyTotal = null, weeklyUsed = null, weeklyRemaining = null;
		if (Box(body["usage"]) is { } usage)
		{
			weeklyTotal = Number(usage, "limit");
			weeklyUsed = Number(usage, "used");
			weeklyRemaining = Number(usage, "remaining");
			if (weeklyRemaining is null && weeklyTotal is not null && weeklyUsed is not null)
				weeklyRemaining = Math.Max(0, weeklyTotal.Value - weeklyUsed.Value);
			if (weeklyTotal is not null || weeklyUsed is not null || weeklyRemaining is not null)
				windows.Add(new UsageWindow("weekly", weeklyTotal, weeklyUsed, weeklyRemaining));
		}

		if (Box(body["totalQuota"]) is { } totalQuota)
		{
			var limit = Number(totalQuota, "limit") ?? 100;
			var used = Number(totalQuota, "used");
			var remaining = Number(totalQuota, "remaining");
			// used>0 表示已达上限/冻结（粘性 remaining 不可信）。
			if (used is > 0) windows.Add(new UsageWindow("monthly", limit, limit, 0));
			else if (remaining is not null || limit > 0)
			{
				var spent = used ?? (remaining is not null ? Math.Max(0, limit - remaining.Value) : 0);
				windows.Add(new UsageWindow("monthly", limit, spent, remaining ?? Math.Max(0, limit - spent)));
			}
		}

		var booster = ParseKimiBooster(body);
		if (windows.Count == 0 && booster is null) return new(false);
		return new(true, ProviderUsageKind.Credits,
			Credits: new UsageCredits(weeklyTotal ?? windows.FirstOrDefault()?.Total,
				weeklyUsed ?? windows.FirstOrDefault()?.Used,
				weeklyRemaining ?? windows.FirstOrDefault()?.Remaining,
				windows.Count > 0 ? windows : null),
			Booster: booster);
	}

	private static string UnitChar(string unit) => unit.StartsWith("HOUR", StringComparison.Ordinal) ? "h"
		: unit.StartsWith("DAY", StringComparison.Ordinal) ? "d"
		: unit.StartsWith("MIN", StringComparison.Ordinal) ? "m" : unit.ToLowerInvariant();

	private static UsageBooster? ParseKimiBooster(JsonObject body)
	{
		var balance = UsageJson.NumberAt(body, "boosterWallet.balance.amountLeft");
		if (balance is null) return null;
		const double fixedPoint = 1_000_000;
		double Major(double value) => value / fixedPoint / 100;
		var total = UsageJson.NumberAt(body, "boosterWallet.balance.amount");
		var usedCents = UsageJson.NumberAt(body, "boosterWallet.monthlyUsed.priceInCents");
		var limitCents = UsageJson.NumberAt(body, "boosterWallet.monthlyChargeLimit.priceInCents");
		var enabled = body["boosterWallet"]?["monthlyChargeLimitEnabled"];
		var unlimited = enabled is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag;
		return new UsageBooster(Major(balance.Value),
			total is null ? null : Major(total.Value),
			UsageJson.TextAt(body, "boosterWallet.monthlyUsed.currency"),
			usedCents is null ? null : usedCents / 100,
			limitCents is null || unlimited ? null : limitCents / 100,
			unlimited);
	}

	// ── MiniMax Coding Plan ────────────────────────────────────────────────

	/// <summary>
	/// MiniMax /v1/token_plan/remains：model_remains 每行一个模型桶（general / video），
	/// 各带 5h 滚动窗与周窗。鉴权失败也是 HTTP 200，只能看 base_resp.status_code。
	/// 接口给「剩余百分比」，UI 要「已用」，所以翻转成 100 - remaining。
	/// </summary>
	private static UsageProbeResponse ParseMinimaxPlan(JsonObject body)
	{
		var status = Number(body, "base_resp.status_code");
		if (status is not null and not 0) return new(false);
		if (body["model_remains"] is not JsonArray rows) return new(false);

		JsonObject? Pick()
		{
			var objects = rows.OfType<JsonObject>().ToArray();
			return objects.FirstOrDefault(row => UsageJson.TextAt(row, "model_name") == "general") ??
				objects.FirstOrDefault(row => UsageJson.TextAt(row, "model_name") != "video");
		}
		if (Pick() is not { } row) return new(false);

		UsagePeriod? Read(string prefix, string resetKey)
		{
			double? remaining = Number(row, $"{prefix}_remaining_percent");
			double? percent = null;
			if (remaining is not null) percent = Math.Round(Math.Clamp(100 - remaining.Value, 0, 100), 1);
			else
			{
				var total = Number(row, $"{prefix}_total_count");
				var left = Number(row, $"{prefix}_usage_count");
				if (total is > 0 && left is not null)
					percent = Math.Round(Math.Clamp((total.Value - Math.Clamp(left.Value, 0, total.Value)) / total.Value * 100, 0, 100), 1);
			}
			if (percent is null) return null;
			var resetsAt = EpochMilliseconds(row[resetKey]);
			return new UsagePeriod(percent, resetsAt);
		}

		var periods = new UsagePeriods(Read("current_interval", "end_time"), Read("current_weekly", "weekly_end_time"));
		return periods.IsEmpty ? new(false) : new(true, ProviderUsageKind.Periods, Periods: periods);
	}

	private static DateTimeOffset? EpochMilliseconds(JsonNode? node)
	{
		var number = UsageJson.Number(node);
		if (number is null or <= 0) return null;
		try { return DateTimeOffset.FromUnixTimeMilliseconds((long)number.Value); }
		catch (ArgumentOutOfRangeException) { return null; }
	}

	// ── Sub2API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Sub2API 网关 GET /v1/usage：响应随计费形态分三支——钱包（顶层 balance + unit）、
	/// 订阅（subscription 日/周/月 usage/limit 对）、key 额度（quota 三件套 + rate_limits[]）。
	/// 面板 JWT 余额段（/api/v1/auth/me）不在此解析：PiWeft 只保留主用量。
	/// </summary>
	private static UsageProbeResponse ParseSub2ApiUsage(JsonObject root)
	{
		var unit = UsageJson.TextAt(root, "unit");
		var wallet = Number(root, "balance");
		if (wallet is not null && Box(root["subscription"]) is null)
			return new(true, ProviderUsageKind.Balance, Balance: wallet, Currency: unit);

		var windows = new List<UsageWindow>();
		var quota = Box(root["quota"]);
		var quotaTotal = quota is null ? null : Number(quota, "limit");
		var quotaUsed = quota is null ? null : Number(quota, "used");
		var quotaRemaining = quota is null ? null : Number(quota, "remaining");

		if (root["rate_limits"] is JsonArray rateLimits)
		{
			foreach (var entry in rateLimits.OfType<JsonObject>())
			{
				var key = UsageJson.TextAt(entry, "window") switch
				{
					"5h" => "fiveHour",
					"1d" => "daily",
					"7d" => "weekly",
					_ => null,
				};
				if (key is null) continue;
				var rateTotal = Number(entry, "limit");
				var rateUsed = Number(entry, "used");
				if (rateTotal is null && rateUsed is null) continue;
				windows.Add(new UsageWindow(key, rateTotal, rateUsed));
			}
		}

		if (Box(root["subscription"]) is { } subscription)
		{
			foreach (var (key, usageField, limitField) in new[]
			{
				("daily", "daily_usage_usd", "daily_limit_usd"),
				("weekly", "weekly_usage_usd", "weekly_limit_usd"),
				("monthly", "monthly_usage_usd", "monthly_limit_usd"),
			})
			{
				var limit = Number(subscription, limitField);
				if (limit is null or <= 0) continue;
				windows.Add(new UsageWindow(key, limit, Math.Max(0, Number(subscription, usageField) ?? 0)));
			}
			// 旧版部署别名：只有 used_quota/total_quota，作月档兜底。
			if (!windows.Any(window => window.Key == "monthly"))
			{
				var totalQuota = Number(subscription, "total_quota");
				if (totalQuota is > 0)
					windows.Add(new UsageWindow("monthly", totalQuota, Math.Max(0, Number(subscription, "used_quota") ?? 0)));
			}
		}

		var remainingRaw = Number(root, "remaining");
		var remaining = remainingRaw is >= 0 ? remainingRaw : quotaRemaining is >= 0 ? quotaRemaining : null;
		var actualCost = Number(root, "usage.total.actual_cost");
		var total = quotaTotal is > 0 ? quotaTotal : null;
		var used = total is not null ? quotaUsed : actualCost;
		if (windows.Count == 0 && remaining is null && used is null && total is null) return new(false);
		return new(true, ProviderUsageKind.Credits,
			Credits: new UsageCredits(total, used, remaining, windows.Count > 0 ? windows : null));
	}
}
