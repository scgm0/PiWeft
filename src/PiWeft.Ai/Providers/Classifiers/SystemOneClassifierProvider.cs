using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;
using PiWeft.Ai.Utils;

namespace PiWeft.Ai.Providers.Classifiers;

/// TypeSafe System One 的分类协议。OpenRouter / OpenCode / Vercel 代理同一套请求，
/// Cloudflare Workers AI 用 <c>{ model, input }</c> 包一层并返回 Cloudflare envelope。
/// 对应 pi 的 <c>typesafe-system-one</c> 与 <c>cloudflare-workers-ai-system-one</c>。
public sealed class SystemOneClassifierProvider : IClassifierApiProvider
{
	public const string TypesafeApiId = "typesafe-system-one";
	public const string CloudflareApiId = "cloudflare-workers-ai-system-one";

	private const string ImageOptInHeader = "TypeSafe-Danger-Zone-Images";
	private const int MaxDataUrlImages = 4;

	private readonly HttpClient _http;
	private readonly bool _cloudflare;

	public SystemOneClassifierProvider(bool cloudflare, HttpClient? httpClient = null)
	{
		_cloudflare = cloudflare;
		_http = httpClient ?? SharedHttpClient.Instance;
	}

	public string Api => _cloudflare ? CloudflareApiId : TypesafeApiId;

	public async Task<ClassifierResult> ClassifyAsync(
		Model model,
		ClassifierContext context,
		ProviderRequestOptions options,
		CancellationToken cancellationToken = default)
	{
		var result = new ClassifierResult
		{
			Api = model.Api,
			Provider = model.Provider,
			Model = model.Id,
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		};

		try
		{
			var apiKey = await ProviderEnvironment.ResolveApiKeyAsync(model.Provider, new StreamOptions
			{
				ApiKey = options.ApiKey,
				Headers = options.Headers,
			}, cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(apiKey))
			{
				return result with
				{
					StopReason = StopReason.Error,
					ErrorMessage = $"No API key for provider: {model.Provider}",
				};
			}

			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (options.Headers is { Count: > 0 } extra)
			{
				foreach (var (name, value) in extra)
				{
					if (value is not null)
					{
						headers[name] = value;
					}
				}
			}

			var requestBody = BuildRequest(model, context, headers);
			var body = _cloudflare
				? new JsonObject { ["model"] = model.Id, ["input"] = requestBody }
				: requestBody;
			var baseUrl = ProviderEnvironment.ExpandBaseUrl(model.Provider, model.BaseUrl).TrimEnd('/');
			var url = _cloudflare ? baseUrl + "/run" : baseUrl + "/systemone";

			using var request = new HttpRequestMessage(HttpMethod.Post, url)
			{
				Content = new StringContent(body.ToJsonString(PiJson.NodeOptions), Encoding.UTF8, "application/json"),
			};
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
			request.Headers.TryAddWithoutValidation("accept", "application/json");
			request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);
			ApplyHeaders(request, model.Headers);
			foreach (var (name, value) in headers)
			{
				request.Headers.TryAddWithoutValidation(name, value);
			}

			using var timeout = options.TimeoutMs is > 0
				? new CancellationTokenSource(TimeSpan.FromMilliseconds(options.TimeoutMs.Value))
				: null;
			using var linked = timeout is null
				? null
				: CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
			using var response = await _http.SendAsync(
				request,
				HttpCompletionOption.ResponseContentRead,
				linked?.Token ?? cancellationToken).ConfigureAwait(false);
			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return result with
				{
					StopReason = StopReason.Error,
					ErrorMessage = $"{Label} failed ({(int)response.StatusCode}): {Truncate(payload, 2000)}",
				};
			}

			var output = Unwrap(JsonNode.Parse(payload));
			if (output is null)
			{
				return result with { StopReason = StopReason.Error, ErrorMessage = $"{Label} returned an unexpected response" };
			}

