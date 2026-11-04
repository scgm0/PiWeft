using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Compaction;

public sealed record BranchSummaryResult
{
	public required string Summary { get; init; }

	public Usage Usage { get; init; } = new();

	public IReadOnlyList<string> ReadFiles { get; init; } = [];

	public IReadOnlyList<string> ModifiedFiles { get; init; } = [];

	public int SummarizedEntries { get; init; }
}

/// 树导航时的分支摘要：叶子移动到别处时，被放弃的那条路径会被摘要，
/// 使其上下文不至于丢失。对应 pi 的 <c>branch-summarization.ts</c>。
public static class BranchSummarizer
{
	public const string BranchSummaryPrompt = """
		Create a structured summary of this conversation branch for context when returning later.

		Use this EXACT format:

		## Goal
		[What was the user trying to accomplish in this branch?]

		## Constraints & Preferences
		- [Any constraints, preferences, or requirements mentioned]
		- [Or "(none)" if none were mentioned]

		## Progress
		### Done
		- [x] [Completed tasks/changes]

		### In Progress
		- [ ] [Work that was started but not finished]

		### Blocked
		- [Issues preventing progress, if any]

		## Key Decisions
		- **[Decision]**: [Brief rationale]

		## Next Steps
		1. [What should happen next to continue this work]

		Keep each section concise. Preserve exact file paths, function names, and error messages.
		""";

	/// 从 <paramref name="fromId"/> 回溯到它与 <paramref name="targetId"/> 的共同祖先之间的条目，
	/// 按时间顺序排列。这些就是导航将要放弃的条目。
	public static IReadOnlyList<SessionEntry> CollectAbandonedEntries(
		SessionManager session,
		string fromId,
		string targetId)
	{
		var fromPath = session.GetBranch(fromId);
		var targetPath = session.GetBranch(targetId);

		var targetIds = new HashSet<string>(targetPath.Select(static entry => entry.Id), StringComparer.Ordinal);

		// 沿被放弃的路径上行，直到仍位于目标路径上的最深条目。
		var commonAncestorIndex = -1;
		for (var i = fromPath.Count - 1; i >= 0; i--)
		{
			if (targetIds.Contains(fromPath[i].Id))
			{
				commonAncestorIndex = i;
				break;
			}
		}

		var abandoned = new List<SessionEntry>();
		for (var i = commonAncestorIndex + 1; i < fromPath.Count; i++)
		{
			abandoned.Add(fromPath[i]);
		}

		return abandoned;
	}

	/// 摘要被放弃的分支，把叶子移动到 <paramref name="targetId"/>，并在那里追加一条
	/// <c>branch_summary</c> 条目。没有可摘要内容时返回 <c>null</c>。
	public static async Task<BranchSummaryResult?> NavigateWithSummaryAsync(
		SessionManager session,
		Model model,
		StreamFn streamFn,
		StreamOptions options,
		CompactionSettings settings,
		BranchSummarySettings branchSettings,
		string targetId,
		string? customInstructions = null,
		CancellationToken cancellationToken = default,
		string? thinkingLevel = null)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(model);

		var leafId = session.LeafId;
		if (leafId is null || string.Equals(leafId, targetId, StringComparison.Ordinal))
		{
			session.Branch(targetId);
			return null;
		}

		var abandoned = CollectAbandonedEntries(session, leafId, targetId);
		if (abandoned.Count == 0)
		{
			session.Branch(targetId);
			return null;
		}

		if (branchSettings.SkipPrompt)
		{
			return null;
		}

		var messages = SelectWithinBudget(abandoned, model.ContextWindow, branchSettings.ReserveTokens);

		var instructions = customInstructions is { Length: > 0 } focus
			? $"{BranchSummaryPrompt}\n\nAdditional focus: {focus}"
			: BranchSummaryPrompt;

		var (summary, usage) = await Compactor.GenerateSummaryAsync(
			model,
			streamFn,
			options,
			settings,
			messages,
			customInstructions: null,
			previousSummary: null,
			cancellationToken,
			basePromptOverride: instructions,
			thinkingLevel: thinkingLevel).ConfigureAwait(false);

		var fileOperations = CollectFileOperations(abandoned, messages);
		var details = new JsonObject
		{
			["readFiles"] = new JsonArray([.. fileOperations.ReadFiles.OrderBy(static f => f, StringComparer.Ordinal)
				.Select(static f => (JsonNode?)JsonValue.Create(f))]),
			["modifiedFiles"] = new JsonArray([.. fileOperations.ModifiedFiles.OrderBy(static f => f, StringComparer.Ordinal)
				.Select(static f => (JsonNode?)JsonValue.Create(f))]),
		};

		session.Branch(targetId);
		session.AppendBranchSummary(leafId, summary, usage, details);

