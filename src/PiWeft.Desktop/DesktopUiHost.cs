using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// 桌面的 <see cref="IUiHost"/>：把 <c>ask_question</c> 的请求交给会话标签自带的
/// 内联问询卡（<see cref="AskQuestionPanel"/>）。卡片在 UI 线程上构建，代理循环在后台等待回答。
/// 为什么不再有窗口级弹窗：PiDeck 的 ask 卡片钉在对话区底部，与输入框同级，用户能一边翻历史
/// 一边回答；遮罩弹窗会把整窗压暗并抢走滚动。同一个面板也承接扩展的
/// <c>select / confirm / editor</c> 请求 —— 它们同样是“代理停下等你输入”。
internal sealed class DesktopUiHost : IUiHost
{
	private SessionTab? _tab;
	private UIElement? _owner;
	private AskQuestionPanel? _overlayPanel;

	public bool HasUi => true;

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

	public Task<string?> SelectAsync(UiSelectRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<string?>(
			new AskQuestion("dialog", "select", request.Title,
				[.. request.Options.Select(static option => new AskOption(option, option, null))], null, null),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.String
				? answers[0].Value!.GetValue<string>()
				: null);

	public Task<bool?> ConfirmAsync(UiConfirmRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<bool?>(
			new AskQuestion("dialog", "confirm", request.Message is { Length: > 0 } ? $"{request.Title}\n{request.Message}" : request.Title, null, null, null),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.True
				? true
				: answers.Count > 0 ? false : null);

	public Task<string?> EditorAsync(UiEditorRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<string?>(
			new AskQuestion("dialog", "editor", request.Title, null, null, request.Prefill),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.String
				? answers[0].Value!.GetValue<string>()
				: null);

	public void Notify(UiNotifyRequest request)
	{
	}

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
				_ = RunAsync(tab, questions, review, cancellationToken, completion);
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
		SessionTab tab,
		IReadOnlyList<AskQuestion> questions,
		bool review,
		CancellationToken cancellationToken,
		TaskCompletionSource<string?> completion)
	{
		try
		{
			completion.TrySetResult(await tab.AskPanel.ShowAsync(questions, review, cancellationToken).ConfigureAwait(true));
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
				_ = RunDialogAsync(tab, question, cancellationToken, completion, project);
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
			var (answers, _) = AskQuestions.ParseResponse(raw, [question]);
			completion.TrySetResult(project(answers));
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	private static async Task RunDialogAsync<T>(
		SessionTab tab,
		AskQuestion question,
		CancellationToken cancellationToken,
		TaskCompletionSource<T?> completion,
		Func<IReadOnlyList<AskAnswer>, T?> project)
	{
		try
		{
			var raw = await tab.AskPanel.ShowAsync([question], review: false, cancellationToken).ConfigureAwait(true);
			var (answers, _) = AskQuestions.ParseResponse(raw, [question]);
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
