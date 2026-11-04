using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.Desktop;

/// settings.json 里 <c>defaultTools</c> 与 <c>codemode</c> 的读写语义（与 pi 一致）：
/// 纯名字整体替换默认集，<c>+name</c>/<c>-name</c> 在继承集上增删，空数组是「禁用全部」，
/// 删掉键则回到继承/默认。
internal static class ConfigurationTools
{
	public static IReadOnlyList<string> Catalog { get; } = BuildCatalog();

	public static bool IsExtension(string name) => string.Equals(name, "codemode", StringComparison.Ordinal);

	private static List<string> BuildCatalog()
	{
		// powershell 只在 Windows 上声明，与 BuiltinTools.Create 保持一致。
		List<string> names = ["read", "bash"];
		if (OperatingSystem.IsWindows()) names.Add("powershell");
		names.AddRange(["edit", "write", "grep", "find", "ls", "todo", "ask_question"]);
		// codemode 默认不启用：与 BuiltinTools.Catalog 一样只出现在目录里，需要显式选中。
		names.Add("codemode");
		return names;
	}

	public static List<string>? ReadEntries(JsonObject root)
	{
		if (root["defaultTools"] is not JsonArray array) return null;
		var entries = new List<string>(array.Count);
		foreach (var node in array)
		{
			if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) entries.Add(text);
		}
		return entries;
	}

	public static IReadOnlyList<string> Resolve(IReadOnlyList<string>? entries)
	{
		if (entries is null) return [.. CodingAgentSettings.DefaultToolNames];
		// 出现纯名字即整体替换默认集；显式空列表解析为空选择。
		var plain = entries.Where(static entry => !IsModifier(entry)).ToList();
		List<string> tools = plain.Count > 0 || entries.Count == 0 ? plain : [.. CodingAgentSettings.DefaultToolNames];
		foreach (var entry in entries)
		{
			if (!IsModifier(entry) || entry.Length < 2) continue;
			var name = entry[1..];
			var index = tools.IndexOf(name);
			if (entry[0] == '+' && index < 0) tools.Add(name);
			else if (entry[0] == '-' && index >= 0) tools.RemoveAt(index);
		}
		return tools;
	}

	public static IReadOnlyList<string> ResolveInLayer(IReadOnlyList<string>? baseEntries, IReadOnlyList<string>? entries) =>
		Resolve(CodingAgentSettings.MergeDefaultToolEntries(baseEntries, entries));

	public static bool IsEnabled(IReadOnlyList<string>? baseEntries, IReadOnlyList<string>? entries, string name) =>
		ResolveInLayer(baseEntries, entries).Contains(name, StringComparer.Ordinal);

	/// 开关一个工具并返回该层新的原始条目；<c>null</c> 表示删除该键（回到继承/默认）。
	/// 编码与 <see cref="Encode"/> 共用回验，显式空选择后只开一个工具不会意外带回默认集。
	public static IReadOnlyList<string>? SetEnabled(
		IReadOnlyList<string>? baseEntries, IReadOnlyList<string>? entries, string name, bool on)
	{
		List<string> selection = [.. ResolveInLayer(baseEntries, entries)];
		var index = selection.IndexOf(name);
		if (on && index < 0) InsertInCatalogOrder(selection, name);
		else if (!on && index >= 0) selection.RemoveAt(index);
		return Encode(baseEntries, entries, selection);
	}

	/// 按目录顺序插入：关掉再打开一个默认工具时顺序要与默认集一致，否则「回到默认」判定失败，
	/// 会把本可以删除的键写成一份冗长的显式列表。
	private static void InsertInCatalogOrder(List<string> selection, string name)
	{
		var rank = Rank(name);
		if (rank >= 0)
		{
			for (var index = 0; index < selection.Count; index++)
			{
				var other = Rank(selection[index]);
				if (other < 0 || other > rank)
				{
					selection.Insert(index, name);
					return;
				}
			}
		}
		selection.Add(name);
	}

	private static int Rank(string name)
	{
		for (var index = 0; index < Catalog.Count; index++)
			if (string.Equals(Catalog[index], name, StringComparison.Ordinal)) return index;
		return -1;
	}

	/// 把期望的生效工具集编码回该层最小的原始条目：先保留现值，再尝试删除键，
	/// 然后是「保留其它条目 + 增量」，最后用纯名字列表兜底。<c>null</c> 表示删除该键。
	public static IReadOnlyList<string>? Encode(
		IReadOnlyList<string>? baseEntries, IReadOnlyList<string>? entries, IReadOnlyList<string> selection)
	{
		bool Resolves(IReadOnlyList<string>? candidate) => Same(ResolveInLayer(baseEntries, candidate), selection);

		// 现值已经表达目标（含手写的未知工具与顺序）时原样保留。
		if (Resolves(entries)) return entries;
		// 删除键 = 回到继承值/默认；精确命中时优先，保持配置干净。
		if (Resolves(null)) return null;

		var current = ResolveInLayer(baseEntries, entries);
		var changed = new HashSet<string>(StringComparer.Ordinal);
		foreach (var name in current)
			if (!selection.Contains(name, StringComparer.Ordinal)) changed.Add(name);
		foreach (var name in selection)
			if (!current.Contains(name, StringComparer.Ordinal)) changed.Add(name);

		// 与改动无关的原条目（含用户手写的未知名字与顺序）原样保留。
		List<string> kept = [];
		foreach (var entry in entries ?? [])
			if (!changed.Contains(IsModifier(entry) && entry.Length > 1 ? entry[1..] : entry)) kept.Add(entry);

		List<string> modifiers = [];
		foreach (var name in current)
			if (!selection.Contains(name, StringComparer.Ordinal)) modifiers.Add("-" + name);
		foreach (var name in selection)
			if (!current.Contains(name, StringComparer.Ordinal)) modifiers.Add("+" + name);

		List<string> withModifiers = [.. kept, .. modifiers];
		List<string> plain = [.. selection];
		foreach (var candidate in new List<string>[] { kept, withModifiers, modifiers, plain })
			if (Resolves(candidate)) return candidate;
		return plain;
	}

	public static bool DisablesAll(IReadOnlyList<string>? entries) => entries is { Count: 0 };

	public static bool Same(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
	{
		if (left is null || right is null) return left is null && right is null;
		if (left.Count != right.Count) return false;
		for (var index = 0; index < left.Count; index++)
			if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
		return true;
	}

	/// 只写调用方真正改过的 <c>codemode</c> 子键，保留未知嵌套字段；
	/// 返回 <c>null</c> 表示整个 <c>codemode</c> 键应当移除（避免落盘 <c>codemode: {}</c> 脏数据）。
	public static JsonObject? PatchCodemode(
		JsonNode? existing, string? mode, int? inlineBudget, bool modeChanged, bool inlineBudgetChanged)
	{
		var next = existing is JsonObject current ? current.DeepClone().AsObject() : new JsonObject();
		if (modeChanged)
		{
			if (mode is null) next.Remove("mode"); else next["mode"] = mode;
		}
		if (inlineBudgetChanged)
		{
			if (inlineBudget is null) next.Remove("inlineBudget"); else next["inlineBudget"] = inlineBudget.Value;
		}
		return next.Count == 0 ? null : next;
	}

	private static bool IsModifier(string entry) => entry.Length > 0 && entry[0] is '+' or '-';
}
