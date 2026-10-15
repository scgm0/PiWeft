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
