using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Compaction;

/// <summary>一次保温决策及其经济学输入，对应 pi 的 <c>CacheWarmingDecision</c>。</summary>
public sealed record CacheWarmingDecision
{
	public required bool Idle { get; init; }

	public required double WarmCost { get; init; }

	public required double MissCost { get; init; }

	public required double ContinuationProbability { get; init; }

	public required double ExpectedSavings { get; init; }

	public required bool EconomicsAvailable { get; init; }

	/// <summary><c>warm</c> 或 <c>stop</c>。</summary>
	public required string Action { get; init; }
}

/// <summary>保温器的可观察状态，供会话/RPC/桌面展示。</summary>
public sealed record CacheWarmingStatus
{
	public required string State { get; init; }

	public string? Reason { get; init; }

	public DateTimeOffset? NextWarmAt { get; init; }

	public CacheWarmingDecision? Decision { get; init; }

	/// <summary>扩展把 pi 的决策改成了另一个动作。</summary>
	public bool ExtensionOverride { get; init; }
}

/// <summary>每次刷新前发给扩展的决策事件，对应 pi 的 <c>cache_warming_decision</c>。</summary>
public sealed record CacheWarmingDecisionEvent
{
	public string Type => "cache_warming_decision";

	public required bool Idle { get; init; }

	public required double WarmCost { get; init; }

	public required double MissCost { get; init; }

	public required double ContinuationProbability { get; init; }

	public required double ExpectedSavings { get; init; }

	public required bool EconomicsAvailable { get; init; }

	/// <summary>pi 自己的决策：<c>warm</c> 或 <c>stop</c>。</summary>
	public required string Action { get; init; }
}

/// <summary>
/// 在提示缓存到期前用 1 token 输出重放最近一次请求，保持 Provider 的 prompt cache 命中。
/// 对应 pi 的 <c>CacheWarmer</c>：streaming 阶段在代理运行期间保温，agent settled 后停止；
/// idle 阶段使用更低的中断概率和 30 分钟上限。每次刷新前可经 <c>cache_warming_decision</c>
/// 回调让扩展覆盖 pi 的决策。
/// </summary>
public sealed class CacheWarmer : IDisposable
{
	private const int RefreshMarginMs = 10_000;
	private const double MinimumExpectedSavings = 0.05;
	private const double IdleContinuationProbability = 0.15;
	private static readonly TimeSpan MaxStreamingAge = TimeSpan.FromHours(1);
	private static readonly TimeSpan MaxIdleAge = TimeSpan.FromMinutes(30);

	private readonly StreamFn _streamFn;
	private readonly Func<string> _mode;
	private readonly Action<Usage> _recordUsage;
	private readonly Func<CacheWarmingDecisionEvent, CancellationToken, Task<string?>>? _decide;
	private readonly Action<CacheWarmingStatus>? _statusChanged;
	private CacheWarmingStatus _status;
	private readonly Lock _gate = new();
	private CancellationTokenSource? _cancellation;
	private Task? _loop;
	private DateTimeOffset _startedAt;
	private Model? _model;
	private IReadOnlyList<Message>? _messages;
	private bool _idle;
	private bool _disposed;

	public CacheWarmer(
		StreamFn streamFn,
		Func<string> mode,
		Action<Usage> recordUsage,
		Func<CacheWarmingDecisionEvent, CancellationToken, Task<string?>>? decide = null,
		Action<CacheWarmingStatus>? statusChanged = null)
	{
		_streamFn = streamFn;
		_mode = mode;
		_recordUsage = recordUsage;
		_decide = decide;
		_statusChanged = statusChanged;
		_status = new CacheWarmingStatus { State = "inactive", Reason = "waiting for first request" };
	}

	public CacheWarmingStatus Status
	{
		// 保温跑在后台任务里，状态却被 RPC / 桌面从别的线程读。普通字段读到旧值会让
		// 状态卡在上一个阶段（也直接制造了时序敏感的测试抖动），所以按 volatile 访问。
		get => Volatile.Read(ref _status);
		private set
		{
			Volatile.Write(ref _status, value);
			try
			{
				_statusChanged?.Invoke(value);
			}
			catch
			{
				// 观察者失败不影响保温。
			}
		}
	}

