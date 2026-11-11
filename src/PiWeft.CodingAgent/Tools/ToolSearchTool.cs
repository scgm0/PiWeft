using System.Text.Json.Nodes;
using PiWeft.Agent;

namespace PiWeft.CodingAgent.Tools;

/// 原生 <c>tool_search</c> 工具，对应 pi 的 <c>builtin:tool-search</c> 扩展：在尚未声明的
/// <c>codemode</c>/<c>deferred</c> 工具里按 BM25 检索，把命中的工具写成 <c>tool-load</c> 条目
/// 并声明给模型。真正的实现在会话里（要读写会话记录与工具集），目录里只放占位。
public static class ToolSearchTool
{
	public const string Name = "tool_search";

	public const string Snippet = "Search for tools that are not loaded yet and load the matches";

	public const string Description =
		"# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools for the next model call.\n\n" +
		"Some of the tools, such as tools of MCP servers, may not have been provided to you upfront, and you should use this tool (`tool_search`) to search for the required tools. " +
		"For MCP tool discovery, always use `tool_search`.";

	/// 参数声明：会话里的实现直接复用，避免两处 schema 漂移。
	public static JsonObject Parameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["query"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Search query for deferred tools.",
			},
			["limit"] = new JsonObject
			{
				["type"] = "number",
				["description"] = "Maximum number of tools to return. Defaults to 8.",
			},
		},
		["required"] = new JsonArray("query"),
	};

	/// 目录与工具集里的占位：会话构造时换成绑定当前分支的实现。
	public static BuiltinTool CreatePlaceholder() => new(
		new AgentTool
		{
			Name = Name,
			Label = Name,
			Description = Description,
			Parameters = Parameters(),
			Execute = static _ => Task.FromResult(AgentToolResult.Error("tool_search is not available in this session")),
		},
		Snippet,
		[]);
}
