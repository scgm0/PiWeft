using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.JavaScript.Tests;

public sealed class CodeModeSandboxTests
{
	private static CodeModeTool Tool(string name, Func<JsonNode?, CancellationToken, Task<JsonNode?>> execute) => new()
	{
		Name = name,
		Description = $"The {name} tool",
		InputSchema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
			["required"] = new JsonArray("path"),
		},
		OutputSchema = new JsonObject { ["type"] = "string" },
		Execute = execute,
	};

	private static Task<CodeModeResult> Run(string code, params CodeModeTool[] tools) =>
		new CodeModeSandbox(new CodeModeSandboxOptions { Tools = tools, Timeout = TimeSpan.FromSeconds(30) })
			.ExecuteAsync(code);

	[Fact]
	public async Task Execute_ReturnsValueAndTextOutput()
	{
		var result = await Run("""
			text("hello");
			return 21 * 2;
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(42, result.Value!.GetValue<int>());
		var item = Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output));
		Assert.Equal("hello", item.Text);
		Assert.False(item.Console);
	}

	[Fact]
	public async Task Execute_CallsToolsWithJsonArguments()
	{
		JsonNode? seen = null;
		var result = await Run(
			"""const value = await tools.echo({ path: "a.txt" }); text(value);""",
			Tool("echo", (arguments, _) =>
			{
				seen = arguments;
				return Task.FromResult<JsonNode?>(JsonValue.Create("content"));
			}));

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal("a.txt", seen!["path"]!.GetValue<string>());
		Assert.Equal("content", Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output)).Text);
		var call = Assert.Single(result.Calls);
		Assert.Equal("echo", call.Name);
		Assert.Equal(CodeModeCallStatus.Ok, call.Status);
	}

	[Fact]
	public async Task Execute_ExposesToolNamesAsIdentifiers()
	{
		var result = await Run(
			"""const value = await tools.my_tool({ path: "x" }); return value;""",
			Tool("my-tool", (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create(7))));

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(7, result.Value!.GetValue<int>());
	}

	[Fact]
	public async Task Execute_RejectsToolFailuresInsideTheScript()
	{
		var result = await Run(
			"""
			try {
			  await tools.fail({ path: "x" });
			} catch (error) {
			  text("caught: " + error.message);
			}
			""",
			Tool("fail", (_, _) => throw new InvalidOperationException("nope")));

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal("caught: nope", Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output)).Text);
		Assert.Equal(CodeModeCallStatus.Error, Assert.Single(result.Calls).Status);
	}

	[Fact]
	public async Task Execute_ReportsScriptErrorsWithStack()
	{
		var result = await Run("""
			throw new Error("boom");
			""");

		Assert.False(result.Ok);
		Assert.Equal(CodeModeErrorKind.Script, result.Error!.Kind);
		Assert.Contains("boom", result.Error.Message);
		Assert.Contains("codemode.js", result.Error.Stack);
	}

	[Fact]
	public async Task Execute_CollectsConsoleOutputSeparately()
	{
		var result = await Run("""
			console.log("a", 1);
			text("b");
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(2, result.Output.Count);
		Assert.True(((CodeModeTextItem)result.Output[0]).Console);
		Assert.False(((CodeModeTextItem)result.Output[1]).Console);
	}

	[Fact]
	public async Task Execute_ExitStopsTheScriptAndKeepsOutput()
	{
		var result = await Run("""
			text("before");
			exit();
			text("after");
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal("before", Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output)).Text);
	}

	[Fact]
	public async Task Execute_StoreAndLoadRoundTripAcrossCalls()
	{
		var result = await Run(
			"""
			const previous = load("runs") ?? 0;
			store("runs", previous + 1);
			store("gone", undefined);
			return load("runs");
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(1, result.Value!.GetValue<int>());
		Assert.Equal(1, result.StoreWrites.Set["runs"]!.GetValue<int>());
		Assert.Equal(["gone"], result.StoreWrites.Delete);
	}

	[Fact]
	public async Task Execute_LoadReadsTheProvidedStore()
	{
		var sandbox = new CodeModeSandbox(new CodeModeSandboxOptions { Timeout = TimeSpan.FromSeconds(30) });
		var result = await sandbox.ExecuteAsync(
			"""return load("counter") + 1;""",
			new CodeModeExecuteOptions
			{
				Store = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["counter"] = 41 },
			});

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(42, result.Value!.GetValue<int>());
	}

	[Fact]
	public async Task Execute_ImageOutputIsValidatedAndReported()
	{
		// 1x1 PNG。
		var result = await Run(
			"""image("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");""");

		Assert.True(result.Ok, result.Error?.Message);
		var image = Assert.IsType<CodeModeImageItem>(Assert.Single(result.Output));
		Assert.Equal("image/png", image.MimeType);
	}

	[Fact]
	public async Task Execute_RejectsRemoteImages()
	{
		var result = await Run("""image("https://example.com/a.png");""");

		Assert.False(result.Ok);
		Assert.Contains("remote image", result.Error!.Message);
	}

	[Fact]
	public async Task Execute_UnknownToolListsCloseMatches()
	{
		var result = await Run(
			"""try { await tools.raed({}); } catch (error) { text(error.message); }""",
			Tool("read", (_, _) => Task.FromResult<JsonNode?>(null)));

		Assert.True(result.Ok, result.Error?.Message);
		var message = Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output)).Text;
		Assert.Contains("tools.raed does not exist", message);
		Assert.Contains("Available: read", message);
	}

	[Fact]
	public async Task Execute_TimesOutRunsAwayScripts()
	{
		var sandbox = new CodeModeSandbox(new CodeModeSandboxOptions { Timeout = TimeSpan.FromMilliseconds(300) });
		var result = await sandbox.ExecuteAsync("while (true) {}");

		Assert.False(result.Ok);
		Assert.Equal(CodeModeErrorKind.Timeout, result.Error!.Kind);
	}

	[Fact]
	public async Task Execute_TimeoutCancelsPendingToolCalls()
	{
		var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var sandbox = new CodeModeSandbox(new CodeModeSandboxOptions
		{
			Timeout = TimeSpan.FromMilliseconds(300),
			Tools =
			[
				Tool("hang", async (_, cancellationToken) =>
				{
					try
					{
						await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
					}
					catch (OperationCanceledException)
					{
						cancelled.TrySetResult(true);
						throw;
					}

					return null;
				}),
			],
		});

		var result = await sandbox.ExecuteAsync("""await tools.hang({ path: "x" });""");

		Assert.False(result.Ok);
		Assert.Equal(CodeModeErrorKind.Timeout, result.Error!.Kind);
		Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)), "the tool call was not cancelled");
	}

	[Fact]
	public async Task Execute_AbortReportsAborted()
	{
		var sandbox = new CodeModeSandbox(new CodeModeSandboxOptions
		{
			Timeout = TimeSpan.FromSeconds(30),
			Tools =
			[
				Tool("hang", async (_, cancellationToken) =>
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
					return null;
				}),
			],
		});
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
		var result = await sandbox.ExecuteAsync(
			"""await tools.hang({ path: "x" });""",
			new CodeModeExecuteOptions { CancellationToken = cts.Token });

		Assert.False(result.Ok);
		Assert.Equal(CodeModeErrorKind.Aborted, result.Error!.Kind);
	}

	[Fact]
	public async Task Execute_DetectsAPromiseThatCanNeverSettle()
	{
		var result = await Run("await new Promise(() => {});");

		Assert.False(result.Ok);
		Assert.Equal(CodeModeErrorKind.Script, result.Error!.Kind);
		Assert.Contains("can never settle", result.Error.Message);
	}

	[Fact]
	public async Task Execute_ScriptCannotReachTheHostOrModules()
	{
		var result = await Run("""
			const missing = [];
			for (const name of ["require", "process", "fetch", "setTimeout", "XMLHttpRequest", "WebAssembly", "importNamespace"]) {
			  if (typeof globalThis[name] !== "undefined") missing.push(name);
			}
			return missing;
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Empty(result.Value!.AsArray());
	}

	[Fact]
	public async Task Execute_HidesHostAndWebGlobals()
	{
		var result = await Run("""
			text([
			  typeof fetch, typeof setTimeout, typeof Worker, typeof localStorage,
			  typeof __cmCall, typeof __cmFinish,
			].join(","));
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(
			"undefined,undefined,undefined,undefined,undefined,undefined",
			Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output)).Text);
	}

	[Fact]
	public async Task Execute_IgnoresUnhandledRejections()
	{
		var result = await Run("""
			Promise.reject(new Error("ignored"));
			return 1;
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(1, result.Value!.GetValue<int>());
	}

	[Fact]
	public async Task Execute_AllowsEvalAndFunctionConstructors()
	{
		var result = await Run("""
			const compiled = eval("1 + 1");
			const multiply = new Function("a", "b", "return a * b");
			const after = await Promise.resolve(3);
			text(String(compiled));
			return compiled + multiply(6, 7) + eval("after - 3");
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(44, result.Value!.GetValue<int>());
		Assert.Equal("2", Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output)).Text);
	}

	[Fact]
	public async Task Execute_SearchToolsRanksAndDescribeToolReturnsTheDescription()
	{
		// 与工具工厂一致：Description 是完整样例（描述 + 声明），describeTool/searchTools 原样返回。
		var grep = new CodeModeTool
		{
			Name = "grep",
			Description = "Search file contents for patterns",
			InputSchema = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Pattern to search for" },
				},
			},
			Execute = static (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("")),
		};
		grep = grep with { Description = CodeModeDeclarations.RenderSample(grep) };

		var result = await Run(
			"""
			const matches = searchTools("pattern");
			text(matches.map(function (match) { return match.name; }).join(","));
			text(describeTool("grep"));
			return describeTool("missing");
			""",
			new CodeModeTool
			{
				Name = "read",
				Description = "Read file contents",
				InputSchema = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
				},
				Execute = static (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("")),
			},
			grep);

		Assert.True(result.Ok, result.Error?.Message);
		var texts = result.Output.OfType<CodeModeTextItem>().Select(static item => item.Text).ToList();
		Assert.Equal("grep", texts[0]);
		Assert.Contains("grep(args:", texts[1]);
		Assert.Null(result.Value);
	}

	[Fact]
	public async Task Execute_SearchAndDescribeNamespaces()
	{
		static Task<JsonNode?> Ok(JsonNode? _, CancellationToken __) => Task.FromResult<JsonNode?>(JsonValue.Create("ok"));
		var sandbox = new CodeModeSandbox(new CodeModeSandboxOptions
		{
			Tools =
			[
				new CodeModeTool
				{
					Name = "docs_search",
					Description = "Search the documentation",
					Namespace = "mcp__docs",
					NamespaceAliases = ["docs"],
					NamespaceDescription = "Documentation tools",
					NamespaceInstructions = "Search before writing.",
					Execute = Ok,
				},
				new CodeModeTool
				{
					Name = "code_search",
					Description = "Search the source code",
					Namespace = "code",
					Execute = Ok,
				},
			],
		});

		var result = await sandbox.ExecuteAsync("""
			const matches = searchTools("search", { namespace: "docs" });
			text(matches.map(function (match) { return match.name; }).join(","));
			const namespace = describeNamespace("mcp__docs");
			return [
			  namespace.name,
			  namespace.aliases.join(","),
			  namespace.tools.join(","),
			  describeNamespace("missing") === undefined,
			  namespace.description,
			  namespace.instructions,
			].join("|");
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal("docs_search", Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output)).Text);
		Assert.Equal("mcp__docs|docs|docs_search|true|Documentation tools|Search before writing.", result.Value!.GetValue<string>());
	}

	[Fact]
	public async Task Execute_SearchToolsRejectsBadLimits()
	{
		var result = await Run("""searchTools("read", { limit: 0 });""");

		Assert.False(result.Ok);
		Assert.Contains("limit must be a positive integer", result.Error!.Message);
	}

	[Fact]
	public async Task Execute_SearchToolsWithAnEmptyQueryReturnsNothing()
	{
		var result = await Run("""return searchTools("").length;""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(0, result.Value!.GetValue<int>());
	}

	[Fact]
	public async Task Execute_ModelsUseTheHostCatalog()
	{
		var sandbox = new CodeModeSandbox(new CodeModeSandboxOptions
		{
			Timeout = TimeSpan.FromSeconds(30),
			Models = request => request.Operation switch
			{
				CodeModeModelOperation.Get => new JsonObject
				{
					["id"] = request.Id,
					["provider"] = request.Provider,
					["type"] = request.Type,
				},
				_ => new JsonArray(
					(JsonNode)new JsonObject { ["id"] = "smoke", ["type"] = request.Type },
					(JsonNode)new JsonObject { ["id"] = "other", ["type"] = request.Type }),
			},
		});

		var result = await sandbox.ExecuteAsync("""
			const listed = models.getModelsOfType("chat");
			const available = models.getAvailableOfType("image", "test");
			const one = models.getModelOfType("chat", "test", "smoke");
			text(listed.map(function (model) { return model.id; }).join(","));
			text(String(available.length));
			return one.provider + "/" + one.id;
			""");

		Assert.True(result.Ok, result.Error?.Message);
		var texts = result.Output.OfType<CodeModeTextItem>().Select(static item => item.Text).ToList();
		Assert.Equal("smoke,other", texts[0]);
		Assert.Equal("2", texts[1]);
		Assert.Equal("test/smoke", result.Value!.GetValue<string>());
	}

	[Fact]
	public async Task Execute_ModelsRejectUnknownTypesAndMissingHost()
	{
		var unknown = await Run("""models.getModelsOfType("video");""");
		Assert.False(unknown.Ok);
		Assert.Contains("Unknown model type", unknown.Error!.Message);

		var missing = await Run("""models.getModelsOfType("chat");""");
		Assert.False(missing.Ok);
		Assert.Contains("models.* is not available", missing.Error!.Message);
	}

	[Fact]
	public async Task Execute_RunsOnADedicatedWorkerThread()
	{
		var caller = Environment.CurrentManagedThreadId;
		var threads = new System.Collections.Concurrent.ConcurrentBag<int>();
		var result = await Run(
			"""
			const first = await tools.thread({ path: "a" });
			const second = await tools.thread({ path: "b" });
			return first + "," + second;
			""",
			Tool("thread", async (_, _) =>
			{
				var id = Environment.CurrentManagedThreadId;
				threads.Add(id);
				await Task.Delay(20);
				return JsonValue.Create(id);
			}));

		Assert.True(result.Ok, result.Error?.Message);
		var ids = threads.Distinct().ToList();
		Assert.Single(ids);
		Assert.NotEqual(caller, ids[0]);
		Assert.Equal($"{ids[0]},{ids[0]}", result.Value!.GetValue<string>());
	}

	[Fact]
	public async Task Execute_OutputOverflowFailsTheScriptEvenIfCaught()
	{
		var sandbox = new CodeModeSandbox(new CodeModeSandboxOptions
		{
			Timeout = TimeSpan.FromSeconds(30),
			MaxOutputChars = 100,
		});

		var result = await sandbox.ExecuteAsync("""
			try {
			  for (let index = 0; index < 100; index++) text("0123456789");
			} catch (error) {
			}
			return "done";
			""");

		Assert.False(result.Ok);
		Assert.Equal(CodeModeErrorKind.Script, result.Error!.Kind);
		Assert.Contains("exceeded the limit", result.Error.Message);
		Assert.NotEmpty(result.Output);
	}

	[Fact]
	public async Task Execute_StoreEnforcesTheValueSizeLimit()
	{
		var result = await Run("""
			try {
			  store("big", "a".repeat(300000));
			} catch (error) {
			  text(error.message);
			}
			""");

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Contains("more than the limit", Assert.IsType<CodeModeTextItem>(Assert.Single(result.Output)).Text);
		Assert.Empty(result.StoreWrites.Set);
	}

	[Fact]
	public async Task Execute_ReportsUnserializableReturnValues()
	{
		var result = await Run("""
			const value = {};
			value.self = value;
			return value;
			""");

		Assert.False(result.Ok);
		Assert.Equal(CodeModeErrorKind.Script, result.Error!.Kind);
		var message = result.Error.Message;
		Assert.True(
			message.Contains("circular", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("cyclic", StringComparison.OrdinalIgnoreCase),
			message);
	}

	[Fact]
	public async Task Execute_PromiseAllRunsToolsConcurrently()
	{
		var result = await Run(
			"""
			const values = await Promise.all([
			  tools.delay({ path: "a" }),
			  tools.delay({ path: "b" }),
			]);
			return values;
			""",
			Tool("delay", async (arguments, _) =>
			{
				await Task.Delay(50);
				return JsonValue.Create(arguments!["path"]!.GetValue<string>());
			}));

		Assert.True(result.Ok, result.Error?.Message);
		Assert.Equal(2, result.Value!.AsArray().Count);
		Assert.Equal(2, result.Calls.Count);
	}
}
