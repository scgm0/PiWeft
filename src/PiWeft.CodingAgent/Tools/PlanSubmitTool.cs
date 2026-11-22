using System.Text.Json.Nodes;
using PiWeft.Agent;

namespace PiWeft.CodingAgent.Tools;

/// 计划模式的结构化提交入口：模型把步骤作为数组传进来，UI 不必再从回复文本里解析
/// `Plan:` 标题。文本解析仍保留，供没有这个工具的宿主与旧会话回放使用。
public static class PlanSubmitTool
{
	public const string Name = "submit_plan";

	/// 只声明给计划模式。工具在普通模式下不出现在工具面，守卫也会拒绝直接调用。
	public static BuiltinTool Create(Func<CodingAgentSession?> session) => new(
		new AgentTool
		{
			Name = Name,
			Label = "Submit Plan",
			Description =
				"Submit the implementation plan for user review. Use this instead of writing a `Plan:` section. " +
				"Call it once, after read-only exploration, with the ordered steps. Then stop and wait for the user's decision.",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["steps"] = new JsonObject
					{
						["type"] = "array",
						["description"] = "Ordered implementation steps, one short sentence each.",
						["items"] = new JsonObject { ["type"] = "string" },
					},
				},
				["required"] = new JsonArray("steps"),
				["additionalProperties"] = false,
			},
			PlanAccess = ToolPlanAccess.Allowed,
			Execute = invocation => Task.FromResult(Submit(session(), invocation)),
		},
		"Submit an ordered plan for approval",
		[
			"submit_plan is only available in plan mode. After it returns, stop: the user reviews the plan and decides whether to execute.",
			"One short sentence per step, no nested numbering, no slash commands.",
		]);

	private static AgentToolResult Submit(CodingAgentSession? session, AgentToolInvocation invocation)
	{
		if (session is null)
		{
			return AgentToolResult.Error("submit_plan is not bound to a session");
		}

		if (invocation.Arguments["steps"] is not JsonArray array)
		{
			return AgentToolResult.Error("submit_plan expects a steps array");
		}

		var steps = new List<string>(array.Count);
		foreach (var node in array)
		{
			if (node is JsonValue value && value.TryGetValue<string>(out var text))
			{
				steps.Add(text);
			}
		}

		return session.SubmitPlanFromTool(steps);
	}
}
