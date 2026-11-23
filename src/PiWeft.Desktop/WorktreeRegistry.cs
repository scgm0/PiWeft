using System.Text.Json.Nodes;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// 一条隔离副本记录：仓库、目录、分支与父会话文件。父会话文件不在了，副本就成了孤儿。
internal sealed record WorktreeEntry(
	string Repository,
	string Path,
	string Branch,
	string ParentSessionFile,
	string ChildName,
	long CreatedAt);

/// 隔离副本的轻量登记表（<c>&lt;agentDir&gt;/worktrees/index.json</c>）。
/// 目的只有一个：父会话文件被删/归档后，副本还能被找出来并提供清理入口。
internal static class WorktreeRegistry
{
	private const int MaxEntries = 256;

	private static readonly Lock Gate = new();
	private static List<WorktreeEntry>? _entries;
	private static string? _pathOverride;

	/// <summary>登记表路径。</summary>
	public static string FilePath =>
		_pathOverride ?? System.IO.Path.Combine(SessionPaths.GetAgentDir(), "worktrees", "index.json");

	/// <summary>测试用：把登记表指向临时目录；传 null 恢复默认。</summary>
	internal static void UseFileForTests(string? path)
	{
		lock (Gate)
		{
			_pathOverride = path;
			_entries = null;
		}
	}

	/// <summary>记一条副本；同一个目录只留一条。</summary>
	public static void Record(WorktreeEntry entry)
	{
		lock (Gate)
		{
			var entries = EnsureLoadedLocked();
			entries.RemoveAll(candidate => string.Equals(candidate.Path, entry.Path, StringComparison.Ordinal));
			entries.Add(entry);
			if (entries.Count > MaxEntries)
			{
				entries.RemoveRange(0, entries.Count - MaxEntries);
			}

			SaveLocked(entries);
		}
	}

	/// <summary>副本被删除或并入主工作区后摘掉登记。</summary>
	public static void Forget(string path)
	{
		lock (Gate)
		{
			var entries = EnsureLoadedLocked();
			if (entries.RemoveAll(candidate => string.Equals(candidate.Path, path, StringComparison.Ordinal)) > 0)
			{
				SaveLocked(entries);
			}
		}
	}

	/// <summary>
	/// 父会话文件已经不存在、目录还在的副本。默认要求创建超过一天，避免刚归档的会话马上被清掉。
	/// 只给清理入口用，不自动删。
	/// </summary>
	public static IReadOnlyList<WorktreeEntry> Orphans(TimeSpan? olderThan = null)
	{
		lock (Gate)
		{
			var entries = EnsureLoadedLocked();
			var threshold = DateTimeOffset.UtcNow.Subtract(olderThan ?? TimeSpan.FromDays(1)).ToUnixTimeMilliseconds();
			var orphans = new List<WorktreeEntry>();
			foreach (var entry in entries)
			{
				if (entry.CreatedAt > threshold || !Directory.Exists(entry.Path))
				{
					continue;
				}

				if (entry.ParentSessionFile.Length > 0 && File.Exists(entry.ParentSessionFile))
				{
					continue;
				}

				orphans.Add(entry);
			}

			return orphans;
		}
	}

	private static List<WorktreeEntry> EnsureLoadedLocked()
	{
		if (_entries is not null)
		{
			return _entries;
		}

		_entries = [];
		try
		{
			var path = FilePath;
			if (!File.Exists(path))
			{
				return _entries;
			}

			if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array)
			{
				return _entries;
			}

			foreach (var item in array)
			{
				if (item is not JsonObject data)
				{
					continue;
				}

				var worktreePath = Text(data["path"]);
				if (worktreePath is not { Length: > 0 })
				{
					continue;
				}

				_entries.Add(new WorktreeEntry(
					Text(data["repository"]) ?? string.Empty,
					worktreePath,
					Text(data["branch"]) ?? string.Empty,
					Text(data["parentSessionFile"]) ?? string.Empty,
					Text(data["childName"]) ?? string.Empty,
					Number(data["createdAt"])));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			_entries = [];
		}

		return _entries;
	}

	private static void SaveLocked(List<WorktreeEntry> entries)
	{
		try
		{
			var path = FilePath;
			var directory = System.IO.Path.GetDirectoryName(path);
			if (directory is { Length: > 0 })
			{
				Directory.CreateDirectory(directory);
			}

			var array = new JsonArray([.. entries.Select(static entry => (JsonNode)new JsonObject
			{
				["repository"] = entry.Repository,
				["path"] = entry.Path,
				["branch"] = entry.Branch,
				["parentSessionFile"] = entry.ParentSessionFile,
				["childName"] = entry.ChildName,
				["createdAt"] = entry.CreatedAt,
			})]);
			var temp = path + ".tmp";
			File.WriteAllText(temp, array.ToJsonString(PiJson.NodeOptions));
			File.Move(temp, path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 登记表只是清理入口的索引，写不进去不影响副本本身。
		}
	}

	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static long Number(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}
