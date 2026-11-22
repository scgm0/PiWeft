using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// 桌面的 <see cref="IUiHost"/>：把 <c>ask_question</c> 的请求交给会话标签自带的
/// 内联问询卡（<see cref="AskQuestionPanel"/>）。卡片在 UI 线程上构建，代理循环在后台等待回答。
/// 为什么不再有窗口级弹窗：PiDeck 的 ask 卡片钉在对话区底部，与输入框同级，用户能一边翻历史
/// 一边回答；遮罩弹窗会把整窗压暗并抢走滚动。同一个面板也承接扩展的
/// <c>select / confirm / editor</c> 请求，它们同样是「代理停下等你输入」。
internal sealed class DesktopUiHost : IUiHost
{
	private SessionTab? _tab;
	private SessionTab? _parentTab;
	private string? _source;
	private string? _alertSessionId;
	private UIElement? _owner;
	private AskQuestionPanel? _overlayPanel;

	public bool HasUi => true;

	/// 没有自己的标签时把交互转给父标签：子代理在后台跑，问题要有地方问。
	/// <paramref name="source"/> 是卡片上的来源标签（子代理名字），
	/// <paramref name="alertSessionId"/> 让等待提示音归到子会话而不是父会话。
	public void AttachParent(SessionTab parent, string? source, string? alertSessionId = null)
	{
		_parentTab = parent;
		_source = source;
		_alertSessionId = alertSessionId;
	}

	/// 自己的标签关掉后回到父标签路由。
	public void DetachTab()
	{
		_tab = null;
	}

	/// 父标签也没了（父会话关闭、子会话回收）：断掉所有引用，别再往死标签上送问题。
	public void DetachParent()
	{
		_tab = null;
		_parentTab = null;
		_source = null;
		_alertSessionId = null;
	}

	/// 窗口级宿主：会话还没建出来时的项目信任询问用（没有页签可以挂内联卡片），
	/// 卡片以窗口浮层展示，回答完即收。
	public void Attach(UIElement owner) => _owner = owner;

	public void Attach(SessionTab tab)
	{
		_tab = tab;
		tab.AskPanel.BindSession(tab.Session.Session.SessionId);
	}

