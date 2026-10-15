using PiWeft.Ai;
using PiWeft.Ai.Auth;
using Xunit;

namespace PiWeft.Cli.Tests;

[Collection("process-environment")]
public sealed class ModelAvailabilityTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-available-" + Guid.NewGuid().ToString("N"));
	private readonly Dictionary<string, string?> _previous = new();
	private static readonly string[] Variables = ["PI_CODING_AGENT_DIR", "FIXTURE_AVAILABLE_API_KEY", "AZURE_OPENAI_API_KEY",
		"GOOGLE_CLOUD_API_KEY", "GOOGLE_ACCESS_TOKEN", "GOOGLE_APPLICATION_CREDENTIALS", "AWS_ACCESS_KEY_ID", "AWS_ACCESS_KEY",
		"AWS_SECRET_ACCESS_KEY", "AWS_SECRET_KEY"];

	public ModelAvailabilityTests()
	{
		Directory.CreateDirectory(_root);
		foreach (var variable in Variables)
		{
			_previous[variable] = Environment.GetEnvironmentVariable(variable);
			Environment.SetEnvironmentVariable(variable, null);
		}
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _root);
	}

	private static Model Model(string id = "test", string provider = "fixture-available", string api = "openai-completions") =>
		new() { Id = id, Provider = provider, Api = api, Name = id };

	[Fact]
	public void MissingCredentialsHideModelsIncludingTheStartupFallback()
	{
		Assert.Empty(ModelAvailability.Filter([Model()], new Dictionary<string, string>()));
		Assert.Single(ModelAvailability.Filter([Model()], new Dictionary<string, string>(), "explicit-test-key"));
		Assert.Empty(ModelAvailability.Filter([Model(api: "unsupported")], new Dictionary<string, string>(), "explicit-test-key"));
	}

	[Fact]
	public void ConfiguredStoredAndEnvironmentCredentialsAreRecognizedPerProvider()
	{
		File.WriteAllText(AuthStore.Path(), """
			{"fixture-stored":{"type":"api_key","key":"${TOKEN}","env":{"TOKEN":"stored-test-key"}}}
			""");
		Environment.SetEnvironmentVariable("FIXTURE_AVAILABLE_API_KEY", "env-test-key");
		var models = new[] { Model(), Model(provider: "fixture-stored"), Model(provider: "fixture-configured"), Model(provider: "fixture-missing") };
		var keys = new Dictionary<string, string> { ["fixture-configured"] = "configured-test-key" };
		Assert.Equal(models[..3], ModelAvailability.Filter(models, keys));
	}

	[Theory]
	[InlineData("!never execute this")]
	[InlineData("${PI_TEST_UNSET_AVAILABILITY}")]
	[InlineData("")]
	public void UnresolvedOrCommandAuthIsHiddenAndCanFallBackToTheEnvironment(string key)
	{
		File.WriteAllText(AuthStore.Path(), $$$"""{"fixture-available":{"type":"api_key","key":"{{{key}}}"}}""");
		Assert.Empty(ModelAvailability.Filter([Model()], new Dictionary<string, string>()));
		Environment.SetEnvironmentVariable("FIXTURE_AVAILABLE_API_KEY", "env-test-key");
		Assert.Single(ModelAvailability.Filter([Model()], new Dictionary<string, string>()));
	}

	[Fact]
	public void CodexOauthIsUsableEvenWhenItNeedsARefreshButDoesNotEnableOtherApis()
	{
		CodexAuth.Save(new CodexCredentials { Access = "test-access", Refresh = "test-refresh", Expires = 1 });
		var codex = Model(provider: "openai-codex", api: "openai-codex-responses");
		Assert.Equal(new[] { codex }, ModelAvailability.Filter([codex, Model()], new Dictionary<string, string>()));
	}

	[Fact]
	public void BedrockNeedsBothSigningKeysAndDoesNotUseGenericApiKeys()
	{
		var model = Model(api: "bedrock-converse-stream");
		var keys = new Dictionary<string, string> { [model.Provider] = "irrelevant" };
		Assert.Empty(ModelAvailability.Filter([model], keys));
		Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "test-access");
		Assert.Empty(ModelAvailability.Filter([model], keys));
		Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "test-secret");
		Assert.Single(ModelAvailability.Filter([model], keys));
	}

	[Theory]
	[InlineData("azure-openai-responses", "AZURE_OPENAI_API_KEY")]
	[InlineData("google-vertex", "GOOGLE_ACCESS_TOKEN")]
	[InlineData("google-vertex", "GOOGLE_CLOUD_API_KEY")]
	public void SpecialProviderCredentialsUseTheirActualAuthenticationPaths(string api, string variable)
	{
		Environment.SetEnvironmentVariable(variable, "test-credential");
		Assert.Single(ModelAvailability.Filter([Model(api: api)], new Dictionary<string, string>()));
	}

	[Fact]
	public void EmptyConfiguredKeysCanUseStoredAuthAndMalformedOauthDoesNotHideOtherProviders()
	{
		File.WriteAllText(AuthStore.Path(), """
			{"fixture-available":{"type":"api_key","key":"test-key"},
			 "openai-codex":{"type":"oauth","access":123,"refresh":false}}
			""");
		Assert.Single(ModelAvailability.Filter([Model()], new Dictionary<string, string> { ["fixture-available"] = "" }));
	}

	[Fact]
	public void RequeryReflectsAuthChangesAndTheFullCatalogRemainsAvailable()
	{
		Directory.CreateDirectory(Path.Combine(_root, ".pi"));
		File.WriteAllText(Path.Combine(_root, ".pi", "models.json"), """
			{"providers":{"fixture-available":{"api":"openai-completions","baseUrl":"https://example.test","models":[{"id":"test"}]}}}
			""");
		var arguments = CliArguments.Parse(["--model", "fixture-available/test", "--no-tools"]).Arguments!;
		var (environment, error) = CliEnvironment.Create(arguments, _root);
		Assert.Null(error);
		Assert.Contains(environment!.ListModels(), model => model.Provider == "fixture-available");
		Assert.DoesNotContain(environment.ListAvailableModels(), model => model.Provider == "fixture-available");
		File.WriteAllText(AuthStore.Path(), """{"fixture-available":{"type":"api_key","key":"test-key"}}""");
		Assert.Contains(environment.ListAvailableModels(), model => model.Provider == "fixture-available");
		File.Delete(AuthStore.Path());
		Assert.DoesNotContain(environment.ListAvailableModels(), model => model.Provider == "fixture-available");
	}

	public void Dispose()
	{
		foreach (var (variable, value) in _previous) Environment.SetEnvironmentVariable(variable, value);
		Directory.Delete(_root, recursive: true);
	}
}
