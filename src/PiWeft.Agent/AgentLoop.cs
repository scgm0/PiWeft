using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Utils;

namespace PiWeft.Agent;

/// <summary>
/// 代理主循环。全程使用 <see cref="Message"/>，只在真正请求 provider 的边界上转换成对方要的形状。
/// 对应 pi 的 <c>agent-loop.ts</c>。
/// </summary>
public static class AgentLoop
{
	private const string TruncatedToolCallMessage =
		"Tool call was truncated by the output token limit and was not executed. Re-issue the call with complete arguments.";

	/// <summary>用新的提示消息启动一次运行：它们会被加入上下文并对外发出事件。</summary>
	public static async Task<IReadOnlyList<Message>> RunAsync(
		IReadOnlyList<Message> prompts,
		AgentContext context,
		AgentLoopConfig config,
		StreamFn streamFn,
		AgentEventSink emit,
		CancellationToken cancellationToken = default)
	{
		var initialMessages = DeclareToolChanges(context, prompts);

		var newMessages = new List<Message>(initialMessages);

		// 就地修改调用方的会话记录，让代理自身的状态能跨多次运行持续增长。
		var currentContext = context;
		currentContext.Messages.AddRange(initialMessages);

		await emit(new AgentStartEvent());
		await emit(new TurnStartEvent());
		foreach (var message in initialMessages)
		{
			await emit(new MessageStartEvent { Message = message });
			await emit(new MessageEndEvent { Message = message });
		}

		await RunLoopAsync(currentContext, newMessages, config, streamFn, emit, cancellationToken);
		return newMessages;
	}

	/// <summary>
	/// 从当前上下文继续一次运行，不新增消息。上下文的最后一条消息必须能转换成
	/// <c>user</c> 或 <c>toolResult</c>，否则 provider 会拒绝该请求。
	/// </summary>
	public static async Task<IReadOnlyList<Message>> ContinueAsync(
		AgentContext context,
		AgentLoopConfig config,
		StreamFn streamFn,
		AgentEventSink emit,
		CancellationToken cancellationToken = default)
	{
		if (context.Messages.Count == 0)
		{
			throw new InvalidOperationException("Cannot continue: no messages in context");
		}

		if (context.Messages[^1] is AssistantMessage)
		{
			throw new InvalidOperationException("Cannot continue from message role: assistant");
		}

		var newMessages = new List<Message>();
		await emit(new AgentStartEvent());
		await emit(new TurnStartEvent());
		await RunLoopAsync(context, newMessages, config, streamFn, emit, cancellationToken);
		return newMessages;
	}

