using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.Cli.Tests;

internal static class TestModels
{
	public static Model Model(string id = "test-model", string provider = "test") => new()
	{
		Id = id,
		Provider = provider,
		Api = "anthropic-messages",
		BaseUrl = "https://example.invalid",
		MaxTokens = 4096,
		ContextWindow = 128_000,
		Reasoning = true,
		Cost = new ModelCost { Input = 1, Output = 2 },
	};

	public static AssistantMessage Blank(Model model) => new()
	{
		Api = model.Api,
		Provider = model.Provider,
		Model = model.Id,
		Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
	};

	public static AssistantMessageEventStream Text(Model model, string text)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var message = blank with
		{
			Content = [new TextContent { Text = text }],
			StopReason = StopReason.Stop,
			Usage = new Usage { Input = 10, Output = 4, TotalTokens = 14 },
		};

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = message });
		stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
		return stream;
	}

	public static AssistantMessageEventStream Error(Model model, string message)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var failed = blank with { StopReason = StopReason.Error, ErrorMessage = message };

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new ErrorEvent { Reason = StopReason.Error, Partial = failed });
		return stream;
	}

	public static AssistantMessageEventStream ToolCall(Model model, string id, string name, JsonObject arguments)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var call = new ToolCall { Id = id, Name = name, Arguments = arguments };
		var message = blank with { Content = [call], StopReason = StopReason.ToolUse };

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new ToolCallStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = message });
		return stream;
	}
}

internal sealed class ScriptedStreamFn
{
	private readonly Queue<Func<Model, AssistantMessageEventStream>> _responses = new();

	public int Calls { get; private set; }

	/// <summary>每次请求模型看到的完整消息快照，便于断言提示词/附件/展开结果。</summary>
	public List<IReadOnlyList<Message>> Requests { get; } = [];

	public void Enqueue(string text) => _responses.Enqueue(model => TestModels.Text(model, text));

	public void EnqueueError(string message) => _responses.Enqueue(model => TestModels.Error(model, message));

	public void EnqueueToolCall(string id, string name, JsonObject arguments) =>
		_responses.Enqueue(model => TestModels.ToolCall(model, id, name, arguments));

	public AssistantMessageEventStream Invoke(Model model, IReadOnlyList<Message> messages, StreamOptions options)
	{
		Calls++;
		Requests.Add([.. messages]);
		var response = _responses.Count > 0
			? _responses.Dequeue()
			: (Func<Model, AssistantMessageEventStream>)(model => TestModels.Text(model, "no scripted response"));
		return response(model);
	}
}

public class CliArgumentsTests
{
	[Fact]
	public void Parse_DefaultsToTextMode()
	{
		var (arguments, error) = CliArguments.Parse([]);

		Assert.Null(error);
		Assert.NotNull(arguments);
		Assert.Equal(CliMode.Text, arguments!.Mode);
		Assert.Empty(arguments.Prompts);
	}

	[Fact]
	public void Parse_ReadsFlagsAndValues()
	{
		var (arguments, error) = CliArguments.Parse(
		[
			"--mode", "json",
			"--model", "anthropic/claude-sonnet-4-5",
			"--provider=anthropic",
			"--thinking", "high",
			"--session-dir", "/tmp/s",
			"--name", "demo",
			"-t", "read,edit",
			"-xt", "bash",
			"--no-session",
			"--list-models", "sonnet",
			"first prompt",
			"--", "--not-a-flag",
		]);

		Assert.Null(error);
		var parsed = arguments!;
		Assert.Equal(CliMode.Json, parsed.Mode);
		Assert.Equal("anthropic/claude-sonnet-4-5", parsed.Model);
		Assert.Equal("anthropic", parsed.Provider);
		Assert.Equal("high", parsed.ThinkingLevel);
		Assert.Equal("/tmp/s", parsed.SessionDir);
		Assert.Equal("demo", parsed.SessionName);
		Assert.Equal(["read", "edit"], parsed.Tools);
		Assert.Equal(["bash"], parsed.ExcludeTools);
		Assert.True(parsed.NoSession);
		Assert.True(parsed.ListModels);
		Assert.Equal("sonnet", parsed.ListModelsSearch);
		Assert.Equal(["first prompt", "--not-a-flag"], parsed.Prompts);
	}

