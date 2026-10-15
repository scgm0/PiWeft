using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Providers.Google;
using PiWeft.Ai.Providers.OpenAi;
using Xunit;

namespace PiWeft.Ai.Tests;

[Collection("process-environment")]
public class AzureOpenAiResponsesProviderTests
{
	private static Model Model(string baseUrl = "https://myresource.openai.azure.com") => new()
	{
		Id = "gpt-5",
		Provider = "azure",
		Api = AzureOpenAiResponsesProvider.ApiId,
		BaseUrl = baseUrl,
		Reasoning = true,
		ContextWindow = 400_000,
		MaxTokens = 128_000,
		Cost = new ModelCost { Input = 1.25, Output = 10 },
	};

	private static HttpResponseMessage Sse(string body)
	{
		var terminated = body.EndsWith("\n\n", StringComparison.Ordinal) ? body : body.TrimEnd('\n') + "\n\n";
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(terminated, Encoding.UTF8, "text/event-stream"),
		};
	}

	private const string StreamBody = """
		event: response.created
		data: {"type":"response.created","response":{"id":"resp_az","model":"gpt-5","status":"in_progress"}}

		event: response.output_item.added
		data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m","role":"assistant","content":[]}}

		event: response.output_text.delta
		data: {"type":"response.output_text.delta","output_index":0,"delta":"azure answer"}

		event: response.completed
		data: {"type":"response.completed","response":{"id":"resp_az","status":"completed","usage":{"input_tokens":8,"output_tokens":4,"total_tokens":12}}}

		""";

	public static TheoryData<string, string> Endpoints => new()
	{
		{ "https://myresource.openai.azure.com", "https://myresource.openai.azure.com/openai/v1" },
		{ "https://myresource.openai.azure.com/", "https://myresource.openai.azure.com/openai/v1" },
		{ "https://myresource.openai.azure.com/openai", "https://myresource.openai.azure.com/openai/v1" },
		{ "https://myresource.openai.azure.com/openai/v1", "https://myresource.openai.azure.com/openai/v1" },
		{ "https://myresource.cognitiveservices.azure.com", "https://myresource.cognitiveservices.azure.com/openai/v1" },
		// A non-Azure host (a proxy or a local gateway) is used as-is.
		{ "https://gateway.example.test/v1", "https://gateway.example.test/v1" },
	};

	[Theory]
	[MemberData(nameof(Endpoints))]
	public void NormalizeBaseUrl_AddsTheAzurePath(string input, string expected)
	{
		Assert.Equal(expected, AzureOpenAiResponsesProvider.NormalizeBaseUrl(input));
	}

	[Fact]
	public void NormalizeBaseUrl_RejectsGarbage()
	{
		Assert.Throws<InvalidOperationException>(() => AzureOpenAiResponsesProvider.NormalizeBaseUrl("not a url"));
	}

	[Fact]
	public void DeploymentMapParsesPairs()
	{
		var map = AzureOpenAiResponsesProvider.ParseDeploymentMap("gpt-5=my-deploy, gpt-5-mini=small,,bad");

		Assert.Equal("my-deploy", map["gpt-5"]);
		Assert.Equal("small", map["gpt-5-mini"]);
		Assert.Equal(2, map.Count);
	}

	[Fact]
	public async Task StreamsThroughAzureWithDeploymentAndApiKeyHeader()
	{
		var previous = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME_MAP");
		Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", "gpt-5=my-deployment");
		try
		{
			var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
			var provider = new AzureOpenAiResponsesProvider(new HttpClient(handler));

			var stream = provider.Stream(
				Model(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions { ApiKey = "azure-key" });

			var events = new List<AssistantMessageEvent>();
			await foreach (var @event in stream)
			{
				events.Add(@event);
			}

			var done = Assert.IsType<DoneEvent>(events[^1]);
			Assert.Equal("azure answer", Assert.IsType<TextContent>(done.Message.Content[0]).Text);
			Assert.Equal("azure-openai-responses", done.Message.Api);
			Assert.Equal(12, done.Message.Usage.TotalTokens);

			var request = handler.LastRequest!;
			Assert.Equal(
				"https://myresource.openai.azure.com/openai/v1/responses?api-version=v1",
				request.RequestUri!.ToString());
			Assert.Equal("azure-key", request.Headers.GetValues("api-key").Single());
			Assert.Null(request.Headers.Authorization);

			using var document = JsonDocument.Parse(handler.LastBody!);
			Assert.Equal("my-deployment", document.RootElement.GetProperty("model").GetString());
		}
		finally
		{
			Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", previous);
		}
	}

	[Fact]
	public async Task ApiVersionAndEndpointCanComeFromTheEnvironment()
	{
		var previousBase = Environment.GetEnvironmentVariable("AZURE_OPENAI_BASE_URL");
		var previousVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION");
		Environment.SetEnvironmentVariable("AZURE_OPENAI_BASE_URL", "https://env.openai.azure.com");
		Environment.SetEnvironmentVariable("AZURE_OPENAI_API_VERSION", "2025-04-01-preview");
		try
		{
			var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
			var provider = new AzureOpenAiResponsesProvider(new HttpClient(handler));

			var stream = provider.Stream(
				Model(baseUrl: string.Empty),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions { ApiKey = "k" });

			await foreach (var _ in stream)
			{
			}

			Assert.Equal(
				"https://env.openai.azure.com/openai/v1/responses?api-version=2025-04-01-preview",
				handler.LastRequest!.RequestUri!.ToString());
		}
		finally
		{
			Environment.SetEnvironmentVariable("AZURE_OPENAI_BASE_URL", previousBase);
			Environment.SetEnvironmentVariable("AZURE_OPENAI_API_VERSION", previousVersion);
		}
	}

	[Fact]
	public async Task MissingEndpointIsReportedClearly()
	{
		var previousBase = Environment.GetEnvironmentVariable("AZURE_OPENAI_BASE_URL");
		var previousResource = Environment.GetEnvironmentVariable("AZURE_OPENAI_RESOURCE_NAME");
		Environment.SetEnvironmentVariable("AZURE_OPENAI_BASE_URL", null);
		Environment.SetEnvironmentVariable("AZURE_OPENAI_RESOURCE_NAME", null);
		try
		{
			var handler = new FakeHttpHandler();
			var provider = new AzureOpenAiResponsesProvider(new HttpClient(handler));

			var stream = provider.Stream(
				Model(baseUrl: string.Empty),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions { ApiKey = "k" });

			var events = new List<AssistantMessageEvent>();
			await foreach (var @event in stream)
			{
				events.Add(@event);
			}

			var error = Assert.IsType<ErrorEvent>(events[^1]);
			Assert.Contains("AZURE_OPENAI", error.Error.ErrorMessage);
			Assert.Null(handler.LastRequest);
		}
		finally
		{
			Environment.SetEnvironmentVariable("AZURE_OPENAI_BASE_URL", previousBase);
			Environment.SetEnvironmentVariable("AZURE_OPENAI_RESOURCE_NAME", previousResource);
		}
	}
}