	private static async Task RunLoopAsync(
		AgentContext initialContext,
		List<Message> newMessages,
		AgentLoopConfig initialConfig,
		StreamFn streamFn,
		AgentEventSink emit,
		CancellationToken cancellationToken)
	{
		var context = initialContext;
		var config = initialConfig;
		AgentTurnContext? lastCompletedTurn = null;
		var explicitContinuation = false;

		// 调用方等待期间输入的 steering 会在第一次请求之前送达。
		var pendingMessages = await PollAsync(config.GetSteeringMessages, cancellationToken);

		while (true)
		{
			var hasMoreToolCalls = true;

			while (hasMoreToolCalls || pendingMessages.Count > 0)
			{
				var preparedMessages = new List<Message>();

				if (lastCompletedTurn is not null)
				{
					if (config.PrepareNextTurn is { } prepareNextTurn)
					{
						var update = await prepareNextTurn(lastCompletedTurn, cancellationToken);
						if (update is not null)
						{
							context = update.Context ?? context;
							preparedMessages = update.Messages is null ? [] : [.. update.Messages];
							config = ApplyUpdate(config, update.Model, update.ThinkingLevel);
						}
					}

					// 准备工作可能耗时较长（例如压缩），因此要把期间排入的 steering 一并取走。
					if (pendingMessages.Count == 0)
					{
						pendingMessages = await PollAsync(config.GetSteeringMessages, cancellationToken);
					}

					await emit(new TurnStartEvent());
				}

				foreach (var outgoing in DeclareToolChanges(context, [.. preparedMessages, .. pendingMessages]))
				{
					await emit(new MessageStartEvent { Message = outgoing });
					await emit(new MessageEndEvent { Message = outgoing });
					context.Messages.Add(outgoing);
					newMessages.Add(outgoing);
				}

				pendingMessages = [];

				if (config.PrepareRequest is { } prepareRequest)
				{
					var update = await prepareRequest(
						new PrepareRequestContext
						{
							Context = context,
							Model = config.Model,
							ThinkingLevel = config.ThinkingLevel,
						},
						cancellationToken);

					if (update is not null)
					{
						context = update.Context ?? context;
						config = ApplyUpdate(config, update.Model, update.ThinkingLevel);
					}
				}

				var message = await StreamAssistantResponseAsync(context, config, streamFn, emit, cancellationToken);
				newMessages.Add(message);

				if (message.StopReason is StopReason.Error or StopReason.Aborted)
				{
					lastCompletedTurn = Turn(message, [], context, newMessages);
					if (config.FinishTurn is { } onFailure)
					{
						await onFailure(lastCompletedTurn, cancellationToken);
					}

					await emit(new TurnEndEvent { Message = message, ToolResults = [] });
					await emit(new AgentEndEvent { Messages = newMessages });
					return;
				}

				var toolCalls = message.Content.OfType<ToolCall>().ToList();
				var toolResults = new List<ToolResultMessage>();
				hasMoreToolCalls = false;

				if (toolCalls.Count > 0)
				{
					// 停止原因是 "length" 说明输出被截断，此时每个工具调用的参数都可能不完整。
					// 全部按失败处理，绝不执行残缺的调用。
					var batch = message.StopReason == StopReason.Length
						? await FailTruncatedAsync(toolCalls, emit)
						: await ExecuteToolCallsAsync(context, message, toolCalls, config, emit, cancellationToken);

					toolResults.AddRange(batch.Messages);
					hasMoreToolCalls = !batch.Terminate;

					foreach (var result in toolResults)
					{
						context.Messages.Add(result);
						newMessages.Add(result);
					}
				}

				lastCompletedTurn = Turn(message, toolResults, context, newMessages);
				var action = config.FinishTurn is { } finishTurn
					? await finishTurn(lastCompletedTurn, cancellationToken)
					: AgentTurnAction.Normal;

				await emit(new TurnEndEvent { Message = message, ToolResults = toolResults });

				if (action == AgentTurnAction.End)
				{
					await emit(new AgentEndEvent { Messages = newMessages });
					return;
				}

				explicitContinuation = action == AgentTurnAction.Continue;
				pendingMessages = await PollAsync(config.GetSteeringMessages, cancellationToken);
				if (hasMoreToolCalls || pendingMessages.Count > 0)
				{
					explicitContinuation = false;
				}
			}

			var followUp = await PollAsync(config.GetFollowUpMessages, cancellationToken);
			if (followUp.Count > 0)
			{
				explicitContinuation = false;
				pendingMessages = followUp;
				continue;
			}

			if (explicitContinuation)
			{
				explicitContinuation = false;
				continue;
			}

			break;
		}

		await emit(new AgentEndEvent { Messages = newMessages });
	}

	private static async Task<AssistantMessage> StreamAssistantResponseAsync(
		AgentContext context,
		AgentLoopConfig config,
		StreamFn streamFn,
		AgentEventSink emit,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<Message> messages = context.Messages;
		if (config.TransformContext is { } transform)
		{
			messages = await transform(messages, cancellationToken);
		}

		var llmMessages = config.ConvertToLlm(messages, cancellationToken);
		var apiKey = config.GetApiKey?.Invoke(config.Model.Provider);
		var options = config.Options with
		{
			ApiKey = string.IsNullOrEmpty(apiKey) ? config.Options.ApiKey : apiKey,
			ThinkingLevel = config.ThinkingLevel,
			CancellationToken = cancellationToken,
		};

		var response = streamFn(config.Model, llmMessages, options);

		AssistantMessage? partial = null;
		var addedPartial = false;

		await foreach (var @event in response.WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			switch (@event)
			{
				case StartEvent start:
					partial = start.Partial;
					context.Messages.Add(partial);
					addedPartial = true;
					await emit(new MessageStartEvent { Message = partial });
					break;

				case DoneEvent or ErrorEvent:
				{
					var final = await response.Result.ConfigureAwait(false);
					final = final with { ThinkingLevel = config.ThinkingLevel };
					if (addedPartial)
					{
						context.Messages[^1] = final;
					}
					else
					{
						context.Messages.Add(final);
						await emit(new MessageStartEvent { Message = final });
					}

					await emit(new MessageEndEvent { Message = final });
					return final;
				}

				default:
					partial = @event.Partial;
					if (addedPartial)
					{
						context.Messages[^1] = partial;
					}

					await emit(new MessageUpdateEvent { Message = partial, AssistantMessageEvent = @event });
					break;
			}
		}

		// provider 没发终止事件就结束了流时，仍然要给出一个结果。
		var fallback = await response.Result.ConfigureAwait(false);
		fallback = fallback with { ThinkingLevel = config.ThinkingLevel };
		if (addedPartial)
		{
			context.Messages[^1] = fallback;
		}
		else
		{
			context.Messages.Add(fallback);
			await emit(new MessageStartEvent { Message = fallback });
		}

		await emit(new MessageEndEvent { Message = fallback });
		return fallback;
	}

