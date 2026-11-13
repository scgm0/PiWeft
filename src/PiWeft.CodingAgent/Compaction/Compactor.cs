using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Compaction;

public sealed record CompactionPlan(int FirstKeptEntryIndex, int TurnStartIndex, bool IsSplitTurn);

public sealed record CompactionResult
{
	public required string FirstKeptEntryId { get; init; }

	public long TokensBefore { get; init; }

	public required string Summary { get; init; }

	public Usage Usage { get; init; } = new();

	public JsonObject? Details { get; init; }
}

/// 上下文压缩：把较旧的条目摘要成结构化检查点，并记为一条 <c>compaction</c> 会话条目，
/// 之后由上下文构建器回放。对应 pi 的 <c>compaction.ts</c>。
public static class Compactor
{
	private readonly record struct CutPointEvaluation(
		CompactionPlan? Plan,
		IReadOnlyList<Message>[] Projected,
		int StartIndex);

	public static CompactionPlan? FindCutPoint(
		IReadOnlyList<SessionEntry> entries,
		int startIndex,
		int endIndex,
		long keepRecentTokens,
		IReadOnlyDictionary<string, ContextEditEntry>? edits = null) =>
		EvaluateCutPoint(entries, startIndex, endIndex, keepRecentTokens, edits).Plan;

	/// 一次投影算完切点、回合起点与该条目是否贡献上下文：旧实现按条目反复
	/// <see cref="MessagesFor"/> 并各走一遍 LINQ，大会话在压缩时会多分配数万个小列表。
	private static CutPointEvaluation EvaluateCutPoint(
		IReadOnlyList<SessionEntry> entries,
		int startIndex,
		int endIndex,
		long keepRecentTokens,
		IReadOnlyDictionary<string, ContextEditEntry>? edits)
	{
		if (startIndex >= endIndex)
		{
			return new CutPointEvaluation(null, [], startIndex);
		}

		var count = endIndex - startIndex;
		var projected = new IReadOnlyList<Message>[count];
		var cutPoints = new List<int>();
		var blocksCut = new bool[count];
		var turnStarts = new bool[count];

		// entries 可能是 ImmutableList（压缩前没有检查点），逐项 foreach 避免 O(log n) 索引。
		var position = 0;
		foreach (var entry in entries)
		{
			if (position >= endIndex)
			{
				break;
			}

			if (position < startIndex)
			{
				position++;
				continue;
			}

			var offset = position - startIndex;
			position++;
			var messages = MessagesFor(entry, edits);
			projected[offset] = messages;

			if (entry is CompactionEntry)
			{
				blocksCut[offset] = true;
				continue;
			}

			var cutPoint = false;
			var turnStart = false;
			foreach (var message in messages)
			{
				cutPoint |= IsCutPointMessage(message);
				turnStart |= IsTurnStartMessage(message);
				if (cutPoint && turnStart)
				{
					break;
				}
			}

			if (cutPoint)
			{
				cutPoints.Add(startIndex + offset);
			}

			blocksCut[offset] = messages.Count > 0;
			turnStarts[offset] = turnStart;
		}

		if (cutPoints.Count == 0)
		{
			return new CutPointEvaluation(new CompactionPlan(startIndex, -1, false), projected, startIndex);
		}

		long accumulated = 0;
		var cutIndex = cutPoints[0];
		for (var i = endIndex - 1; i >= startIndex; i--)
		{
			var messages = projected[i - startIndex];
			long entryTokens = 0;
			foreach (var message in messages)
			{
				entryTokens += ContextTokens.EstimateTokens(message);
			}

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
			var cutPosition = cutPoints.BinarySearch(i);
			cutIndex = cutPosition >= 0
				? cutPoints[cutPosition]
				: ~cutPosition < cutPoints.Count ? cutPoints[~cutPosition] : cutPoints[^1];
			break;
		}

		// 把紧邻的、对上下文没有贡献的条目一并纳入。
		while (cutIndex > startIndex && !blocksCut[cutIndex - 1 - startIndex])
		{
			cutIndex--;
		}

		var startsTurn = turnStarts[cutIndex - startIndex];
		var turnStartIndex = -1;
		if (!startsTurn)
		{
			for (var i = cutIndex - 1; i >= startIndex; i--)
			{
				if (turnStarts[i - startIndex])
				{
					turnStartIndex = i;
					break;
				}
			}
		}

		var plan = new CompactionPlan(cutIndex, turnStartIndex, !startsTurn && turnStartIndex != -1);
		return new CutPointEvaluation(plan, projected, startIndex);
	}