	public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken)
	{
		var questions = AskQuestions.ParseEnvelope(request.Title);
		if (questions is null || questions.Count == 0)
		{
			return Task.FromResult<string?>(null);
		}

		return AskAsync(questions, AskQuestions.ReviewRequested(request.Title), cancellationToken);
	}

	public async Task<string?> SelectAsync(UiSelectRequest request, CancellationToken cancellationToken)
	{
		// PiDeck 的标记与 `label|description` 编码只对 PiDeck 有意义：
		// 卡片显示去掉标记的主副文案，返回的仍是完整选项值。
		string title;
		string? detail = null;
		string? header = null;
		// 计划选单把计划全文接在首行问题之后。拆出来交给卡片正文，标题才不会挤成一行。
		if (request.Title.StartsWith(PlanSteps.NextMarker, StringComparison.Ordinal))
		{
			(title, detail) = PlanSteps.SplitReview(request.Title);
			header = Strings.PlanReviewTitle;
		}
		else
		{
			title = UiSelectText.DisplayTitle(request.Title);
		}

		var selected = await ShowDialogAsync<string?>(
			new AskQuestion("dialog", "select", title,
				[.. request.Options.Select(static option =>
				{
					var (label, description) = UiSelectText.SplitOption(option);
					return new AskOption(label, option, description);
				})], null, null, detail, header),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.String
				? answers[0].Value!.GetValue<string>()
				: null).ConfigureAwait(false);

		// 运行被取消（停止按钮/关闭会话）与用户主动关卡片都返回 null：
		// 取消要抛出去，让计划评审保持模式与草案；关闭同样只收起卡片，退出走模式 chip。
		if (selected is null && cancellationToken.IsCancellationRequested)
		{
			throw new OperationCanceledException(cancellationToken);
		}

		return selected;
	}

	public Task<bool?> ConfirmAsync(UiConfirmRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<bool?>(
			new AskQuestion("dialog", "confirm", request.Message is { Length: > 0 } ? $"{request.Title}\n{request.Message}" : request.Title, null, null, null),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.True
				? true
				: answers.Count > 0 ? false : null);

	public Task<ToolApproval?> ApproveAsync(UiApprovalRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<ToolApproval?>(
			// 三态审批：一次性、本会话记住、拒绝。拒绝不带理由：参数已经展示在卡片里。
			new AskQuestion("approval", "select", request.Message is { Length: > 0 } ? $"{request.Title}\n{request.Message}" : request.Title,
				[
					new AskOption("本次允许", "once", null),
					new AskOption("本会话允许", "session", null),
					new AskOption("拒绝", "deny", null),
				],
				null,
				null),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value is { } value &&
				value.GetValueKind() == System.Text.Json.JsonValueKind.String
					? value.GetValue<string>() switch
					{
						"once" => ToolApproval.Once,
						"session" => ToolApproval.Session,
						"deny" => ToolApproval.Deny,
						_ => null,
					}
					: null);

	public Task<string?> EditorAsync(UiEditorRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<string?>(
			new AskQuestion("dialog", "editor", request.Title, null, null, request.Prefill),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.String
				? answers[0].Value!.GetValue<string>()
				: null);

	public void Notify(UiNotifyRequest request) => Dispatch(() =>
	{
		if (_tab is { } tab) tab.ShowHostNotice(request.Message, request.NotifyType);
	});

	public void SetStatus(UiStatusRequest request) =>
		Dispatch(() => _tab?.SetExtensionStatus(request.Key, request.Text));

	public void SetWidget(UiWidgetRequest request) =>
		Dispatch(() => _tab?.SetExtensionWidget(request.Key, request.Lines));

	public void SetTitle(UiTitleRequest request) =>
		Dispatch(() => _tab?.SetExtensionTitle(request.Title));

	public void SetEditorText(string text) =>
		Dispatch(() => _tab?.SetExtensionText(text));

	public void Hide() => Dispatch(() =>
	{
		_tab?.AskPanel.Close();
		_overlayPanel?.Close();
	});

	private Task<string?> AskAsync(IReadOnlyList<AskQuestion> questions, bool review, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Dispatch(() =>
		{
			if (_tab is { } tab)
			{
				_ = RunAsync(tab.AskPanel, questions, review, null, null, cancellationToken, completion);
				return;
			}

			if (_parentTab is { } parent)
			{
				// 父标签的面板一次只显示一批，多出来的排队（AskQuestionPanel 内部按 FIFO 接）。
				_ = RunAsync(parent.AskPanel, questions, review, _source, _alertSessionId, cancellationToken, completion);
				return;
			}

			if (_owner?.FindVisualRoot() is not Window window)
			{
				completion.TrySetResult(null);
				return;
			}

			_overlayPanel ??= new AskQuestionPanel();
			_ = RunOverlayAsync(_overlayPanel, window, questions, review, cancellationToken, completion);
		});

		return completion.Task;
	}

	private static async Task RunAsync(
		AskQuestionPanel panel,
		IReadOnlyList<AskQuestion> questions,
		bool review,
		string? source,
		string? alertSessionId,
		CancellationToken cancellationToken,
		TaskCompletionSource<string?> completion)
	{
		try
		{
			completion.TrySetResult(
				await panel.ShowAsync(questions, review, cancellationToken, source, alertSessionId).ConfigureAwait(true));
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	private static async Task RunOverlayAsync(
		AskQuestionPanel panel,
		Window window,
		IReadOnlyList<AskQuestion> questions,
		bool review,
		CancellationToken cancellationToken,
		TaskCompletionSource<string?> completion)
	{
		try
		{
			completion.TrySetResult(await panel.ShowOverlayAsync(window, questions, review, cancellationToken).ConfigureAwait(true));
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	private Task<T?> ShowDialogAsync<T>(
		AskQuestion question,
		CancellationToken cancellationToken,
		Func<IReadOnlyList<AskAnswer>, T?> project)
	{
		var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Dispatch(() =>
		{
			if (_tab is { } tab)
			{
				_ = RunDialogAsync(tab.AskPanel, question, null, null, cancellationToken, completion, project);
				return;
			}

			if (_parentTab is { } parent)
			{
				_ = RunDialogAsync(parent.AskPanel, question, _source, _alertSessionId, cancellationToken, completion, project);
				return;
			}

			if (_owner?.FindVisualRoot() is not Window window)
			{
				completion.TrySetResult(default);
				return;
			}

			_overlayPanel ??= new AskQuestionPanel();
			_ = RunOverlayDialogAsync(_overlayPanel, window, question, cancellationToken, completion, project);
		});

		return completion.Task;
	}

	private static async Task RunOverlayDialogAsync<T>(
		AskQuestionPanel panel,
		Window window,
		AskQuestion question,
		CancellationToken cancellationToken,
		TaskCompletionSource<T?> completion,
		Func<IReadOnlyList<AskAnswer>, T?> project)
	{
		try
		{
			var raw = await panel.ShowOverlayAsync(window, [question], review: false, cancellationToken).ConfigureAwait(true);
			var answers = AskQuestions.ParseResponse(raw, [question]).Answers;
			completion.TrySetResult(project(answers));
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	private static async Task RunDialogAsync<T>(
		AskQuestionPanel panel,
		AskQuestion question,
		string? source,
		string? alertSessionId,
		CancellationToken cancellationToken,
		TaskCompletionSource<T?> completion,
		Func<IReadOnlyList<AskAnswer>, T?> project)
	{
		try
		{
			var raw = await panel.ShowAsync([question], review: false, cancellationToken, source, alertSessionId).ConfigureAwait(true);
			var answers = AskQuestions.ParseResponse(raw, [question]).Answers;
			completion.TrySetResult(project(answers));
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	private static void Dispatch(Action action)
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			action();
			return;
		}

		dispatcher.BeginInvoke(action);
	}
}