	private sealed record ExecutedToolCallBatch(List<ToolResultMessage> Messages, bool Terminate)
	{
		public static ExecutedToolCallBatch Empty { get; } = new([], false);
	}

	private static async Task<ExecutedToolCallBatch> ExecuteToolCallsAsync(
		AgentContext context,
		AssistantMessage assistantMessage,
		IReadOnlyList<ToolCall> toolCalls,
		AgentLoopConfig config,
		AgentEventSink emit,
		CancellationToken cancellationToken)
	{
		var hasSequential = toolCalls.Any(call =>
			context.Tools?.FirstOrDefault(t => t.Name == call.Name)?.ExecutionMode == ToolExecutionMode.Sequential);

		return config.ToolExecution == ToolExecutionMode.Sequential || hasSequential
			? await ExecuteSequentialAsync(context, assistantMessage, toolCalls, config, emit, cancellationToken)
			: await ExecuteParallelAsync(context, assistantMessage, toolCalls, config, emit, cancellationToken);
	}

	private static async Task<ExecutedToolCallBatch> ExecuteSequentialAsync(
		AgentContext context,
		AssistantMessage assistantMessage,
		IReadOnlyList<ToolCall> toolCalls,
		AgentLoopConfig config,
		AgentEventSink emit,
		CancellationToken cancellationToken)
	{
		var finalizedCalls = new List<AgentToolCallOutcome>();
		var messages = new List<ToolResultMessage>();

		foreach (var toolCall in toolCalls)
		{
			await emit(new ToolExecutionStartEvent
			{
				ToolCallId = toolCall.Id,
				ToolName = toolCall.Name,
				Arguments = toolCall.Arguments,
			});

			var (prepared, immediate) = await PrepareToolCallAsync(context, assistantMessage, toolCall, config, cancellationToken);
			AgentToolCallOutcome finalized;
			if (immediate is not null)
			{
				finalized = immediate;
			}
			else
			{
				var executed = await ExecutePreparedToolCallAsync(prepared!, toolCall, emit, cancellationToken);
				finalized = await FinalizeAsync(context, assistantMessage, prepared!, executed, config, cancellationToken);
			}

			await EmitToolExecutionEndAsync(finalized, emit);
			var message = CreateToolResultMessage(finalized);
			await EmitToolResultMessageAsync(message, emit);
			finalizedCalls.Add(finalized);
			messages.Add(message);

			if (cancellationToken.IsCancellationRequested)
			{
				break;
			}
		}

		return new ExecutedToolCallBatch(messages, ShouldTerminate(finalizedCalls));
	}

