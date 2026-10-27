using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Agent;
using PiWeft.Ai.Providers;
using PiWeft.Ai.Providers.Anthropic;
using PiWeft.Ai.Providers.Bedrock;
using PiWeft.Ai.Providers.Google;
using PiWeft.Ai.Providers.Mistral;
using PiWeft.Ai.Providers.PiMessages;
using PiWeft.Ai.Providers.OpenAi;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;
using PiWeft.JavaScript;
using PiWeft.Mcp;
using PiWeft.Cli;
using ZLogger;

// NativeAOT smoke test: exercises the catalog, the JSON message model and one full provider stream
// without a network round trip, so a trimmed publish either works or fails loudly here.

var registry = ModelRegistry.LoadDefault();
Console.WriteLine($"catalog: {registry.Count} models across {registry.Providers.Count} providers");

var model = registry.Resolve("anthropic", "claude-sonnet-4-5")
	?? throw new InvalidOperationException("claude-sonnet-4-5 is missing from the catalog");
Console.WriteLine($"model: {model.Id} ctx={model.ContextWindow} max={model.MaxTokens}");

var roundTrip = PiWeft.Ai.Json.PiJson.DeserializeMessage(PiWeft.Ai.Json.PiJson.SerializeMessage(new AssistantMessage
{
	Api = model.Api,
	Provider = model.Provider,
	Model = model.Id,
	Timestamp = 1,
	StopReason = StopReason.Stop,
	Content = [new TextContent { Text = "hello aot" }],
}));
Console.WriteLine($"json round trip: {((AssistantMessage)roundTrip).Content.Count} block(s)");

const string readableCommand = "echo \"\u4f60\u597d <tag> & 'quote'\"";
var readableArguments = new JsonObject { ["command"] = readableCommand };
var readableJson = readableArguments.ToJsonString(PiWeft.Ai.Json.PiJson.NodeOptions);
if (!readableJson.Contains("\u4f60\u597d", StringComparison.Ordinal) ||
	readableJson.Contains("\\u", StringComparison.Ordinal) ||
	JsonNode.Parse(readableJson)!["command"]!.GetValue<string>() != readableCommand)
{
	throw new InvalidOperationException("JSON node encoding failed under NativeAOT");
}

Console.WriteLine("json node encoding: readable UTF-8 and quotes");

var scriptResult = await ScriptFunction.InvokeAsync(
	"return { value: request.a + 1, nested: { ok: true } };",
	new JsonObject { ["a"] = 41 },
	null);
if (scriptResult?["value"]?.GetValue<int>() != 42 || scriptResult["nested"]?["ok"]?.GetValue<bool>() != true)
{
	throw new InvalidOperationException("ScriptFunction failed under NativeAOT");
}

Console.WriteLine("js script function: 42");

const string streamBody = """
	event: message_start
	data: {"type":"message_start","message":{"id":"msg_aot","model":"claude-sonnet-4-5","usage":{"input_tokens":5}}}

	event: content_block_start
	data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

	event: content_block_delta
	data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello from AOT"}}

	event: content_block_stop
	data: {"type":"content_block_stop","index":0}

	event: message_delta
	data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}

	event: message_stop
	data: {"type":"message_stop"}

	""";

var provider = new AnthropicProvider(new HttpClient(new StaticHandler(streamBody)));
var gateway = new ModelsGateway(registry);
gateway.Register(provider);
gateway.Register(new OpenAiCompletionsProvider());