			return result with
			{
				Answers = ParseAnswers(output["answers"], context.Questions),
				Usage = ParseUsage(output["usage"], model),
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

	private string Label => _cloudflare ? "Cloudflare Workers AI" : "TypeSafe System One";

	private JsonObject BuildRequest(Model model, ClassifierContext context, Dictionary<string, string> headers)
	{
		var questions = new JsonObject();
		foreach (var (id, question) in context.Questions)
		{
			questions[id] = BuildQuestion(question);
		}

		var state = context.State.DeepClone();
		var body = new JsonObject
		{
			["state"] = state,
			["questions"] = questions,
		};

		if (context.Images is not { Count: > 0 } images)
		{
			return body;
		}

		var imageInput = ImageInputFor(model);
		if (imageInput == "images")
		{
			if (images.Count > MaxDataUrlImages)
			{
				throw new InvalidOperationException($"{Label} accepts at most {MaxDataUrlImages} images, got {images.Count}");
			}

			var list = new JsonArray();
			foreach (var image in images)
			{
				if (image.MimeType is not ("image/png" or "image/jpeg" or "image/webp"))
				{
					throw new InvalidOperationException($"{Label} accepts PNG, JPEG and WebP images, got {image.MimeType}");
				}

				list.Add((JsonNode?)$"data:{image.MimeType};base64,{image.Data}");
			}

			body["images"] = list;
			return body;
		}

		if (imageInput == "state-image-urls")
		{
			var parts = new JsonArray();
			foreach (var image in images)
			{
				parts.Add((JsonNode?)new JsonObject
				{
					["type"] = "image_url",
					["image_url"] = new JsonObject { ["url"] = $"data:{image.MimeType};base64,{image.Data}" },
				});
			}

			parts.Add(state.DeepClone());
			body["state"] = parts;
			return body;
		}

		var assets = new JsonObject();
		var names = new JsonArray();
		for (var index = 0; index < images.Count; index++)
		{
			var image = images[index];
			var format = image.MimeType switch
			{
				"image/png" => "png",
				"image/jpeg" => "jpeg",
				_ => throw new InvalidOperationException($"{Label} accepts PNG and JPEG images, got {image.MimeType}"),
			};
			var name = $"<image-{index + 1}>";
			names.Add((JsonNode?)name);
			assets[name] = new JsonObject
			{
				["type"] = "image",
				["format"] = format,
				["data_base64"] = image.Data,
			};
		}

		body["assets"] = assets;
		names.Add(state.DeepClone());
		body["state"] = names;
		if (SendImageOptIn(model))
		{
			headers[ImageOptInHeader] = "true";
		}

		return body;
	}

	private string ImageInputFor(Model model)
	{
		if (ModelCompat.Text(model, "imageInput") is { Length: > 0 } configured)
		{
			return configured;
		}

		if (_cloudflare)
		{
			return model.Id.StartsWith("@cf/cloudflare/clef", StringComparison.Ordinal) ? "images" : "assets";
		}

		return string.Equals(model.Provider, "openrouter", StringComparison.Ordinal) ||
			model.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase)
				? "state-image-urls"
				: "assets";
	}

	private bool SendImageOptIn(Model model)
	{
		if (ModelCompat.Flag(model, "sendImageOptInHeader") is { } configured)
		{
			return configured;
		}

		return !_cloudflare &&
			!string.Equals(model.Provider, "openrouter", StringComparison.Ordinal) &&
			!model.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
	}

	private static JsonObject BuildQuestion(ClassifierQuestion question)
	{
		var result = new JsonObject
		{
			["type"] = question.Type == "bool" ? "noul" : question.Type,
			["instructions"] = question.Instructions,
		};

		switch (question)
		{
			case ClassifierChoiceQuestion choice:
				var criteria = new JsonObject();
				foreach (var (label, meaning) in choice.Criteria)
				{
					criteria[label] = meaning;
				}

				result["criteria"] = criteria;
				break;
			case ClassifierScoreQuestion score:
				result["criteria"] = new JsonArray([.. score.Criteria.Select(static label => (JsonNode?)label)]);
				break;
			case ClassifierBoolQuestion boolean:
				result["trueMeaning"] = boolean.TrueMeaning;
				result["falseMeaning"] = boolean.FalseMeaning;
				break;
		}

		return result;
	}

