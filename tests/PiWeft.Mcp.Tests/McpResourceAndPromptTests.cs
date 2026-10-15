using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// <summary>
/// Exercises the follow-up MCP capabilities against the real fixture server: paginated discovery, the three resource
/// tools, prompts, and the live tool refresh a server triggers with <c>notifications/tools/list_changed</c>.
/// </summary>
public class McpResourceAndPromptTests : IAsyncLifetime
{
	private McpProcess? _process;

	public async Task InitializeAsync()
	{
		var fixture = McpFixtures.FixtureAssembly();
		var dotnet = McpFixtures.DotnetHost();
		Assert.False(fixture is null || dotnet is null, $"fixture={fixture ?? "<null>"} dotnet={dotnet ?? "<null>"}");

		_process = await McpProcess.StartAsync(
			new McpServerConfig
			{
				Name = "fixture",
				Command = dotnet!,
				Arguments = [fixture!],
				Env = new Dictionary<string, string> { ["FIXTURE_GREETING"] = "live" },
				Timeout = 30,
			},
			CancellationToken.None);
	}

	public async Task DisposeAsync()
	{
		if (_process is not null)
		{
			await _process.DisposeAsync();
		}
	}

	private McpClient Client => _process!.Client;

	private IReadOnlyList<AgentTool> ResourceTools() => McpResourceTools.Create(() => [Client]);

	private static JsonObject Arguments(string json) => JsonNode.Parse(json)!.AsObject();

	private async Task<AgentToolResult> CallAsync(AgentTool tool, string json, string id = "c1") =>
		await tool.Execute(new AgentToolInvocation
		{
			ToolCallId = id,
			ToolName = tool.Name,
			Arguments = Arguments(json),
		});

	[Fact]
	public async Task CapabilitiesAreReadFromTheHandshake()
	{
		Assert.True(Client.Capabilities.HasTools);
		Assert.True(Client.Capabilities.ToolsListChanged);
		Assert.True(Client.Capabilities.HasResources);
		Assert.True(Client.Capabilities.ResourcesSubscribe);
		Assert.True(Client.Capabilities.ResourcesListChanged);
		Assert.True(Client.Capabilities.HasPrompts);
		Assert.True(Client.Capabilities.PromptsListChanged);
	}

	[Fact]
	public async Task ListsEveryResourcePageAndHidesMcpApps()
	{
		var list = ResourceTools().Single(static tool => tool.Name == McpResourceTools.ListResourcesTool);
		var result = await CallAsync(list, "{}");

		var payload = result.StructuredContent!.AsObject();
		var resources = payload["resources"]!.AsArray();

		// Two pages; the ui:// item on the second page must be filtered out.
		Assert.Equal(2, resources.Count);
		Assert.Equal("fixture", resources[0]!["server"]!.GetValue<string>());
		Assert.Equal("fixture://notes/one", resources[0]!["uri"]!.GetValue<string>());
		Assert.Equal("First note", resources[0]!["title"]!.GetValue<string>());
		Assert.Equal(12, resources[0]!["size"]!.GetValue<long>());
		Assert.Equal("fixture://data/blob", resources[1]!["uri"]!.GetValue<string>());

		// The model sees the same JSON as the structured result.
		var text = Assert.IsType<TextContent>(result.Content[0]).Text;
		Assert.Contains("fixture://notes/one", text);
		Assert.DoesNotContain("ui://dashboard", text);
	}

	[Fact]
	public async Task ListsTemplatesAndRequiresAServerForACursor()
	{
		var templates = ResourceTools().Single(static tool => tool.Name == McpResourceTools.ListResourceTemplatesTool);
		var result = await CallAsync(templates, "{}");

		var payload = result.StructuredContent!.AsObject();
		var listed = payload["resourceTemplates"]!.AsArray();
		Assert.Single(listed);
		Assert.Equal("fixture://notes/{id}", listed[0]!["uriTemplate"]!.GetValue<string>());
		Assert.Equal("fixture", listed[0]!["server"]!.GetValue<string>());

		var list = ResourceTools().Single(static tool => tool.Name == McpResourceTools.ListResourcesTool);
		var failure = await Assert.ThrowsAsync<McpToolArgumentException>(() => CallAsync(list, """{"cursor":"page-2"}"""));
		Assert.Contains("cursor can only be used", failure.Message);
	}

