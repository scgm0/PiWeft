using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>动态 JS route 与 <c>pi.virtual-model-state</c> 条目。</summary>
public sealed class VirtualModelSessionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-virtual-" + Guid.NewGuid().ToString("N")[..8]);

	public VirtualModelSessionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private ModelRegistry Registry()
	{
		var registry = new ModelRegistry();
		registry.Add(ScriptedSessions.Model("a") with { Provider = "p", Id = "a", Name = "A" });
		registry.Add(ScriptedSessions.Model("b") with { Provider = "p", Id = "b", Name = "B" });
		registry.AddVirtual(new VirtualModelDefinition
		{
			Provider = "router",
			Id = "auto",
			Name = "Auto",
			ThinkingLevels = ["off", "high"],
			Route = VirtualModelScript.Create("router", "auto",
				"""
				const next = { count: ((request.state && request.state.count) || 0) + 1 };
				const target = ctx.modelRegistry.find("p", next.count === 1 ? "a" : "b");
				return { model: target, thinkingLevel: "high", state: next };
				"""),
		});
		return registry;
	}

	[Fact]
	public async Task RouteScriptSelectsAPhysicalModelAndStoresStateOnTheBranch()
	{
		var registry = Registry();
		var model = registry.Get("router", "auto")!;
		var script = new ScriptedStreamFn();
		script.EnqueueText("first");
		script.EnqueueText("second");
		var entries = new List<EntryAppendedEvent>();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			Registry = registry,
			StreamFn = script.Invoke,
			Session = SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
		});
		using var subscription = session.Subscribe(@event =>
		{
			if (@event is EntryAppendedEvent appended) entries.Add(appended);
			return ValueTask.CompletedTask;
		});

		await session.PromptAsync("first");
		Assert.Equal("a", script.LastModel!.Id);

		await session.PromptAsync("second");
		Assert.Equal("b", script.LastModel!.Id);

		var states = session.Session.GetBranch()
			.OfType<CustomEntry>()
			.Where(entry => entry.CustomType == VirtualModels.StateEntryType)
			.ToArray();
		Assert.Equal(2, states.Length);
		Assert.All(states, entry => Assert.Equal("router", entry.Data!["provider"]!.GetValue<string>()));
		Assert.All(states, entry => Assert.Equal("auto", entry.Data!["modelId"]!.GetValue<string>()));
		Assert.Equal(1, states[0].Data!["state"]!["count"]!.GetValue<int>());
		Assert.Equal(2, states[1].Data!["state"]!["count"]!.GetValue<int>());
		Assert.Equal(2, entries.Count);
	}

	[Fact]
	public async Task DirectProviderProbeRoutesWithoutWritingState()
	{
		var registry = new ModelRegistry();
		registry.Add(ScriptedSessions.Model("a") with { Provider = "p", Id = "a" });
		registry.AddVirtual(new VirtualModelDefinition
		{
			Provider = "router",
			Id = "auto",
			Name = "Auto",
			Route = VirtualModelScript.Create("router", "auto",
				"return { model: ctx.modelRegistry.find('p', 'a'), thinkingLevel: 'off', state: { seen: request.reason } };"),
		});
		var script = new ScriptedStreamFn();
		script.EnqueueText("ok");
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = registry.Get("router", "auto")!,
			Registry = registry,
			StreamFn = script.Invoke,
			Session = SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
		});

		var result = await session.TestConnectionAsync();

		Assert.True(result.Ok);
		Assert.Equal("a", script.LastModel!.Id);
		Assert.DoesNotContain(session.Session.GetBranch().OfType<CustomEntry>(),
			entry => entry.CustomType == VirtualModels.StateEntryType);
	}

	[Fact]
	public async Task RouteScriptSeesRetryReasonAndTheFailedResponse()
	{
		var registry = new ModelRegistry();
		registry.Add(ScriptedSessions.Model("a") with { Provider = "p", Id = "a" });
		registry.Add(ScriptedSessions.Model("b") with { Provider = "p", Id = "b" });
		registry.AddVirtual(new VirtualModelDefinition
		{
			Provider = "router",
			Id = "auto",
			Name = "Auto",
			Route = VirtualModelScript.Create("router", "auto",
				"""
				const failedMessage = request.failed && request.failed.message && request.failed.message.errorMessage;
				const next = { reason: request.reason, failed: failedMessage || null };
				const target = ctx.modelRegistry.find("p", request.reason === "retry" ? "b" : "a");
				return { model: target, thinkingLevel: "off", state: next };
				"""),
		});
		var script = new ScriptedStreamFn();
		script.EnqueueError("overloaded");
		script.EnqueueText("recovered");
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = registry.Get("router", "auto")!,
			Registry = registry,
			StreamFn = script.Invoke,
			Session = SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
			Settings = new CodingAgentSettings
			{
				Retry = new RetryPolicy(Enabled: true, MaxRetries: 1, BaseDelayMs: 1),
			},
		});

		await session.PromptAsync("hello");

		Assert.Equal(2, script.Requests.Count);
		Assert.Equal("b", script.LastModel!.Id);
		var state = session.Session.GetBranch().OfType<CustomEntry>()
			.Last(entry => entry.CustomType == VirtualModels.StateEntryType);
		Assert.Equal("retry", state.Data!["state"]!["reason"]!.GetValue<string>());
		Assert.Equal("overloaded", state.Data["state"]!["failed"]!.GetValue<string>());
	}
}