var stream = gateway.Stream(
	model,
	new Context { SystemPrompt = "be terse", Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions { ApiKey = "smoke" });

var deltas = 0;
await foreach (var @event in stream)
{
	if (@event is TextDeltaEvent)
	{
		deltas++;
	}
}

var message = await stream.Result;
var text = message.Content.OfType<TextContent>().First().Text;
Console.WriteLine($"stream: '{text}' deltas={deltas} stop={message.StopReason} cost={message.Usage.Cost.Total:F6}");

if (text != "Hello from AOT" || message.StopReason != StopReason.Stop)
{
	return 1;
}

// OpenAI-compatible path, sharing the same gateway.
const string openAiBody = """
	data: {"id":"c1","model":"deepseek-chat","choices":[{"index":0,"delta":{"content":"Hi "}}]}

	data: {"id":"c1","choices":[{"index":0,"delta":{"content":"there"},"finish_reason":"stop"}]}

	data: {"id":"c1","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2}}

	data: [DONE]

	""";

var openAiModel = registry.List().FirstOrDefault(m => m.Api == OpenAiCompletionsProvider.ApiId && m.Type == ModelType.Chat)
	?? throw new InvalidOperationException("no openai-completions model in the catalog");
var openAiProvider = new OpenAiCompletionsProvider(new HttpClient(new StaticHandler(openAiBody)));
gateway.Register(openAiProvider);

var openAiStream = gateway.Stream(
	openAiModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions { ApiKey = "smoke" });

await foreach (var _ in openAiStream)
{
}

var openAiMessage = await openAiStream.Result;
Console.WriteLine(
	$"openai: '{openAiMessage.Content.OfType<TextContent>().First().Text}' stop={openAiMessage.StopReason} tokens={openAiMessage.Usage.TotalTokens}");

if (openAiMessage.Usage.TotalTokens != 5)
{
	return 1;
}

// Agent loop: one tool call, then a final answer, all in-process.
var turn = 0;
var agent = new Agent(new AgentOptions
{
	Model = model,
	StreamFn = (m, _, _) => AgentStream(m, turn++),
	ToolExecution = ToolExecutionMode.Sequential,
	Tools =
	[
		new AgentTool
		{
			Name = "echo",
			Label = "echo",
			Description = "echo a value",
			Execute = invocation => Task.FromResult(
				AgentToolResult.Text("echoed:" + invocation.Arguments["value"])),
		},
	],
});

var agentEvents = 0;
agent.Subscribe(_ =>
{
	agentEvents++;
	return ValueTask.CompletedTask;
});

var agentMessages = await agent.PromptAsync("go");
var toolResult = agentMessages.OfType<ToolResultMessage>().Single();
Console.WriteLine(
	$"agent: messages={agentMessages.Count} events={agentEvents} toolResult='{((TextContent)toolResult.Content[0]).Text}'");

if (((TextContent)toolResult.Content[0]).Text != "echoed:hi" || turn != 2)
{
	return 1;
}

// Session format: create, append, reload, rebuild context.
var sessionDir = Path.Combine(Path.GetTempPath(), "piweft-aot-smoke-" + Guid.NewGuid().ToString("N")[..8]);
var sessionMessages = 0;
try
{
	var session = SessionManager.Create("/tmp/demo", sessionDir, id: "smoke-session");
	session.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello"), Timestamp = 1 });
	session.AppendMessage(new AssistantMessage
	{
		Api = model.Api,
		Provider = model.Provider,
		Model = model.Id,
		Content = [new TextContent { Text = "hi back" }],
		StopReason = StopReason.Stop,
		Usage = new Usage { Input = 3, Output = 2, TotalTokens = 5 },
		Timestamp = 2,
	});
	session.AppendModelChange(model.Provider, model.Id);

	var reopened = SessionManager.Open(session.SessionFile!);
	sessionMessages = reopened.BuildSessionContext().Messages.Count;
	Console.WriteLine($"session: entries={reopened.Entries.Count} contextMessages={sessionMessages}");
}
finally
{
	if (Directory.Exists(sessionDir))
	{
		Directory.Delete(sessionDir, recursive: true);
	}
}

if (sessionMessages != 2)
{
	return 1;
}

// Real pi sessions, when this machine has them: every line must round-trip byte-identically.
var realRoot = Path.Combine(SessionPaths.GetAgentDir(), "sessions");
if (Directory.Exists(realRoot))
{
	var files = Directory.EnumerateFiles(realRoot, "*" + SessionFormat.Extension, SearchOption.AllDirectories).ToList();
	var lines = 0;
	var mismatches = 0;
	foreach (var file in files)
	{
		foreach (var line in File.ReadLines(file))
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			lines++;
			var parsed = SessionJson.ParseLine(line);
			var reserialized = parsed switch
			{
				SessionHeader header => SessionJson.Serialize(header),
				SessionEntry entry => SessionJson.Serialize(entry),
				_ => string.Empty,
			};

			if (reserialized.Length == 0 || !JsonNode.DeepEquals(JsonNode.Parse(line), JsonNode.Parse(reserialized)))
			{
				mismatches++;
			}
		}
	}

	Console.WriteLine($"real sessions: files={files.Count} lines={lines} mismatches={mismatches}");
	if (mismatches != 0)
	{
		return 1;
	}
}

// OpenAI Responses API path.
const string responsesBody = """
	event: response.created
	data: {"type":"response.created","response":{"id":"resp_1","model":"gpt-5","status":"in_progress"}}

	event: response.output_item.added
	data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m","role":"assistant","content":[]}}

	event: response.output_text.delta
	data: {"type":"response.output_text.delta","output_index":0,"delta":"Hi from responses"}

	event: response.completed
	data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":4,"output_tokens":2,"total_tokens":6}}}

	""";

var responsesModel = registry.List().First(m => m.Api == OpenAiResponsesProvider.ApiId && m.Type == ModelType.Chat);
gateway.Register(new OpenAiResponsesProvider(new HttpClient(new StaticHandler(responsesBody))));

var responsesStream = gateway.Stream(
	responsesModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions { ApiKey = "smoke" });

await foreach (var _ in responsesStream)
{
}

var responsesMessage = await responsesStream.Result;
Console.WriteLine(
	$"responses: '{responsesMessage.Content.OfType<TextContent>().First().Text}' stop={responsesMessage.StopReason} tokens={responsesMessage.Usage.TotalTokens}");

