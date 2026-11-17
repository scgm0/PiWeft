using System.Runtime.CompilerServices;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

/// 手动继续生成的准入规则：这条请求原样再跑一遍还有机会成功时才给「继续生成」。
public static class ContinuationPolicy
{
	// 界面每个按键、每帧都会问一次，错误文本最长可以带整段供应商响应体。
	// 结论只跟那条助手消息有关，按对象身份记住上一次的结果（弱键，不吊住已丢弃的消息）。
	private static readonly ConditionalWeakTable<AssistantMessage, StrongBox<bool>> Verdicts = new();

	/// 输出被截断，或失败在（可能换过模型后）再发一次还有机会成功：瞬态失败、配额/余额、
	/// 凭据、模型不存在、上下文超限。请求参数、内容过滤、证书这类重跑不会变好的不接。
	/// 判定看 <see cref="RetryClassifier.IsManuallyRecoverable"/>；自动重试的开关与次数不影响这里。
	public static bool IsContinuable(AssistantMessage assistant) =>
		Verdicts.GetValue(assistant, static message => new StrongBox<bool>(Classify(message))).Value;

	private static bool Classify(AssistantMessage assistant) => assistant.StopReason switch
	{
		StopReason.Length => true,
		StopReason.Error => RetryClassifier.IsManuallyRecoverable(assistant),
		_ => false,
	};
}
