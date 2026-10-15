using System.Net;
using System.Text;
using PiWeft.Cli;
using Xunit;

namespace PiWeft.Cli.Tests;

public sealed class ProviderSwitchCredentialTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-provider-switch-" + Guid.NewGuid().ToString("N"));

	[Theory]
	[InlineData(null)]
	[InlineData("explicit-key")]
	public async Task SwitchingProviderUsesItsOwnKeyAndKeepsExplicitOverrides(string? explicitKey)
	{
		Directory.CreateDirectory(Path.Combine(_root, ".pi"));
		File.WriteAllText(Path.Combine(_root, ".pi", "models.json"), """
			{"providers":{
			  "fixture-switch-a":{"api":"anthropic-messages","baseUrl":"https://a.example.test","apiKey":"key-a","models":[{"id":"model-a"}]},
			  "fixture-switch-b":{"api":"anthropic-messages","baseUrl":"https://b.example.test","apiKey":"key-b","models":[{"id":"model-b"}]}
			}}
			""");
		var args = new List<string> { "--model", "fixture-switch-a/model-a", "--no-tools", "--no-skills", "--no-prompt-templates", "--no-session" };
		if (explicitKey is not null) args.AddRange(["--api-key", explicitKey]);
		var arguments = CliArguments.Parse([.. args]).Arguments!;
		var handler = new CredentialHandler();
		var (environment, error) = CliEnvironment.Create(arguments, _root, handler);
		Assert.Null(error);
		Assert.NotNull(environment);
		using var session = environment.CreateSession(arguments, _root);
		await session.PromptAsync("first");
		session.SetModel(environment.Registry.Resolve("fixture-switch-b", "model-b")!);
		await session.PromptAsync("second");
		Assert.Equal(new[] { explicitKey ?? "key-a", explicitKey ?? "key-b" }, handler.Keys);
	}

	private sealed class CredentialHandler : HttpMessageHandler
	{
		public List<string> Keys { get; } = [];
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Keys.Add(request.Headers.GetValues("x-api-key").Single());
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(FakeAnthropicHandler.StreamBody, Encoding.UTF8, "text/event-stream"),
			});
		}
	}

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}
}
