using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

/// <summary>
/// 把旧版 pi 会话升级到当前格式。pi 在打开会话时就会迁移并就地重写文件
/// （<c>session-manager.ts</c> 的 <c>migrateToCurrentVersion</c>），这里保持同一语义，
/// 否则旧会话在本端口里会读成另一种形状：
/// <list type="bullet">
/// <item><description><strong>v1 → v2</strong>：条目没有 <c>id</c>/<c>parentId</c>，树与分支投影只能
/// 看到最后一条；压缩条目用 <c>firstKeptEntryIndex</c> 下标引用，模型里没有这个字段。</description></item>
/// <item><description><strong>v2 → v3</strong>：消息角色 <c>hookMessage</c> 改名为 <c>custom</c>；
/// 不改名时它会被当成未知角色整体塞进原始对象，桌面与上下文都按另一种语义处理。</description></item>
/// </list>
/// 迁移是幂等的：已经是当前版本的文件原样返回 <see langword="false"/>，调用方不得重写。
/// </summary>
internal static class SessionMigration
{
	/// <summary>
	/// 就地把 <paramref name="entries"/>（头部 + 条目，按文件顺序）升级到
	/// <see cref="SessionFormat.CurrentVersion"/>；返回是否发生了迁移。
	/// </summary>
	/// <remarks>
	/// 没有头部（外部工具写出的会话）时不做迁移 —— pi 的加载分支也只对带头部的文件调用
	/// <c>migrateToCurrentVersion</c>，把无头文件补一个头部是另一条路径（见 SessionManager.Persist）。
	/// </remarks>
	public static bool Migrate(List<object> entries)
	{
		var headerIndex = entries.FindIndex(static entry => entry is SessionHeader);
		if (headerIndex < 0)
		{
			return false;
		}

		var header = (SessionHeader)entries[headerIndex];
		if (header.Version >= SessionFormat.CurrentVersion)
		{
			return false;
		}

		var version = header.Version;
		if (version < 2)
		{
			MigrateV1ToV2(entries);
		}

		if (version < 3)
		{
			MigrateV2ToV3(entries);
		}

		entries[headerIndex] = header with { Version = SessionFormat.CurrentVersion };
		return true;
	}

	/// <summary>v1 没有条目树：按文件顺序补 <c>id</c>/<c>parentId</c>，并把压缩下标换成 id。</summary>
	private static void MigrateV1ToV2(List<object> entries)
	{
		var taken = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in entries.OfType<SessionEntry>())
		{
			if (entry.Id.Length > 0)
			{
				taken.Add(entry.Id);
			}
		}

		// 先给所有条目分配 id，再换算压缩引用：v1 的下标可以指向更靠后的条目，
		// 一趟循环里边分配边解析会读到还没分配 id 的目标（pi 的实现就有这个窗口）。
		var assigned = new string[entries.Count];
		string? previousId = null;
		for (var index = 0; index < entries.Count; index++)
		{
			if (entries[index] is not SessionEntry entry)
			{
				continue;
			}

			var id = NewId(taken);
			assigned[index] = id;
			entries[index] = WithTree(entry, id, previousId);
			previousId = id;
		}

		for (var index = 0; index < entries.Count; index++)
		{
			if (entries[index] is not CompactionEntry compaction ||
				compaction.Extra is not { } extra ||
				!extra.TryGetPropertyValue("firstKeptEntryIndex", out var node) ||
				node is null)
			{
				continue;
			}

			var firstKeptEntryId = compaction.FirstKeptEntryId;
			if (node is JsonValue value &&
				value.GetValueKind() == JsonValueKind.Number &&
				value.TryGetValue<int>(out var keptIndex) &&
				keptIndex >= 0 && keptIndex < entries.Count && assigned[keptIndex].Length > 0)
			{
				firstKeptEntryId = assigned[keptIndex];
			}

			var remaining = (JsonObject)extra.DeepClone();
			remaining.Remove("firstKeptEntryIndex");
			entries[index] = compaction with
			{
				FirstKeptEntryId = firstKeptEntryId,
				Extra = remaining.Count > 0 ? remaining : null,
			};
		}
	}

	/// <summary>
	/// 未知条目写回时整行输出原始对象（见 <see cref="UnknownSessionEntry"/>），
	/// 只改模型上的 <c>Id</c> 不会落到文件里，必须同时改原始行。
	/// </summary>
	private static SessionEntry WithTree(SessionEntry entry, string id, string? parentId)
	{
		if (entry is not UnknownSessionEntry unknown)
		{
			return entry with { Id = id, ParentId = parentId };
		}

		var raw = (JsonObject)unknown.Raw.DeepClone();
		raw["id"] = id;
		raw["parentId"] = parentId is null ? null : JsonValue.Create(parentId);
		return unknown with { Id = id, ParentId = parentId, Raw = raw };
	}

	/// <summary>v2 → v3：<c>hookMessage</c> 角色改名为 <c>custom</c>（原始对象一起改）。</summary>
	private static void MigrateV2ToV3(List<object> entries)
	{
		for (var index = 0; index < entries.Count; index++)
		{
			if (entries[index] is not SessionMessageEntry { Message: CustomMessage { CustomRole: "hookMessage" } message } entry)
			{
				continue;
			}

			var raw = message.Raw is null ? null : (JsonObject)message.Raw.DeepClone();
			if (raw is not null)
			{
				raw["role"] = "custom";
			}

			entries[index] = entry with
			{
				Message = message with { CustomRole = "custom", Raw = raw },
			};
		}
	}

	/// <summary>8 位十六进制 id，与 pi 的 <c>generateId</c> 同形；冲突时退回完整 UUID。</summary>
	private static string NewId(HashSet<string> taken)
	{
		for (var attempt = 0; attempt < 100; attempt++)
		{
			var id = Guid.NewGuid().ToString("N")[..8];
			if (taken.Add(id))
			{
				return id;
			}
		}

		var full = Guid.NewGuid().ToString();
		taken.Add(full);
		return full;
	}
}
