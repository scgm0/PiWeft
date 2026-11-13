using System.Text.Json.Nodes;
using PiWeft.Agent;

namespace PiWeft.CodingAgent.Tools;

/// 关联会话的三个工具：<c>start_session</c> / <c>send_to_session</c> / <c>list_linked_sessions</c>。
/// 名字与用途对齐 PiDeck 的 <c>pi-deck-linked-sessions.ts</c>；没有宿主时（纯 CLI）不注册。
public static class LinkedSessionsTool
{
	public const string StartName = "start_session";

	public const string SendName = "send_to_session";

	public const string ListName = "list_linked_sessions";

	public static bool IsName(string name) =>
		name is StartName or SendName or ListName;

	/// 三个工具名。宿主存在时无论 `--tools` 怎么配置都会补上（PiDeck 的扩展工具同样不受内置工具筛选影响）。
	public static IReadOnlyList<string> Names { get; } = [StartName, SendName, ListName];

	/// 按名字取一个工具并绑定到会话（会话构造/工具刷新时用）。
	public static BuiltinTool Bind(string name, Func<CodingAgentSession?> session) =>
		Create(session).Single(tool => string.Equals(tool.Tool.Name, name, StringComparison.Ordinal));

	/// 全部三个工具；<paramref name="session"/> 在调用时才取当前会话，切换会话不需要重建工具。
	public static IReadOnlyList<BuiltinTool> Create(Func<CodingAgentSession?> session) =>
	[
		new BuiltinTool(
			new AgentTool
			{
				Name = StartName,
				Label = "Start Session",
				Description =
					"Start a separate, user-visible session in the same project for a self-contained task, or register a proposal card for the user to decide. " +
					"initiation decides who approves: user_asked starts immediately (the user explicitly asked for another/parallel/background session); " +
					"offer asks the user with a New session / Do it here / Dismiss card and waits for the answer (default); " +
					"own_initiative only shows the card and returns immediately (work you found but the user did not ask for). " +
					"The child cannot see this conversation: write a self-contained brief with scope, definition of done, how to verify, and what not to touch. " +
					"The call returns a JSON status; a started child reports its final reply back here when each turn ends.",
				Parameters = StartParameters(),
				Execute = invocation => ExecuteStart(session(), invocation),
			},
			"Start a separate, user-visible session for a self-contained task",
			[
				"Use start_session only for work that should live beyond this turn and has its own output. Questions, a few file reads, or steps of the current task belong in this session.",
			"initiation: user_asked when the user explicitly asked for a separate/parallel/background session; offer when the user named the task but did not say how (a card asks New session / Do it here); own_initiative for follow-up work you found but the user did not ask for (card only, no session until the user clicks).",
				"The brief must be self-contained: the child cannot see this conversation. Write scope, done criteria, verification and forbidden areas in your own words.",
				"Do not edit the same files from both sessions. Use send_to_session for follow-ups instead of starting another session for the same task.",
				"A child report is the child's own account, not verified evidence. Check files, diffs and command output before telling the user the work is done; do not predict or poll the child.",
			]),

		new BuiltinTool(
			new AgentTool
			{
				Name = SendName,
				Label = "Send To Session",
				Description =
					"Send a message to a directly linked session: a child you started, or the parent session that started you. " +
					"The message queues behind the recipient's current turn; this call does not wait for a reply.",
				Parameters = SendParameters(),
				Execute = invocation => ExecuteSend(session(), invocation),
			},
			"Send a follow-up message to a linked parent or child session",
			[
				"Use send_to_session for requirements on a linked session; do not start another session for the same task.",
			]),

		new BuiltinTool(
			new AgentTool
			{
				Name = ListName,
				Label = "List Linked Sessions",
				Description =
					"List the child sessions this session started (state, turns, last report) and the parent session when this is a linked child.",
				Parameters = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject(),
				},
				Execute = invocation => ExecuteList(session(), invocation),
			},
			"List linked child sessions and their latest reports",
			[]),
	];

	private static JsonObject StartParameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["title"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Short title for the child session; a few words (≤80 chars). Defaults to the first line of the prompt.",
			},
			["prompt"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Self-contained brief for the child: files/scope, definition of done, verification, and forbidden areas.",
			},
			["initiation"] = new JsonObject
			{
				["type"] = "string",
				["enum"] = new JsonArray("user_asked", "offer", "own_initiative"),
				["description"] = "user_asked starts immediately; offer asks the user first and waits (default); own_initiative only registers a proposal card.",
			},
			["proposal_id"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Start a previously registered own_initiative proposal (when the user says to open it); title/prompt can be omitted then.",
			},
		},
	};

	private static JsonObject SendParameters() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["session_id"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Target session id: a child started by this session, or the parent session that started this one.",
			},
			["message"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Message to send; it queues behind the recipient's current turn.",
			},
		},
		["required"] = new JsonArray("session_id", "message"),
	};

	private static async Task<AgentToolResult> ExecuteStart(CodingAgentSession? session, AgentToolInvocation invocation)
	{
		var hub = session?.Linked;
		if (hub is null)
		{
			return AgentToolResult.Error("Linked sessions are not available in this host.");
		}

		var result = await hub.StartAsync(
			Text(invocation.Arguments["title"]),
			Text(invocation.Arguments["prompt"]) ?? string.Empty,
			Text(invocation.Arguments["initiation"]) ?? LinkedSessionProposal.Offer,
			Text(invocation.Arguments["proposal_id"]),
			invocation.CancellationToken).ConfigureAwait(false);
		return Operation(result);
	}

	private static async Task<AgentToolResult> ExecuteSend(CodingAgentSession? session, AgentToolInvocation invocation)
	{
		var hub = session?.Linked;
		if (hub is null)
		{
			return AgentToolResult.Error("Linked sessions are not available in this host.");
		}

		var result = await hub.SendAsync(
			Text(invocation.Arguments["session_id"]) ?? string.Empty,
			Text(invocation.Arguments["message"]) ?? string.Empty,
			invocation.CancellationToken).ConfigureAwait(false);
		return Operation(result);
	}

	private static Task<AgentToolResult> ExecuteList(CodingAgentSession? session, AgentToolInvocation invocation)
	{
		var hub = session?.Linked;
		if (hub is null)
		{
			return Task.FromResult(AgentToolResult.Error("Linked sessions are not available in this host."));
		}

		return Task.FromResult(AgentToolResult.Text(hub.List().ToJsonString()));
	}

	/// PiDeck 的关联会话卡片按「失败：」前缀识别错误；成功结果是 JSON 文本。
	private static AgentToolResult Operation(LinkedSessionOperation result) =>
		result.Ok ? AgentToolResult.Text(result.Text) : AgentToolResult.Error("失败：" + result.Text);

	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0
			? text.Trim()
			: null;
}
