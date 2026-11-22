using System.Collections.Frozen;
using System.Text.RegularExpressions;
using PiWeft.Agent;
using PiWeft.CodingAgent.Compatibility;

namespace PiWeft.CodingAgent;

/// 原生计划模式：只读探索 + 计划确认。与 PiDeck 的 <c>pi-deck-plan-mode.ts</c> 对齐：
/// 桌面输入框的 Plan 开关会在消息前注入隐藏标记 <c>__PI_DECK_PLAN_MODE__</c>，
/// 本实现在此识别它、停用写工具，并在下一条无标记消息时自动退出。
public static partial class PlanMode
{
	public const string Marker = PiDeckProtocol.PlanModeMarker;

	/// 计划模式停用的工具。`powershell` 与 bash 是两条独立的 shell，守卫只查 bash；
	/// 不一起停用的话，`Set-Content` / `Remove-Item` 能绕过只读承诺。
	/// 这里只列要靠名字识别的旧路径；完整的准入看 <see cref="IsToolAllowed"/>。
	public static readonly string[] DisabledTools = ["edit", "write", "powershell"];

	/// 工具集与每次调用前的守卫共用这一条：默认拒绝，只读工具显式放行，
	/// MCP 与扩展工具没有声明就进不来。
	public static bool IsToolAllowed(AgentTool tool) =>
		tool.PlanAccess != ToolPlanAccess.Denied && !DisabledToolSet.Contains(tool.Name);

	/// 工具本身不可用（不含 bash 的命令级判据）。
	public static bool IsToolDenied(AgentTool tool) => !IsToolAllowed(tool);

	/// 工具过滤与每次调用前的守卫都查这张表：用冻结集合，别每次线性扫数组。
	private static readonly FrozenSet<string> DisabledToolSet =
		DisabledTools.ToFrozenSet(StringComparer.Ordinal);

	public static bool IsDisabled(string name) => DisabledToolSet.Contains(name);

	public const string Instructions =
		"<plan_mode>\n" +
		"You are in plan mode. Do not modify the workspace: writing tools are unavailable, and only read-only shell commands are allowed. " +
		"If a requirement is unclear, use ask_question before writing the plan. " +
		"Explore with read-only tools, then submit the plan with the submit_plan tool: pass the ordered steps as an array, then stop for approval.\n" +
		"If submit_plan is unavailable, end the reply with a line that is exactly `Plan:` (the word and a colon, no bold, no other text on the line).\n" +
		"Plan format:\n" +
		"- Follow it with numbered steps, one per line: `1. ...`, `2. ...`. Keep each step to one short sentence; do not merge or nest steps.\n" +
		"- Put the numbered steps at the end of the reply; the UI parses that section and ignores anything after it.\n" +
		"</plan_mode>";

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

	/// 计划模式下 shell 是否只读：必须是已知安全命令，且不含破坏性片段。
	/// 与 PiDeck 的 <c>isSafeCommand</c> 同一判据。
	public static bool IsReadOnlyShellCommand(string command) =>
		!DestructiveRegex().IsMatch(command) && SafeRegex().IsMatch(command);

	[GeneratedRegex(
		@"\brm\b|\brmdir\b|\bmv\b|\bcp\b|\bmkdir\b|\btouch\b|\bchmod\b|\bchown\b|\btee\b|\btruncate\b|(^|[^<])>(?!>)|>>|\bnpm\s+(install|uninstall|update|ci|link|publish)|\byarn\s+(add|remove|install|publish)|\bpnpm\s+(add|remove|install)|\bpip\s+(install|uninstall)|\bgit\s+(add|commit|push|pull|merge|rebase|reset|checkout|branch\s+-[dD]|stash|cherry-pick|revert|tag|init|clone)|\bsudo\b|\bkill\b|\b(vim?|nano|emacs|code|subl)\b|" +
		// 只读命令名后仍能写的参数：find 的删除/执行、sort 的输出、curl 的下载、awk 的解释器调用。
		@"\bfind\b[^\n]*\s-(delete|exec|execdir|ok|okdir|fprint|fprint0|fls|fprintf)\b|\bsort\b[^\n]*\s-o\b|\bcurl\b[^\n]*\s(-o|-O|--output|--remote-name)\b|\bawk\b[^\n]*\b(system|exec)\s*\(|" +
		// Git 的写参数：--output 落盘、branch 建分支、remote 改配置、config 非 --get。
		@"\bgit\s+(log|diff|show)\b[^\n]*--output|\bgit\s+branch\s+[^\s-]|\bgit\s+remote\s+(add|remove|rm|set-url|prune|update|rename)\b|\bgit\s+config\s+(?!--get\b)|" +
		// env 带参数可以执行任意命令，date -s 改系统时间。
		@"\benv\s+\S|\bdate\s+-s\b",
		RegexOptions.IgnoreCase)]
	private static partial Regex DestructiveRegex();

	[GeneratedRegex(
		@"^\s*(cat|head|tail|grep|find|ls|pwd|echo|printf|wc|sort|uniq|diff|file|stat|du|df|tree|which|whereis|type|printenv|uname|whoami|id|date|ps)\b|^\s*env\s*$|^\s*git\s+(status|log|diff|show|branch|remote|config\s+--get|ls-|rev-parse|describe|blame|grep|shortlog|show-ref|for-each-ref|cat-file|ls-tree|ls-files)\b|^\s*npm\s+(list|ls|view|info|search|outdated|audit)|^\s*yarn\s+(list|info|why|audit)|^\s*node\s+--version|^\s*python\s+--version|^\s*curl\s|^\s*wget\s+-O\s*-|^\s*jq\b|^\s*sed\s+-n|^\s*awk\b|^\s*rg\b|^\s*fd\b|^\s*bat\b|^\s*eza\b",
		RegexOptions.IgnoreCase)]
	private static partial Regex SafeRegex();
}