	private static async Task<ExecutedToolCallBatch> ExecuteParallelAsync(
		AgentContext context,
		AssistantMessage assistantMessage,
		IReadOnlyList<ToolCall> toolCalls,
		AgentLoopConfig config,
		AgentEventSink emit,
		CancellationToken cancellationToken)
	{
		var entries = new List<Func<Task<AgentToolCallOutcome>>>();

		foreach (var toolCall in toolCalls)
		{
			await emit(new ToolExecutionStartEvent
			{
				ToolCallId = toolCall.Id,
				ToolName = toolCall.Name,
				Arguments = toolCall.Arguments,
			});

			var (prepared, immediate) = await PrepareToolCallAsync(context, assistantMessage, toolCall, config, cancellationToken);
			if (immediate is not null)
			{
				await EmitToolExecutionEndAsync(immediate, emit);
				var captured = immediate;
				entries.Add(() => Task.FromResult(captured));
				if (cancellationToken.IsCancellationRequested)
				{
					break;
				}

				continue;
			}

			var preparedCall = prepared!;
			entries.Add(async () =>
			{
				if (cancellationToken.IsCancellationRequested)
				{
					var aborted = new AgentToolCallOutcome
					{
						ToolCall = toolCall,
						Result = AgentToolResult.Error("Operation aborted"),
						IsError = true,
					};
					await EmitToolExecutionEndAsync(aborted, emit);
					return aborted;
				}

				var executed = await ExecutePreparedToolCallAsync(preparedCall, toolCall, emit, cancellationToken);
				var finalized = await FinalizeAsync(context, assistantMessage, preparedCall, executed, config, cancellationToken);
				await EmitToolExecutionEndAsync(finalized, emit);
				return finalized;
			});

			if (cancellationToken.IsCancellationRequested)
			{
				break;
			}
		}

		var ordered = await Task.WhenAll(entries.Select(entry => entry())).ConfigureAwait(false);

		var messages = new List<ToolResultMessage>(ordered.Length);
		foreach (var finalized in ordered)
		{
			var message = CreateToolResultMessage(finalized);
			await EmitToolResultMessageAsync(message, emit);
			messages.Add(message);
		}

		return new ExecutedToolCallBatch(messages, ShouldTerminate(ordered));
	}

	private static async Task<ExecutedToolCallBatch> FailTruncatedAsync(
		IReadOnlyList<ToolCall> toolCalls,
		AgentEventSink emit)
	{
		var messages = new List<ToolResultMessage>(toolCalls.Count);
		foreach (var toolCall in toolCalls)
		{
			await emit(new ToolExecutionStartEvent
			{
				ToolCallId = toolCall.Id,
				ToolName = toolCall.Name,
				Arguments = toolCall.Arguments,
			});

			var outcome = new AgentToolCallOutcome
			{
				ToolCall = toolCall,
				Result = AgentToolResult.Error(TruncatedToolCallMessage),
				IsError = true,
			};

			await EmitToolExecutionEndAsync(outcome, emit);
			var message = CreateToolResultMessage(outcome);
			await EmitToolResultMessageAsync(message, emit);
			messages.Add(message);
		}

		return new ExecutedToolCallBatch(messages, false);
	}

	private static async Task<(PreparedToolCall? Prepared, AgentToolCallOutcome? Immediate)> PrepareToolCallAsync(
		AgentContext context,
		AssistantMessage assistantMessage,
		ToolCall toolCall,
		AgentLoopConfig config,
		CancellationToken cancellationToken)
	{
		var tool = context.Tools?.FirstOrDefault(t => string.Equals(t.Name, toolCall.Name, StringComparison.Ordinal));
		if (tool is null)
		{
			return (null, Immediate(toolCall, $"Tool {toolCall.Name} not found"));
		}

		var arguments = toolCall.Arguments;
		if (tool.PrepareArguments is { } prepare)
		{
			arguments = prepare(arguments);
		}

		// 在执行任何钩子或工具之前，先按声明的 schema 校验并做类型转换。
		var validation = JsonSchemaValidator.ValidateToolArguments(tool.ToDeclaration(), arguments);
		if (validation.Error is { } validationError)
		{
			return (null, Immediate(toolCall, validationError));
		}

		arguments = validation.Value!;

		if (config.BeforeToolCall is { } beforeToolCall)
		{
			var hook = await beforeToolCall(
				new BeforeToolCallContext
				{
					AssistantMessage = assistantMessage,
					ToolCall = toolCall,
					Arguments = arguments,
					Context = context,
				},
				cancellationToken);

			if (hook?.Block == true)
			{
				var reason = hook.Reason ?? $"Tool call blocked by policy: {toolCall.Name}";
				return (null, new AgentToolCallOutcome
				{
					ToolCall = toolCall,
					Result = AgentToolResult.Error(reason) with { Terminate = hook.Terminate },
					IsError = true,
				});
			}
		}

		return (new PreparedToolCall(toolCall, tool, arguments), null);
	}

