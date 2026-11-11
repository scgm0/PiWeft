using System.Text.Json.Nodes;
using PiWeft.CodingAgent;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 子代理工具在调用过程里的展示：行内摘要与展开后的字段。
public sealed class SubagentToolDisplayTests
{
	private static ChatEntry Call(string tool, string arguments) => new()
	{
		Role = ChatRole.Tool,
		Text = string.Empty,
		ToolName = tool,
		ToolCallId = "call-1",
		ToolArguments = arguments,
		IsStreaming = true,
	};

	[Fact]
	public void DelegateSummaryNamesTheAgentAndTheFirstTaskLine()
	{
		var entry = Call("delegate", """{"agent":"scout","task":"找出登录流程的入口。\n另外看下测试。"}""");
		var text = ExecutionProcess.ToolAction(entry, running: false);
		Assert.Equal("委派 scout · 找出登录流程的入口。", text);
	}

	[Fact]
	public void WaitSummaryListsTheTargets()
	{
		var entry = Call("wait_subagents", """{"names":["code-review","widget-wiring"],"timeout_ms":600000}""");
		Assert.Equal("等待子代理 code-review, widget-wiring", ExecutionProcess.ToolAction(entry, running: false));
	}

	[Fact]
	public void WaitWithoutNamesSaysAllRunning()
	{
		var entry = Call("wait_subagents", "{}");
		Assert.Equal("等待子代理 全部在跑的子代理", ExecutionProcess.ToolAction(entry, running: false));
	}

	[Fact]
	public void ExpandedDelegateShowsFieldsAndTheTaskBlock()
	{
		var entry = Call("delegate", """{"agent":"worker","name":"fix-build","context":"fork","task":"把构建修到全绿。"}""");
		var details = ToolCallDetails.For(entry);

		Assert.Equal(3, details.Fields.Count);
		Assert.Contains(details.Fields, field => field.Label == Strings.ToolFieldAgent && field.Value == "worker");
		Assert.Contains(details.Fields, field => field.Label == Strings.ToolFieldSubagentName && field.Value == "fix-build");
		Assert.Equal(Strings.ToolFieldTask, details.BlockLabel);
		Assert.Equal("把构建修到全绿。", details.BlockText);
	}

	[Fact]
	public void ExpandedWaitShowsTargetsAndTimeout()
	{
		var entry = Call("wait_subagents", """{"names":["a","b"],"timeout_ms":120000}""");
		var details = ToolCallDetails.For(entry);
		Assert.Contains(details.Fields, field => field.Label == Strings.ToolFieldSubagentNames && field.Value == "a, b");
		Assert.Contains(details.Fields, field => field.Label == Strings.ToolFieldTimeout && field.Value == "2 分钟");
	}

	[Fact]
	public void ListSummaryHasNoDanglingDetail()
	{
		var entry = Call("list_subagents", "{}");
		Assert.Equal("列出子代理", ExecutionProcess.ToolAction(entry, running: false));
	}

	[Fact]
	public void SendAndStopSummariesUseTheTarget()
	{
		var send = Call("send_to_subagent", """{"id":"scout-1","message":"补一条测试。"}""");
		Assert.Equal("通知子代理 scout-1", ExecutionProcess.ToolAction(send, running: false));

		var stop = Call("stop_subagent", """{"id":"scout-1"}""");
		Assert.Equal("停止子代理 scout-1", ExecutionProcess.ToolAction(stop, running: false));
	}

	[Fact]
	public void DelegateContextIsMappedToChinese()
	{
		var entry = Call("delegate", """{"agent":"worker","context":"fork","task":"继续。"}""");
		var details = ToolCallDetails.For(entry);
		Assert.Contains(details.Fields, field => field.Label == Strings.ToolFieldContext && field.Value == "继承当前会话");
	}

	[Fact]
	public void MalformedArgumentsNeverThrow()
	{
		foreach (var arguments in new[] { "not json", "null", "[]", """{"names":["a",1],"timeout_ms":"x"}""", """{"agent":null}""" })
		{
			foreach (var tool in new[] { "delegate", "wait_subagents", "list_subagents", "send_to_subagent", "stop_subagent" })
			{
				var entry = Call(tool, arguments);
				_ = ExecutionProcess.ToolAction(entry, running: false);
				_ = ToolCallDetails.For(entry);
			}
		}
	}

	[Fact]
	public void ToolResultsKeepChineseReadable()
	{
		// ToJsonString() 默认把中文写成 \uXXXX；工具结果要走宽松编码器。
		var node = new JsonObject { ["text"] = "已完成" };
		Assert.Contains("\\u", node.ToJsonString());
		Assert.DoesNotContain("\\u", node.ToJsonString(PiWeft.Ai.Json.PiJson.NodeOptions));
	}
}