	[Fact]
	public async Task ReadsATextResource()
	{
		var read = ResourceTools().Single(static tool => tool.Name == McpResourceTools.ReadResourceTool);
		var result = await CallAsync(read, """{"server":"fixture","uri":"fixture://notes/one"}""");

		Assert.False(result.IsError);
		Assert.Equal("hello resources", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.Equal("fixture", result.StructuredContent!["server"]!.GetValue<string>());
		Assert.Equal("hello resources", result.StructuredContent!["contents"]![0]!["text"]!.GetValue<string>());
	}

	[Fact]
	public async Task LabelsEveryContentOfAMultiContentRead()
	{
		var read = ResourceTools().Single(static tool => tool.Name == McpResourceTools.ReadResourceTool);
		var result = await CallAsync(read, """{"server":"fixture","uri":"fixture://notes"}""");

		var texts = result.Content.OfType<TextContent>().Select(static block => block.Text).ToList();
		Assert.Equal(["fixture://notes/one:", "one", "fixture://notes/two:", "two"], texts);
	}

	[Fact]
	public async Task DecodesJsonBlobsAndSavesBinaryOnes()
	{
		var read = ResourceTools().Single(static tool => tool.Name == McpResourceTools.ReadResourceTool);

		var json = await CallAsync(read, """{"server":"fixture","uri":"fixture://data/json"}""");
		Assert.Equal("""{"ok":true}""", Assert.IsType<TextContent>(json.Content[0]).Text);

		var binary = await CallAsync(read, """{"server":"fixture","uri":"fixture://data/blob"}""");
		var note = Assert.IsType<TextContent>(binary.Content[0]).Text;
		Assert.Contains("Binary resource fixture://data/blob", note);
		Assert.Contains("saved to", note);

		// The saved file holds the decoded bytes.
		var path = note[(note.IndexOf("saved to ", StringComparison.Ordinal) + 9)..].TrimEnd(']');
		Assert.Equal("binary body", await File.ReadAllTextAsync(path));
		File.Delete(path);
	}

	[Fact]
	public async Task AnEmptyResourceIsReportedInsteadOfReturningNothing()
	{
		var read = ResourceTools().Single(static tool => tool.Name == McpResourceTools.ReadResourceTool);
		var result = await CallAsync(read, """{"server":"fixture","uri":"fixture://empty"}""");

		Assert.Equal("Resource fixture://empty is empty.", Assert.IsType<TextContent>(result.Content[0]).Text);
	}

	[Fact]
	public async Task AnUnknownServerNamesTheOnesThatHaveResources()
	{
		var read = ResourceTools().Single(static tool => tool.Name == McpResourceTools.ReadResourceTool);

		var failure = await Assert.ThrowsAsync<McpToolArgumentException>(() =>
			CallAsync(read, """{"server":"nope","uri":"fixture://notes/one"}"""));

		Assert.Contains("has no resources", failure.Message);
		Assert.Contains("fixture", failure.Message);
	}

	[Fact]
	public async Task OnlyServersWithTheCapabilityGetResourceTools()
	{
		var connection = await McpServers.ConnectAllAsync(
		[
			new McpServerConfig
			{
				Name = "fixture",
				Command = McpFixtures.DotnetHost()!,
				Arguments = [McpFixtures.FixtureAssembly()!],
			},
		]);

		await using var _ = connection;

		var provider = Assert.Single(connection.ResourceServers);
		Assert.Equal("fixture", provider.ConfiguredName);
		Assert.True(provider.Capabilities.HasResources);

		// The resource tools read the provider list at call time, so they work with the connection's view.
		var tools = McpResourceTools.Create(() => connection.ResourceServers);
		var list = tools.Single(static tool => tool.Name == McpResourceTools.ListResourcesTool);
		var result = await CallAsync(list, """{"server":"fixture"}""");
		// Both visible resources, and the ui:// one filtered out even though it is requested by name.
		Assert.Equal(2, result.StructuredContent!["resources"]!.AsArray().Count);
	}

	[Fact]
	public async Task PromptsAreListedAndRendered()
	{
		var prompts = await Client.ListPromptsAsync(CancellationToken.None);

		var prompt = Assert.Single(prompts);
		Assert.Equal("review", prompt.Name);
		Assert.Equal("Review a diff", prompt.Description);
		Assert.Equal("target", Assert.Single(prompt.Arguments)["name"]!.GetValue<string>());

		var rendered = await Client.GetPromptAsync(
			"review",
			new JsonObject { ["target"] = "the parser" },
			CancellationToken.None);

		var message = Assert.Single(rendered.Messages);
		Assert.Equal("user", message.Role);
		Assert.Equal("Review the parser carefully.", Assert.IsType<TextContent>(Assert.Single(message.Content)).Text);
		Assert.Equal("Review a diff", rendered.Description);
	}

	[Fact]
	public async Task AListChangedNotificationRefreshesTheTools()
	{
		await using var connection = await McpServers.ConnectAllAsync(
		[
			new McpServerConfig
			{
				Name = "fixture",
				Command = McpFixtures.DotnetHost()!,
				Arguments = [McpFixtures.FixtureAssembly()!],
				Env = new Dictionary<string, string> { ["FIXTURE_GREETING"] = "live" },
				Timeout = 30,
			},
		]);

		Assert.Equal(3, connection.Tools.Count);
		Assert.DoesNotContain(connection.Tools, static tool => tool.Name == "mcp__fixture__runtime_tool");

		var refreshed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.ToolsChanged += _ => refreshed.TrySetResult(true);

		var add = connection.Tools.Single(static tool => tool.Name == "mcp__fixture__add_tool");
		var result = await add.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1",
			ToolName = add.Name,
			Arguments = new JsonObject { ["name"] = "runtime_tool" },
		});

