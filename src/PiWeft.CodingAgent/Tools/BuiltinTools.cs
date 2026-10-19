using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Tools;

/// <summary>一个内置工具，以及它对提示词的贡献。</summary>
public sealed record BuiltinTool(AgentTool Tool, string Snippet, IReadOnlyList<string> Guidelines);

/// <summary>内置编码工具集，顺序与 pi 的声明顺序一致。</summary>
public static class BuiltinTools
{
	/// <summary>按 pi 声明顺序排列的工具，系统提示里也使用同一顺序。</summary>
	public static IReadOnlyList<BuiltinTool> Create(string cwd) =>
	[
		new BuiltinTool(
			FileTools.Read(cwd),
			"Read file contents",
			["Use read to examine files instead of cat or sed."]),

		new BuiltinTool(
			BashTool.Create(cwd),
			"Execute bash commands (ls, grep, find, etc.)",
			["You can inspect PI_* environment variables for current model and session details."]),

		.. OperatingSystem.IsWindows()
			? [new BuiltinTool(
				PowerShellTool.Create(cwd),
				"Execute PowerShell commands",
				["You can inspect PI_* environment variables for current model and session details."])]
			: Array.Empty<BuiltinTool>(),

		new BuiltinTool(
			FileTools.Edit(cwd),
			"Make precise file edits with exact text replacement, including multiple disjoint edits in one call",
			[
				"Use edit for precise changes (edits[].oldText must match exactly)",
				"When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
				"Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
				"Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions.",
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

	/// <summary>只取可执行的工具，供 <see cref="AgentContext.Tools"/> 使用。</summary>
	public static IReadOnlyList<AgentTool> Tools(string cwd) => [.. Create(cwd).Select(static builtin => builtin.Tool)];

	/// <summary>为自定义工具集构建工具声明列表。</summary>
	public static IReadOnlyList<JsonObject> Describe(IEnumerable<AgentTool> tools) =>
		[.. tools.Select(static tool => new JsonObject { ["name"] = tool.Name })];
}