if (responsesMessage.Usage.TotalTokens != 6 || responsesMessage.StopReason != StopReason.Stop)
{
	return 1;
}

// Google Gemini path.
const string geminiBody = """
	data: {"responseId":"resp_g1","modelVersion":"gemini-2.5-flash","candidates":[{"content":{"role":"model","parts":[{"text":"Hi from gemini"}]}}]}

	data: {"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":6,"candidatesTokenCount":3}}

	""";

var geminiModel = registry.List().First(m => m.Api == GoogleGenerativeAiProvider.ApiId && m.Type == ModelType.Chat);
gateway.Register(new GoogleGenerativeAiProvider(new HttpClient(new StaticHandler(geminiBody))));

var geminiStream = gateway.Stream(
	geminiModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions { ApiKey = "smoke" });

await foreach (var _ in geminiStream)
{
}

var geminiMessage = await geminiStream.Result;
Console.WriteLine(
	$"gemini: '{geminiMessage.Content.OfType<TextContent>().First().Text}' stop={geminiMessage.StopReason} tokens={geminiMessage.Usage.TotalTokens}");

if (geminiMessage.Usage.TotalTokens != 9 || geminiMessage.StopReason != StopReason.Stop)
{
	return 1;
}

// Azure OpenAI Responses path (endpoint and api-key differ from OpenAI).
const string azureBody = """
	event: response.created
	data: {"type":"response.created","response":{"id":"resp_az","status":"in_progress"}}

	event: response.output_item.added
	data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m","role":"assistant","content":[]}}

	event: response.output_text.delta
	data: {"type":"response.output_text.delta","output_index":0,"delta":"Hi from azure"}

	event: response.completed
	data: {"type":"response.completed","response":{"id":"resp_az","status":"completed","usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}

	""";

Environment.SetEnvironmentVariable("AZURE_OPENAI_BASE_URL", "https://smoke.openai.azure.com");
var azureModel = registry.List().First(m => m.Api == AzureOpenAiResponsesProvider.ApiId && m.Type == ModelType.Chat);
gateway.Register(new AzureOpenAiResponsesProvider(new HttpClient(new StaticHandler(azureBody))));

var azureStream = gateway.Stream(
	azureModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions { ApiKey = "smoke" });

await foreach (var _ in azureStream)
{
}

var azureMessage = await azureStream.Result;
Console.WriteLine(
	$"azure: '{azureMessage.Content.OfType<TextContent>().First().Text}' stop={azureMessage.StopReason} tokens={azureMessage.Usage.TotalTokens}");

if (azureMessage.Usage.TotalTokens != 5 || azureMessage.StopReason != StopReason.Stop)
{
	return 1;
}

// Vertex AI path (project, location and ADC bearer token).
const string vertexBody = """
	data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Hi from vertex"}]}}]}

	data: {"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":4,"candidatesTokenCount":2}}

	""";

Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", "smoke-project");
Environment.SetEnvironmentVariable("GOOGLE_CLOUD_LOCATION", "us-central1");
Environment.SetEnvironmentVariable("GOOGLE_ACCESS_TOKEN", "smoke-token");
var vertexModel = registry.List().First(m => m.Api == GoogleVertexProvider.ApiId && m.Type == ModelType.Chat);
gateway.Register(new GoogleVertexProvider(new HttpClient(new StaticHandler(vertexBody))));

var vertexStream = gateway.Stream(
	vertexModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions());

await foreach (var _ in vertexStream)
{
}

var vertexMessage = await vertexStream.Result;
Console.WriteLine(
	$"vertex: '{vertexMessage.Content.OfType<TextContent>().First().Text}' stop={vertexMessage.StopReason} tokens={vertexMessage.Usage.TotalTokens}");

if (vertexMessage.Usage.TotalTokens != 6 || vertexMessage.StopReason != StopReason.Stop)
{
	return 1;
}

// AWS Bedrock Converse path (SigV4 signing plus binary event-stream framing).
Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIA_SMOKE");
Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "smoke-secret");

static byte[] BedrockFrame(string eventType, string payload) => AwsEventStream.Encode(
	new Dictionary<string, string> { [":event-type"] = eventType, [":message-type"] = "event" },
	Encoding.UTF8.GetBytes(payload));

var bedrockModel = registry.List().First(m => m.Api == BedrockConverseProvider.ApiId && m.Type == ModelType.Chat);
var bedrockBytes = new List<byte>();
bedrockBytes.AddRange(BedrockFrame("messageStart", """{"role":"assistant"}"""));
bedrockBytes.AddRange(BedrockFrame("contentBlockStart", """{"contentBlockIndex":0,"start":{}}"""));
bedrockBytes.AddRange(BedrockFrame("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"Hi from bedrock"}}"""));
bedrockBytes.AddRange(BedrockFrame("contentBlockStop", """{"contentBlockIndex":0}"""));
bedrockBytes.AddRange(BedrockFrame("messageStop", """{"stopReason":"end_turn"}"""));
bedrockBytes.AddRange(BedrockFrame("metadata", """{"usage":{"inputTokens":6,"outputTokens":3}}"""));

