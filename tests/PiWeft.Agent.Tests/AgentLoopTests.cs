using System.Text.Json.Nodes;
using PiWeft.Ai;
using Xunit;

namespace PiWeft.Agent.Tests;

public class AgentLoopTests
{
	private static AgentTool Tool(
		string name,
		Func<AgentToolInvocation, Task<AgentToolResult>> execute,
		ToolExecutionMode? mode = null) => new()
		{
			Name = name,
			Label = name,
			Description = $"{name} tool",
			Parameters = new JsonObject { ["type"] = "object" },
			Execute = execute,
			ExecutionMode = mode,
		};

	private static AgentTool Echo(string name = "echo", ToolExecutionMode? mode = null) =>
		Tool(name, invocation => Task.FromResult(AgentToolResult.Text($"echo:{invocation.Arguments["value"]}")), mode);

	[Fact]
	public async Task PromptEmitsExpectedEventSequence()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("hello");
		var recorder = new EventRecorder();

		var agent = new Agent(new AgentOptions
		{
			SystemPrompt = "sys",
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
		});

		using var subscription = agent.Subscribe(recorder.Record);
		var messages = await agent.PromptAsync("hi");

		Assert.Equal(
			[
				"agent_start",
				"turn_start",
				"message_start:user",
				"message_end:user",
				"message_start:assistant",
				"message_update",
				"message_update",
				"message_update",
				"message_end:assistant",
				"turn_end",
				"agent_end",
			],
			recorder.Names);

