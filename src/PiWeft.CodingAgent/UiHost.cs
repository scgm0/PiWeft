namespace PiWeft.CodingAgent;

public sealed record UiInputRequest(string Title, string? Placeholder);

public sealed record UiSelectRequest(string Title, IReadOnlyList<string> Options, int? TimeoutMs);

public sealed record UiConfirmRequest(string Title, string? Message, int? TimeoutMs);

public sealed record UiEditorRequest(string Title, string? Prefill);

public sealed record UiNotifyRequest(string Message, string NotifyType);

public sealed record UiStatusRequest(string Key, string? Text);

public sealed record UiWidgetRequest(string Key, IReadOnlyList<string>? Lines, string? Placement);

public sealed record UiTitleRequest(string Title);

/// 宿主交互能力。CLI 交互模式、RPC 与桌面各自实现；headless（print/JSON）不提供实现，
/// <c>ask_question</c> 会明确告知模型当前环境无法交互，不静默阻塞。
/// 对话框方法对应 pi RPC <c>extension_ui</c> 协议（select/confirm/input/editor），
/// 返回 <c>null</c> 表示取消、超时或宿主不支持；<see cref="Notify"/> 是不等待回应的通知。
public interface IUiHost
{
	bool HasUi { get; }

	/// 是否使用 PiDeck 的扩展 widget 协议。桌面端已有原生 todo 卡片，不需要再推
	/// pi-deck-todo；RPC 宿主（PiDeck）需要。
	bool SupportsPiDeckWidgets => false;

	Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken);

	Task<string?> SelectAsync(UiSelectRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

	Task<bool?> ConfirmAsync(UiConfirmRequest request, CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

	Task<string?> EditorAsync(UiEditorRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

	void Notify(UiNotifyRequest request)
	{
	}

	void SetStatus(UiStatusRequest request)
	{
	}

	void SetStatus(string key, string? text) => SetStatus(new UiStatusRequest(key, text));

	void SetWidget(UiWidgetRequest request)
	{
	}

	void SetWidget(string key, IReadOnlyList<string>? lines, string? placement = null) =>
		SetWidget(new UiWidgetRequest(key, lines, placement));

	void SetTitle(UiTitleRequest request)
	{
	}

	void SetTitle(string title) => SetTitle(new UiTitleRequest(title));

	void SetEditorText(string text)
	{
	}
}