	/// 就地压缩会话：摘要切点之前的全部内容，并追加一条 <c>compaction</c> 条目。
	/// 没有值得压缩的内容时返回 <c>null</c>。
	/// <param name="currentSystemMessage">当前运行时提示词；为 <c>null</c> 时从分支里的 system 消息合并。</param>
	/// <param name="tokensBefore">调用方已算好的压缩前上下文 token；省略时现场估算。</param>
	public static async Task<CompactionResult?> CompactAsync(
		SessionManager session,
		Model model,
		StreamFn streamFn,
		StreamOptions options,
		CompactionSettings settings,
		string? customInstructions = null,
		CancellationToken cancellationToken = default,
		string? thinkingLevel = null,
		SystemMessage? currentSystemMessage = null,
		long? tokensBefore = null)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(streamFn);

		var entries = session.BuildContextEntries();

		// 从前一次压缩之后开始摘要（如果存在）。BuildContextEntries 把最新检查点放在最前，
		// 与 pi 的 findIndex 一致，取第一个就是最新的那个。
		var startIndex = 0;
		string? previousSummary = null;
		CompactionEntry? previousCompaction = null;
		var entryIndex = 0;
		foreach (var entry in entries)
		{
			if (entry is CompactionEntry previous)
			{
				previousSummary = previous.Summary;
				previousCompaction = previous;
				startIndex = entryIndex + 1;
				break;
			}

			entryIndex++;
		}

		// 开头的 system 消息就是提示词，由压缩检查点直接携带，从不参与摘要。
		while (startIndex < entries.Count && entries[startIndex] is SessionMessageEntry { Message: SystemMessage })
		{
			startIndex++;
		}

		// 与实时上下文相同的投影规则：context_edit 隐藏的尝试不计 token、不进入摘要，
		// 内容替换按替换后的版本摘要（参照 pi 的 buildSessionProjection）。
		var edits = SessionManager.LatestContextEdits(entries);
		var evaluation = EvaluateCutPoint(entries, startIndex, entries.Count, settings.KeepRecentTokens, edits);
		var plan = evaluation.Plan;
		if (plan is null || plan.FirstKeptEntryIndex <= startIndex)
		{
			return null;
		}

		// 与 pi 一致：拆分回合时历史到回合起点为止，起点之前的消息全归入 turn prefix，
		// 即使那一段刚好从切点边界开始（此时历史为空，用上一次摘要充当）。
		var historyEnd = plan.IsSplitTurn
			? plan.TurnStartIndex
			: plan.FirstKeptEntryIndex;

		var historyMessages = FlattenProjected(evaluation.Projected, startIndex, historyEnd, evaluation.StartIndex);
		var turnPrefixMessages = plan.IsSplitTurn
			? FlattenProjected(evaluation.Projected, historyEnd, plan.FirstKeptEntryIndex, evaluation.StartIndex)
			: [];

		// 调用方（会话）已有增量估算缓存，直接传进来，避免压缩前再整段估算一次。
		var estimatedTokensBefore = tokensBefore ?? ContextTokens.EstimateContextTokens(session.BuildProjectedMessages()).Tokens;

