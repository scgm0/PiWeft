using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// 输入区消息的发送形态。目标模式靠首行隐藏标记让会话创建/推进目标；
/// 计划模式直接落在会话状态上，不需要标记。
internal static class ComposerPrompt
{
	/// 斜杠命令原样发出（与 PiDeck 一致）：标记前缀会把 <c>/skill:</c>、<c>/模板</c> 变成普通正文。
	/// 只带图片没有文字时给一条兜底指令，目标模式仍然拿得到 objective。
	public static string Compose(string text, ComposerMode mode)
	{
		if (mode != ComposerMode.Goal)
		{
			return text;
		}

		var trimmed = text.Trim();
		if (trimmed.StartsWith('/'))
		{
			return text;
		}

		var objective = trimmed.Length > 0 ? text : Strings.GoalContinueInstruction;
		return $"{GoalMode.Marker}\n{objective}";
	}
}