gateway.Register(new BedrockConverseProvider(new HttpClient(new BytesHandler([.. bedrockBytes]))));

var bedrockStream = gateway.Stream(
	bedrockModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions());

await foreach (var _ in bedrockStream)
{
}

var bedrockMessage = await bedrockStream.Result;
Console.WriteLine(
	$"bedrock: '{bedrockMessage.Content.OfType<TextContent>().First().Text}' stop={bedrockMessage.StopReason} tokens={bedrockMessage.Usage.TotalTokens}");

if (bedrockMessage.Usage.TotalTokens != 9 || bedrockMessage.StopReason != StopReason.Stop)
{
	return 1;
}

// pi-messages protocol (serialized assistant events over SSE).
const string piMessagesBody = """
	data: {"type":"start"}

	data: {"type":"text_start","contentIndex":0}

	data: {"type":"text_delta","contentIndex":0,"delta":"Hi from pi-messages"}

	data: {"type":"text_end","contentIndex":0,"content":"Hi from pi-messages"}

	data: {"type":"done","reason":"stop","usage":{"input":4,"output":3,"cacheRead":0,"cacheWrite":0,"totalTokens":7}}

	""";

var piMessagesModel = registry.List().First(m => m.Api == PiMessagesProvider.ApiId && m.Type == ModelType.Chat);
gateway.Register(new PiMessagesProvider(new HttpClient(new StaticHandler(piMessagesBody))));

var piMessagesStream = gateway.Stream(
	piMessagesModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions { ApiKey = "smoke" });

await foreach (var _ in piMessagesStream)
{
}

var piMessagesResult = await piMessagesStream.Result;
Console.WriteLine(
	$"pi-messages: '{piMessagesResult.Content.OfType<TextContent>().First().Text}' stop={piMessagesResult.StopReason} tokens={piMessagesResult.Usage.TotalTokens}");

if (piMessagesResult.Usage.TotalTokens != 7 || piMessagesResult.StopReason != StopReason.Stop)
{
	return 1;
}

// Mistral chat completions (content delivered as parts).
const string mistralBody = """
	data: {"id":"m1","choices":[{"index":0,"delta":{"content":[{"type":"text","text":"Hi from mistral"}]},"finish_reason":null}]}

	data: {"id":"m1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

	data: [DONE]

	""";

var mistralModel = registry.List().First(m => m.Api == MistralConversationsProvider.ApiId && m.Type == ModelType.Chat);
gateway.Register(new MistralConversationsProvider(new HttpClient(new StaticHandler(mistralBody))));

var mistralStream = gateway.Stream(
	mistralModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions { ApiKey = "smoke" });

await foreach (var _ in mistralStream)
{
}

var mistralResult = await mistralStream.Result;
Console.WriteLine(
	$"mistral: '{mistralResult.Content.OfType<TextContent>().First().Text}' stop={mistralResult.StopReason}");

if (mistralResult.StopReason != StopReason.Stop)
{
	return 1;
}

// ChatGPT Codex backend (Responses shape with store:false and a ChatGPT OAuth bearer token).
const string codexBody = """
	event: response.created
	data: {"type":"response.created","response":{"id":"resp_codex","status":"in_progress"}}

	event: response.output_item.added
	data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m","role":"assistant","content":[]}}

	event: response.output_text.delta
	data: {"type":"response.output_text.delta","output_index":0,"delta":"Hi from codex"}

	event: response.completed
	data: {"type":"response.completed","response":{"id":"resp_codex","status":"completed","usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8}}}

	""";

static string CodexToken()
{
	static string Encode(string value) =>
		Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	return $"{Encode("{\"alg\":\"none\"}")}.{Encode("{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"acct_smoke\"}}")}.sig";
}

var codexModel = registry.List().First(m => m.Api == OpenAiCodexResponsesProvider.ApiId && m.Type == ModelType.Chat);
gateway.Register(new OpenAiCodexResponsesProvider(new HttpClient(new StaticHandler(codexBody))));

var codexStream = gateway.Stream(
	codexModel,
	new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
	new StreamOptions { ApiKey = CodexToken() });

await foreach (var _ in codexStream)
{
}

var codexMessage = await codexStream.Result;
Console.WriteLine(
	$"codex: '{codexMessage.Content.OfType<TextContent>().First().Text}' stop={codexMessage.StopReason} tokens={codexMessage.Usage.TotalTokens}");

if (codexMessage.Usage.TotalTokens != 8 || codexMessage.StopReason != StopReason.Stop)
{
	return 1;
}

// MCP resource tools: listing, reading and the model-facing text, all without a server process.
var resourceTools = McpResourceTools.Create(static () => [new SmokeResources()]);