		string summary;
		Usage usage;
		if (plan.IsSplitTurn && turnPrefixMessages.Count > 0)
		{
			// 切点之前没有新的可摘要内容时，用上一次摘要当作历史；pi 同样不为空历史发请求。
			string historyText;
			Usage historyUsage = new();
			var summarizedHistory = historyMessages.Count > 0;
			if (summarizedHistory)
			{
				var history = await GenerateSummaryAsync(
					model,
					streamFn,
					options,
					settings,
					historyMessages,
					customInstructions,
					previousSummary,
					cancellationToken,
					thinkingLevel: thinkingLevel).ConfigureAwait(false);
				historyText = history.Text;
				historyUsage = history.Usage;
			}
			else
			{
				historyText = previousSummary ?? "No prior history.";
			}

			var turnPrefix = await GenerateSummaryAsync(
				model,
				streamFn,
				options,
				settings,
				turnPrefixMessages,
				customInstructions,
				previousSummary: null,
				cancellationToken,
				CompactionPrompts.TurnPrefixSummarizationPrompt,
				thinkingLevel).ConfigureAwait(false);

			summary = $"{historyText}\n\n---\n\n**Turn Context (split turn):**\n\n{turnPrefix.Text}";
			usage = summarizedHistory ? CombineUsage(historyUsage, turnPrefix.Usage) : turnPrefix.Usage;
		}
		else
		{
			if (historyMessages.Count == 0)
			{
				return null;
			}

			var history = await GenerateSummaryAsync(
				model,
				streamFn,
				options,
				settings,
				historyMessages,
				customInstructions,
				previousSummary,
				cancellationToken,
				thinkingLevel: thinkingLevel).ConfigureAwait(false);
			summary = history.Text;
			usage = history.Usage;
		}

		var firstKeptEntryId = entries[plan.FirstKeptEntryIndex].Id;
		var checkpoint = currentSystemMessage ?? MergeSystemMessages(session);

		// 文件清单跨压缩累积：上一次压缩读/过的文件仍然影响后续继续工作。
		var fileOperations = ConversationSerializer.CollectFileOperations(historyMessages.Concat(turnPrefixMessages));
		MergePreviousFileOperations(previousCompaction, fileOperations);
		var (readFiles, modifiedFiles) = ConversationSerializer.ComputeFileLists(fileOperations);
		summary += ConversationSerializer.FormatFileOperations(readFiles, modifiedFiles);

		var detailsNode = new JsonObject
		{
			["readFiles"] = new JsonArray([.. readFiles.Select(static f => (JsonNode?)JsonValue.Create(f))]),
			["modifiedFiles"] = new JsonArray([.. modifiedFiles.Select(static f => (JsonNode?)JsonValue.Create(f))]),
		};

		session.AppendCompaction(
			summary,
			firstKeptEntryId,
			estimatedTokensBefore,
			checkpoint,
			usage,
			detailsNode);

