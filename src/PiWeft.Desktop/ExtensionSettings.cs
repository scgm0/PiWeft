using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Extensions;

namespace PiWeft.Desktop;

/// settings.json <c>extensions</c> 的读写：只改该键，保留文件里的其它字段。
/// 开关写入绝对路径与 <c>builtin:name</c>，与 pi 的 <c>+</c>/<c>-</c> 规则同一形状。
internal static class ExtensionSettings
{
	public static IReadOnlyList<string> ReadEntries(JsonObject root)
	{
		if (root["extensions"] is not JsonArray array)
		{
			return [];
		}

		var entries = new List<string>(array.Count);
		foreach (var node in array)
		{
			if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
			{
				entries.Add(text);
			}
		}

		return entries;
	}

	public static bool TrySetEnabled(string agentDir, string path, bool enabled, out string error) =>
		TryWrite(agentDir, entries => ExtensionRules.SetEnabled(entries, path, enabled, agentDir), out error);

	public static bool TrySetBuiltinEnabled(string agentDir, string name, bool enabled, out string error) =>
		TryWrite(agentDir, entries => ExtensionRules.SetBuiltinEnabled(entries, name, enabled), out error);

	private static bool TryWrite(
		string agentDir,
		Func<IReadOnlyList<string>, IReadOnlyList<string>> mutate,
		out string error)
	{
		error = string.Empty;
		try
		{
			// 设置页只写全局文件：项目层由配置文件页维护，避免开关顺手改写仓库里的 .pi/settings.json。
			var store = new ConfigurationStore(agentDir, agentDir);
			var document = store.Read(ConfigFile.Settings);
			if (document.Error is not null)
			{
				error = document.Error;
				return false;
			}

			var root = document.Parse();
			var entries = mutate(ReadEntries(root));
			if (entries.Count == 0)
			{
				root.Remove("extensions");
			}
			else
			{
				var nodes = new JsonNode?[entries.Count];
				for (var index = 0; index < entries.Count; index++)
				{
					nodes[index] = JsonValue.Create(entries[index]);
				}

				root["extensions"] = new JsonArray(nodes);
			}

			store.Save(document, root.ToJsonString(ConfigurationStore.WriteOptions));
			return true;
		}
		catch (Exception exception) when (exception is ConfigurationConflictException
			or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			error = exception.Message;
			return false;
		}
	}
}
