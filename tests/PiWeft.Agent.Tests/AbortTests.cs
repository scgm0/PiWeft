using System.Text.Json.Nodes;
using PiWeft.Ai;
using Xunit;

namespace PiWeft.Agent.Tests;

public class AbortTests
{
	private static AgentTool WaitingTool(TaskCompletionSource started) => new()
	{
		Name = "wait",
		Label = "wait",
		Description = "Waits until the run is aborted",
		Parameters = new JsonObject { ["type"] = "object" },
		ExecutionMode = ToolExecutionMode.Sequential,
		Execute = async invocation =>
		{
			started.TrySetResult();
			await Task.Delay(Timeout.Infinite, invocation.CancellationToken);
			return AgentToolResult.Text("never");
		},
	};

	[Fact]
	public async Task AbortDuringStreamEmitsTerminalEventsAndKeepsPartialText()
	{
		var model = FakeStream.Model();
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var agent = new Agent(new AgentOptions
		{
			Model = model,
			StreamFn = (_, _, options) =>
			{
				var stream = new AssistantMessageEventStream();
				var partial = new AssistantMessage
				{
					Api = model.Api,
					Provider = model.Provider,
					Model = model.Id,
					Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				};
				stream.Push(new StartEvent { Partial = partial });
				partial = partial with { Content = [new TextContent { Text = "half an answer" }] };
				stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = "half an answer", Partial = partial });
				started.TrySetResult();

				// 真实 provider 也会在取消时推送 aborted；阅读端先被取消时收不到这条事件。
				_ = Task.Run(async () =>
				{
					try
					{
						await Task.Delay(Timeout.Infinite, options.CancellationToken);
					}
					catch (OperationCanceledException)
					{
						stream.Push(new ErrorEvent
						{
							Reason = StopReason.Aborted,
							Partial = partial with { StopReason = StopReason.Aborted, ErrorMessage = "Request aborted" },
						});
					}
				});
				return stream;
			},
		});

		var recorder = new EventRecorder();
		using var subscription = agent.Subscribe(recorder.Record);

		var prompt = agent.PromptAsync("go");
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		agent.Abort();

		var messages = await prompt.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(agent.IsStreaming);
		var aborted = Assert.IsType<AssistantMessage>(messages[^1]);
		Assert.Equal(StopReason.Aborted, aborted.StopReason);
		Assert.Equal("half an answer", Assert.IsType<TextContent>(aborted.Content[0]).Text);
		Assert.Equal("agent_end", recorder.Names.Last());
		Assert.Contains("message_end:assistant", recorder.Names);
	}

	[Fact]
	public async Task AbortDuringPrepareNextTurnStillEmitsAgentEnd()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "echo", new JsonObject()));

		var echo = new AgentTool
		{
			Name = "echo",
			Label = "echo",
			Description = "echo",
			Parameters = new JsonObject { ["type"] = "object" },
			Execute = _ => Task.FromResult(AgentToolResult.Text("ok")),
		};

		using var cancellation = new CancellationTokenSource();
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			Tools = [echo],
			ToolExecution = ToolExecutionMode.Sequential,
			PrepareNextTurn = (_, _) =>
			{
				// 模拟自动压缩等长时间准备阶段被用户取消。
				cancellation.Cancel();
				throw new OperationCanceledException();
			},
		});

		var recorder = new EventRecorder();
		using var subscription = agent.Subscribe(recorder.Record);

		var messages = await agent.PromptAsync("go", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Single(script.Requests);
		Assert.False(agent.IsStreaming);
		Assert.Equal(StopReason.Aborted, Assert.IsType<AssistantMessage>(messages[^1]).StopReason);
		Assert.Contains("turn_end", recorder.Names);
		Assert.Equal("agent_end", recorder.Names.Last());
	}

	[Fact]
	public async Task AbortDuringToolExecutionDoesNotStartAnotherRequest()
	{
		var model = FakeStream.Model();
		var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "wait", new JsonObject()));
		script.EnqueueText("should not run");

		var agent = new Agent(new AgentOptions
		{
			Model = model,
			StreamFn = script.Invoke,
			Tools = [WaitingTool(toolStarted)],
			ToolExecution = ToolExecutionMode.Sequential,
		});

		var recorder = new EventRecorder();
		using var subscription = agent.Subscribe(recorder.Record);

		var prompt = agent.PromptAsync("go");
		await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		agent.Abort();

		var messages = await prompt.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Single(script.Requests);
		Assert.False(agent.IsStreaming);
		Assert.IsType<ToolResultMessage>(messages[^2]);
		Assert.Equal(StopReason.Aborted, Assert.IsType<AssistantMessage>(messages[^1]).StopReason);
		Assert.Equal("agent_end", recorder.Names.Last());
	}

	[Fact]
	public async Task AbortDuringToolBatchAnswersEveryToolCall()
	{
		// pi #9986：批量中途取消时，没跑到的调用也要有 tool_result，否则会话里留下
		// 没有配对的 tool_use，下一次请求只能靠重放补丁才发得出去。
		var model = FakeStream.Model();
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse,
			("c1", "wait", new JsonObject()),
			("c2", "wait", new JsonObject()),
			("c3", "wait", new JsonObject()));

		var agent = new Agent(new AgentOptions
		{
			Model = model,
			StreamFn = script.Invoke,
			Tools = [WaitingTool(started)],
			ToolExecution = ToolExecutionMode.Sequential,
		});

		var recorder = new EventRecorder();
		using var subscription = agent.Subscribe(recorder.Record);

		var prompt = agent.PromptAsync("go");
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		agent.Abort();

		var messages = await prompt.WaitAsync(TimeSpan.FromSeconds(5));

		var results = messages.OfType<ToolResultMessage>().Where(result => result.ToolName == "wait").ToList();
		Assert.Equal(["c1", "c2", "c3"], results.Select(result => result.ToolCallId));
		Assert.All(results, result => Assert.True(result.IsError));
		Assert.Equal(2, results.Count(result =>
			result.Content.OfType<TextContent>().Any(text => text.Text == "Operation aborted")));
		Assert.Equal(3, recorder.Names.Count(name => name == "tool_start:wait"));
		Assert.Equal(3, recorder.Names.Count(name => name == "tool_end:wait"));
	}
}
