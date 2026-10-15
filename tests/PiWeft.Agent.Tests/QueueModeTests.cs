using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using Xunit;

namespace PiWeft.Agent.Tests;

/// <summary>Queue delivery modes and <c>ClearQueue</c>, which back the RPC steering/follow-up commands.</summary>
public class QueueModeTests
{
	/// <summary>Builds an agent whose only tool enqueues messages on the running agent.</summary>
	private static (Agent Agent, ScriptedStreamFn Script) Build(AgentQueueMode mode, Action<Agent> onTool)
	{
		var script = new ScriptedStreamFn();
		Agent? agent = null;

		var tool = new AgentTool
		{
			Name = "enqueue",
			Label = "enqueue",
			Description = "enqueue",
			Parameters = new JsonObject { ["type"] = "object" },
			Execute = _ =>
			{
				onTool(agent!);
				return Task.FromResult(AgentToolResult.Text("ok"));
			},
		};

		agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			ToolExecution = ToolExecutionMode.Sequential,
			Tools = [tool],
		})
		{
			SteeringMode = mode,
		};

		return (agent, script);
	}

	[Fact]
	public void ClearQueueReturnsAndDropsPendingMessages()
	{
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
		});

		agent.Steer(new UserMessage { Content = MessageContent.FromText("a"), Timestamp = 1 });
		agent.FollowUp(new UserMessage { Content = MessageContent.FromText("b"), Timestamp = 2 });

		var (steering, followUp) = agent.ClearQueue();

		Assert.Equal("a", Assert.IsType<UserMessage>(Assert.Single(steering)).Content.Text);
		Assert.Equal("b", Assert.IsType<UserMessage>(Assert.Single(followUp)).Content.Text);

		var again = agent.ClearQueue();
		Assert.Empty(again.Steering);
		Assert.Empty(again.FollowUp);
	}

	[Fact]
	public void QueueModesDefaultToOneAtATime()
	{
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
		});

		Assert.Equal(AgentQueueMode.OneAtATime, agent.SteeringMode);
		Assert.Equal(AgentQueueMode.OneAtATime, agent.FollowUpMode);
	}

	[Fact]
	public async Task OneAtATimeDeliversASingleSteeringMessagePerDrainPoint()
	{
		var (agent, script) = Build(AgentQueueMode.OneAtATime, tool =>
		{
			tool.Steer(new UserMessage { Content = MessageContent.FromText("first steer"), Timestamp = 1 });
			tool.Steer(new UserMessage { Content = MessageContent.FromText("second steer"), Timestamp = 2 });
		});

		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "enqueue", new JsonObject()));
		script.EnqueueText("after first");
		script.EnqueueText("after second");

		await agent.PromptAsync("go");

		Assert.Equal(3, script.Requests.Count);
		Assert.Contains(script.Requests[1].Messages, static m => m is UserMessage u && u.Content.Text == "first steer");
		Assert.DoesNotContain(script.Requests[1].Messages, static m => m is UserMessage u && u.Content.Text == "second steer");
		Assert.Contains(script.Requests[2].Messages, static m => m is UserMessage u && u.Content.Text == "second steer");
	}

	[Fact]
	public async Task AllDeliversEverySteeringMessageInOneDrainPoint()
	{
		var (agent, script) = Build(AgentQueueMode.All, tool =>
		{
			tool.Steer(new UserMessage { Content = MessageContent.FromText("first steer"), Timestamp = 1 });
			tool.Steer(new UserMessage { Content = MessageContent.FromText("second steer"), Timestamp = 2 });
		});

		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "enqueue", new JsonObject()));
		script.EnqueueText("done");

		await agent.PromptAsync("go");

		Assert.Contains(script.Requests[1].Messages, static m => m is UserMessage u && u.Content.Text == "first steer");
		Assert.Contains(script.Requests[1].Messages, static m => m is UserMessage u && u.Content.Text == "second steer");
	}

	[Fact]
	public async Task FollowUpModeGatesQueuedContinuations()
	{
		var (agent, script) = Build(AgentQueueMode.OneAtATime, tool =>
		{
			tool.FollowUp(new UserMessage { Content = MessageContent.FromText("follow one"), Timestamp = 1 });
			tool.FollowUp(new UserMessage { Content = MessageContent.FromText("follow two"), Timestamp = 2 });
		});

		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "enqueue", new JsonObject()));
		script.EnqueueText("after tools");
		script.EnqueueText("after first follow up");
		script.EnqueueText("after second follow up");

		await agent.PromptAsync("go");

		// One follow-up is delivered per drain point, so the second waits for another completion.
		Assert.Equal(4, script.Requests.Count);
		Assert.Contains(script.Requests[2].Messages, static m => m is UserMessage u && u.Content.Text == "follow one");
		Assert.DoesNotContain(script.Requests[2].Messages, static m => m is UserMessage u && u.Content.Text == "follow two");
		Assert.Contains(script.Requests[3].Messages, static m => m is UserMessage u && u.Content.Text == "follow two");
	}
}
