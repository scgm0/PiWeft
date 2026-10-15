namespace PiWeft.CodingAgent;

/// <summary>一次结构化 UI 输入请求，对应 pi RPC 的 <c>extension_ui_request</c>（<c>method: "input"</c>）。</summary>
public sealed record UiInputRequest(string Title, string? Placeholder);

/// <summary>
/// 宿主交互能力。CLI 交互模式、RPC 与桌面各自实现；headless（print/JSON）不提供实现，
/// <c>ask_question</c> 会明确告知模型当前环境无法交互，而不是静默阻塞。
/// </summary>
public interface IUiHost
{
	/// <summary>是否具备可用的交互界面。</summary>
	bool HasUi { get; }

	/// <summary>展示一个输入请求并等待回答；返回 <c>null</c> 表示取消或不可用。</summary>
	Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken);
}
