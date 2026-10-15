using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using Xunit;

namespace PiWeft.Agent.Tests;

/// <summary>Tool-argument validation happens inside the loop, before hooks and execution.</summary>
public class ToolValidationTests
{
	private static AgentTool TypedTool(
		string name,
		JsonObject parameters,
		Func<AgentToolInvocation, Task<AgentToolResult>> execute) => new()
		{
			Name = name,
			Label = name,
			Description = name,
			Parameters = parameters,
			Execute = execute,
		};

	private static JsonObject ReadSchema => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["path"] = new JsonObject { ["type"] = "string" },
			["limit"] = new JsonObject { ["type"] = "number" },
		},
		["required"] = new JsonArray("path"),
	};

	[Fact]
	public async Task InvalidArgumentsProduceAnErrorResultAndDoNotRunTheTool()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "read", new JsonObject { ["limit"] = 5 }));
		script.EnqueueText("retry");

		var ran = false;
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			ToolExecution = ToolExecutionMode.Sequential,
			Tools =
			[
				TypedTool("read", ReadSchema, _ =>
				{
					ran = true;
					return Task.FromResult(AgentToolResult.Text("ran"));
				}),
			],
		});

		var messages = await agent.PromptAsync("go");
		var toolResult = messages.OfType<ToolResultMessage>().Single();

		Assert.False(ran);
		Assert.True(toolResult.IsError);

		var text = Assert.IsType<TextContent>(toolResult.Content[0]).Text;
		Assert.Contains("Validation failed for tool \"read\"", text);
		Assert.Contains("- path: Expected required property", text);
		Assert.Contains("Received arguments:", text);

		// The model gets the validation failure back and can retry.
		Assert.Equal(2, script.Requests.Count);
	}

	[Fact]
	public async Task ValidatedArgumentsAreCoercedBeforeExecution()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "read", new JsonObject
		{
			["path"] = 42,
			["limit"] = "10",
		}));
		script.EnqueueText("done");

		JsonObject? seen = null;
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			ToolExecution = ToolExecutionMode.Sequential,
			Tools =
			[
				TypedTool("read", ReadSchema, invocation =>
				{
					seen = invocation.Arguments;
					return Task.FromResult(AgentToolResult.Text("ok"));
				}),
			],
		});

		await agent.PromptAsync("go");

		Assert.NotNull(seen);
		Assert.Equal("42", seen!["path"]!.GetValue<string>());
		Assert.Equal(10d, seen["limit"]!.GetValue<double>());
	}

	[Fact]
	public async Task ValidationRunsBeforeTheToolCallHooks()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "read", new JsonObject { ["limit"] = 1 }));
		script.EnqueueText("done");

		var hookRan = false;
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			ToolExecution = ToolExecutionMode.Sequential,
			Tools = [TypedTool("read", ReadSchema, _ => Task.FromResult(AgentToolResult.Text("ran")))],
			BeforeToolCall = (_, _) =>
			{
				hookRan = true;
				return ValueTask.FromResult<BeforeToolCallResult?>(null);
			},
		});

		await agent.PromptAsync("go");

		Assert.False(hookRan);
	}

	[Fact]
	public async Task OptionalNullsAreDroppedSoToolsSeeAbsentArguments()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "read", new JsonObject
		{
			["path"] = "a.txt",
			["limit"] = null,
		}));
		script.EnqueueText("done");

		JsonObject? seen = null;
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			ToolExecution = ToolExecutionMode.Sequential,
			Tools =
			[
				TypedTool("read", ReadSchema, invocation =>
				{
					seen = invocation.Arguments;
					return Task.FromResult(AgentToolResult.Text("ok"));
				}),
			],
		});

		await agent.PromptAsync("go");

		Assert.NotNull(seen);
		Assert.False(seen!.ContainsKey("limit"));
	}

	[Fact]
	public async Task PrepareArgumentsShimRunsBeforeValidation()
	{
		var script = new ScriptedStreamFn();
		// The legacy shape a provider might send: a bare string instead of an object.
		script.EnqueueToolCalls(StopReason.ToolUse, ("c1", "read", new JsonObject { ["path"] = new JsonObject() }));
		script.EnqueueText("done");

		var tool = TypedTool("read", ReadSchema, _ => Task.FromResult(AgentToolResult.Text("ran"))) with
		{
			PrepareArguments = arguments => new JsonObject { ["path"] = "rewritten.txt" },
		};

		JsonObject? seen = null;
		var agent = new Agent(new AgentOptions
		{
			Model = FakeStream.Model(),
			StreamFn = script.Invoke,
			ToolExecution = ToolExecutionMode.Sequential,
			Tools =
			[
				tool with
				{
					Execute = invocation =>
					{
						seen = invocation.Arguments;
						return Task.FromResult(AgentToolResult.Text("ok"));
					},
				},
			],
		});

		await agent.PromptAsync("go");

		Assert.NotNull(seen);
		Assert.Equal("rewritten.txt", seen!["path"]!.GetValue<string>());
	}
}