		Assert.False(result.IsError);
		Assert.Equal("registered runtime_tool", Assert.IsType<TextContent>(result.Content[0]).Text);

		// The server announced the change; the connection refreshed its list in place.
		Assert.True(
			await Task.WhenAny(refreshed.Task, Task.Delay(TimeSpan.FromSeconds(20))) == refreshed.Task,
			"the tools/list_changed notification never refreshed the tool list");

		Assert.Equal(4, connection.Tools.Count);
		var added = connection.Tools.Single(static tool => tool.Name == "mcp__fixture__runtime_tool");
		Assert.Equal("Tool added at runtime by live", added.Description);
	}

}

/// <summary>Shared helpers for reaching the fixture server and building a session around it.</summary>
internal static class McpFixtures
{
	public static string? FixtureAssembly()
	{
		var directory = AppContext.BaseDirectory;
		var candidates = new[]
		{
			Path.Combine(directory, "PiWeft.Mcp.FixtureServer.dll"),
			Path.Combine(directory, "..", "..", "..", "..", "..", "tools", "PiWeft.Mcp.FixtureServer", "bin", "Debug", "net10.0", "PiWeft.Mcp.FixtureServer.dll"),
			Path.Combine(directory, "..", "..", "..", "..", "..", "tools", "PiWeft.Mcp.FixtureServer", "bin", "Release", "net10.0", "PiWeft.Mcp.FixtureServer.dll"),
		};

		return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
	}

	public static string? DotnetHost()
	{
		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		foreach (var directory in path.Split(Path.PathSeparator))
		{
			if (directory.Length == 0)
			{
				continue;
			}

			var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

}