		Assert.Equal(2, messages.Count);
		Assert.Equal("hello", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(messages[1]).Content[0]).Text);
		Assert.False(agent.IsStreaming);
	}

	[Fact]
	public async Task ExecutesToolCallsThenStreamsFollowUp()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "echo", new JsonObject { ["value"] = 7 }));
		script.EnqueueText("done");

		var executed = 0;
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools = [Echo()],
			ToolExecution = ToolExecutionMode.Sequential,
			BeforeToolCall = (_, _) =>
			{
				executed++;
				return ValueTask.FromResult<BeforeToolCallResult?>(null);
			},
		});

		var messages = await agent.PromptAsync("go");

		Assert.Equal(1, executed);
		Assert.Equal(2, script.Requests.Count);

		// The loop announces the executable tool set with a system message before the prompt.
		Assert.IsType<SystemMessage>(messages[0]);
		Assert.IsType<UserMessage>(messages[1]);
		Assert.Equal(StopReason.ToolUse, Assert.IsType<AssistantMessage>(messages[2]).StopReason);

		var toolResult = Assert.IsType<ToolResultMessage>(messages[3]);
		Assert.Equal("c1", toolResult.ToolCallId);
		Assert.False(toolResult.IsError);
		Assert.Equal("echo:7", Assert.IsType<TextContent>(toolResult.Content[0]).Text);

		// The follow-up request sees the assistant tool call and its result.
		var secondRequest = script.Requests[1].Messages;
		Assert.Contains(secondRequest, m => m is ToolResultMessage);
		Assert.Contains(secondRequest, m => m is AssistantMessage);
	}

	[Fact]
	public async Task BeforeToolCallCanBlockExecution()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "echo", new JsonObject { ["value"] = 1 }));
		script.EnqueueText("ok");

		var toolRan = false;
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools =
			[
				Tool("echo", _ =>
				{
					toolRan = true;
					return Task.FromResult(AgentToolResult.Text("ran"));
				}),
			],
			ToolExecution = ToolExecutionMode.Sequential,
			BeforeToolCall = (_, _) => ValueTask.FromResult<BeforeToolCallResult?>(new BeforeToolCallResult
			{
				Block = true,
				Reason = "not allowed",
			}),
		});

		var messages = await agent.PromptAsync("go");

		Assert.False(toolRan);
		var toolResult = messages.OfType<ToolResultMessage>().Single();
		Assert.True(toolResult.IsError);
		Assert.Equal("not allowed", Assert.IsType<TextContent>(toolResult.Content[0]).Text);
	}

	[Fact]
	public async Task AfterToolCallOverridesResult()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "echo", new JsonObject { ["value"] = 1 }));
		script.EnqueueText("ok");

		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools = [Echo()],
			ToolExecution = ToolExecutionMode.Sequential,
			AfterToolCall = (_, _) => ValueTask.FromResult<AfterToolCallResult?>(new AfterToolCallResult
			{
				Content = [new TextContent { Text = "overridden" }],
				IsError = true,
			}),
		});

		var messages = await agent.PromptAsync("go");

		var toolResult = messages.OfType<ToolResultMessage>().Single();
		Assert.True(toolResult.IsError);
		Assert.Equal("overridden", Assert.IsType<TextContent>(toolResult.Content[0]).Text);
	}

	[Fact]
	public async Task TerminateStopsTheRunAfterOneRequest()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "echo", new JsonObject { ["value"] = 1 }));

		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools = [Tool("echo", _ => Task.FromResult(AgentToolResult.Text("bye") with { Terminate = true }))],
			ToolExecution = ToolExecutionMode.Sequential,
		});

		await agent.PromptAsync("go");

		Assert.Single(script.Requests);
	}

	[Fact]
	public async Task LengthStopFailsToolCallsWithoutExecuting()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.Length, ("c1", "echo", new JsonObject { ["value"] = 1 }));
		script.EnqueueText("retry");

		var toolRan = false;
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools =
			[
				Tool("echo", _ =>
				{
					toolRan = true;
					return Task.FromResult(AgentToolResult.Text("ran"));
				}),
			],
			ToolExecution = ToolExecutionMode.Sequential,
		});

		var messages = await agent.PromptAsync("go");

		Assert.False(toolRan);
		var toolResult = messages.OfType<ToolResultMessage>().Single();
		Assert.True(toolResult.IsError);
		Assert.Contains("truncated", Assert.IsType<TextContent>(toolResult.Content[0]).Text);
	}

	[Fact]
	public async Task ParallelModeRunsEveryToolAndKeepsSourceOrder()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(
			StopReason.ToolUse,
			("c1", "echo", new JsonObject { ["value"] = "a" }),
			("c2", "echo", new JsonObject { ["value"] = "b" }));
		script.EnqueueText("done");

		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools = [Echo()],
			ToolExecution = ToolExecutionMode.Parallel,
		});

		var messages = await agent.PromptAsync("go");

		var results = messages.OfType<ToolResultMessage>().ToList();
		var first = results[0];
		var second = results[1];
		Assert.Equal("c1", first.ToolCallId);
		Assert.Equal("echo:a", Assert.IsType<TextContent>(first.Content[0]).Text);
		Assert.Equal("c2", second.ToolCallId);
		Assert.Equal("echo:b", Assert.IsType<TextContent>(second.Content[0]).Text);
	}

	[Fact]
	public async Task SteeringMessagesAreInjectedAfterToolCalls()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "echo", new JsonObject { ["value"] = 1 }));
		script.EnqueueText("steered");

		Agent? agent = null;
		var tool = Tool("echo", _ =>
		{
			agent!.Steer(new UserMessage
			{
				Content = MessageContent.FromText("actually do this"),
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			});
			return Task.FromResult(AgentToolResult.Text("ok"));
		});

		agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools = [tool],
			ToolExecution = ToolExecutionMode.Sequential,
		});

		await agent.PromptAsync("go");

		Assert.Equal(2, script.Requests.Count);
		Assert.Contains(
			script.Requests[1].Messages,
			m => m is UserMessage user && user.Content.Text == "actually do this");
	}

	[Fact]
	public async Task StopReasonErrorEndsTheRun()
	{
		var script = new ScriptedStreamFn();
		script.Enqueue(model => FakeStream.Error(model, "boom"));
		var recorder = new EventRecorder();

		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
		});

		using var subscription = agent.Subscribe(recorder.Record);
		var messages = await agent.PromptAsync("go");

		Assert.Equal(2, messages.Count);
		Assert.Equal("boom", agent.ErrorMessage);
		Assert.Equal("agent_end", recorder.Names.Last());
		Assert.Single(script.Requests);
	}

	[Fact]
	public async Task FollowUpMessagesContinueAfterTheAgentWouldStop()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "echo", new JsonObject { ["value"] = 1 }));
		script.EnqueueText("after tools");
		script.EnqueueText("after follow-up");

		Agent? agent = null;
		var tool = Tool("echo", _ =>
		{
			agent!.FollowUp(new UserMessage
			{
				Content = MessageContent.FromText("one more"),
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			});
			return Task.FromResult(AgentToolResult.Text("ok"));
		});

		agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools = [tool],
			ToolExecution = ToolExecutionMode.Sequential,
		});

		await agent.PromptAsync("go");

		// The tool batch schedules one more request; the follow-up is injected after that request yields no tool calls.
		Assert.Equal(3, script.Requests.Count);
		Assert.Contains(
			script.Requests[2].Messages,
			m => m is UserMessage user && user.Content.Text == "one more");
	}
}
