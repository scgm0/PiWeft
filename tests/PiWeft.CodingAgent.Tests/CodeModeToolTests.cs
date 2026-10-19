using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public sealed class CodeModeToolTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-codemode-" + Guid.NewGuid().ToString("N")[..8]);

	public CodeModeToolTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
		GC.SuppressFinalize(this);
	}

	private CodingAgentSession CreateSession(
		ModelRegistry? registry = null,
		CodingAgentSettings? settings = null,
		IReadOnlyList<AgentTool>? extraTools = null,
		SessionManager? session = null,
		ImagesGateway? images = null,
		ClassifiersGateway? classifiers = null)
	{
		var tools = extraTools is null
			? BuiltinTools.CatalogTools(_root)
			: [.. BuiltinTools.CatalogTools(_root), .. extraTools];
		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = session ?? SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
			Settings = settings ?? new CodingAgentSettings(),
			Tools = tools,
			Registry = registry,
			Images = images,
			Classifiers = classifiers,
		});
	}

	private static AgentTool DiscoveryTool(string name, string exposure, string description = "A tool") => new()
	{
		Name = name,
		Label = name,
		Description = description,
		Exposure = exposure,
		Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
		Execute = static _ => Task.FromResult(AgentToolResult.Text("called")),
	};

	private static AgentToolInvocation Invoke(string tool, string code) => new()
	{
		ToolCallId = "call-1",
		ToolName = tool,
		Arguments = new JsonObject { ["code"] = code },
	};

	private static string Text(AgentToolResult result) =>
		string.Join("\n", result.Content.Select(static block => block switch
		{
			TextContent text => text.Text,
			ImageContent => "[image]",
			_ => string.Empty,
		}));

	[Fact]
	public void SetTools_RebuildsTheCodemodeDescriptionAndDirectToolHints()
	{
		using var session = CreateSession();
		var late = new AgentTool
		{
			Name = "late_tool",
			Label = "late_tool",
			Description = "A tool that appeared later",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
			Execute = static _ => Task.FromResult(AgentToolResult.Text("ok")),
		};

		session.SetTools([.. session.Tools, late]);

		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);
		// mode=on：直接声明给模型的工具不重复列在 codemode 描述里。
		Assert.DoesNotContain("### `late_tool`", codemode.Description);
		Assert.DoesNotContain("### `read`", codemode.Description);

		// 但它们的声明里会说明怎么从脚本调用，且重复 SetTools 不会叠加。
		var lateTool = session.DeclaredTools.Single(static tool => tool.Name == "late_tool");
		Assert.Contains("Codemode: `tools.late_tool(args)` resolves to a string.", lateTool.Description);
		Assert.Equal(1, CountOccurrences(lateTool.Description, "Codemode: `tools."));
	}

	private static int CountOccurrences(string text, string value) =>
		text.Split(value, StringSplitOptions.None).Length - 1;

	[Fact]
	public void OnlyMode_ListsEveryCallableToolAndHidesDirectDeclarations()
	{
		var settings = new CodingAgentSettings { Codemode = new CodemodeSettings { Mode = "only" } };
		using var session = CreateSession(settings: settings);

		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);
		Assert.Contains("### `read`", codemode.Description);
		Assert.Contains("- Use read to examine files instead of cat or sed.", codemode.Description);
		Assert.DoesNotContain("### `codemode`", codemode.Description);

		// mode=only：直接工具只给脚本，不声明给模型；codemode 自身仍然声明。
		Assert.Contains(session.DeclaredTools, static tool => tool.Name == CodeModeTool.Name);
		Assert.DoesNotContain(session.DeclaredTools, static tool => tool.Name == "read");
		Assert.Contains(session.Tools, static tool => tool.Name == "read");
	}

	[Fact]
	public void Description_ListsCallableToolsButNotCodemode()
	{
		var read = FileTools.Read(_root);
		var description = CodeModeTool.CreateDescription([read, FileTools.Write(_root)]);

		Assert.Contains("Run JavaScript that calls other tools", description);
		Assert.Contains("### `read`", description);
		Assert.Contains("read(args:", description);
		Assert.Contains("ALL_TOOLS", description);
		Assert.DoesNotContain("### `codemode`", description);
	}

	[Fact]
	public void Settings_ReadCodemodeModeAndInlineBudget()
	{
		File.WriteAllText(Path.Combine(_root, "settings.json"), """
			{ "codemode": { "mode": "only", "inlineBudget": 1234 } }
			""");

		var settings = CodingAgentSettings.Load(_root, agentDir: _root, includeProject: false);

		Assert.Equal("only", settings.Codemode.Mode);
		Assert.Equal(1234, settings.Codemode.InlineBudget);
	}

	[Fact]
	public void ApplyTools_AddsTheCodemodeHintToDirectDeclarationsOnly()
	{
		using var session = CreateSession();

		var read = session.DeclaredTools.Single(static tool => tool.Name == "read");
		Assert.Contains("Codemode: `tools.read(args)` resolves to a string.", read.Description);

		var codemode = session.DeclaredTools.Single(static tool => tool.Name == CodeModeTool.Name);
		Assert.DoesNotContain("tools.codemode", codemode.Description);
	}

	[Fact]
	public void SystemPrompt_ListsServersWhoseToolsAreNotDeclared()
	{
		using var session = CreateSession(extraTools:
		[
			DiscoveryTool("docs_search", ToolExposure.Codemode) with
			{
				Namespace = "mcp__docs",
				NamespaceAliases = ["docs"],
				NamespaceDescription = "Docs server",
			},
		]);

		var system = PiWeft.Ai.Transcript.GetText(Assert.IsType<SystemMessage>(session.ProjectedMessages[0]));

		Assert.Contains("MCP servers whose tools are not declared to you.", system);
		Assert.Contains("- `mcp__docs` (codemode) Docs server", system);
	}

	[Fact]
	public void Description_GroupsToolsByNamespace()
	{
		var read = FileTools.Read(_root);
		var docs = DiscoveryTool("docs_search", ToolExposure.Direct, "Search the docs") with
		{
			Namespace = "mcp__docs",
			NamespaceDescription = "Docs server",
		};

		var description = CodeModeTool.CreateDescription([read, docs], inlineBudget: 3000);

		Assert.Contains("## mcp__docs\nDocs server", description);
		Assert.Contains("### `docs_search`", description);
		Assert.True(
			description.IndexOf("### `read`", StringComparison.Ordinal)
			< description.IndexOf("## mcp__docs", StringComparison.Ordinal));
	}

	[Fact]
	public void Description_LabelsNamespacesWhoseToolsDoNotFitTheBudget()
	{
		var docs = DiscoveryTool("docs_search", ToolExposure.Direct, "Search the docs") with
		{
			Namespace = "mcp__docs",
			NamespaceDescription = "Docs server",
		};

		var description = CodeModeTool.CreateDescription([docs], inlineBudget: 1);

		Assert.Contains("## mcp__docs (tools not listed)", description);
		Assert.DoesNotContain("### `docs_search`", description);
	}

	[Fact]
	public async Task Execute_RunsScriptsThatCallNestedTools()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ntwo\n");
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			const content = await tools.read({ path: "a.txt" });
			text(content.toUpperCase());
			return 2;
			"""));

		Assert.False(result.IsError, Text(result));
		var text = Text(result);
		Assert.Contains("Script completed", text);
		Assert.Contains("ONE", text);
		Assert.Contains("2", text);

		var details = Assert.IsType<JsonObject>(result.Details);
		var call = Assert.Single(details["calls"]!.AsArray());
		Assert.Equal("read", call!["name"]!.GetValue<string>());
		Assert.Equal("ok", call["status"]!.GetValue<string>());
	}

	[Fact]
	public async Task Execute_ReportsNestedToolFailuresInsideTheScriptAndAsErrorResult()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			await tools.read({ path: "missing.txt" });
			"""));

		Assert.True(result.IsError);
		var text = Text(result);
		Assert.Contains("Script failed", text);
		Assert.Contains("read (error)", text);
	}

	[Fact]
	public async Task Execute_SearchToolsAndDescribeToolUseTheLiveToolSet()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			const matches = searchTools("file contents");
			text(matches.map(function (match) { return match.name; }).join(","));
			text(describeTool(matches[0].name));
			"""));

		Assert.False(result.IsError, Text(result));
		var text = Text(result);
		Assert.Contains("read", text);
		Assert.Contains("args:", text);
	}

	[Fact]
	public async Task Execute_ModelsReadTheSessionRegistry()
	{
		var registry = new ModelRegistry();
		registry.Add(new Model
		{
			Id = "smoke",
			Provider = "test",
			Name = "Smoke",
			Api = "test",
			ContextWindow = 1000,
			MaxTokens = 100,
			Headers = new Dictionary<string, string> { ["authorization"] = "secret" },
		});
		using var session = CreateSession(registry);
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			const listed = models.getModelsOfType("chat", "test");
			const one = models.getModelOfType("chat", "test", "smoke");
			text(String(listed.length));
			return one.id + ":" + (one.headers === undefined ? "no-headers" : "leaked");
			"""));

		Assert.False(result.IsError, Text(result));
		Assert.Contains("smoke:no-headers", Text(result));
	}

	[Fact]
	public async Task Execute_ReturnedValueParticipatesInOutputNumbering()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			text("first");
			console.log("log line");
			return "second";
			"""));

		Assert.False(result.IsError, Text(result));
		var text = Text(result);
		Assert.Contains("==> text 1/2 <==\nfirst", text);
		Assert.Contains("==> text 2/2 <==\nsecond", text);
		Assert.True(
			text.IndexOf("<console_output>", StringComparison.Ordinal)
			> text.IndexOf("==> text 2/2", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Execute_ZeroOutputTokensTruncatesAndSpillsTheFullOutput()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			// @options: {"max_output_tokens": 0}
			text("hello world");
			"""));

		Assert.False(result.IsError, Text(result));
		Assert.Contains("Warning: truncated output", Text(result));
		Assert.DoesNotContain("hello world", Text(result));

		var details = Assert.IsType<JsonObject>(result.Details);
		var path = details["fullOutputPath"]!.GetValue<string>();
		Assert.True(File.Exists(path));
		Assert.Contains("hello world", File.ReadAllText(path));
		File.Delete(path);
	}

	private const string PngBase64 =
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

	private static ModelRegistry ModelCallRegistry()
	{
		var registry = new ModelRegistry();
		registry.Add(new Model
		{
			Id = "fake-classifier",
			Provider = "test",
			Name = "Fake classifier",
			Api = FakeClassifierProvider.ApiId,
			Type = ModelType.Classifier,
		});
		registry.Add(new Model
		{
			Id = "fake-image",
			Provider = "test",
			Name = "Fake image",
			Api = FakeImagesProvider.ApiId,
			Type = ModelType.Image,
		});
		return registry;
	}

	private sealed class FakeClassifierProvider : IClassifierApiProvider
	{
		public const string ApiId = "fake-classifier-api";

		public string Api => ApiId;

		public Task<ClassifierResult> ClassifyAsync(
			Model model,
			ClassifierContext context,
			ProviderRequestOptions options,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(new ClassifierResult
			{
				Api = model.Api,
				Provider = model.Provider,
				Model = model.Id,
				Answers = new Dictionary<string, ClassifierAnswer>
				{
					["quality"] = new ClassifierChoiceAnswer
					{
						Choice = "good",
						Confidence = 0.9,
						Probabilities = new Dictionary<string, double> { ["good"] = 0.9, ["bad"] = 0.1 },
					},
				},
				Usage = new Usage { Input = 10, Output = 2, TotalTokens = 12, Cost = new UsageCost { Total = 0.01 } },
			});
	}

	private sealed class FakeImagesProvider : IImageApiProvider
	{
		public const string ApiId = "fake-images-api";

		public string Api => ApiId;

		public Task<ImageGenerationResult> GenerateAsync(
			Model model,
			ImageGenerationRequest request,
			ProviderRequestOptions options,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(new ImageGenerationResult
			{
				Api = model.Api,
				Provider = model.Provider,
				Model = model.Id,
				Output =
				[
					new TextContent { Text = "generated" },
					new ImageContent { Data = PngBase64, MimeType = "image/png" },
				],
				Usage = new Usage { Input = 5, TotalTokens = 5, Cost = new UsageCost { Total = 0.02 } },
			});
	}

	[Fact]
	public async Task Execute_ClassifyAndGenerateImagesUseTheGateways()
	{
		var classifiers = new ClassifiersGateway();
		classifiers.Register(new FakeClassifierProvider());
		var images = new ImagesGateway();
		images.Register(new FakeImagesProvider());
		using var session = CreateSession(ModelCallRegistry(), images: images, classifiers: classifiers);
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			const classifier = models.getModelOfType("classifier", "test", "fake-classifier");
			const verdict = await models.classify(classifier, {
			  state: { text: "hello" },
			  questions: {
			    quality: { type: "choice", instructions: "How good?", criteria: { good: "good", bad: "bad" } },
			  },
			});
			text(verdict.answers.quality.choice);

			const imageModel = models.getModelOfType("image", "test", "fake-image");
			const generated = await models.generateImages(imageModel, {
			  input: [{ type: "text", text: "a cat" }],
			});
			image(generated.output[1]);
			return generated.output.length;
			"""));

		Assert.False(result.IsError, Text(result));
		Assert.Contains("good", Text(result));
		Assert.Contains(result.Content, static block => block is ImageContent);

		var details = Assert.IsType<JsonObject>(result.Details);
		var modelCalls = details["calls"]!.AsArray()
			.Where(static call => call!["name"]!.GetValue<string>().StartsWith("models.", StringComparison.Ordinal))
			.ToList();
		Assert.Equal(2, modelCalls.Count);
		Assert.NotNull(result.Usage);
		Assert.True(result.Usage!.Cost.Total > 0);
	}

	[Fact]
	public async Task Execute_ClassifyRejectsAnInvalidContext()
	{
		var classifiers = new ClassifiersGateway();
		classifiers.Register(new FakeClassifierProvider());
		using var session = CreateSession(ModelCallRegistry(), classifiers: classifiers);
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			const classifier = models.getModelOfType("classifier", "test", "fake-classifier");
			await models.classify(classifier, { questions: {} });
			"""));

		Assert.True(result.IsError);
		Assert.Contains("context.state", Text(result));
	}

	[Fact]
	public async Task Execute_SavesImagesBesideTheirPathLabel()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			image("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
			"""));

		Assert.False(result.IsError, Text(result));
		Assert.Contains(result.Content, static block => block is ImageContent);
		var label = Assert.Single(result.Content.OfType<TextContent>(), static text => text.Text.StartsWith("[Image saved to ", StringComparison.Ordinal));
		var path = label.Text["[Image saved to ".Length..label.Text.IndexOf(" (", StringComparison.Ordinal)];
		Assert.True(File.Exists(path));
		File.Delete(path);
	}

	[Fact]
	public async Task Execute_StructuredContentIsReturnedWhenTheToolDeclaresAnOutputSchema()
	{
		using var session = CreateSession();
		var structured = new AgentTool
		{
			Name = "structured",
			Label = "structured",
			Description = "Returns structured data",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
			OutputSchema = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "number" } },
			},
			Execute = static _ => Task.FromResult(new AgentToolResult
			{
				Content = [new TextContent { Text = """{"value":42}""" }],
				StructuredContent = new JsonObject { ["value"] = 42 },
			}),
		};
		session.SetTools([.. session.Tools, structured]);

		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);
		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			const data = await tools.structured({});
			return data.value;
			"""));

		Assert.False(result.IsError, Text(result));
		Assert.Contains("42", Text(result));
	}

	[Fact]
	public void Description_AppendsToolGuidelines()
	{
		var read = FileTools.Read(_root);
		var description = CodeModeTool.CreateDescription(
			[read],
			guidelines: static _ => ["Prefer read over cat."]);

		Assert.Contains("- Prefer read over cat.", description);
	}

	[Fact]
	public async Task Execute_PublishesNestedCallUpdates()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "hello\n");
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);
		var updates = new List<AgentToolResult>();

		var result = await codemode.Execute(new AgentToolInvocation
		{
			ToolCallId = "call-1",
			ToolName = CodeModeTool.Name,
			Arguments = new JsonObject { ["code"] = """await tools.read({ path: "a.txt" });""" },
			OnUpdate = updates.Add,
		});

		Assert.False(result.IsError, Text(result));
		Assert.NotEmpty(updates);
		Assert.Contains(updates, update =>
			update.Details?["calls"] is JsonArray { Count: 1 } calls
			&& calls[0]!["name"]!.GetValue<string>() == "read");
	}

	[Fact]
	public async Task Execute_CallsCutOffByTheScriptEndAreCancelled()
	{
		using var session = CreateSession();
		session.SetTools([
			.. session.Tools,
			new AgentTool
			{
				Name = "hang",
				Label = "hang",
				Description = "Never finishes",
				Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
				Execute = async invocation =>
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, invocation.CancellationToken);
					return AgentToolResult.Text("late");
				},
			},
		]);
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			tools.hang({});
			exit();
			"""));

		Assert.False(result.IsError, Text(result));
		var details = Assert.IsType<JsonObject>(result.Details);
		var calls = details["calls"]!.AsArray();
		Assert.Single(calls);
		Assert.True(
			calls[0]!["status"]!.GetValue<string>() == "cancelled",
			calls[0]!.ToJsonString());
	}

	[Fact]
	public async Task CodemodeExposure_IsCallableFromScriptsButNotDeclared()
	{
		using var session = CreateSession(extraTools: [DiscoveryTool("script_only", ToolExposure.Codemode)]);
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		Assert.Contains(session.Tools, static tool => tool.Name == "script_only");
		Assert.DoesNotContain(session.DeclaredTools, static tool => tool.Name == "script_only");
		Assert.Contains("### `script_only`", codemode.Description);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			return await tools.script_only({});
			"""));

		Assert.False(result.IsError, Text(result));
		Assert.Contains("called", Text(result));
	}

	[Fact]
	public async Task DeferredTool_IsLoadedByToolSearchAndRecordedOnTheBranch()
	{
		using var session = CreateSession(extraTools: [DiscoveryTool("deferred_tool", ToolExposure.Deferred)]);

		Assert.DoesNotContain(session.DeclaredTools, static tool => tool.Name == "deferred_tool");
		var toolSearch = session.DeclaredTools.Single(static tool => tool.Name == "tool_search");

		var result = await toolSearch.Execute(new AgentToolInvocation
		{
			ToolCallId = "search-1",
			ToolName = "tool_search",
			Arguments = new JsonObject { ["query"] = "deferred" },
		});

		Assert.False(result.IsError, Text(result));
		Assert.Contains("deferred_tool", Text(result));
		Assert.Contains(session.DeclaredTools, static tool => tool.Name == "deferred_tool");
		Assert.Contains(session.Session.Entries, static entry =>
			entry is CustomEntry { CustomType: "tool-load" } custom
			&& custom.Data?["tools"] is JsonArray { Count: 1 });
	}

	[Fact]
	public async Task DeferredTool_LoadIsRestoredOnASecondSession()
	{
		var sessionManager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		using (var first = CreateSession(
			extraTools: [DiscoveryTool("deferred_tool", ToolExposure.Deferred)],
			session: sessionManager))
		{
			var search = first.DeclaredTools.Single(static tool => tool.Name == "tool_search");
			await search.Execute(new AgentToolInvocation
			{
				ToolCallId = "search-1",
				ToolName = "tool_search",
				Arguments = new JsonObject { ["query"] = "deferred" },
			});
		}

		using var second = CreateSession(
			extraTools: [DiscoveryTool("deferred_tool", ToolExposure.Deferred)],
			session: sessionManager);
		Assert.Contains(second.DeclaredTools, static tool => tool.Name == "deferred_tool");
	}

	[Fact]
	public void CodemodeExposure_AutoEnablesCodemode()
	{
		var tools = BuiltinTools.Tools(_root);
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
			Settings = new CodingAgentSettings(),
			Tools = [.. tools, DiscoveryTool("script_only", ToolExposure.Codemode)],
		});

		Assert.Contains(session.Tools, static tool => tool.Name == CodeModeTool.Name);
		Assert.DoesNotContain(session.DeclaredTools, static tool => tool.Name == "script_only");
	}

	[Fact]
	public async Task Execute_StorePersistsOnTheSessionBranch()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		await codemode.Execute(Invoke(CodeModeTool.Name, """store("counter", 41);"""));

		var entry = Assert.Single(session.Session.Entries.OfType<CustomEntry>());
		Assert.Equal(CodeModeTool.EntryType, entry.CustomType);
		Assert.Equal(41, entry.Data!["set"]!["counter"]!.GetValue<int>());

		var second = await codemode.Execute(Invoke(CodeModeTool.Name, """
			store("counter", load("counter") + 1);
			return load("counter");
			"""));

		Assert.False(second.IsError, Text(second));
		Assert.Contains("42", Text(second));
	}

	[Fact]
	public async Task Execute_StoreWritesAreDroppedWhenTheScriptFails()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			store("counter", 1);
			throw new Error("boom");
			"""));

		Assert.True(result.IsError);
		Assert.DoesNotContain(session.Session.Entries, static entry => entry is CustomEntry);
	}

	[Fact]
	public async Task Execute_HonoursTheOptionsLine()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, """
			// @options: {"timeout_ms": 200}
			while (true) {}
			"""));

		Assert.True(result.IsError);
		Assert.Contains("timed out", Text(result));
	}

	[Fact]
	public async Task Execute_SourceErrorsBecomeToolErrors()
	{
		using var session = CreateSession();
		var codemode = session.Tools.Single(static tool => tool.Name == CodeModeTool.Name);

		var result = await codemode.Execute(Invoke(CodeModeTool.Name, "   "));

		Assert.True(result.IsError);
		Assert.Contains("non-empty", Text(result));
	}
}