	private static JsonObject? Unwrap(JsonNode? root)
	{
		if (root is not JsonObject body)
		{
			return null;
		}

		if (body["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok)
		{
			throw new InvalidOperationException(CloudflareError(body["errors"]));
		}

		if (body["result"] is not JsonObject result)
		{
			return body;
		}

		if (result.ContainsKey("answers"))
		{
			return result;
		}

		if (result["state"] is JsonValue state && state.TryGetValue<string>(out var status))
		{
			if (!string.Equals(status, "Completed", StringComparison.Ordinal))
			{
				throw new InvalidOperationException($"Cloudflare Workers AI run did not complete (state: {status})");
			}

			return result["result"] as JsonObject;
		}

		return result;
	}

	private static string CloudflareError(JsonNode? errors)
	{
		if (errors is JsonArray array)
		{
			var messages = array
				.OfType<JsonObject>()
				.Select(static item => item["message"]?.GetValue<string>())
				.Where(static message => message is { Length: > 0 })
				.ToList();
			if (messages.Count > 0)
			{
				return string.Join("; ", messages);
			}
		}

		return "Cloudflare Workers AI request failed";
	}

	private static IReadOnlyDictionary<string, ClassifierAnswer> ParseAnswers(
		JsonNode? raw,
		IReadOnlyDictionary<string, ClassifierQuestion> questions)
	{
		if (raw is not JsonObject answers)
		{
			throw new InvalidOperationException("System One returned no answers");
		}

		var result = new Dictionary<string, ClassifierAnswer>(StringComparer.Ordinal);
		foreach (var (id, question) in questions)
		{
			if (answers[id] is not JsonObject answer)
			{
				throw new InvalidOperationException($"System One did not return an answer for {id}");
			}

			switch (question)
			{
				case ClassifierChoiceQuestion:
					if (!string.Equals(answer["type"]?.GetValue<string>(), "choice", StringComparison.Ordinal) ||
						answer["choice"]?.GetValue<string>() is not { Length: > 0 } choice)
					{
						throw new InvalidOperationException($"System One did not return a choice answer for {id}");
					}

					var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
					if (answer["probabilities"] is JsonObject rawProbabilities)
					{
						foreach (var (label, value) in rawProbabilities)
						{
							probabilities[label] = Number(value, $"{id}.{label}");
						}
					}

					result[id] = new ClassifierChoiceAnswer
					{
						Choice = choice,
						Probabilities = probabilities,
						Confidence = Number(answer["confidence"], $"{id}.confidence"),
					};
					break;

				case ClassifierScoreQuestion:
					if (!string.Equals(answer["type"]?.GetValue<string>(), "score", StringComparison.Ordinal))
					{
						throw new InvalidOperationException($"System One did not return a score answer for {id}");
					}

					result[id] = new ClassifierScoreAnswer
					{
						Score = Number(answer["score"], $"{id}.score"),
						Confidence = Number(answer["confidence"], $"{id}.confidence"),
					};
					break;

				default:
					if (!string.Equals(answer["type"]?.GetValue<string>(), "noul", StringComparison.Ordinal))
					{
						throw new InvalidOperationException($"System One did not return a bool answer for {id}");
					}

					result[id] = new ClassifierBoolAnswer
					{
						Probability = Number(answer["noul"], $"{id}.noul"),
					};
					break;
			}
		}

		return result;
	}

	private static Usage? ParseUsage(JsonNode? raw, Model model)
	{
		if (raw is not JsonObject usage)
		{
			return null;
		}

		var input = Number(usage["input_tokens"], "usage.input_tokens", allowMissing: true);
		var output = Number(usage["output_tokens"], "usage.output_tokens", allowMissing: true);
		if (input == 0 && output == 0)
		{
			return null;
		}

		var result = new Usage
		{
			Input = (long)input,
			Output = (long)output,
			TotalTokens = (long)(input + output),
		};
		return CostCalculator.Calculate(model, result);
	}

	private static double Number(JsonNode? node, string label, bool allowMissing = false)
	{
		if (node is JsonValue value)
		{
			if (value.TryGetValue<double>(out var real))
			{
				return real;
			}

			if (value.TryGetValue<long>(out var integer))
			{
				return integer;
			}

			if (value.TryGetValue<JsonElement>(out var element) &&
				element.ValueKind == JsonValueKind.Number &&
				element.TryGetDouble(out real))
			{
				return real;
			}
		}

		if (allowMissing)
		{
			return 0;
		}

		throw new InvalidOperationException($"System One returned invalid number for {label}");
	}

	private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
	{
		if (headers is null)
		{
			return;
		}

		foreach (var (name, value) in headers)
		{
			request.Headers.TryAddWithoutValidation(name, value);
		}
	}

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
