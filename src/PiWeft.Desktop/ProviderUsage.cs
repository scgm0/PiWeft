using System.Globalization;
using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

/// <summary>用量结果的展示形态。三类形态互斥，解析不出形态时查询按失败处理。</summary>
internal enum ProviderUsageKind
{
	/// <summary>三档占用百分比（滚动 / 周 / 月），如 opencode、火山方舟、MiniMax 套餐。</summary>
	Periods,
	/// <summary>剩余额度（金额 + 币种），如 DeepSeek、Moonshot 官方余额。</summary>
	Balance,
	/// <summary>额度点数（总额 / 已用 / 剩余），可带并列窗口与独立货币。</summary>
	Credits,
}

/// <summary>一档百分比用量；重置时间与可用状态都可能缺省。</summary>
internal sealed record UsagePeriod(double? Percent, DateTimeOffset? ResetsAt = null, string? Status = null);

internal sealed record UsagePeriods(UsagePeriod? Rolling = null, UsagePeriod? Weekly = null, UsagePeriod? Monthly = null)
{
	public bool IsEmpty => Rolling is null && Weekly is null && Monthly is null;
}

/// <summary>并列限额窗口（如智谱 5h 窗 + 周窗、xAI 套餐 + 按需）。</summary>
internal sealed record UsageWindow(string Key, double? Total = null, double? Used = null, double? Remaining = null, double? Percent = null);

internal sealed record UsageCredits(double? Total = null, double? Used = null, double? Remaining = null,
	IReadOnlyList<UsageWindow>? Windows = null);

/// <summary>与主额度并存的独立货币（如 Kimi Boost 点数），不与主额度混单位。</summary>
internal sealed record UsageBooster(double Balance, double? Total = null, string? Currency = null,
	double? MonthlyUsed = null, double? MonthlyChargeLimit = null, bool UnlimitedMonthly = false);

/// <summary>一次用量查询的完整结果；失败时只带可展示的本地文案，不带响应原文。</summary>
internal sealed record ProviderUsageResult(
	bool Success,
	ProviderUsageKind? Kind = null,
	UsagePeriods? Periods = null,
	double? Balance = null,
	string? Currency = null,
	UsageCredits? Credits = null,
	UsageBooster? Booster = null,
	string? Error = null,
	string? Detail = null,
	bool Disabled = false,
	DateTimeOffset At = default,
	int IntervalMinutes = UsageProbeStore.DefaultIntervalMinutes)
{
	public static ProviderUsageResult Failure(string error, string? detail = null) =>
		new(false, Error: error, Detail: detail, At: DateTimeOffset.UtcNow);

	public static ProviderUsageResult DisabledResult() =>
		new(false, Error: Strings.UsageDisabled, Disabled: true, At: DateTimeOffset.UtcNow, IntervalMinutes: 0);
}