[Collection("process-environment")]
public class GoogleVertexProviderTests
{
	private static Model Model() => new()
	{
		Id = "gemini-2.5-flash",
		Provider = "google-vertex",
		Api = GoogleVertexProvider.ApiId,
		BaseUrl = "https://{location}-aiplatform.googleapis.com",
		Reasoning = true,
		ContextWindow = 1_048_576,
		MaxTokens = 65_536,
		Cost = new ModelCost { Input = 0.3, Output = 2.5 },
	};

	private static HttpResponseMessage Sse(string body)
	{
		var terminated = body.EndsWith("\n\n", StringComparison.Ordinal) ? body : body.TrimEnd('\n') + "\n\n";
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(terminated, Encoding.UTF8, "text/event-stream"),
		};
	}

	private const string StreamBody = """
		data: {"responseId":"resp_v1","modelVersion":"gemini-2.5-flash","candidates":[{"content":{"role":"model","parts":[{"text":"vertex answer"}]}}]}

		data: {"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2}}

		""";

	public static TheoryData<string, string, string> Urls => new()
	{
		// location, project, expected url
		{
			"us-central1",
			"my-project",
			"https://us-central1-aiplatform.googleapis.com/v1/projects/my-project/locations/us-central1/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse"
		},
		{
			"europe-west4",
			"proj-2",
			"https://europe-west4-aiplatform.googleapis.com/v1/projects/proj-2/locations/europe-west4/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse"
		},
		{
			// The global endpoint has no region prefix.
			"global",
			"proj-3",
			"https://aiplatform.googleapis.com/v1/projects/proj-3/locations/global/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse"
		},
	};

	[Theory]
	[MemberData(nameof(Urls))]
	public void BuildUrl_SubstitutesLocationProjectAndModel(string location, string project, string expected)
	{
		var previousLocation = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION");
		var previousProject = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
		Environment.SetEnvironmentVariable("GOOGLE_CLOUD_LOCATION", location);
		Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", project);
		try
		{
			Assert.Equal(expected, GoogleVertexProvider.BuildUrl(Model(), new StreamOptions()));
		}
		finally
		{
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_LOCATION", previousLocation);
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", previousProject);
		}
	}

	[Fact]
	public void MissingProjectIsReportedClearly()
	{
		var previous = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
		var previousId = Environment.GetEnvironmentVariable("GOOGLE_PROJECT_ID");
		Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", null);
		Environment.SetEnvironmentVariable("GOOGLE_PROJECT_ID", null);
		try
		{
			var error = Assert.Throws<InvalidOperationException>(() => GoogleVertexProvider.ResolveProject());
			Assert.Contains("GOOGLE_CLOUD_PROJECT", error.Message);
		}
		finally
		{
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", previous);
			Environment.SetEnvironmentVariable("GOOGLE_PROJECT_ID", previousId);
		}
	}

	[Fact]
	public async Task StreamsThroughVertexWithABearerTokenFromTheEnvironment()
	{
		var previousToken = Environment.GetEnvironmentVariable(GoogleAdc.AccessTokenVariable);
		var previousProject = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
		Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, "test-access-token");
		Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", "proj");
		try
		{
			var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
			var provider = new GoogleVertexProvider(new HttpClient(handler));

			var stream = provider.Stream(
				Model(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions());

			await foreach (var _ in stream)
			{
			}

			var message = await stream.Result;
			Assert.Equal("vertex answer", Assert.IsType<TextContent>(message.Content[0]).Text);
			Assert.Equal("google-vertex", message.Api);
			Assert.Equal(7, message.Usage.TotalTokens);

			var request = handler.LastRequest!;
			Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
			Assert.Equal("test-access-token", request.Headers.Authorization.Parameter);
			Assert.Contains("/v1/projects/proj/locations/", request.RequestUri!.ToString());
		}
		finally
		{
			Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, previousToken);
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", previousProject);
		}
	}

	[Fact]
	public async Task AnApiKeyUsesExpressModeInsteadOfABearerToken()
	{
		var previousKey = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_API_KEY");
		var previousProject = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
		var previousToken = Environment.GetEnvironmentVariable(GoogleAdc.AccessTokenVariable);
		Environment.SetEnvironmentVariable("GOOGLE_CLOUD_API_KEY", "vertex-key");
		Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", "proj");
		Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, null);
		try
		{
			var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
			var provider = new GoogleVertexProvider(new HttpClient(handler));

			var stream = provider.Stream(
				Model(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions());

			await foreach (var _ in stream)
			{
			}

			var request = handler.LastRequest!;
			Assert.Equal("vertex-key", request.Headers.GetValues("x-goog-api-key").Single());
			Assert.Null(request.Headers.Authorization);
		}
		finally
		{
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_API_KEY", previousKey);
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", previousProject);
			Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, previousToken);
		}
	}
}

