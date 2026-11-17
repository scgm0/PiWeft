using System.Globalization;
using PiWeft.CodingAgent.Stats;

namespace PiWeft.Desktop;

/// 使用统计的读数格式。token 缩写、聊天时长与日期标签集中在这里，
/// 面板与测试用同一套口径，避免「1.2M」和「1.2 M」两种写法。
internal static class UsageStatsFormat
{
	public static string Tokens(long tokens) => tokens switch
	{
		>= 1_000_000_000 => (tokens / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "B",
		>= 1_000_000 => (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
		>= 1_000 => (tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
		_ => tokens.ToString(CultureInfo.InvariantCulture),
	};

	public static string Duration(double milliseconds)
	{
		if (!double.IsFinite(milliseconds) || milliseconds <= 0)
		{
			return "--";
		}

		if (milliseconds < 60_000)
		{
			return "<1 分";
		}

		var minutes = (long)(milliseconds / 60_000);

		var days = minutes / (24 * 60);
		var hours = minutes % (24 * 60) / 60;
		var rest = minutes % 60;
		if (days > 0)
		{
			return hours > 0 ? $"{days} 天 {hours} 小时" : $"{days} 天";
		}

		if (hours > 0)
		{
			return rest > 0 ? $"{hours} 小时 {rest} 分" : $"{hours} 小时";
		}

		return $"{rest} 分";
	}

	public static string Days(int days) => $"{days} 天";

	public static string Percent(double? value) => value is { } number && double.IsFinite(number)
		? number.ToString("0.#", CultureInfo.InvariantCulture) + "%"
		: "--";

	public static string Cost(double cost) => cost > 0
		? cost.ToString("0.##", CultureInfo.InvariantCulture)
		: "--";

	public static string ModelName(ModelStats model) =>
		model.DisplayName.Length > 0 ? model.DisplayName : Strings.UsageStatsUnknownModel;

	public static string DayLabel(string date) =>
		DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
			? parsed.ToString("M/d", CultureInfo.InvariantCulture)
			: date;

	public static string MonthLabel(string month) =>
		DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
			? parsed.ToString("yy/M", CultureInfo.InvariantCulture)
			: month;

	public static string ShortDate(string date) =>
		DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
			? parsed.ToString("yyyy/M/d", CultureInfo.InvariantCulture)
			: date;

	public static string RefreshTime(DateTimeOffset time) =>
		time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