	/// <summary>开始为一条已发送的请求保温；新的调用会替换旧的计时器。</summary>
	public void Start(Model model, IReadOnlyList<Message> messages, StreamOptions options)
	{
		Cancel("replaced by a newer request");
		if (_disposed || messages.Count == 0)
		{
			return;
		}

		var mode = _mode().ToLowerInvariant();
		if (mode is not ("streaming" or "idle"))
		{
			Status = new CacheWarmingStatus { State = "inactive", Reason = "cache warming disabled" };
			return;
		}

		var retention = options.CacheRetention ??
			(string.Equals(Environment.GetEnvironmentVariable("PI_CACHE_RETENTION"), "long", StringComparison.OrdinalIgnoreCase)
				? CacheRetentions.Long
				: CacheRetentions.Short);
		if (retention == CacheRetentions.None ||
			model.PromptCache is not { } cache ||
			!cache.TryGetValue(retention, out var ttlSeconds) ||
			ttlSeconds <= 10)
		{
			Status = new CacheWarmingStatus
			{
				State = "inactive",
				Reason = retention == CacheRetentions.None
					? "request disabled prompt caching"
					: "cache lifetime unavailable",
			};
			return;
		}

		var ttl = TimeSpan.FromSeconds(ttlSeconds);
		var delayMs = Math.Max(1, Math.Min((int)(ttl.TotalMilliseconds * 0.9), (int)ttl.TotalMilliseconds - RefreshMarginMs));
		var decision = Evaluate(model, messages, idle: false);
		Status = new CacheWarmingStatus
		{
			State = "scheduled",
			NextWarmAt = DateTimeOffset.UtcNow.AddMilliseconds(delayMs),
			Decision = decision,
		};
		if (decision.Action == "stop" && _decide is null)
		{
			Status = new CacheWarmingStatus
			{
				State = "inactive",
				Reason = decision.EconomicsAvailable ? "expected savings below threshold" : "cache economics unavailable",
				Decision = decision,
			};
			return;
		}

		var token = new CancellationTokenSource();
		_startedAt = DateTimeOffset.UtcNow;
		_model = model;
		_messages = messages;
		_idle = mode == "idle";
		lock (_gate)
		{
			_cancellation = token;
			_loop = RunAsync(model, messages, options with
			{
				MaxTokens = 1,
				CacheRetention = retention,
			}, TimeSpan.FromMilliseconds(delayMs), token.Token);
		}
	}

	/// <summary>代理运行结束：streaming 保温停止；idle 保温切换到更短的 30 分钟上限。</summary>
	public void AgentSettled()
	{
		if (_mode().Equals("streaming", StringComparison.OrdinalIgnoreCase))
		{
			Cancel("agent run settled");
			return;
		}

		_idle = true;
		if (DateTimeOffset.UtcNow - _startedAt >= MaxIdleAge)
		{
			Cancel("30-minute idle safety limit reached");
			return;
		}

		if (_model is { } model && _messages is { } messages)
		{
			var decision = Evaluate(model, messages, idle: true);
			if (decision.Action == "stop" && _decide is null)
			{
				Cancel(decision.EconomicsAvailable
					? "expected savings below threshold"
					: "cache economics unavailable");
				Status = Status with { Decision = decision };
			}
			else
			{
				Status = Status with { Decision = decision };
			}
		}
	}

	public void Cancel() => Cancel("inactive");

	private void Cancel(string reason)
	{
		lock (_gate)
		{
			_cancellation?.Cancel();
			_cancellation?.Dispose();
			_cancellation = null;
			_loop = null;
		}

		_model = null;
		_messages = null;
		if (Status.State != "inactive")
		{
			Status = new CacheWarmingStatus { State = "inactive", Reason = reason, Decision = Status.Decision };
		}
	}

