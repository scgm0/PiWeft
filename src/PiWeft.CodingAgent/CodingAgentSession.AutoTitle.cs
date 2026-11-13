using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent;

/// 自动会话标题：PiDeck 的 `pi-deck-session-title.ts` 在本端口的等价实现。
/// <para>
/// PiDeck 启动 pi 时注入 `PIDECK_AUTO_SESSION_TITLE=1`（关掉写 0），并在收到
/// <c>setStatus("pideck:auto-title", 标题)</c> 后紧随的 <c>session_info_changed</c> 才认这次改名。
/// 切到本端口后会话同样要能自动命名，否则侧栏里全是「新会话」。
/// </para>
/// <para>
/// 只在显式注入 1 时工作：纯 CLI 与桌面自己起的会话不该被悄悄改名。标题请求是旁路：
/// 独立 Context、独立取消、30 秒预算、最多两次（第二次升输出预算）；任何失败都只是本轮不命名。
/// </para>
public sealed partial class CodingAgentSession
{
	public const string AutoTitleVariable = "PIDECK_AUTO_SESSION_TITLE";

	/// PiDeck 识别自动改名用的 status key；改名必须紧跟这条 status。
	public const string AutoTitleStatusKey = "pideck:auto-title";

	private const int TitleMaxTokens = 512;

	private const int TitleMaxTokensEscalated = 2048;

	private static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(30);

	private bool _autoTitleEnabled;
	private bool _autoTitleEligible;
	private bool _autoTitleManual;
	private bool _autoTitleApplying;
	private bool _autoTitlePending;
	private int _autoTitleAttempts;
	private int _autoTitleRun;
	private int _autoTitleAttemptedRun = -1;
	private CancellationTokenSource? _autoTitleCancellation;
	private IDisposable? _autoTitleLifecycle;

	/// 环境变量显式开启才生效：PiDeck 总是写 0/1，其它宿主不写。
	private static bool AutoTitleEnabledByEnvironment() =>
		Environment.GetEnvironmentVariable(AutoTitleVariable) is "1";

	/// 建会话时判定是否还有命名资格：只命名空白会话，且没有手动标题。
	private void InitializeAutoTitle()
	{
		_autoTitleEnabled = AutoTitleEnabledByEnvironment();
		if (!_autoTitleEnabled)
		{
			return;
		}

		_autoTitleCancellation = new CancellationTokenSource();
		_autoTitleEligible = Session.CurrentName() is null && !HasConversation();
		_autoTitleLifecycle = _agent.Subscribe(AutoTitleLifecycleAsync);
	}

	private bool HasConversation()
	{
		foreach (var entry in Session.GetBranch())
		{
			if (entry is SessionMessageEntry { Message: UserMessage or AssistantMessage })
			{
				return true;
			}
		}

		return false;
	}

	/// 用户（或宿主）自己改名：手动意图优先，取消挂起的旁路请求，之后不再自动命名。
	private void NoteManualSessionName()
	{
		if (!_autoTitleEnabled || _autoTitleApplying)
		{
			return;
		}

		_autoTitleManual = true;
		CancelAutoTitle();
	}

	private void CancelAutoTitle()
	{
		try
		{
			_autoTitleCancellation?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// 会话已释放。
		}
	}

	private ValueTask AutoTitleLifecycleAsync(AgentEvent @event)
	{
		switch (@event)
		{
			case AgentStartEvent:
				// 真实运行才算一次重试边界，重复的 settled 不消耗标题请求次数。
				_autoTitleRun++;
				break;
			case AgentEndEvent { WillRetry: false }:
				TryAutoTitle();
				break;
		}

		return ValueTask.CompletedTask;
	}

	private void TryAutoTitle()
	{
		if (!_autoTitleEnabled ||
			!_autoTitleEligible ||
			_autoTitleManual ||
			_autoTitlePending ||
			_autoTitleAttempts >= AutoTitle.MaxAttempts ||
			_autoTitleAttemptedRun == _autoTitleRun ||
			Session.CurrentName() is not null)
		{
			return;
		}

		var input = AutoTitle.Build(FirstUserText(), FirstCompletedAssistantText());
		if (input is null)
		{
			return;
		}

		_autoTitleAttempts++;
		_autoTitleAttemptedRun = _autoTitleRun;

		if (input.DirectTitle is { Length: > 0 } direct)
		{
			// 只插了模板/技能、没写正文：标签本身就是最准确的标题，不再花一次模型请求。
			ApplyAutoTitle(direct);
			return;
		}

		_autoTitlePending = true;
		_ = Task.Run(() => RequestAutoTitleAsync(input));
	}

