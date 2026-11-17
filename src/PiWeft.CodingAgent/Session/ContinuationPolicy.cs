using System.Runtime.CompilerServices;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

/// 手动继续生成的准入规则：这条请求原样再跑一遍还有机会成功时才给「继续生成」。
public static class ContinuationPolicy
{
	// 界面每个按键、每帧都会问一次，错误文本最长可以带整段供应商响应体。
	// 结论只跟那条助手消息有关，按对象身份记住上一次的结果（弱键，不吊住已丢弃的消息）。
	private static readonly ConditionalWeakTable<AssistantMessage, StrongBox<bool>> Verdicts = new();

	/// 输出被截断，或失败属于瞬态（限流、服务端、超时、传输、空响应、流提前结束）。
	/// 账号、凭据、请求参数这类错误重跑只会同样失败，交给标题栏的「重试」或换模型。
	/// 判定复用 <see cref="RetryClassifier.IsRetryable"/>；自动重试的开关与次数不影响这里。
	public static bool IsContinuable(AssistantMessage assistant) =>
		Verdicts.GetValue(assistant, static message => new StrongBox<bool>(Classify(message))).Value;

	private static bool Classify(AssistantMessage assistant) => assistant.StopReason switch
	{
		StopReason.Length => true,
		StopReason.Error => RetryClassifier.IsRetryable(assistant),
		_ => false,
	};
}
