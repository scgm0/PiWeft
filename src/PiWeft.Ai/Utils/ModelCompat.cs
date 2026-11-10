using System.Text.Json.Nodes;

namespace PiWeft.Ai.Utils;

/// 模型目录里 <c>compat</c> 的读取口径。pi 把能力开关放在模型条目的 <c>compat</c> 里
/// （<c>supportsStrictMode</c>、<c>supportsOpenAIGrammarTools</c> 等），缺省即「没有声明」：
/// 缺省值由调用方决定，这里不替 provider 猜。
internal static class ModelCompat
{
	/// 布尔开关；字段缺失或不是布尔时返回 <c>null</c>。
	public static bool? Flag(Model model, string key) =>
		model.Compat?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

	/// 布尔开关，带缺省值。
	public static bool FlagOr(Model model, string key, bool fallback) => Flag(model, key) ?? fallback;

	/// 字符串字段；缺失或不是字符串时返回 <c>null</c>。
	public static string? Text(Model model, string key) =>
		model.Compat?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	/// 数组字段；缺失或不是数组时返回空列表。
	public static IReadOnlyList<JsonNode?> Array(Model model, string key) =>
		model.Compat?[key] is JsonArray array ? [.. array] : [];
}