var listOutcome = await resourceTools.Single(static tool => tool.Name == McpResourceTools.ListResourcesTool)
	.Execute(new AgentToolInvocation
	{
		ToolCallId = "r1",
		ToolName = McpResourceTools.ListResourcesTool,
		Arguments = new JsonObject(),
	});

var readOutcome = await resourceTools.Single(static tool => tool.Name == McpResourceTools.ReadResourceTool)
	.Execute(new AgentToolInvocation
	{
		ToolCallId = "r2",
		ToolName = McpResourceTools.ReadResourceTool,
		Arguments = new JsonObject { ["server"] = "smoke", ["uri"] = "smoke://notes" },
	});

var listed = listOutcome.StructuredContent!["resources"]!.AsArray();
var readText = string.Join("|", readOutcome.Content.OfType<TextContent>().Select(static block => block.Text));
Console.WriteLine($"mcp resources: listed={listed.Count} read={readText}");

// One resource: the ui:// MCP App entry is filtered out, and the JSON blob came back as text.
if (listed.Count != 1 || !readText.Contains("smoke://a:") || !readText.Contains("alpha") || !readText.Contains("{\"ok\":true}"))
{
	return 1;
}

// Full coding session: sectioned prompt + built-in tools + persisted session file.
var workspace = Path.Combine(Path.GetTempPath(), "pi-aot-tools-" + Guid.NewGuid().ToString("N")[..8]);
var readBack = string.Empty;
var persistedEntries = 0;
var projectedMessages = 0;
try
{
	Directory.CreateDirectory(workspace);
	File.WriteAllText(Path.Combine(workspace, "sample.txt"), "hello from the tool test\n", new UTF8Encoding(false));

	var codingTurn = 0;
	using var codingSession = new CodingAgentSession(new CodingAgentSessionOptions
	{
		Cwd = workspace,
		Model = model,
		StreamFn = (m, _, _) => CodingStream(m, codingTurn++),
		Session = SessionManager.Create(workspace, Path.Combine(workspace, "sessions")),
	});

	var codingMessages = await codingSession.PromptAsync("read sample.txt");
	readBack = string.Concat(codingMessages
		.OfType<ToolResultMessage>()
		.Single()
		.Content
		.OfType<TextContent>()
		.Select(static block => block.Text));

	var (tokens, cost) = codingSession.UsageTotals();

	// Reopen the file the session wrote and rebuild the context from it.
	var reopened = SessionManager.Open(codingSession.Session.SessionFile!);
	persistedEntries = reopened.Entries.Count;
	projectedMessages = reopened.BuildProjectedMessages().Count;

	Console.WriteLine(
		$"coding session: entries={persistedEntries} projected={projectedMessages} tokens={tokens} cost={cost:F6} read='{readBack.Trim()}'");

	// Compaction: fold the older entries into a checkpoint entry.
	using var compactor = new CodingAgentSession(new CodingAgentSessionOptions
	{
		Cwd = workspace,
		Model = model,
		StreamFn = (m, _, _) => TextStream(m, "## Goal\nsmoke compaction summary"),
		Session = reopened,
		Settings = new CodingAgentSettings
		{
			Compaction = new CompactionSettings { ReserveTokens = 4096, KeepRecentTokens = 1 },
		},
	});

	var compactionResult = await compactor.CompactAsync();
	var compactionEntries = reopened.Entries.Count(static entry => entry is CompactionEntry);
	Console.WriteLine(
		$"compaction: applied={compactionResult is not null} entries={compactionEntries} projected={reopened.BuildProjectedMessages().Count}");

	if (compactionResult is null || compactionEntries != 1)
	{
		return 1;
	}

	// Extension host under NativeAOT: Jint interop is exactly where trimming breaks, and
	// every bridge here is a ClrFunction on purpose — this stage is what proves that.
	var extensionDirectory = Path.Combine(workspace, "agent", "extensions");
	Directory.CreateDirectory(extensionDirectory);
	File.WriteAllText(Path.Combine(extensionDirectory, "smoke.js"), """
		module.exports = function (pi) {
			pi.registerCommand("smoked", { description: "smoke", handler: function (arg) { globalThis.smokeSeen = arg; } });
			pi.on("tool_call", function (payload) { if (payload.toolName === "bash") { payload.block = true; payload.reason = "smoke blocked"; } });
			pi.ui.setStatus("smoke", "running");
		};
		""");
	var previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
	Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", Path.Combine(workspace, "agent"));
	using var extensionHost = new PiWeft.CodingAgent.Extensions.ExtensionHost();
	extensionHost.Load(workspace, trusted: true);
	var extensionLoaded = extensionHost.States.Count == 1 && extensionHost.States[0].Loaded;
	extensionHost.RunCommand("smoked", "argument");
	// Restore the smoke's own agent dir before anything else touches it.
	Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", previousAgentDir);
	Console.WriteLine($"extension host: loaded={extensionLoaded} unsupported={extensionHost.UnsupportedEvents.Count}");
	if (!extensionLoaded)
	{
		Console.WriteLine("  diagnostics: " + string.Join(" | ", extensionHost.States.SelectMany(static state => state.Diagnostics)));
		return 1;
	}

	// Schema validation: a call missing a required argument is rejected before the tool runs.
	var validationTurn = 0;
	using (var validationSession = new CodingAgentSession(new CodingAgentSessionOptions
	{
		Cwd = workspace,
		Model = model,
		StreamFn = (m, _, _) => ValidationStream(m, validationTurn++),
		Session = SessionManager.InMemory(workspace),
		Skills = [],
	}))
	{
		var validationMessages = await validationSession.PromptAsync("read without a path");
		var validationText = string.Concat(validationMessages
			.OfType<ToolResultMessage>()
			.Single()
			.Content
			.OfType<TextContent>()
			.Select(static block => block.Text));

		Console.WriteLine($"validation: {validationText.Split('\n')[0]}");
		if (!validationText.Contains("Validation failed for tool", StringComparison.Ordinal))
		{
			return 1;
		}
	}

	// Skills and prompt templates discovered from the workspace.
	var skillDirectory = Path.Combine(workspace, ".pi", "skills", "demo-skill");
	Directory.CreateDirectory(skillDirectory);
	File.WriteAllText(
		Path.Combine(skillDirectory, "SKILL.md"),
		"---\nname: demo-skill\ndescription: A demo skill.\n---\n\nDo the demo thing.\n",
		new UTF8Encoding(false));

	var promptDirectory = Path.Combine(workspace, ".pi", "prompts");
	Directory.CreateDirectory(promptDirectory);
	File.WriteAllText(Path.Combine(promptDirectory, "review.md"), "Review $ARGUMENTS carefully.", new UTF8Encoding(false));

	using var featureSession = new CodingAgentSession(new CodingAgentSessionOptions
	{
		Cwd = workspace,
		Model = model,
		StreamFn = (m, _, _) => TextStream(m, "ok"),
		Session = SessionManager.InMemory(workspace),
	});

	var expandedTemplate = featureSession.ExpandPromptTemplate("/review the diff");
	var skillNames = string.Join(",", featureSession.DiscoveredSkills.Select(static skill => skill.Name));
	var featureSystem = featureSession.Messages.OfType<SystemMessage>().Single();
	var hasSkillsSection = featureSystem.Sections?.ContainsKey("skills") == true;
	Console.WriteLine($"skills: [{skillNames}] section={hasSkillsSection} template='{expandedTemplate}'");

	if (expandedTemplate != "Review the diff carefully." || !hasSkillsSection || !skillNames.Contains("demo-skill", StringComparison.Ordinal))
	{
		return 1;
	}

	// Branch summarization when navigating to an earlier entry.
	var branchSession = SessionManager.InMemory(workspace);
	branchSession.AppendMessage(featureSystem);
	var branchRoot = branchSession.AppendMessage(new UserMessage { Content = MessageContent.FromText("root"), Timestamp = 1 });
	var branchAnswer = branchSession.AppendMessage(new AssistantMessage
	{
		Api = model.Api,
		Provider = model.Provider,
		Model = model.Id,
		Content = [new TextContent { Text = "root answer" }],
		StopReason = StopReason.Stop,
		Timestamp = 2,
	});
	branchSession.AppendMessage(new UserMessage { Content = MessageContent.FromText("left turn"), Timestamp = 3 });

	using var branchCoding = new CodingAgentSession(new CodingAgentSessionOptions
	{
		Cwd = workspace,
		Model = model,
		StreamFn = (m, _, _) => TextStream(m, "## Goal\nbranch summary"),
		Session = branchSession,
		Skills = [],
	});

	var branchResult = await branchCoding.NavigateWithSummaryAsync(branchAnswer);
	var branchEntries = branchSession.Entries.Count(static entry => entry is BranchSummaryEntry);
	Console.WriteLine($"branch summary: applied={branchResult is not null} entries={branchEntries} leaf={branchSession.LeafId}");

	if (branchResult is null || branchEntries != 1)
	{
		return 1;
	}

	_ = branchRoot;
}
finally
{
	if (Directory.Exists(workspace))
	{
		Directory.Delete(workspace, recursive: true);
	}
}

