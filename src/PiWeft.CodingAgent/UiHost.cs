namespace PiWeft.CodingAgent;

/// <summary>一次结构化 UI 输入请求，对应 pi RPC 的 <c>extension_ui_request</c>（<c>method: "input"</c>）。</summary>
public sealed record UiInputRequest(string Title, string? Placeholder);

/// <summary>单选对话框，对应 <c>extension_ui_request</c> 的 <c>method: "select"</c>。</summary>
public sealed record UiSelectRequest(string Title, IReadOnlyList<string> Options, int? TimeoutMs);

/// <summary>确认对话框，对应 <c>extension_ui_request</c> 的 <c>method: "confirm"</c>。</summary>
public sealed record UiConfirmRequest(string Title, string? Message, int? TimeoutMs);

/// <summary>多行编辑对话框，对应 <c>extension_ui_request</c> 的 <c>method: "editor"</c>。</summary>
public sealed record UiEditorRequest(string Title, string? Prefill);

/// <summary>通知（fire-and-forget），对应 <c>extension_ui_request</c> 的 <c>method: "notify"</c>。</summary>
public sealed record UiNotifyRequest(string Message, string NotifyType);

/// <summary>状态栏条目，对应 <c>method: "setStatus"</c>；<see cref="Text"/> 为 null 时清除。</summary>
public sealed record UiStatusRequest(string Key, string? Text);

/// <summary>输入框上/下方的 widget，对应 <c>method: "setWidget"</c>；<see cref="Lines"/> 为 null 时清除。</summary>
public sealed record UiWidgetRequest(string Key, IReadOnlyList<string>? Lines, string? Placement);

/// <summary>窗口/会话标题，对应 <c>method: "setTitle"</c>。</summary>
public sealed record UiTitleRequest(string Title);

/// <summary>
/// 宿主交互能力。CLI 交互模式、RPC 与桌面各自实现；headless（print/JSON）不提供实现，
/// <c>ask_question</c> 会明确告知模型当前环境无法交互，而不是静默阻塞。
/// <para>
/// 对话框方法对应 pi RPC <c>extension_ui</c> 协议（select/confirm/input/editor），
/// 返回 <c>null</c> 表示取消、超时或宿主不支持；<see cref="Notify"/> 是不等待回应的通知。
/// </para>
/// </summary>
public interface IUiHost
{
	/// <summary>是否具备可用的交互界面。</summary>
	bool HasUi { get; }

	/// <summary>展示一个输入请求并等待回答；返回 <c>null</c> 表示取消或不可用。</summary>
	Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken);

	/// <summary>单选对话框；返回所选选项文本，<c>null</c> 表示取消或不可用。</summary>
	Task<string?> SelectAsync(UiSelectRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

	/// <summary>确认对话框；<c>null</c> 表示宿主不支持，<c>false</c> 表示用户拒绝或取消。</summary>
	Task<bool?> ConfirmAsync(UiConfirmRequest request, CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

	/// <summary>多行编辑对话框；返回编辑后的文本，<c>null</c> 表示取消或不可用。</summary>
	Task<string?> EditorAsync(UiEditorRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

	/// <summary>展示一条通知；不等待回应，宿主可忽略。</summary>
	void Notify(UiNotifyRequest request)
	{
	}

	/// <summary>设置/清除状态栏条目（<c>setStatus</c>）；不等待回应。</summary>
	void SetStatus(UiStatusRequest request)
	{
	}

	void SetStatus(string key, string? text) => SetStatus(new UiStatusRequest(key, text));

	/// <summary>设置/清除输入框附近的 widget（<c>setWidget</c>）；不等待回应。</summary>
	void SetWidget(UiWidgetRequest request)
	{
	}

	void SetWidget(string key, IReadOnlyList<string>? lines, string? placement = null) =>
		SetWidget(new UiWidgetRequest(key, lines, placement));

	/// <summary>设置窗口/会话标题（<c>setTitle</c>）。</summary>
	void SetTitle(UiTitleRequest request)
	{
	}

	void SetTitle(string title) => SetTitle(new UiTitleRequest(title));

	/// <summary>替换输入框文本（<c>set_editor_text</c>）。</summary>
	void SetEditorText(string text)
	{
	}
}