	private string? FirstUserText()
	{
		foreach (var entry in Session.GetBranch())
		{
			if (entry is SessionMessageEntry { Message: UserMessage user } && user.Content.Text is { Length: > 0 } text)
			{
				return text;
			}
		}

		return null;
	}

	/// 首条已正常结束的助手正文；主轮被中断或报错时不给标题请求当参考。
	private string? FirstCompletedAssistantText()
	{
		foreach (var entry in Session.GetBranch())
		{
			if (entry is SessionMessageEntry { Message: AssistantMessage assistant } &&
				assistant.StopReason is not (StopReason.Error or StopReason.Aborted) &&
				MessageText.Of(assistant) is { Length: > 0 } text)
			{
				return text;
			}
		}

		return null;
	}

	private async Task RequestAutoTitleAsync(AutoTitle.TitleInput input)
	{
		try
		{
			var cancellation = _autoTitleCancellation;
			if (cancellation is null || cancellation.IsCancellationRequested)
			{
				return;
			}

			using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
			budget.CancelAfter(TitleTimeout);

			// 独立上下文：系统提示与一条用户消息，不读主会话的 system/tools/thinking。
			var messages = new Message[]
			{
				new SystemMessage { Content = MessageContent.FromText(AutoTitle.SystemPrompt) },
				new UserMessage
				{
					Content = MessageContent.FromText(BuildTitlePrompt(input)),
					Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				},
			};

			var model = _agent.Model;
			foreach (var maxTokens in (ReadOnlySpan<int>)[TitleMaxTokens, TitleMaxTokensEscalated])
			{
				var options = _streamOptions with
				{
					MaxTokens = maxTokens,
					CancellationToken = budget.Token,
					// 标题请求不进会话的 prompt cache / 亲和链路。
					SessionId = null,
				};

				var stream = _routedStreamFn(model, messages, options);
				await foreach (var _ in stream.WithCancellation(budget.Token).ConfigureAwait(false))
				{
				}

				var response = await stream.Result.ConfigureAwait(false);
				if (response.StopReason is StopReason.Error or StopReason.Aborted)
				{
					return;
				}

				var text = MessageText.Of(response);
				if (AutoTitle.CleanTitle(text) is { } title)
				{
					ApplyAutoTitle(title);
					return;
				}

				// 只有「输出预算被推理吃光」值得升预算重发；其它空响应重发结果一样。
				if (maxTokens >= TitleMaxTokensEscalated ||
					response.StopReason != StopReason.Length ||
					text.Trim().Length > 0)
				{
					return;
				}
			}
		}
		catch (OperationCanceledException)
		{
			// 会话关闭/用户改名/超时：本轮不命名。
		}
		catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or ObjectDisposedException or NotSupportedException)
		{
			// 标题是非关键旁路：认证失败、模型异常都只让这一轮不命名。
		}
		finally
		{
			_autoTitlePending = false;
		}
	}

	private static string BuildTitlePrompt(AutoTitle.TitleInput input)
	{
		var assistant = input.AssistantText is { Length: > 0 } text
			? $"\n\nAssistant's first response:\n{text}"
			: string.Empty;
		return $"User's first request:\n{input.UserText}{assistant}";
	}

	/// 写标题：先发 status 标记，再改名。PiDeck 只认紧邻的那次 session_info_changed。
	private void ApplyAutoTitle(string title)
	{
		if (_autoTitleManual || Session.CurrentName() is not null)
		{
			return;
		}

		_autoTitleApplying = true;
		try
		{
			UiHost?.SetStatus(new UiStatusRequest(AutoTitleStatusKey, title));
			SetSessionName(title);
		}
		catch (InvalidOperationException)
		{
			// 会话已在销毁/替换：丢弃旁路结果。
		}
		finally
		{
			_autoTitleApplying = false;
			_autoTitleEligible = false;
		}
	}
}