	private static async Task<ExecutedToolCallOutcome> ExecutePreparedToolCallAsync(
		PreparedToolCall prepared,
		ToolCall toolCall,
		AgentEventSink emit,
		CancellationToken cancellationToken)
	{
		var updates = new List<ValueTask>();
		var acceptingUpdates = true;
		var startedAt = Stopwatch.GetTimestamp();

		try
		{
			var result = await prepared.Tool.Execute(new AgentToolInvocation
			{
				ToolCallId = toolCall.Id,
				ToolName = toolCall.Name,
				Arguments = prepared.Arguments,
				CancellationToken = cancellationToken,
				OnUpdate = partial =>
				{
					if (!acceptingUpdates)
					{
						return;
					}

					updates.Add(emit(new ToolExecutionUpdateEvent
					{
						ToolCallId = toolCall.Id,
						ToolName = toolCall.Name,
						Arguments = prepared.Arguments,
						PartialResult = partial,
					}));
				},
			}).ConfigureAwait(false);

			var durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
			acceptingUpdates = false;
			await AwaitUpdatesAsync(updates).ConfigureAwait(false);
			return new ExecutedToolCallOutcome(result, result.IsError, durationMs);
		}
		catch (Exception ex)
		{
			var durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
			acceptingUpdates = false;
			await AwaitUpdatesAsync(updates).ConfigureAwait(false);
			return new ExecutedToolCallOutcome(AgentToolResult.Error(ex.Message), true, durationMs);
		}
		finally
		{
			acceptingUpdates = false;
		}
	}

	private static async Task AwaitUpdatesAsync(List<ValueTask> updates)
	{
		foreach (var update in updates)
		{
			await update.ConfigureAwait(false);
		}
	}

	private static async Task<AgentToolCallOutcome> FinalizeAsync(
		AgentContext context,
		AssistantMessage assistantMessage,
		PreparedToolCall prepared,
		ExecutedToolCallOutcome executed,
		AgentLoopConfig config,
		CancellationToken cancellationToken)
	{
		var result = executed.Result;
		var isError = executed.IsError;

		if (config.AfterToolCall is { } afterToolCall)
		{
			var hook = await afterToolCall(
				new AfterToolCallContext
				{
					AssistantMessage = assistantMessage,
					ToolCall = prepared.ToolCall,
					Arguments = prepared.Arguments,
					Result = result,
					IsError = isError,
					Context = context,
				},
				cancellationToken);

			if (hook is not null)
			{
				result = result with
				{
					Content = hook.Content ?? result.Content,
					Details = hook.HasDetails ? hook.Details : result.Details,
					StructuredContent = hook.HasStructuredContent ? hook.StructuredContent : (hook.Content is not null ? null : result.StructuredContent),
					Usage = hook.Usage ?? result.Usage,
					Terminate = hook.Terminate ?? result.Terminate,
					IsError = hook.IsError ?? result.IsError,
				};
				isError = hook.IsError ?? isError;
			}
		}

		return new AgentToolCallOutcome
		{
			ToolCall = prepared.ToolCall,
			Result = result,
			IsError = isError,
			DurationMs = executed.DurationMs,
		};
	}

	private static AgentToolCallOutcome Immediate(ToolCall toolCall, string message) => new()
	{
		ToolCall = toolCall,
		Result = AgentToolResult.Error(message),
		IsError = true,
	};

	private static bool ShouldTerminate(IReadOnlyList<AgentToolCallOutcome> outcomes) =>
		outcomes.Count > 0 && outcomes.All(outcome => outcome.Result.Terminate);

	private static async ValueTask EmitToolResultMessageAsync(ToolResultMessage message, AgentEventSink emit)
	{
		await emit(new MessageStartEvent { Message = message });
		await emit(new MessageEndEvent { Message = message });
	}

	private static async ValueTask EmitToolExecutionEndAsync(AgentToolCallOutcome outcome, AgentEventSink emit) =>
		await emit(new ToolExecutionEndEvent
		{
			ToolCallId = outcome.ToolCall.Id,
			ToolName = outcome.ToolCall.Name,
			Result = outcome.Result,
			IsError = outcome.IsError,
			DurationMs = outcome.DurationMs,
		});