if (!readBack.Contains("hello from the tool test", StringComparison.Ordinal) || persistedEntries != 4 || projectedMessages != 4)
{
	return 1;
}

// codemode: the Jint sandbox (and the async tool bridge) must survive trimming.
var codeModeSandbox = new CodeModeSandbox(new CodeModeSandboxOptions
{
	Tools =
	[
		new PiWeft.JavaScript.CodeModeTool
		{
			Name = "echo",
			Description = "echo a value",
			InputSchema = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
			},
			Execute = (arguments, _) => Task.FromResult<JsonNode?>(
				JsonValue.Create(arguments!["value"]!.GetValue<string>())),
		},
	],
	Models = request => new JsonArray(
		(JsonNode)new JsonObject { ["id"] = "smoke", ["type"] = request.Type }),
	Timeout = TimeSpan.FromSeconds(30),
});
var codeModeBasic = await codeModeSandbox.ExecuteAsync("""text("a"); return 1;""");
Console.WriteLine($"codemode basic: ok={codeModeBasic.Ok} value={codeModeBasic.Value} error={codeModeBasic.Error?.Message}");

var codeModeSecond = await codeModeSandbox.ExecuteAsync("""return 1;""");
Console.WriteLine($"codemode second: ok={codeModeSecond.Ok} value={codeModeSecond.Value} error={codeModeSecond.Error?.Message}");