/// <summary>响应路径取值与数值收窄：宽松容错，字段缺失或类型不符时返回 null。</summary>
internal static class UsageJson
{
	public static JsonNode? At(JsonNode? node, string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return null;
		foreach (var segment in path.Split(['.', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (node is JsonObject obj) node = obj[segment];
			else if (node is JsonArray array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
				index >= 0 && index < array.Count) node = array[index];
			else return null;
		}
		return node;
	}

	/// <summary>数值收窄：接受数字或可解析的数字字符串（网关余额字段常见字符串）。</summary>
	public static double? Number(JsonNode? node)
	{
		if (node is not JsonValue value) return null;
		if (value.TryGetValue<double>(out var number)) return double.IsFinite(number) ? number : null;
		return value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
			? number : null;
	}

	public static double? NumberAt(JsonNode? node, string path) => Number(At(node, path));

	public static string? TextAt(JsonNode? node, string path) =>
		At(node, path) is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text.Trim() : null;
}

/// <summary>
/// 用量展示的纯函数：档位色、金额格式、窗口标签、相对时间与内联摘要。
/// 与 PiDeck 的 providerUsageDisplay 对齐：百分比 ≥90% 红、≥70% 橙，其余绿；
/// 余额 ≤0 红、不足总额 10% 橙。
/// </summary>
internal static class ProviderUsageDisplay
{
	public enum Tone { Ok, Low, Empty, Neutral }

	/// <summary>币种代码 → 常用符号；未知代码原样作为后缀，避免「XYZ3.5」这类粘连。</summary>
	public static string CurrencySymbol(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
	{
		"CNY" or "RMB" => "¥",
		"USD" => "$",
		"EUR" => "€",
		"GBP" => "£",
		_ => string.Empty,
	};

	public static bool IsKnownCurrency(string? code) =>
		CurrencySymbol(code).Length > 0;

	private static readonly string[] CurrencyCodes = ["CNY", "RMB", "USD", "EUR", "GBP"];

	/// <summary>金额/点数：最多两位小数，整数不显示小数点。</summary>
	public static string Amount(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

	/// <summary>余额：已知币种用「¥110」前缀，未知代码用「110 XYZ」后缀，无币种只用数字。</summary>
	public static string Money(double value, string? currency)
	{
		var code = (currency ?? string.Empty).Trim();
		if (code.Length > 0 && (IsKnownCurrency(code) || CurrencyCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))
			return CurrencySymbol(code) + Amount(value);
		return code.Length > 0 ? $"{Amount(value)} {code}" : Amount(value);
	}

	public static string Percent(double value) => $"{Math.Round(value).ToString("0", CultureInfo.InvariantCulture)}%";

	/// <summary>单窗口已用百分比：优先 used/total（used 缺失时用 total-remaining 反推），其次服务端 percent。</summary>
	public static double? WindowPercent(UsageWindow window)
	{
		var used = window.Used ?? (window.Remaining is { } remaining && window.Total is { } total ? total - remaining : null);
		if (window.Total is { } limit && limit > 0 && used is { } value) return Math.Clamp(value / limit * 100, 0, 100);
		return window.Percent is { } percent && double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : null;
	}

	public static double? PeriodPercent(UsagePeriods? periods)
	{
		if (periods is null) return null;
		var values = new[] { periods.Rolling, periods.Weekly, periods.Monthly }
			.Select(period => period?.Percent)
			.Where(percent => percent is not null)
			.Select(percent => Math.Clamp(percent!.Value, 0, 100))
			.ToArray();
		return values.Length == 0 ? null : values.Max();
	}

	/// <summary>结果里可推导出的最高用量百分比；余额形态没有上限概念，返回 null。</summary>
	public static double? UsagePercent(ProviderUsageResult result)
	{
		if (!result.Success) return null;
		if (result.Kind == ProviderUsageKind.Periods) return PeriodPercent(result.Periods);
		if (result.Kind != ProviderUsageKind.Credits || result.Credits is not { } credits) return null;
		var windowPercents = (credits.Windows ?? [])
			.Select(WindowPercent)
			.Where(percent => percent is not null)
			.Select(percent => Math.Clamp(percent!.Value, 0, 100))
			.ToArray();
		if (windowPercents.Length > 0) return windowPercents.Max();
		if (credits.Total is { } total && total > 0)
		{
			var used = credits.Used ?? (credits.Remaining is { } remaining ? total - remaining : null);
			if (used is { } value) return Math.Clamp(value / total * 100, 0, 100);
		}
		return null;
	}

	public static Tone PercentTone(double? percent) => percent switch
	{
		null => Tone.Neutral,
		>= 90 => Tone.Empty,
		>= 70 => Tone.Low,
		_ => Tone.Ok,
	};

	public static Tone ToneOf(ProviderUsageResult result)
	{
		var percent = UsagePercent(result);
		if (percent is not null) return PercentTone(percent);
		if (result.Kind == ProviderUsageKind.Balance)
		{
			if (result.Balance is { } balance) return balance <= 0 ? Tone.Empty : Tone.Ok;
			return Tone.Neutral;
		}
		if (result.Kind != ProviderUsageKind.Credits || result.Credits is not { } credits) return Tone.Neutral;
		var remaining = credits.Remaining ?? (credits.Total is { } total && credits.Used is { } used ? total - used : null);
		if (remaining is null) return Tone.Neutral;
		if (remaining <= 0) return Tone.Empty;
		return credits.Total is > 0 && remaining / credits.Total < 0.1 ? Tone.Low : Tone.Ok;
	}

	public static string WindowLabel(string key) => key switch
	{
		"fiveHour" => Strings.UsageWindowFiveHour,
		"daily" => Strings.UsageWindowDaily,
		"weekly" => Strings.UsageWindowWeekly,
		"monthly" => Strings.UsageWindowMonthly,
		"mcpMonthly" => Strings.UsageWindowMcpMonthly,
		"included" => Strings.UsageWindowIncluded,
		"onDemand" => Strings.UsageWindowOnDemand,
		"rolling" => Strings.UsageWindowRolling,
		"primary" => Strings.UsageWindowPrimary,
		"secondary" => Strings.UsageWindowSecondary,
		_ => key,
	};

	/// <summary>内联摘要的一段：标签 + 数值 + 档位色。</summary>
	internal sealed record Segment(string Label, string Text, Tone Tone);

	/// <summary>
	/// 多档摘要（统计面板底部一行展开用）：periods / credits 窗口逐档「窗口名 + 百分比」，
	/// 无总额但有剩余时降级为「窗口名 + 剩余」；balance 为「余额 + 金额」；
	/// credits 主值为「剩余 + 数值」。返回 null 表示没有可展示数值。
	/// </summary>
	public static IReadOnlyList<Segment>? Segments(ProviderUsageResult result)
	{
		if (!result.Success) return null;
		if (result.Kind == ProviderUsageKind.Periods && result.Periods is { } periods)
		{
			var segments = new List<Segment>();
			foreach (var (label, period) in new[]
			{
				(Strings.UsageWindowRolling, periods.Rolling),
				(Strings.UsageWindowWeekly, periods.Weekly),
				(Strings.UsageWindowMonthly, periods.Monthly),
			})
			{
				if (period?.Percent is not { } percent) continue;
				segments.Add(new(label, Percent(percent), PercentTone(percent)));
			}
			return segments.Count > 0 ? segments : null;
		}
		if (result.Kind == ProviderUsageKind.Balance && result.Balance is { } balance)
			return [new(Strings.UsageBalanceShort, Money(balance, result.Currency), ToneOf(result))];
		if (result.Kind != ProviderUsageKind.Credits || result.Credits is not { } credits) return null;

		var list = new List<Segment>();
		var windows = credits.Windows ?? [];
		foreach (var window in windows)
		{
			if (WindowPercent(window) is { } percent)
				list.Add(new(WindowLabel(window.Key), Percent(percent), PercentTone(percent)));
			else if (window.Remaining is { } remaining)
				list.Add(new(WindowLabel(window.Key), Amount(remaining), Tone.Neutral));
		}
		if (windows.Count > 0 && list.Count > 0) return list;
		var left = credits.Remaining ?? (credits.Total is { } total && credits.Used is { } used ? total - used : null);
		if (left is not null) return [new(Strings.UsageRemainingShort, Amount(left.Value), ToneOf(result))];
		return credits.Used is { } spent ? [new(Strings.UsageUsedShort, Amount(spent), Tone.Neutral)] : null;
	}

	/// <summary>相对更新时间：刚刚 / n 分钟前 / n 小时前 / n 天前；超过 30 天给短日期。</summary>
	public static string RelativeTime(DateTimeOffset at, DateTimeOffset? now = null)
	{
		var elapsed = (now ?? DateTimeOffset.UtcNow) - at;
		if (elapsed < TimeSpan.Zero) return Strings.UsageTimeJustNow;
		if (elapsed.TotalMinutes < 1) return Strings.UsageTimeJustNow;
		if (elapsed.TotalHours < 1) return Strings.UsageTimeMinutes((int)elapsed.TotalMinutes);
		if (elapsed.TotalDays < 1) return Strings.UsageTimeHours((int)elapsed.TotalHours);
		if (elapsed.TotalDays <= 30) return Strings.UsageTimeDays((int)elapsed.TotalDays);
		return at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
	}

	/// <summary>错误文案：只透出本地生成的分类文案，绝不回显响应原文。</summary>
	public static string ErrorText(Exception error) => error switch
	{
		InvalidOperationException ex when ex.Message == Strings.UsageMissingKey || ex.Message == Strings.UsageUnavailable ||
			ex.Message == Strings.UsageResponseTooLarge || ex.Message.StartsWith(Strings.UsageHttpPrefix, StringComparison.Ordinal) => ex.Message,
		ArgumentException or System.Text.Json.JsonException or System.IO.InvalidDataException => Strings.UsageInvalidConfig,
		_ => Strings.UsageUnavailable,
	};
}
