using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

public sealed record BuiltinTool(AgentTool Tool, string Snippet, IReadOnlyList<string> Guidelines);

public static class BuiltinTools
{
	public static IReadOnlyList<BuiltinTool> Create(string cwd, Func<PiWeft.Ai.Model?>? model = null) =>
	[
		// tool_search 与 pi 的 builtin:tool-search 同义：MCP 工具默认是 codemode exposure，
		// 没有它就只能靠 codemode 发现。默认启用，可像其它工具一样用 defaultTools 关掉。
		ToolSearchTool.CreatePlaceholder(),

		new BuiltinTool(
			FileTools.Read(cwd, model),
			"Read file contents",
			["Use read to examine files instead of cat or sed."]),

		new BuiltinTool(
			BashTool.Create(cwd),
			"Execute bash commands (ls, grep, find, etc.)",
			["You can inspect PI_* environment variables for current model and session details."]),

		// powershell 与 pi 一样始终在目录里：非 Windows 上执行时才报
		// 「The powershell tool is only available on Windows.」，默认不启用。
		new BuiltinTool(
			PowerShellTool.Create(cwd),
			"Execute PowerShell commands",
			["You can inspect PI_* environment variables for current model and session details."]),

		new BuiltinTool(
			FileTools.Edit(cwd),
			"Make precise file edits with exact text replacement, including multiple disjoint edits in one call",
			[
				"Use edit for targeted changes. Batch changes to one file in a single call.",
				"Each edits[].oldText must exactly and uniquely match the original file. Keep matches short and non-overlapping; merge nearby changes without including large unchanged regions.",
			]),

		new BuiltinTool(
			FileTools.Write(cwd),
			"Create or overwrite files",
			["Use write only for new files or complete rewrites."]),

		new BuiltinTool(
			SearchTools.Grep(cwd),
			"Search file contents for patterns (respects .gitignore)",
			[]),

		new BuiltinTool(
			SearchTools.Find(cwd),
			"Find files by glob pattern (respects .gitignore)",
			[]),

		new BuiltinTool(
			SearchTools.Ls(cwd),
			"List directory contents",
			[]),

		// todo 工具需要会话上下文，这里先放一个未绑定的占位，实际会话构造时会重新绑定
		// （见 CodingAgentSession）。这样 --tools/--exclude-tools 的过滤对它同样生效。
		TodoTool.Create(static () => null),

		// 同 todo：ask_question 需要会话的宿主 UI，会话构造时重新绑定。
		AskQuestionTool.Create(static () => null),
	];

	/// 可被 <c>--tools</c>/<c>--exclude-tools</c> 或 <c>defaultTools</c> 选中的完整目录。
	/// 比默认集多出 <c>codemode</c>：与 pi 一样它默认不启用，需要显式选中
	/// （<c>--tools +codemode</c> 或 <c>"defaultTools": ["+codemode"]</c>）。
	public static IReadOnlyList<BuiltinTool> Catalog(string cwd, Func<PiWeft.Ai.Model?>? model = null) =>
	[
		.. Create(cwd, model),
		// codemode 的可调用工具集来自会话，这里先放占位，会话构造时再重新绑定
		// （见 CodingAgentSession）。
		CodeModeTool.CreatePlaceholder(),
	];

	public static IReadOnlyList<AgentTool> Tools(string cwd, Func<PiWeft.Ai.Model?>? model = null) =>
		[.. Create(cwd, model).Select(static builtin => builtin.Tool)];

	public static IReadOnlyList<AgentTool> CatalogTools(string cwd, Func<PiWeft.Ai.Model?>? model = null) =>
		[.. Catalog(cwd, model).Select(static builtin => builtin.Tool)];

	public static IReadOnlyList<JsonObject> Describe(IEnumerable<AgentTool> tools) =>
		[.. tools.Select(static tool => new JsonObject { ["name"] = tool.Name })];
}
