using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Compaction;

/// <summary>压缩在会话记录上的切分位置。</summary>
public sealed record CompactionPlan(int FirstKeptEntryIndex, int TurnStartIndex, bool IsSplitTurn);

/// <summary>一次压缩运行的结果。</summary>
public sealed record CompactionResult
{
	public required string FirstKeptEntryId { get; init; }

	public long TokensBefore { get; init; }

	public required string Summary { get; init; }

	public Usage Usage { get; init; } = new();

	public string? Details { get; init; }
}

/// <summary>
/// 上下文压缩：把较旧的条目摘要成结构化检查点，并记为一条 <c>compaction</c> 会话条目，
/// 之后由上下文构建器回放。对应 pi 的 <c>compaction.ts</c>。
/// </summary>
public static class Compactor
{
	/// <summary>找出切分位置，使大约 <paramref name="keepRecentTokens"/> 个令牌保持原样。</summary>
	public static CompactionPlan? FindCutPoint(
		IReadOnlyList<SessionEntry> entries,
		int startIndex,
		int endIndex,
		long keepRecentTokens)
	{
		if (startIndex >= endIndex)
		{
			return null;
		}

		var cutPoints = new List<int>();
		for (var i = startIndex; i < endIndex; i++)
		{
			if (entries[i] is CompactionEntry)
			{
				continue;
			}

			if (MessagesFor(entries[i]).Any(IsCutPointMessage))
			{
				cutPoints.Add(i);
			}
		}

		if (cutPoints.Count == 0)
		{
			return new CompactionPlan(startIndex, -1, false);
		}

		long accumulated = 0;
		var cutIndex = cutPoints[0];

		for (var i = endIndex - 1; i >= startIndex; i--)
		{
			var entryTokens = MessagesFor(entries[i]).Sum(ContextTokens.EstimateTokens);
			if (entryTokens == 0)
			{
				continue;
			}

			accumulated += entryTokens;
			if (accumulated < keepRecentTokens)
			{
				continue;
			}

			// 优先取该条目处或其之后最近的合法切点；若末尾的工具结果本身就超预算，
			// 则改为保留它们前面那次助手的工具调用。
			var candidate = cutPoints.FirstOrDefault(point => point >= i, -1);
			cutIndex = candidate >= 0 ? candidate : cutPoints[^1];
			break;
		}

		// 把紧邻的、对上下文没有贡献的条目一并纳入。
		while (cutIndex > startIndex)
		{
			var previous = entries[cutIndex - 1];
			if (previous is CompactionEntry || MessagesFor(previous).Count > 0)
			{
				break;
			}

			cutIndex--;
		}

		var startsTurn = MessagesFor(entries[cutIndex]).Any(IsTurnStartMessage);
		var turnStartIndex = startsTurn ? -1 : FindTurnStartIndex(entries, cutIndex, startIndex);

		return new CompactionPlan(cutIndex, turnStartIndex, !startsTurn && turnStartIndex != -1);
	}