var codeModeEval = await codeModeSandbox.ExecuteAsync("""return eval("1 + 1");""");
Console.WriteLine($"codemode eval: ok={codeModeEval.Ok} value={codeModeEval.Value} error={codeModeEval.Error?.Message}");

var codeModeSearch = await codeModeSandbox.ExecuteAsync("""return searchTools("echo")[0].name;""");
Console.WriteLine($"codemode search: ok={codeModeSearch.Ok} value={codeModeSearch.Value} error={codeModeSearch.Error?.Message}");

var codeModeModels = await codeModeSandbox.ExecuteAsync("""return models.getModelsOfType("chat")[0].id;""");
Console.WriteLine($"codemode models: ok={codeModeModels.Ok} value={codeModeModels.Value} error={codeModeModels.Error?.Message}");

var codeMode = await codeModeSandbox.ExecuteAsync("""
	const value = await tools.echo({ value: "hi" });
	const compiled = eval("1 + 1");
	const found = searchTools("echo")[0].name;
	const model = models.getModelsOfType("chat")[0].id;
	text("codemode:" + value);
	return compiled + 40 + (found === "echo" ? 0 : 100) + (model === "smoke" ? 0 : 100);
	""");
var codeModeText = string.Join("|", codeMode.Output.OfType<CodeModeTextItem>().Select(item => item.Text));
Console.WriteLine($"codemode: ok={codeMode.Ok} value={codeMode.Value} output={codeModeText}");
if (!codeMode.Ok || codeMode.Value?.GetValue<int>() != 42 || codeModeText != "codemode:hi")
{
	Console.WriteLine($"codemode failed: {codeMode.Error?.Kind} {codeMode.Error?.Message}");
	return 1;
}

await VerifyHostLogsAsync();
Console.WriteLine("AOT SMOKE OK");
return 0;

static async Task VerifyHostLogsAsync()
{
	var directory = Path.Combine(Path.GetTempPath(), "piweft-aot-log-" + Guid.NewGuid().ToString("N"));
	var previous = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
	var previousLevel = Environment.GetEnvironmentVariable("PI_LOG_LEVEL");
	try
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", directory);
		Environment.SetEnvironmentVariable("PI_LOG_LEVEL", "Information");
		using (AppLog.Start("aot-smoke"))
		{
			var args = CliArguments.Parse(["--mode", "rpc", "--model", "anthropic/claude-sonnet-4-5", "--no-tools", "--no-session", "--no-mcp", "--no-skills", "--no-context-files", "--no-prompt-templates"]).Arguments!;
			var (environment, error) = CliEnvironment.Create(args, directory);
			var runner = new CliRunner(environment ?? throw new InvalidOperationException(error), new StringWriter(), new StringWriter());
			if (await runner.RunAsync(args, directory, new StringReader("{\"type\":\"exit\"}"), CancellationToken.None) != 0)
				throw new InvalidOperationException("CLI logging smoke failed");
			AppLog.For<CliRunner>().ZLogInformation($"Logging completed: reason={StopReason.Error.ToString()}");
		}
		var text = string.Concat(Directory.GetFiles(Path.Combine(directory, "logs"), "*.log").Select(File.ReadAllText));
		if (!text.Contains("CLI run: mode=Rpc", StringComparison.Ordinal) || !text.Contains("reason=Error", StringComparison.Ordinal))
			throw new InvalidOperationException("NativeAOT host logs were not flushed");
		Console.WriteLine("host logging: CLI mode and stop reason flushed");
	}
	finally
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", previous);
		Environment.SetEnvironmentVariable("PI_LOG_LEVEL", previousLevel);
		if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
	}
}

static AssistantMessageEventStream TextStream(Model model, string text)
{
	var stream = new AssistantMessageEventStream();
	var blank = Blank(model);
	var message = blank with { Content = [new TextContent { Text = text }], StopReason = StopReason.Stop };
	stream.Push(new StartEvent { Partial = blank });
	stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
	stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = message });
	stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });
	stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
	return stream;
}

static AssistantMessageEventStream ValidationStream(Model model, int turn)
{
	if (turn == 0)
	{
		// Missing the required "path" argument.
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var call = new ToolCall { Id = "c1", Name = "read", Arguments = new JsonObject { ["limit"] = 5 } };
		var withCall = blank with { Content = [call], StopReason = StopReason.ToolUse };
		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new ToolCallStartEvent { ContentIndex = 0, Partial = withCall });
		stream.Push(new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = withCall });
		stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = withCall });
		return stream;
	}

	return TextStream(model, "retried");
}