	[Fact]
	public void Parse_RejectsUnknownFlagsAndBadModes()
	{
		Assert.NotNull(CliArguments.Parse(["--nope"]).Error);
		Assert.NotNull(CliArguments.Parse(["--mode", "xml"]).Error);
		Assert.NotNull(CliArguments.Parse(["--model"]).Error);
	}

	[Fact]
	public void Parse_AcceptsOutOfScopeFlagsWithoutConsumingPrompts()
	{
		var (arguments, error) = CliArguments.Parse(["--no-extensions", "--no-mcp", "hello"]);

		Assert.Null(error);
		Assert.Equal(["hello"], arguments!.Prompts);
	}

	[Fact]
	public void Parse_SessionFlagImpliesResume()
	{
		var (arguments, _) = CliArguments.Parse(["--session", "/tmp/x.jsonl"]);

		Assert.Equal("/tmp/x.jsonl", arguments!.Session);
		Assert.True(arguments.Resume);
	}

	[Fact]
	public void Parse_CollectsFileArgumentsSeparately()
	{
		var (arguments, error) = CliArguments.Parse(["@a.png", "look", "@b.md", "--", "@c.txt"]);

		Assert.Null(error);
		Assert.Equal(["a.png", "b.md", "c.txt"], arguments!.FileArgs);
		Assert.Equal(["look"], arguments.Prompts);
	}

	[Fact]
	public void Parse_ReadsModelsAndExportFlags()
	{
		var (arguments, error) = CliArguments.Parse(["--models", "anthropic/*, sonnet ,", "--export", "/tmp/s.jsonl", "out.html"]);

		Assert.Null(error);
		Assert.Equal(["anthropic/*", "sonnet"], arguments!.Models);
		Assert.Equal("/tmp/s.jsonl", arguments.Export);
		Assert.Equal(["out.html"], arguments.Prompts);
	}

	[Fact]
	public void Parse_ReadsProjectTrustFlags()
	{
		Assert.True(CliArguments.Parse(["-a"]).Arguments!.ProjectTrustOverride == true);
		Assert.True(CliArguments.Parse(["--approve"]).Arguments!.ProjectTrustOverride == true);
		Assert.True(CliArguments.Parse(["--no-approve"]).Arguments!.ProjectTrustOverride == false);
		Assert.Null(CliArguments.Parse([]).Arguments!.ProjectTrustOverride);
	}
}

public class CliEnvironmentTests
{
	private static ModelRegistry Registry()
	{
		var registry = new ModelRegistry();
		registry.Add(TestModels.Model("claude-sonnet-4-5", "anthropic") with { Api = "anthropic-messages" });
		registry.Add(TestModels.Model("claude-opus-4-5", "anthropic") with { Api = "anthropic-messages" });
		registry.Add(TestModels.Model("gpt-5", "openai") with { Api = "openai-completions" });
		registry.Add(TestModels.Model("gemini-3-pro", "google") with { Api = "google-generative-ai" });
		// A provider whose api this port has no adapter for.
		registry.Add(TestModels.Model("mystery-1", "mystery") with { Api = "totally-unknown-api" });
		return registry;
	}

	[Fact]
	public void FindModel_ResolvesQualifiedIdsGlobsAndSubstrings()
	{
		var registry = Registry();

		Assert.Equal("gpt-5", CliEnvironment.FindModel(registry, "openai/gpt-5")!.Id);
		Assert.Equal("gpt-5", CliEnvironment.FindModel(registry, "gpt-5")!.Id);
		Assert.Equal("claude-opus-4-5", CliEnvironment.FindModel(registry, "anthropic/*opus*")!.Id);
		Assert.Equal("claude-sonnet-4-5", CliEnvironment.FindModel(registry, "sonnet")!.Id);
		Assert.Null(CliEnvironment.FindModel(registry, "does-not-exist"));
	}

	[Fact]
	public void Create_GatesProjectSettingsWhenUntrusted()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-env-trust-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(Path.Combine(root, ".pi", "skills"));
		File.WriteAllText(
			Path.Combine(root, ".pi", "settings.json"),
			"""{"timeoutMs":12345}""");