	private async Task RunAsync(
		Model model,
		IReadOnlyList<Message> messages,
		StreamOptions options,
		TimeSpan delay,
		CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var maxAge = _idle ? MaxIdleAge : MaxStreamingAge;
				var remaining = _startedAt + maxAge - DateTimeOffset.UtcNow;
				if (remaining <= TimeSpan.Zero)
				{
					Status = new CacheWarmingStatus
					{
						State = "inactive",
						Reason = _idle ? "30-minute idle safety limit reached" : "one-hour safety limit reached",
					};
					return;
				}

				await Task.Delay(delay < remaining ? delay : remaining, cancellationToken).ConfigureAwait(false);
				if (DateTimeOffset.UtcNow - _startedAt >= maxAge)
				{
					Status = new CacheWarmingStatus
					{
						State = "inactive",
						Reason = _idle ? "30-minute idle safety limit reached" : "one-hour safety limit reached",
					};
					return;
				}

				var decision = Evaluate(model, messages, _idle);
				var action = decision.Action;
				var extensionOverride = false;
				if (_decide is { } decide)
				{
					try
					{
						action = await decide(new CacheWarmingDecisionEvent
						{
							Idle = decision.Idle,
							WarmCost = decision.WarmCost,
							MissCost = decision.MissCost,
							ContinuationProbability = decision.ContinuationProbability,
							ExpectedSavings = decision.ExpectedSavings,
							EconomicsAvailable = decision.EconomicsAvailable,
							Action = decision.Action,
						}, cancellationToken).ConfigureAwait(false) ?? decision.Action;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch
					{
						action = decision.Action;
					}

					if (action is not ("warm" or "stop"))
					{
						action = decision.Action;
					}

					extensionOverride = !string.Equals(action, decision.Action, StringComparison.Ordinal);
				}

				if (string.Equals(action, "stop", StringComparison.Ordinal))
				{
					Status = new CacheWarmingStatus
					{
						State = "inactive",
						Reason = extensionOverride
							? "stopped by extension"
							: decision.EconomicsAvailable
								? "expected savings below threshold"
								: "cache economics unavailable",
						Decision = decision,
						ExtensionOverride = extensionOverride,
					};
					return;
				}

				Status = new CacheWarmingStatus
				{
					State = "refreshing",
					Decision = decision,
					ExtensionOverride = extensionOverride,
				};

				var stream = _streamFn(model, messages, options);
				await foreach (var _ in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
				{
				}

				var response = await stream.Result.ConfigureAwait(false);
				if (response.StopReason is StopReason.Error or StopReason.Aborted)
				{
					Status = new CacheWarmingStatus
					{
						State = "inactive",
						Reason = "cache refresh failed",
						Decision = decision,
					};
					return;
				}

				_recordUsage(response.Usage);
				Status = new CacheWarmingStatus
				{
					State = "scheduled",
					NextWarmAt = DateTimeOffset.UtcNow.Add(delay),
					Decision = decision,
					ExtensionOverride = extensionOverride,
				};
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception)
		{
			// 保温失败不影响会话；下一次真实请求会重新建立缓存。
			if (Status.State != "inactive")
			{
				Status = new CacheWarmingStatus { State = "inactive", Reason = "cache refresh failed", Decision = Status.Decision };
			}
		}
	}

	private static CacheWarmingDecision Evaluate(Model model, IReadOnlyList<Message> messages, bool idle)
	{
		var promptTokens = ContextTokens.EstimateContextTokens(messages).Tokens;
		var inputRate = model.Cost.Input;
		var cacheReadRate = model.Cost.CacheRead;
		var cacheWriteRate = model.Cost.CacheWrite;
		var outputRate = model.Cost.Output;

		var cacheHitCost = cacheReadRate * promptTokens / 1_000_000;
		var missRate = cacheWriteRate > 0 ? cacheWriteRate : inputRate;
		var cacheMissCost = missRate * promptTokens / 1_000_000;
		var warmCost = cacheHitCost + outputRate / 1_000_000;
		var missCost = Math.Max(0, cacheMissCost - cacheHitCost);
		var probability = idle ? IdleContinuationProbability : 1;
		var available = promptTokens > 0 && (cacheHitCost > 0 || cacheMissCost > 0);
		var expectedSavings = probability * missCost - warmCost;
		return new CacheWarmingDecision
		{
			Idle = idle,
			WarmCost = warmCost,
			MissCost = missCost,
			ContinuationProbability = probability,
			ExpectedSavings = expectedSavings,
			EconomicsAvailable = available,
			Action = available && expectedSavings >= MinimumExpectedSavings ? "warm" : "stop",
		};
	}

	public void Dispose()
	{
		_disposed = true;
		Cancel();
	}
}
