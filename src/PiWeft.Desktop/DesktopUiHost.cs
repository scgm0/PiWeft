using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// <summary>
/// 桌面的 <see cref="IUiHost"/>：把 <c>ask_question</c> 的请求交给窗口级问询卡片。
/// 卡片在 UI 线程上构建，代理循环在后台等待回答。每个会话标签持有自己的实例。
/// </summary>
internal sealed class DesktopUiHost : IUiHost
{
	private readonly AskQuestionPanel _panel = new();
	private UIElement? _owner;

	public bool HasUi => true;

	/// <summary>会话标签的视图，用来定位窗口。</summary>
	public void Attach(UIElement owner) => _owner = owner;

	public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken)
	{
		var questions = AskQuestions.ParseEnvelope(request.Title);
		if (questions is null || questions.Count == 0)
		{
			return Task.FromResult<string?>(null);
		}

		var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Dispatch(() =>
		{
			if (_owner is not { } owner)
			{
				completion.TrySetResult(null);
				return;
			}

			_ = ShowAsync(owner, questions, cancellationToken, completion);
		});

		return completion.Task;
	}

	/// <summary>单选对话框：复用问询卡片的 select 渲染。</summary>
	public Task<string?> SelectAsync(UiSelectRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<string?>(
			new AskQuestion("dialog", "select", request.Title,
				[.. request.Options.Select(static option => new AskOption(option, option, null))], null, null),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.String
				? answers[0].Value!.GetValue<string>()
				: null);

	/// <summary>确认对话框：复用问询卡片的 confirm 渲染。</summary>
	public Task<bool?> ConfirmAsync(UiConfirmRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<bool?>(
			new AskQuestion("dialog", "confirm", request.Message is { Length: > 0 } ? $"{request.Title}\n{request.Message}" : request.Title, null, null, null),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.True
				? true
				: answers.Count > 0 ? false : null);

	/// <summary>多行编辑对话框：复用问询卡片的 editor 渲染。</summary>
	public Task<string?> EditorAsync(UiEditorRequest request, CancellationToken cancellationToken) =>
		ShowDialogAsync<string?>(
			new AskQuestion("dialog", "editor", request.Title, null, null, request.Prefill),
			cancellationToken,
			static answers => answers.Count > 0 && answers[0].Value?.GetValueKind() == System.Text.Json.JsonValueKind.String
				? answers[0].Value!.GetValue<string>()
				: null);

	/// <summary>桌面没有全局通知面：暂不实现（保持 no-op，避免噪声）。</summary>
	public void Notify(UiNotifyRequest request)
	{
	}

	/// <summary>把单问题卡片挂到窗口上并等待回答；取消或关闭返回 <c>null</c>。</summary>
	private Task<T?> ShowDialogAsync<T>(
		AskQuestion question,
		CancellationToken cancellationToken,
		Func<IReadOnlyList<AskAnswer>, T?> project)
	{
		var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Dispatch(() =>
		{
			if (_owner is not { } owner)
			{
				completion.TrySetResult(default);
				return;
			}

			_ = RunDialogAsync(owner, question, cancellationToken, completion, project);
		});

		return completion.Task;
	}

	private async Task RunDialogAsync<T>(
		UIElement owner,
		AskQuestion question,
		CancellationToken cancellationToken,
		TaskCompletionSource<T?> completion,
		Func<IReadOnlyList<AskAnswer>, T?> project)
	{
		try
		{
			var raw = await _panel.ShowAsync(owner, [question], cancellationToken).ConfigureAwait(true);
			var (answers, _) = AskQuestions.ParseResponse(raw, [question]);
			completion.TrySetResult(project(answers));
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	private async Task ShowAsync(
		UIElement owner,
		IReadOnlyList<AskQuestion> questions,
		CancellationToken cancellationToken,
		TaskCompletionSource<string?> completion)
	{
		try
		{
			completion.TrySetResult(await _panel.ShowAsync(owner, questions, cancellationToken).ConfigureAwait(true));
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	/// <summary>关闭未完成的卡片（标签或窗口销毁）。</summary>
	public void Hide() => Dispatch(_panel.Close);

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