		try
		{
			var (trusted, trustedError) = CliEnvironment.Create(new CliArguments { ProjectTrustOverride = true }, root);
			Assert.Null(trustedError);
			Assert.True(trusted!.ProjectTrusted);
			Assert.Equal(12345, trusted.Settings.TimeoutMs);

			var (untrusted, untrustedError) = CliEnvironment.Create(new CliArguments { ProjectTrustOverride = false }, root);
			Assert.Null(untrustedError);
			Assert.False(untrusted!.ProjectTrusted);
			// 不受信时不合并项目 settings.json。
			Assert.Null(untrusted.Settings.TimeoutMs);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void MatchesModelPattern_SupportsGlobsAndSubstrings()
	{
		var sonnet = TestModels.Model("claude-sonnet-4-5", "anthropic");

		Assert.True(CliEnvironment.MatchesModelPattern(sonnet, "anthropic/*"));
		Assert.True(CliEnvironment.MatchesModelPattern(sonnet, "*sonnet*"));
		Assert.True(CliEnvironment.MatchesModelPattern(sonnet, "Sonnet"));
		Assert.False(CliEnvironment.MatchesModelPattern(sonnet, "openai/*"));
		Assert.False(CliEnvironment.MatchesModelPattern(sonnet, "gpt"));
	}

	[Fact]
	public void ResolveModel_PrefersFlagsThenSettingsAndValidatesTheApi()
	{
		var registry = Registry();

		var withFlag = CliEnvironment.ResolveModel(
			registry,
			new CliArguments { Model = "gpt-5" },
			new CodingAgentSettings(),
			out var error);
		Assert.Null(error);
		Assert.Equal("gpt-5", withFlag!.Id);

		// Every api this port implements resolves.
		var gemini = CliEnvironment.ResolveModel(
			registry,
			new CliArguments { Model = "google/gemini-3-pro" },
			new CodingAgentSettings(),
			out _);
		Assert.Equal("gemini-3-pro", gemini!.Id);

		// A model whose api this port does not implement is rejected with a clear message.
		var unsupported = CliEnvironment.ResolveModel(
			registry,
			new CliArguments { Model = "mystery/mystery-1" },
			new CodingAgentSettings(),
			out var unsupportedError);
		Assert.Null(unsupported);
		Assert.Contains("totally-unknown-api", unsupportedError!);
	}

	[Fact]
	public void ResolveTools_AppliesAllowlistAndDenylist()
	{
		var cwd = Path.GetTempPath();

		var onlyRead = CliEnvironment.ResolveTools(new CliArguments { Tools = ["read"] }, cwd);
		Assert.Equal(["read"], onlyRead.Select(static t => t.Name));

		var noBash = CliEnvironment.ResolveTools(new CliArguments { ExcludeTools = ["bash"] }, cwd);
		Assert.DoesNotContain(noBash, static t => t.Name == "bash");
		Assert.Contains(noBash, static t => t.Name == "read");

		Assert.Empty(CliEnvironment.ResolveTools(new CliArguments { NoTools = true }, cwd));
		Assert.Empty(CliEnvironment.ResolveTools(new CliArguments { NoBuiltinTools = true }, cwd));

		var glob = CliEnvironment.ResolveTools(new CliArguments { Tools = ["re*", "ls"] }, cwd);
		Assert.Equal(["read", "ls"], glob.Select(static t => t.Name));
	}

	[Fact]
	public void ResolveTools_DefaultsToThePiToolSet()
	{
		// 与 pi 一致：默认 read/bash/edit/write，grep/find/ls 默认关闭；
		// 另外保留 PiDeck 依赖的原生 todo/ask_question。
		var tools = CliEnvironment.ResolveTools(new CliArguments(), Path.GetTempPath());
		Assert.Equal(
			["read", "bash", "edit", "write", "todo", "ask_question"],
			tools.Select(static tool => tool.Name));
	}

	[Fact]
	public void ResolveTools_PlusEntriesAdjustTheDefaultSet()
	{
		var tools = CliEnvironment.ResolveTools(
			new CliArguments { Tools = ["+grep", "-todo"] },
			Path.GetTempPath());

		Assert.Contains(tools, static tool => tool.Name == "grep");
		Assert.Contains(tools, static tool => tool.Name == "read");
		Assert.DoesNotContain(tools, static tool => tool.Name == "todo");
	}

	[Fact]
	public void ResolveTools_ExposesCodemodeOnlyWhenSelected()
	{
		// 与 pi 的 inactive 注册一致：默认不启用，但可以从完整目录里选中。
		var defaults = CliEnvironment.ResolveTools(new CliArguments(), Path.GetTempPath());
		Assert.DoesNotContain(defaults, static tool => tool.Name == "codemode");

		var selected = CliEnvironment.ResolveTools(new CliArguments { Tools = ["+codemode"] }, Path.GetTempPath());
		Assert.Contains(selected, static tool => tool.Name == "codemode");
	}

	[Fact]
	public void ResolveTools_HonorsSettingsDefaultTools()
	{
		var settings = new CodingAgentSettings { DefaultTools = ["read", "grep"], DefaultToolsConfigured = true };
		var tools = CliEnvironment.ResolveTools(new CliArguments(), Path.GetTempPath(), settings);
		Assert.Equal(["read", "grep"], tools.Select(static tool => tool.Name));
	}

	[Fact]
	public void ResolveTools_EmptySettingsDefaultToolsDisablesEveryTool()
	{
		// 显式空数组是 pi 的「禁用全部」；不能因为「空列表等于没配置」退回默认集。
		var settings = new CodingAgentSettings { DefaultTools = [], DefaultToolsConfigured = true };
		Assert.Empty(CliEnvironment.ResolveTools(new CliArguments(), Path.GetTempPath(), settings));
	}


	[Fact]
	public void ResolveModel_BuildsFallbackForAnUnknownModelWithProvider()
	{
		var registry = ModelRegistry.LoadDefault();
		var (arguments, _) = CliArguments.Parse(["--provider", "anthropic", "--model", "claude-brand-new"]);

		var model = CliEnvironment.ResolveModel(registry, arguments!, new CodingAgentSettings(), out var error);

		Assert.Null(error);
		Assert.NotNull(model);
		Assert.Equal("anthropic", model!.Provider);
		Assert.Equal("claude-brand-new", model.Id);
		Assert.False(string.IsNullOrEmpty(model.BaseUrl));
	}

	[Fact]
	public void ResolveModel_DoesNotEscapeTheRequestedProvider()
	{
		var registry = ModelRegistry.LoadDefault();
		var (arguments, _) = CliArguments.Parse(["--provider", "anthropic", "--model", "gpt-5.5"]);

		var model = CliEnvironment.ResolveModel(registry, arguments!, new CodingAgentSettings(), out _);

		Assert.NotNull(model);
		Assert.Equal("anthropic", model!.Provider);
	}

	[Fact]
	public void Parse_CollectsExplicitResourcePaths()
	{
		var (arguments, error) = CliArguments.Parse(
			["--skill", "a/SKILL.md", "--skill", "b", "--prompt-template", "p.md", "--theme", "t.json", "--no-themes"]);

		Assert.Null(error);
		Assert.Equal(["a/SKILL.md", "b"], arguments!.Skills);
		Assert.Equal(["p.md"], arguments.PromptTemplates);
		Assert.Equal(["t.json"], arguments.Themes);
		Assert.True(arguments.NoThemes);
	}

	[Fact]
	public void Parse_SplitsTheThinkingSuffixFromTheModel()
	{
		var (arguments, error) = CliArguments.Parse(["--model", "anthropic/claude:high"]);
		Assert.Null(error);
		Assert.Equal("anthropic/claude", arguments!.Model);
		Assert.Equal("high", arguments.ThinkingLevel);

		// 显式 --thinking 优先于模型后缀，与参数顺序无关。
		var (explicitThinking, explicitError) = CliArguments.Parse(["--thinking", "off", "--model", "anthropic/claude:high"]);
		Assert.Null(explicitError);
		Assert.Equal("off", explicitThinking!.ThinkingLevel);

		// 不是思考级别的冒号后缀保持原样（例如镜像 tag）。
		var (untouched, untouchedError) = CliArguments.Parse(["--model", "registry/model:latest"]);
		Assert.Null(untouchedError);
		Assert.Equal("registry/model:latest", untouched!.Model);
	}

	[Fact]
	public void Parse_RecordsTheAcceptedCompatibilityFlags()
	{
		var (arguments, error) = CliArguments.Parse(["-e", "./ext.js", "--tui-mode", "fullscreen", "-ne"]);
		Assert.Null(error);
		Assert.Equal(["-e", "--tui-mode", "-ne"], arguments!.AcceptedCompatFlags);
	}
}
