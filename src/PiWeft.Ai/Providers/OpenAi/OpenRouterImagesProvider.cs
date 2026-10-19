using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;

namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>OpenRouter 的 chat-completions 生图方言；对应 pi 的 <c>openrouter-images</c>。</summary>
public sealed class OpenRouterImagesProvider : IImageApiProvider
{
	public const string ApiId = "openrouter-images";
	private readonly HttpClient _http;

	public OpenRouterImagesProvider(HttpClient? httpClient = null) => _http = httpClient ?? SharedHttpClient.Instance;

	public string Api => ApiId;

	public async Task<ImageGenerationResult> GenerateAsync(
		Model model,
		ImageGenerationRequest request,
		ProviderRequestOptions options,
		CancellationToken cancellationToken = default)
	{
		var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var result = new ImageGenerationResult
		{
			Api = model.Api,
			Provider = model.Provider,
			Model = model.Id,
			Timestamp = started,
		};

		try
		{
			var apiKey = options.ApiKey ?? ProviderEnvironment.ResolveApiKey(model.Provider, new StreamOptions
			{
				ApiKey = options.ApiKey,
				Headers = options.Headers,
			});
			if (string.IsNullOrWhiteSpace(apiKey))
			{
				return result with
				{
					StopReason = StopReason.Error,
					ErrorMessage = $"No API key for provider: {model.Provider}",
				};
			}

			var content = new JsonArray();
			foreach (var block in request.Input)
			{
				switch (block)
				{
					case TextContent text:
						content.Add((JsonNode?)new JsonObject
						{
							["type"] = "text",
							["text"] = text.Text,
						});
						break;
					case ImageContent image:
						content.Add((JsonNode?)new JsonObject
						{
							["type"] = "image_url",
							["image_url"] = new JsonObject
							{
								["url"] = $"data:{image.MimeType};base64,{image.Data}",
							},
						});
						break;
				}
			}

			var outputModalities = new JsonArray();
			outputModalities.Add((JsonNode?)"image");
			if (model.Output?.Contains("text", StringComparer.Ordinal) == true)
			{
				outputModalities.Add((JsonNode?)"text");
			}

			var messages = new JsonArray();
			messages.Add((JsonNode?)new JsonObject
			{
				["role"] = "user",
				["content"] = content,
			});
			var body = new JsonObject
			{
				["model"] = model.Id,
				["messages"] = messages,
				["stream"] = false,
				["modalities"] = outputModalities,
			};

			if (options.OnPayload?.Invoke(body, model) is { } replaced)
			{
				body = (JsonObject)replaced;
			}

			var baseUrl = string.IsNullOrEmpty(model.BaseUrl)
				? "https://openrouter.ai/api/v1"
				: model.BaseUrl.TrimEnd('/');
			using var httpRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
			{
				Content = new StringContent(body.ToJsonString(PiJson.NodeOptions), Encoding.UTF8, "application/json"),
			};
			httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
			httpRequest.Headers.TryAddWithoutValidation("accept", "application/json");
			httpRequest.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);
			ApplyHeaders(httpRequest, model.Headers);
			ApplyNullableHeaders(httpRequest, options.Headers);

			using var timeout = options.TimeoutMs is > 0
				? new CancellationTokenSource(TimeSpan.FromMilliseconds(options.TimeoutMs.Value))
				: null;
			using var linked = timeout is null
				? null
				: CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
			using var response = await _http.SendAsync(
				httpRequest,
				HttpCompletionOption.ResponseHeadersRead,
				linked?.Token ?? cancellationToken).ConfigureAwait(false);
			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return result with
				{
					StopReason = StopReason.Error,
					ErrorMessage = $"OpenRouter image request failed ({(int)response.StatusCode}): {payload}",
				};
			}

			var root = JsonNode.Parse(payload) as JsonObject;
			var message = root?["choices"]?[0]?["message"] as JsonObject;
			var output = new List<ContentBlock>();
			if (message?["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var contentText) && contentText.Length > 0)
			{
				output.Add(new TextContent { Text = contentText });
			}

			if (message?["images"] is JsonArray images)
			{
				foreach (var image in images)
				{
					if (ImageJson.ImageDataUrl(image) is { Length: > 0 } data)
					{
						output.Add(new ImageContent
						{
							Data = data,
							MimeType = ImageJson.ImageMimeType(image) ?? "image/png",
						});
					}
				}
			}

			return result with
			{
				Output = output,
				ResponseId = ImageJson.String(root?["id"]),
				Usage = ParseUsage(root?["usage"], model),
			};
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return result with { StopReason = StopReason.Aborted, ErrorMessage = "Request aborted" };
		}
		catch (OperationCanceledException)
		{
			return result with { StopReason = StopReason.Error, ErrorMessage = "Request timed out" };
		}
		catch (Exception exception)
		{
			return result with { StopReason = StopReason.Error, ErrorMessage = exception.Message };
		}
	}

	private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
	{
		foreach (var (name, value) in headers ?? new Dictionary<string, string>())
		{
			request.Headers.TryAddWithoutValidation(name, value);
		}
	}

	private static void ApplyNullableHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string?>? headers)
	{
		foreach (var (name, value) in headers ?? new Dictionary<string, string?>())
		{
			if (value is null)
			{
				request.Headers.Remove(name);
			}
			else
			{
				request.Headers.TryAddWithoutValidation(name, value);
			}
		}
	}

	private static Usage ParseUsage(JsonNode? raw, Model model)
	{
		var prompt = ImageJson.Long(raw?["prompt_tokens"]);
		var completion = ImageJson.Long(raw?["completion_tokens"]);
		var cacheWrite = ImageJson.Long(raw?["prompt_tokens_details"]?["cache_write_tokens"]);
		var cached = ImageJson.Long(raw?["prompt_tokens_details"]?["cached_tokens"]);
		var cacheRead = Math.Max(0, cached - cacheWrite);
		var input = prompt > 0 ? Math.Max(0, prompt - cacheRead - cacheWrite) : 0;
		return CostCalculator.Calculate(model, new Usage
		{
			Input = input,
			Output = completion,
			CacheRead = cacheRead,
			CacheWrite = cacheWrite,
			TotalTokens = Math.Max(0, prompt) + Math.Max(0, completion),
		});
	}
}