	/// <summary>包含 <paramref name="entryIndex"/> 的那个轮次的起始条目下标，找不到为 -1。</summary>
	public static int FindTurnStartIndex(IReadOnlyList<SessionEntry> entries, int entryIndex, int startIndex)
	{
		for (var i = entryIndex; i >= startIndex; i--)
		{
			if (MessagesFor(entries[i]).Any(IsTurnStartMessage))
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// 就地压缩会话：摘要切点之前的全部内容，并追加一条 <c>compaction</c> 条目。
	/// 没有值得压缩的内容时返回 <c>null</c>。
	/// </summary>
	public static async Task<CompactionResult?> CompactAsync(
		SessionManager session,
		Model model,
		StreamFn streamFn,
		StreamOptions options,
		CompactionSettings settings,
		string? customInstructions = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(streamFn);

		var entries = session.BuildContextEntries();

		// 从前一次压缩之后开始摘要（如果存在）。
		var startIndex = 0;
		string? previousSummary = null;
		for (var i = 0; i < entries.Count; i++)
		{
			if (entries[i] is CompactionEntry previous)
			{
				previousSummary = previous.Summary;
				startIndex = i + 1;
			}
		}

		// 开头的 system 消息就是提示词，由压缩检查点直接携带，从不参与摘要。
		while (startIndex < entries.Count && entries[startIndex] is SessionMessageEntry { Message: SystemMessage })
		{
			startIndex++;
		}

		var plan = FindCutPoint(entries, startIndex, entries.Count, settings.KeepRecentTokens);
		if (plan is null || plan.FirstKeptEntryIndex <= startIndex)
		{
			return null;
		}

		var historyEnd = plan.IsSplitTurn && plan.TurnStartIndex > startIndex
			? plan.TurnStartIndex
			: plan.FirstKeptEntryIndex;

		var historyMessages = MessagesBetween(entries, startIndex, historyEnd);
		var turnPrefixMessages = plan.IsSplitTurn
			? MessagesBetween(entries, historyEnd, plan.FirstKeptEntryIndex)
			: [];

		var tokensBefore = ContextTokens.EstimateContextTokens(session.BuildProjectedMessages()).Tokens;

		var history = await GenerateSummaryAsync(
			model,
			streamFn,
			options,
			settings,
			historyMessages,
			customInstructions,
			previousSummary,
			cancellationToken).ConfigureAwait(false);

		var summary = history.Text;
		var usage = history.Usage;

		if (turnPrefixMessages.Count > 0)
		{
			var turnPrefix = await GenerateSummaryAsync(
				model,
				streamFn,
				options,
				settings,
				turnPrefixMessages,
				customInstructions: null,
				previousSummary: null,
				cancellationToken,
				CompactionPrompts.TurnPrefixSummarizationPrompt).ConfigureAwait(false);

			summary = $"{summary}\n\n---\n\n**Turn Context (split turn):**\n\n{turnPrefix.Text}";
			usage = CombineUsage(usage, turnPrefix.Usage);
		}

		var firstKeptEntryId = entries[plan.FirstKeptEntryIndex].Id;
		var checkpoint = LastSystemMessage(session);

		var details = ConversationSerializer.CollectFileOperations(historyMessages.Concat(turnPrefixMessages));
		var detailsText = ConversationSerializer.FormatFileOperations(
			details.ReadFiles,
			details.ModifiedFiles);

		session.AppendCompaction(
			summary,
			firstKeptEntryId,
			tokensBefore,
			checkpoint,
			usage,
			detailsText.Length > 0 ? new System.Text.Json.Nodes.JsonObject { ["fileOperations"] = detailsText } : null);

		return new CompactionResult
		{
			FirstKeptEntryId = firstKeptEntryId,
			TokensBefore = tokensBefore,
			Summary = summary,
			Usage = usage,
			Details = detailsText,
		};
	}

	/// <summary>用一次专门的模型调用为一段消息生成结构化摘要。</summary>
	public static async Task<(string Text, Usage Usage)> GenerateSummaryAsync(
		Model model,
		StreamFn streamFn,
		StreamOptions options,
		CompactionSettings settings,
		IReadOnlyList<Message> messages,
		string? customInstructions,
		string? previousSummary,
		CancellationToken cancellationToken,
		string? basePromptOverride = null)
	{
		var maxTokens = Math.Min(
			(int)(0.8 * settings.ReserveTokens),
			model.MaxTokens > 0 ? model.MaxTokens : int.MaxValue);

		var basePrompt = basePromptOverride
			?? (previousSummary is null ? CompactionPrompts.SummarizationPrompt : CompactionPrompts.UpdateSummarizationPrompt);

		if (customInstructions is { Length: > 0 } instructions)
		{
			basePrompt = $"{basePrompt}\n\nAdditional focus: {instructions}";
		}

		var conversationText = ConversationSerializer.Serialize(messages);
		var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n";
		if (previousSummary is { Length: > 0 })
		{
			promptText += $"<previous-summary>\n{previousSummary}\n</previous-summary>\n\n";
		}

		promptText += basePrompt;

		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var promptMessages = new List<Message>
		{
			new SystemMessage
			{
				Content = MessageContent.FromText(CompactionPrompts.SummarizationSystemPrompt),
				Timestamp = now,
			},
			new UserMessage
			{
				Content = MessageContent.FromText(promptText),
				Timestamp = now,
			},
		};

		var stream = streamFn(
			model,
			promptMessages,
			options with { MaxTokens = Math.Max(1, maxTokens), ThinkingLevel = ThinkingLevels.Off });

		await foreach (var _ in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			// 消费完整个流；结果在终止事件里。
		}

		var response = await stream.Result.ConfigureAwait(false);
		var failure = SummarizationFailure(response);
		if (failure is not null)
		{
			throw new InvalidOperationException(failure);
		}

		var text = string.Concat(response.Content.OfType<TextContent>().Select(static block => block.Text));
		if (text.Length == 0)
		{
			throw new InvalidOperationException("Summarization produced no text");
		}

		return (text, response.Usage);
	}

	/// <summary>摘要响应不可用的原因；正常时为 <c>null</c>。</summary>
	public static string? SummarizationFailure(AssistantMessage response)
	{
		if (response.StopReason == StopReason.Aborted)
		{
			return "Summarization was aborted";
		}

		if (response.StopReason == StopReason.Error)
		{
			return $"Summarization failed: {response.ErrorMessage ?? "unknown error"}";
		}

		if (response.StopReason == StopReason.Length)
		{
			return "Summarization failed: generation hit the token cap and the summary is incomplete";
		}

		if (response.Content.OfType<ToolCall>().Any())
		{
			return "Summarization attempted to call a tool";
		}

		return null;
	}

	private static Usage CombineUsage(Usage first, Usage second) => new()
	{
		Input = first.Input + second.Input,
		Output = first.Output + second.Output,
		CacheRead = first.CacheRead + second.CacheRead,
		CacheWrite = first.CacheWrite + second.CacheWrite,
		TotalTokens = first.TotalTokens + second.TotalTokens,
		Cost = new UsageCost
		{
			Input = first.Cost.Input + second.Cost.Input,
			Output = first.Cost.Output + second.Cost.Output,
			CacheRead = first.Cost.CacheRead + second.Cost.CacheRead,
			CacheWrite = first.Cost.CacheWrite + second.Cost.CacheWrite,
			Total = first.Cost.Total + second.Cost.Total,
		},
	};

	private static SystemMessage? LastSystemMessage(SessionManager session)
	{
		SystemMessage? checkpoint = null;
		foreach (var entry in session.GetBranch())
		{
			if (entry is SessionMessageEntry { Message: SystemMessage system })
			{
				checkpoint = system;
			}
		}

		return checkpoint;
	}

	private static List<Message> MessagesBetween(IReadOnlyList<SessionEntry> entries, int start, int end)
	{
		var messages = new List<Message>();
		for (var i = start; i < end; i++)
		{
			messages.AddRange(MessagesFor(entries[i]));
		}

		return messages;
	}

	private static IReadOnlyList<Message> MessagesFor(SessionEntry entry) => SessionManager.MessagesForEntry(entry);

	private static bool IsCutPointMessage(Message message) => message switch
	{
		UserMessage or AssistantMessage => true,
		CustomMessage custom => CodingMessages.IsConvertibleRole(custom.CustomRole),
		_ => false,
	};

	private static bool IsTurnStartMessage(Message message) => message switch
	{
		UserMessage => true,
		CustomMessage custom => custom.CustomRole is "bashExecution" or "custom" or "branchSummary" or "compactionSummary",
		_ => false,
	};
}