static AssistantMessageEventStream CodingStream(Model model, int turn)
{
	var stream = new AssistantMessageEventStream();
	var blank = Blank(model);
	stream.Push(new StartEvent { Partial = blank });

	if (turn == 0)
	{
		var call = new ToolCall { Id = "c1", Name = "read", Arguments = new JsonObject { ["path"] = "sample.txt" } };
		var withCall = blank with { Content = [call], StopReason = StopReason.ToolUse };
		stream.Push(new ToolCallStartEvent { ContentIndex = 0, Partial = withCall });
		stream.Push(new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = withCall });
		stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = withCall });
		return stream;
	}

	var text = blank with { Content = [new TextContent { Text = "done" }], StopReason = StopReason.Stop };
	stream.Push(new TextStartEvent { ContentIndex = 0, Partial = text });
	stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = "done", Partial = text });
	stream.Push(new TextEndEvent { ContentIndex = 0, Content = "done", Partial = text });
	stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = text });
	return stream;
}

static AssistantMessage Blank(Model model) => new()
{
	Api = model.Api,
	Provider = model.Provider,
	Model = model.Id,
	Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
};

static AssistantMessageEventStream AgentStream(Model model, int turn)
{
	var stream = new AssistantMessageEventStream();
	var blank = Blank(model);
	stream.Push(new StartEvent { Partial = blank });

	if (turn == 0)
	{
		var call = new ToolCall { Id = "c1", Name = "echo", Arguments = new JsonObject { ["value"] = "hi" } };
		var withCall = blank with { Content = [call], StopReason = StopReason.ToolUse };
		stream.Push(new ToolCallStartEvent { ContentIndex = 0, Partial = withCall });
		stream.Push(new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = withCall });
		stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = withCall });
		return stream;
	}

	var text = blank with { Content = [new TextContent { Text = "final" }], StopReason = StopReason.Stop };
	stream.Push(new TextStartEvent { ContentIndex = 0, Partial = text });
	stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = "final", Partial = text });
	stream.Push(new TextEndEvent { ContentIndex = 0, Content = "final", Partial = text });
	stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = text });
	return stream;
}

/// <summary>A resource provider with no transport, so the resource tools can be exercised under AOT.</summary>
internal sealed class SmokeResources : PiWeft.Mcp.IMcpResourceProvider
{
	public string ConfiguredName => "smoke";

	public TimeSpan Timeout => TimeSpan.FromSeconds(5);

	public PiWeft.Mcp.McpServerCapabilities Capabilities { get; } = new() { HasResources = true };

	public Task<IReadOnlyList<PiWeft.Mcp.McpResource>> ListResourcesAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<PiWeft.Mcp.McpResource>>(
		[
			new PiWeft.Mcp.McpResource { Uri = "smoke://a", Name = "a", MimeType = "text/plain" },
			// An MCP App UI, which the tools must hide from the model.
			new PiWeft.Mcp.McpResource { Uri = "ui://app", Name = "app" },
		]);

	public Task<IReadOnlyList<PiWeft.Mcp.McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<PiWeft.Mcp.McpResourceTemplate>>(
			[new PiWeft.Mcp.McpResourceTemplate { UriTemplate = "smoke://a/{id}", Name = "a" }]);

	public Task<IReadOnlyList<PiWeft.Mcp.McpResourceContent>> ReadResourceAsync(string uri, CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<PiWeft.Mcp.McpResourceContent>>(
		[
			new PiWeft.Mcp.McpResourceContent { Uri = "smoke://a", MimeType = "text/plain", Text = "alpha" },
			new PiWeft.Mcp.McpResourceContent { Uri = "smoke://b", MimeType = "application/json", Blob = Convert.ToBase64String("{\"ok\":true}"u8) },
		]);

	public Task<IReadOnlyList<PiWeft.Mcp.McpPrompt>> ListPromptsAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<PiWeft.Mcp.McpPrompt>>([]);

	public Task<PiWeft.Mcp.McpPromptResult> GetPromptAsync(string name, JsonObject? arguments, CancellationToken cancellationToken) =>
		Task.FromResult(new PiWeft.Mcp.McpPromptResult());
}

/// <summary>Serves a fixed binary body, for the Bedrock event-stream framing.</summary>
internal sealed class BytesHandler(byte[] body) : HttpMessageHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
		Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(body),
		});
}

internal sealed class StaticHandler(string body) : HttpMessageHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// SSE dispatches an event on a blank line; make sure the last one is terminated.
		var terminated = body.EndsWith("\n\n", StringComparison.Ordinal) ? body : body.TrimEnd('\n') + "\n\n";

		return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(terminated, Encoding.UTF8, "text/event-stream"),
		});
	}
}
