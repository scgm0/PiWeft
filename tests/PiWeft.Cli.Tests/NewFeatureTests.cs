using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.Cli.Tests;

public sealed class NewFeatureTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-new-" + Guid.NewGuid().ToString("N")[..8]);

	public NewFeatureTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession CreateSession()
	{
		var session = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		session.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello") });
		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = new Model { Id = "m", Provider = "p", Api = "openai-completions" },
			StreamFn = static (model, messages, options) => throw new NotSupportedException(),
			Session = session,
			Tools = [],
			Settings = new CodingAgentSettings(),
			Persist = false,
			RecordInitialThinkingLevel = false,
		});
	}

	[Fact]
	public void ExportJsonlWritesHeaderAndEntries()
	{
		using var session = CreateSession();
		var path = Path.Combine(_root, "out.jsonl");

		CliRunner.ExportJsonl(session, path);

		var lines = File.ReadAllLines(path);
		Assert.Equal(2, lines.Length);
		Assert.Contains("\"type\":\"session\"", lines[0]);
		Assert.Contains("\"type\":\"message\"", lines[1]);
	}

	[Fact]
	public void BugReportCreatesZipWithReportAndTranscript()
	{
		using var session = CreateSession();
		var path = BugReport.Create(session, "something broke", agentDir: _root);

		Assert.True(File.Exists(path));
		using var archive = ZipFile.OpenRead(path);
		Assert.Contains(archive.Entries, static entry => entry.Name == "report.md");
		Assert.Contains(archive.Entries, static entry => entry.Name == "session.jsonl");

		var report = archive.Entries.Single(static entry => entry.Name == "report.md");
		using var reader = new StreamReader(report.Open());
		var text = reader.ReadToEnd();
		Assert.Contains("something broke", text);
		Assert.DoesNotContain("sk-", text);
	}

	[Fact]
	public void LlamaRouterBuildsAnOpenAiCompatibleModel()
	{
		var model = LlamaRouter.ToModel(new LlamaRouter.ModelInfo("qwen.gguf", "loaded", "Qwen", 32768, 8192, true));

		Assert.Equal("llama.cpp", model.Provider);
		Assert.Equal("openai-completions", model.Api);
		Assert.Equal(LlamaRouter.InferenceBaseUrl, model.BaseUrl);
		Assert.Equal(32768, model.ContextWindow);
		Assert.Equal(8192, model.MaxTokens);
		Assert.Contains("image", model.Input);
	}

	[Fact]
	public async Task LlamaRouterSearchesHuggingFace()
	{
		var handler = new FakeHandler("""[{"id":"owner/model-GGUF","downloads":1234}]""");
		var models = await LlamaRouter.SearchHuggingFaceAsync("qwen", handler);

		var model = Assert.Single(models);
		Assert.Equal("owner/model-GGUF", model.Id);
		Assert.Equal(1234, model.Downloads);
	}

	[Fact]
	public async Task LlamaRouterParsesModelStatus()
	{
		var handler = new FakeHandler("""{"data":[{"id":"qwen.gguf","status":{"value":"loaded"},"name":"Qwen"}]}""");
		var models = await LlamaRouter.ListAsync(handler);

		var model = Assert.Single(models);
		Assert.Equal("qwen.gguf", model.Id);
		Assert.Equal("loaded", model.Status);
	}

	private sealed class FakeHandler(string body) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			});
	}
}