	private static ToolResultMessage CreateToolResultMessage(AgentToolCallOutcome outcome) => new()
	{
		ToolCallId = outcome.ToolCall.Id,
		ToolName = outcome.ToolCall.Name,
		Content = outcome.Result.Content,
		Details = outcome.Result.Details,
		Usage = outcome.Result.Usage,
		IsError = outcome.IsError,
		Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		DurationMs = outcome.DurationMs,
	};

	private static AgentTurnContext Turn(
		AssistantMessage message,
		IReadOnlyList<ToolResultMessage> toolResults,
		AgentContext context,
		IReadOnlyList<Message> newMessages) => new()
		{
			Message = message,
			ToolResults = toolResults,
			Context = context,
			NewMessages = newMessages,
		};

	private static AgentLoopConfig ApplyUpdate(AgentLoopConfig config, Model? model, string? thinkingLevel) =>
		config with
		{
			Model = model ?? config.Model,
			ThinkingLevel = thinkingLevel ?? config.ThinkingLevel,
		};

	private static async Task<List<Message>> PollAsync(
		Func<CancellationToken, ValueTask<IReadOnlyList<Message>>>? source,
		CancellationToken cancellationToken)
	{
		if (source is null)
		{
			return [];
		}

		var messages = await source(cancellationToken).ConfigureAwait(false);
		return messages.Count == 0 ? [] : [.. messages];
	}

	/// <summary>
	/// 把“运行时能执行的工具”与“会话记录里已经声明的工具”之间的差异补上，
	/// 这样重放会话记录总能得到与 <see cref="AgentContext.Tools"/> 完全一致的工具集。
	/// </summary>
	internal static List<Message> DeclareToolChanges(AgentContext context, IReadOnlyList<Message> pendingMessages)
	{
		var baseline = new List<Message>(pendingMessages);
		var systemIndex = -1;
		for (var i = baseline.Count - 1; i >= 0; i--)
		{
			if (baseline[i] is SystemMessage)
			{
				systemIndex = i;
				break;
			}
		}

		var existing = systemIndex >= 0 ? (SystemMessage)baseline[systemIndex] : null;

		// 尚未落盘的 system 消息里的工具字段表达的是意图，不是已提交的状态。
		var committed = new List<Message>(baseline);
		if (existing is not null)
		{
			committed[systemIndex] = existing with { ToolsAdded = null, ToolsRemoved = null };
		}

		var currentTools = Transcript.GetTools([.. context.Messages, .. committed]);
		var desired = (context.Tools ?? []).Select(tool => tool.ToDeclaration()).ToList();

		var added = desired
			.Where(tool => !currentTools.Any(existingTool => string.Equals(existingTool.Name, tool.Name, StringComparison.Ordinal)))
			.ToList();
		var removed = currentTools
			.Where(tool => !desired.Any(desiredTool => string.Equals(desiredTool.Name, tool.Name, StringComparison.Ordinal)))
			.Select(tool => new ToolReference { Name = tool.Name })
			.ToList();

		var unchanged = added.Count == 0 && removed.Count == 0;

		if (existing is not null)
		{
			if (unchanged && existing.ToolsAdded is null && existing.ToolsRemoved is null)
			{
				return baseline;
			}

			baseline[systemIndex] = existing with
			{
				ToolsAdded = added.Count > 0 ? added : null,
				ToolsRemoved = removed.Count > 0 ? removed : null,
			};
			return baseline;
		}

		if (unchanged)
		{
			return baseline;
		}

		var update = new SystemMessage
		{
			Content = MessageContent.Empty,
			ToolsAdded = added.Count > 0 ? added : null,
			ToolsRemoved = removed.Count > 0 ? removed : null,
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		};

		// 插到第一条非 system 消息之前，保证开头的 system 消息仍然在最前。
		var insertIndex = baseline.FindIndex(message => message is not SystemMessage);
		baseline.Insert(insertIndex == -1 ? baseline.Count : insertIndex, update);
		return baseline;
	}

	private sealed record PreparedToolCall(ToolCall ToolCall, AgentTool Tool, JsonObject Arguments);

	private sealed record ExecutedToolCallOutcome(AgentToolResult Result, bool IsError, double? DurationMs);
}