		return new BranchSummaryResult
		{
			Summary = summary,
			Usage = usage,
			ReadFiles = [.. fileOperations.ReadFiles],
			ModifiedFiles = [.. fileOperations.ModifiedFiles],
			SummarizedEntries = abandoned.Count,
		};
	}

	public static async Task<BranchSummaryResult?> SummarizeBranchAsync(
		SessionManager session,
		Model model,
		StreamFn streamFn,
		StreamOptions options,
		CompactionSettings settings,
		BranchSummarySettings branchSettings,
		string targetId,
		string? customInstructions = null,
		CancellationToken cancellationToken = default,
		string? thinkingLevel = null)
	{
		var leafId = session.LeafId;
		if (leafId is null || string.Equals(leafId, targetId, StringComparison.Ordinal))
		{
			return null;
		}

		var abandoned = CollectAbandonedEntries(session, leafId, targetId);
		if (abandoned.Count == 0)
		{
			return null;
		}

		if (branchSettings.SkipPrompt)
		{
			return null;
		}

		var messages = SelectWithinBudget(abandoned, model.ContextWindow, branchSettings.ReserveTokens);

		var instructions = customInstructions is { Length: > 0 } focus
			? $"{BranchSummaryPrompt}\n\nAdditional focus: {focus}"
			: BranchSummaryPrompt;

		var (summary, usage) = await Compactor.GenerateSummaryAsync(
			model,
			streamFn,
			options,
			settings,
			messages,
			customInstructions: null,
			previousSummary: null,
			cancellationToken,
			basePromptOverride: instructions,
			thinkingLevel: thinkingLevel).ConfigureAwait(false);

		var fileOperations = CollectFileOperations(abandoned, messages);

		return new BranchSummaryResult
		{
			Summary = summary,
			Usage = usage,
			ReadFiles = [.. fileOperations.ReadFiles],
			ModifiedFiles = [.. fileOperations.ModifiedFiles],
			SummarizedEntries = abandoned.Count,
		};
	}

	private static List<Message> SelectWithinBudget(
		IReadOnlyList<SessionEntry> abandoned,
		int contextWindow,
		int reserveTokens)
	{
		var budget = contextWindow > 0 ? contextWindow - Math.Max(0, reserveTokens) : 0;

		// 与 pi 的 prepareBranchEntries 一致：工具结果不进分支摘要（内容已在助手的工具调用里），
		// 被 context_edit 隐藏的尝试不重新出现，替换过的内容按替换后的版本摘要。
		var edits = SessionManager.LatestContextEdits(abandoned);
		var candidates = new List<(int Index, List<Message> Messages)>(abandoned.Count);
		for (var index = 0; index < abandoned.Count; index++)
		{
			var entry = abandoned[index];
			if (entry is SessionMessageEntry { Message: ToolResultMessage })
			{
				continue;
			}

			var entryMessages = SessionManager.ProjectEntry(entry, edits.GetValueOrDefault(entry.Id)).ToList();
			if (entryMessages.Count > 0)
			{
				candidates.Add((index, entryMessages));
			}
		}

		if (budget <= 0)
		{
			return [.. candidates.SelectMany(static candidate => candidate.Messages)];
		}

		var selected = new List<(int Index, List<Message> Messages)>();
		long tokens = 0;
		for (var index = candidates.Count - 1; index >= 0; index--)
		{
			var entryMessages = candidates[index].Messages;
			var entryTokens = entryMessages.Sum(ContextTokens.EstimateTokens);
			if (selected.Count > 0 && tokens + entryTokens > budget)
			{
				break;
			}

			selected.Add(candidates[index]);
			tokens += entryTokens;
		}

		selected.Reverse();
		var result = new List<Message>();
		foreach (var (_, entryMessages) in selected)
		{
			result.AddRange(entryMessages);
		}

		return result;
	}

	/// 文件清单除了本次摘要的消息，还要带上被放弃路径里嵌套的分支摘要（与 pi 的 prepareBranchEntries 一致）。
	private static ConversationSerializer.FileOperations CollectFileOperations(
		IReadOnlyList<SessionEntry> abandoned,
		IReadOnlyList<Message> messages)
	{
		var operations = ConversationSerializer.CollectFileOperations(messages);
		foreach (var entry in abandoned)
		{
			// pi 的条件是「非扩展生成」（fromHook 缺省与 false 都算），只有细节对象存在时才合并。
			if (entry is not BranchSummaryEntry { Details: JsonObject details } summary || summary.FromHook == true)
			{
				continue;
			}

			AddPaths(details["readFiles"], operations.ReadFiles);
			AddPaths(details["modifiedFiles"], operations.ModifiedFiles);
		}

		return operations;
	}

	private static void AddPaths(JsonNode? node, HashSet<string> target)
	{
		if (node is not JsonArray array)
		{
			return;
		}

		foreach (var item in array)
		{
			if (item?.GetValueKind() == System.Text.Json.JsonValueKind.String)
			{
				target.Add(item.GetValue<string>());
			}
		}
	}
}
