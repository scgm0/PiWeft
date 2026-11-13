using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent;

/// 关联会话的关系与提议落盘（`<agentDir>/linked-sessions.json`）。
/// <para>
/// PiDeck 把这份数据存在 `<userData>/linked-sessions.json`；本端口放在 agent 目录，
/// 字段名与形状沿用 PiDeck 的 <c>shared/types/linkedSessions.ts</c>，便于两边对照排查。
/// 落盘只解决「重启后还能看到子会话与待决定提议」：进程内的父子联动本身不恢复，
/// 恢复出来的子会话一律是 <c>stopped</c>（没有活运行时，发消息会明确失败）。
/// </para>
/// <para>
/// 多个会话（父会话与子会话）会同时写这份文件：写路径按父会话 id 合并，
/// 静态锁保证同进程内不互相覆盖。文件损坏按空处理，不阻塞会话启动。
/// </para>
public sealed record LinkedSessionSnapshot(
	IReadOnlyList<LinkedSessionLink> Links,
	IReadOnlyList<LinkedSessionProposal> Proposals);

public static class LinkedSessionStore
{
	public const string FileName = "linked-sessions.json";

	public const int MaxBytes = 2 * 1024 * 1024;

	/// 提议的保留期：PiDeck 同值（7 天）。
	public static readonly TimeSpan ProposalTtl = TimeSpan.FromDays(7);

	private static readonly Lock Gate = new();

	public static string PathFor(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), FileName);

	public static LinkedSessionSnapshot Load(string? agentDir = null)
	{
		var path = PathFor(agentDir);
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length is 0 or > MaxBytes)
			{
				return new LinkedSessionSnapshot([], []);
			}

			return Parse(File.ReadAllText(path));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return new LinkedSessionSnapshot([], []);
		}
	}

	/// 用一个会话的关系覆盖文件里同一父会话的条目，其余会话原样保留。
	public static void Merge(string parentSessionId, string projectId, LinkedSessionSnapshot owned, string? agentDir = null)
	{
		lock (Gate)
		{
			var existing = Load(agentDir);
			var links = existing.Links
				.Where(link => !string.Equals(link.ParentSessionId, parentSessionId, StringComparison.Ordinal))
				.Concat(owned.Links)
				.ToList();
			var proposals = existing.Proposals
				.Where(proposal => !string.Equals(proposal.ParentSessionId, parentSessionId, StringComparison.Ordinal))
				.Concat(owned.Proposals)
				.ToList();

			Write(links, proposals, projectId, agentDir);
		}
	}

	private static void Write(
		IReadOnlyList<LinkedSessionLink> links,
		IReadOnlyList<LinkedSessionProposal> proposals,
		string projectId,
		string? agentDir)
	{
		var path = PathFor(agentDir);
		var root = new JsonObject
		{
			["links"] = new JsonArray([.. links.Select(link => (JsonNode)LinkNode(link, projectId))]),
			["proposals"] = new JsonArray([.. proposals.Select(proposal => (JsonNode)ProposalNode(proposal, projectId))]),
		};

		var directory = Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		var temp = path + ".tmp";
		File.WriteAllText(temp, root.ToJsonString(PiJson.NodeOptions));
		File.Move(temp, path, overwrite: true);
	}

	private static JsonObject LinkNode(LinkedSessionLink link, string projectId) => new()
	{
		["childSessionId"] = link.ChildSessionId,
		["parentSessionId"] = link.ParentSessionId,
		["projectId"] = projectId,
		["title"] = link.Title,
		["createdAt"] = link.CreatedAt,
		["state"] = link.State,
		["lastReport"] = link.LastReport,
		["lastReportAt"] = link.LastReportAt,
		["turnCount"] = link.TurnCount,
		["archived"] = false,
	};

	private static JsonObject ProposalNode(LinkedSessionProposal proposal, string projectId) => new()
	{
		["id"] = proposal.Id,
		["parentSessionId"] = proposal.ParentSessionId,
		["projectId"] = projectId,
		["title"] = proposal.Title,
		["prompt"] = proposal.Prompt,
		["initiation"] = proposal.Initiation,
		["status"] = proposal.Status,
		["createdAt"] = proposal.CreatedAt,
		["childSessionId"] = proposal.ChildSessionId,
	};

	internal static LinkedSessionSnapshot Parse(string text)
	{
		if (JsonNode.Parse(text) is not JsonObject root)
		{
			return new LinkedSessionSnapshot([], []);
		}

		var links = new List<LinkedSessionLink>();
		if (root["links"] is JsonArray rawLinks)
		{
			foreach (var raw in rawLinks)
			{
				if (raw is JsonObject item &&
					Text(item, "childSessionId") is { Length: > 0 } child &&
					Text(item, "parentSessionId") is { Length: > 0 } parent)
				{
					links.Add(new LinkedSessionLink(
						child,
						parent,
						Text(item, "title") ?? "子会话",
						// 重启后没有活运行时：恢复出来一律按 stopped，避免界面显示「运行中」。
						LinkedSessionLink.Stopped,
						(int)Number(item, "turnCount"),
						Text(item, "lastReport"),
						Number(item, "lastReportAt"),
						Number(item, "createdAt")));
				}
			}
		}

		var proposals = new List<LinkedSessionProposal>();
		if (root["proposals"] is JsonArray rawProposals)
		{
			var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			foreach (var raw in rawProposals)
			{
				if (raw is not JsonObject item ||
					Text(item, "id") is not { Length: > 0 } id ||
					Text(item, "parentSessionId") is not { Length: > 0 } parent)
				{
					continue;
				}

				var created = Number(item, "createdAt");
				var status = Text(item, "status") ?? LinkedSessionProposal.Pending;
				// 超过保留期的提议不再恢复：卡片早就不在界面上了，留着只会出现在列表里。
				if (now - created > ProposalTtl.TotalMilliseconds)
				{
					continue;
				}

				proposals.Add(new LinkedSessionProposal(
					id,
					parent,
					Text(item, "title") ?? "提议",
					Text(item, "prompt") ?? string.Empty,
					Text(item, "initiation") ?? LinkedSessionProposal.OwnInitiative,
					status,
					created,
					Text(item, "childSessionId")));
			}
		}

		return new LinkedSessionSnapshot(links, proposals);
	}

	private static string? Text(JsonObject item, string key) =>
		item[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
			? value.GetValue<string>()
			: null;

	private static long Number(JsonObject item, string key) =>
		item[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}