		return new CompactionResult
		{
			FirstKeptEntryId = firstKeptEntryId,
			TokensBefore = estimatedTokensBefore,
			Summary = summary,
			Usage = usage,
			Details = detailsNode,
		};
	}

	public static async Task<(string Text, Usage Usage)> GenerateSummaryAsync(
		Model model,
		StreamFn streamFn,
		StreamOptions options,
		CompactionSettings settings,
		IReadOnlyList<Message> messages,
		string? customInstructions,
		string? previousSummary,
		CancellationToken cancellationToken,
		string? basePromptOverride = null,
		string? thinkingLevel = null)
	{
		// 与 pi 现行行为一致：摘要请求使用模型自身的输出上限。
		// compaction.reserveTokens 只决定「何时开始压缩」，不再限制摘要长度；
		// 固定的小上限会让长思考的模型稳定撞上 token cap（上游已修）。
		var basePrompt = basePromptOverride
			?? (previousSummary is null ? CompactionPrompts.SummarizationPrompt : CompactionPrompts.UpdateSummarizationPrompt);

		if (customInstructions is { Length: > 0 } instructions)
		{
			basePrompt = $"{basePrompt}\n\nAdditional focus: {instructions}";
		}

		var conversationText = ConversationSerializer.Serialize(messages);
		var promptBuilder = new StringBuilder(
			conversationText.Length + (previousSummary?.Length ?? 0) + basePrompt.Length + 64);
		promptBuilder.Append("<conversation>\n").Append(conversationText).Append("\n</conversation>\n\n");
		if (previousSummary is { Length: > 0 })
		{
			promptBuilder.Append("<previous-summary>\n").Append(previousSummary).Append("\n</previous-summary>\n\n");
		}

		promptBuilder.Append(basePrompt);
		var promptText = promptBuilder.ToString();

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
			options with
			{
				MaxTokens = model.MaxTokens > 0 ? model.MaxTokens : null,
				// 摘要是单次请求，写缓存前缀不划算，还会挤掉正在用的会话缓存（pi 同义）。
				CacheRetention = CacheRetentions.None,
				// 与 pi 的 createSummarizationOptions 一致：推理模型沿用会话的思考级别（off 时仍是 off）。
				// 以前固定 off 会让支持思考的模型只拿 effort=none，某些模型直接以 400 拒绝摘要请求。
				ThinkingLevel = model.Reasoning ? thinkingLevel ?? ThinkingLevels.Off : ThinkingLevels.Off,
			});

		await foreach (var _ in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			// 消费完整个流；结果在终止事件里。
		}

		var response = await stream.Result.ConfigureAwait(false);
		var failure = SummarizationFailure(response);
		if (failure is not null)
		{
			// 摘要请求本身也可能超过 gateway 的 prefill 容量（旧大会话常见）。
			// 按消息 token 对半拆开，先摘要前半段，再用前一份摘要作为 previousSummary
			// 摘要后半段；递归直到单个请求能被接受。
			if (messages.Count > 1 && ContextOverflow.IsOverflowText(response.ErrorMessage))
			{
				var split = FindSummarySplit(messages);
				if (split > 0 && split < messages.Count)
				{
					var firstMessages = messages.Take(split).ToArray();
					var secondMessages = messages.Skip(split).ToArray();
					var (firstText, firstUsage) = await GenerateSummaryAsync(
						model,
						streamFn,
						options,
						settings,
						firstMessages,
						customInstructions,
						previousSummary,
						cancellationToken,
						basePromptOverride,
						thinkingLevel).ConfigureAwait(false);
					var (secondText, secondUsage) = await GenerateSummaryAsync(
						model,
						streamFn,
						options,
						settings,
						secondMessages,
						customInstructions,
						firstText,
						cancellationToken,
						basePromptOverride,
						thinkingLevel).ConfigureAwait(false);
					return (secondText, CombineUsage(firstUsage, secondUsage));
				}
			}

			throw new InvalidOperationException(failure);
		}

		var text = string.Concat(response.Content.OfType<TextContent>().Select(static block => block.Text));
		if (text.Length == 0)
		{
			throw new InvalidOperationException("Summarization produced no text");
		}

		return (text, response.Usage);
	}

	/// <summary>按估算 token 找到尽量对半的拆分点；两侧都至少保留一条消息。</summary>
	private static int FindSummarySplit(IReadOnlyList<Message> messages)
	{
		if (messages.Count <= 1)
		{
			return 0;
		}

		var estimates = new long[messages.Count];
		long total = 0;
		for (var i = 0; i < messages.Count; i++)
		{
			estimates[i] = Math.Max(1, ContextTokens.EstimateTokens(messages[i]));
			total += estimates[i];
		}

		var half = total / 2;
		long accumulated = 0;
		for (var i = 0; i < messages.Count - 1; i++)
		{
			accumulated += estimates[i];
			if (accumulated >= half)
			{
				return i + 1;
			}
		}

		return messages.Count / 2;
	}

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

	private static SystemMessage? MergeSystemMessages(SessionManager session) =>
		Transcript.GetCurrentSystemMessage(session.BuildProjectedMessages());

	private static void MergePreviousFileOperations(
		CompactionEntry? previous,
		ConversationSerializer.FileOperations operations)
	{
		if (previous is not { Details: JsonObject details } || previous.FromHook == true)
		{
			return;
		}

		AddPaths(details["readFiles"], operations.ReadFiles);
		AddPaths(details["modifiedFiles"], operations.ModifiedFiles);
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

	private static List<Message> FlattenProjected(
		IReadOnlyList<Message>[] projected,
		int start,
		int end,
		int origin)
	{
		var messages = new List<Message>(end - start);
		for (var i = start; i < end; i++)
		{
			var slice = projected[i - origin];
			if (slice.Count > 0)
			{
				messages.AddRange(slice);
			}
		}

		return messages;
	}

	private static IReadOnlyList<Message> MessagesFor(
		SessionEntry entry,
		IReadOnlyDictionary<string, ContextEditEntry>? edits = null) =>
		SessionManager.ProjectEntry(entry, edits?.GetValueOrDefault(entry.Id));

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
