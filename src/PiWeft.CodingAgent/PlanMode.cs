using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent;

/// <summary>
/// 原生计划模式：只读探索 + 计划确认。与 PiDeck 的 <c>pi-deck-plan-mode.ts</c> 对齐：
/// 桌面输入框的 Plan 开关会在消息前注入隐藏标记 <c>__PI_DECK_PLAN_MODE__</c>，
/// 本实现在此识别它、停用写工具，并在下一条无标记消息时自动退出。
/// </summary>
public static partial class PlanMode
{
	/// <summary>PiDeck 输入框使用的隐藏标记。</summary>
	public const string Marker = "__PI_DECK_PLAN_MODE__";

	/// <summary>计划模式下被停用的写工具。</summary>
	public static readonly string[] DisabledTools = ["edit", "write"];

	/// <summary>注入到系统提示词的 <c>plan_mode</c> 章节。</summary>
	public const string Instructions =
		"<plan_mode>\n" +
		"You are in plan mode. Do not modify the workspace: the edit and write tools are unavailable, and only read-only shell commands are allowed. " +
		"Explore with read-only tools, then output a numbered plan and stop for approval.\n" +
		"</plan_mode>";

	/// <summary>如果文本以标记开头就剥离它；返回是否命中标记。</summary>
	public static bool TryStripMarker(string text, out string cleaned)
	{
		if (!text.StartsWith(Marker, StringComparison.Ordinal))
		{
			cleaned = text;
			return false;
		}

		// 标记独占首行，其余内容原样保留（PiDeck 会把可见指令接在标记之后）。
		var newline = text.IndexOf('\n');
		cleaned = newline < 0 ? string.Empty : text[(newline + 1)..];
		return true;
	}

	/// <summary>
	/// 计划模式下 shell 是否只读：必须是已知安全命令，且不含破坏性片段。
	/// 与 PiDeck 的 <c>isSafeCommand</c> 同一判据。
	/// </summary>
	public static bool IsReadOnlyShellCommand(string command) =>
		!DestructiveRegex().IsMatch(command) && SafeRegex().IsMatch(command);

	[GeneratedRegex(
		@"\brm\b|\brmdir\b|\bmv\b|\bcp\b|\bmkdir\b|\btouch\b|\bchmod\b|\bchown\b|\btee\b|\btruncate\b|(^|[^<])>(?!>)|>>|\bnpm\s+(install|uninstall|update|ci|link|publish)|\byarn\s+(add|remove|install|publish)|\bpnpm\s+(add|remove|install)|\bpip\s+(install|uninstall)|\bgit\s+(add|commit|push|pull|merge|rebase|reset|checkout|branch\s+-[dD]|stash|cherry-pick|revert|tag|init|clone)|\bsudo\b|\bkill\b|\b(vim?|nano|emacs|code|subl)\b",
		RegexOptions.IgnoreCase)]
	private static partial Regex DestructiveRegex();

	[GeneratedRegex(
		@"^\s*(cat|head|tail|less|more|grep|find|ls|pwd|echo|printf|wc|sort|uniq|diff|file|stat|du|df|tree|which|whereis|type|env|printenv|uname|whoami|id|date|ps)\b|^\s*git\s+(status|log|diff|show|branch|remote|config\s+--get|ls-)|^\s*npm\s+(list|ls|view|info|search|outdated|audit)|^\s*yarn\s+(list|info|why|audit)|^\s*node\s+--version|^\s*python\s+--version|^\s*curl\s|^\s*wget\s+-O\s*-|^\s*jq\b|^\s*sed\s+-n|^\s*awk\b|^\s*rg\b|^\s*fd\b|^\s*bat\b|^\s*eza\b",
		RegexOptions.IgnoreCase)]
	private static partial Regex SafeRegex();
}