[Collection("process-environment")]
public class GoogleAdcTests
{
	[Fact]
	public void Base64UrlHasNoPaddingOrUnsafeCharacters()
	{
		Assert.Equal("aGVsbG8", GoogleAdc.Base64Url("hello"));
		Assert.Equal("_-8", GoogleAdc.Base64Url([0xFF, 0xEF]));
		Assert.DoesNotContain('=', GoogleAdc.Base64Url("hi"));
	}

	[Fact]
	public void CreateAssertionProducesASignedThreePartJwt()
	{
		using var rsa = RSA.Create(2048);
		var pem = rsa.ExportPkcs8PrivateKeyPem();

		var assertion = GoogleAdc.CreateAssertion("svc@example.test", "https://oauth2.googleapis.com/token", pem);
		var parts = assertion.Split('.');

		Assert.Equal(3, parts.Length);

		var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
		Assert.Equal("RS256", header.GetProperty("alg").GetString());
		Assert.Equal("JWT", header.GetProperty("typ").GetString());

		var claims = JsonDocument.Parse(Decode(parts[1])).RootElement;
		Assert.Equal("svc@example.test", claims.GetProperty("iss").GetString());
		Assert.Equal(GoogleAdc.CloudPlatformScope, claims.GetProperty("scope").GetString());
		Assert.Equal("https://oauth2.googleapis.com/token", claims.GetProperty("aud").GetString());
		Assert.True(claims.GetProperty("exp").GetInt64() > claims.GetProperty("iat").GetInt64());

		// The signature verifies against the matching public key.
		var signed = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
		var signature = FromBase64Url(parts[2]);
		Assert.True(rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
	}

	[Fact]
	public async Task AccessTokenFromTheEnvironmentWins()
	{
		var previous = Environment.GetEnvironmentVariable(GoogleAdc.AccessTokenVariable);
		Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, "explicit-token");
		try
		{
			using var http = new HttpClient();
			Assert.Equal("explicit-token", await GoogleAdc.GetAccessTokenAsync(http, CancellationToken.None));
		}
		finally
		{
			Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, previous);
		}
	}

	[Fact]
	public async Task WithoutAnyCredentialTheFailureExplainsWhatToSet()
	{
		var previousToken = Environment.GetEnvironmentVariable(GoogleAdc.AccessTokenVariable);
		var previousFile = Environment.GetEnvironmentVariable(GoogleAdc.CredentialsVariable);
		Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, null);
		Environment.SetEnvironmentVariable(GoogleAdc.CredentialsVariable, null);
		try
		{
			using var http = new HttpClient();
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				GoogleAdc.GetAccessTokenAsync(http, CancellationToken.None));

			Assert.Contains("GOOGLE_ACCESS_TOKEN", error.Message);
			Assert.Contains("GOOGLE_APPLICATION_CREDENTIALS", error.Message);
		}
		finally
		{
			Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, previousToken);
			Environment.SetEnvironmentVariable(GoogleAdc.CredentialsVariable, previousFile);
		}
	}

	[Fact]
	public async Task ServiceAccountCredentialsAreExchangedAndCached()
	{
		var directory = Path.Combine(Path.GetTempPath(), "pi-adc-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(directory);
		var credentialPath = Path.Combine(directory, "sa.json");

		using var rsa = RSA.Create(2048);
		var tokenUri = "https://oauth2.example.test/token";

		File.WriteAllText(credentialPath, new System.Text.Json.Nodes.JsonObject
		{
			["type"] = "service_account",
			["client_email"] = "svc@example.test",
			["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
			["token_uri"] = tokenUri,
		}.ToJsonString());

		var previousFile = Environment.GetEnvironmentVariable(GoogleAdc.CredentialsVariable);
		var previousToken = Environment.GetEnvironmentVariable(GoogleAdc.AccessTokenVariable);
		Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, null);
		Environment.SetEnvironmentVariable(GoogleAdc.CredentialsVariable, credentialPath);
		GoogleAdc.ClearCache();

		try
		{
			var requests = new List<string>();
			using var http = new HttpClient(new StubHandler(request =>
			{
				requests.Add(request.RequestUri!.ToString());
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(
						"""{"access_token":"adc-token","expires_in":3600,"token_type":"Bearer"}""",
						Encoding.UTF8,
						"application/json"),
				};
			}));

			Assert.Equal("adc-token", await GoogleAdc.GetAccessTokenAsync(http, CancellationToken.None));
			// A second call reuses the cached token instead of hitting the token endpoint again.
			Assert.Equal("adc-token", await GoogleAdc.GetAccessTokenAsync(http, CancellationToken.None));
			Assert.Single(requests);
			Assert.Equal(tokenUri, requests[0]);

			// A failing token endpoint surfaces its body.
			using var failing = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
			{
				Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
			}));

			GoogleAdc.ClearCache();
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
				GoogleAdc.GetAccessTokenAsync(failing, CancellationToken.None));
			Assert.Contains("invalid_grant", error.Message);
		}
		finally
		{
			Environment.SetEnvironmentVariable(GoogleAdc.CredentialsVariable, previousFile);
			Environment.SetEnvironmentVariable(GoogleAdc.AccessTokenVariable, previousToken);
			GoogleAdc.ClearCache();
			Directory.Delete(directory, recursive: true);
		}
	}

	private static string Decode(string part)
	{
		var padded = part.Replace('-', '+').Replace('_', '/');
		padded += new string('=', (4 - (padded.Length % 4)) % 4);
		return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
	}

	private static byte[] FromBase64Url(string part)
	{
		var padded = part.Replace('-', '+').Replace('_', '/');
		padded += new string('=', (4 - (padded.Length % 4)) % 4);
		return Convert.FromBase64String(padded);
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(responder(request));
	}
}
