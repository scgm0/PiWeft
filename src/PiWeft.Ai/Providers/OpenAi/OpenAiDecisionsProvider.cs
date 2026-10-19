using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;

namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>OpenAI Decisions 分类 API；对应 pi 的 <c>openai-decisions</c>。</summary>
public sealed class OpenAiDecisionsProvider : IClassifierApiProvider
{
	public const string ApiId = "openai-decisions";
	private const int MaxImages = 128;
	private readonly HttpClient _http;

	public OpenAiDecisionsProvider(HttpClient? httpClient = null) => _http = httpClient ?? SharedHttpClient.Instance;

	public string Api => ApiId;

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

			var images = context.Images ?? [];
			if (images.Count > MaxImages)
			{
				return result with
				{
					StopReason = StopReason.Error,
					ErrorMessage = $"OpenAI Decisions accepts at most {MaxImages} images, got {images.Count}",
				};
			}

			var state = context.State.ToJsonString(PiJson.NodeOptions);
			JsonNode input = images.Count == 0 ? JsonValue.Create(state)! : BuildImageInput(state, images);
			var questions = new JsonArray();
			foreach (var (id, question) in context.Questions)
			{
				questions.Add((JsonNode?)BuildQuestion(id, question));
			}

			var body = new JsonObject
			{
				["model"] = model.Id,
				["input"] = input,
				["questions"] = questions,
			};
			if (options.OnPayload?.Invoke(body, model) is { } replaced)
			{
				body = (JsonObject)replaced;
			}

			var baseUrl = string.IsNullOrEmpty(model.BaseUrl)
				? "https://api.openai.com/v1"
				: model.BaseUrl.TrimEnd('/');
			using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/decisions")
			{
				Content = new StringContent(body.ToJsonString(PiJson.NodeOptions), Encoding.UTF8, "application/json"),
			};
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
			request.Headers.TryAddWithoutValidation("accept", "application/json");
			request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);
			ApplyHeaders(request, model.Headers);
			ApplyNullableHeaders(request, options.Headers);

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
					ErrorMessage = $"OpenAI Decisions failed ({(int)response.StatusCode}): {payload}",
				};
			}

			var root = JsonNode.Parse(payload) as JsonObject;
			var answers = ParseAnswers(root?["answers"], context);
			return result with
			{
				Answers = answers,
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

	private static JsonArray BuildImageInput(string state, IReadOnlyList<ImageContent> images)
	{
		var content = new JsonArray();
		content.Add((JsonNode?)new JsonObject
		{
			["type"] = "input_text",
			["text"] = state,
		});
		foreach (var image in images)
		{
			content.Add((JsonNode?)new JsonObject
			{
				["type"] = "input_image",
				["image_url"] = $"data:{image.MimeType};base64,{image.Data}",
			});
		}

		var messages = new JsonArray();
		messages.Add((JsonNode?)new JsonObject
		{
			["role"] = "user",
			["content"] = content,
		});
		return messages;
	}

	private static JsonObject BuildQuestion(string id, ClassifierQuestion question) => question switch
	{
		ClassifierChoiceQuestion choice => new JsonObject
		{
			["type"] = "choice",
			["name"] = id,
			["instructions"] = choice.Instructions,
			["choices"] = new JsonArray([.. choice.Criteria.Select(pair => (JsonNode?)new JsonObject
			{
				["value"] = pair.Key,
				["description"] = pair.Value,
			})]),
		},
		ClassifierScoreQuestion score => new JsonObject
		{
			["type"] = "score",
			["name"] = id,
			["instructions"] = score.Instructions,
			["levels"] = new JsonArray([.. score.Criteria.Select(label => (JsonNode?)new JsonObject { ["label"] = label })]),
		},
		ClassifierBoolQuestion boolean => new JsonObject
		{
			["type"] = "predicate",
			["name"] = id,
			["instructions"] = BoolInstructions(boolean),
		},
		_ => throw new InvalidOperationException($"Unsupported classifier question type: {question.Type}"),
	};

	private static string BoolInstructions(ClassifierBoolQuestion question)
	{
		var meanings = new[]
		{
			question.TrueMeaning.Length > 0 ? $"True means: {question.TrueMeaning}" : string.Empty,
			question.FalseMeaning.Length > 0 ? $"False means: {question.FalseMeaning}" : string.Empty,
		}.Where(static text => text.Length > 0).ToArray();
		return meanings.Length > 0 ? $"{question.Instructions}\n\n{string.Join('\n', meanings)}" : question.Instructions;
	}

	private static IReadOnlyDictionary<string, ClassifierAnswer> ParseAnswers(
		JsonNode? raw,
		ClassifierContext context)
	{
		var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
		if (raw is JsonArray array)
		{
			foreach (var item in array)
			{
				if (item is JsonObject answer && answer["name"]?.GetValue<string>() is { Length: > 0 } name)
				{
					byName[name] = answer;
				}
			}
		}

		var answers = new Dictionary<string, ClassifierAnswer>(StringComparer.Ordinal);
		foreach (var (id, question) in context.Questions)
		{
			if (!byName.TryGetValue(id, out var answer))
			{
				throw new InvalidOperationException($"OpenAI Decisions did not return an answer for {id}");
			}

			answers[id] = question switch
			{
				ClassifierChoiceQuestion => new ClassifierChoiceAnswer
				{
					Choice = answer["choice"]?.GetValue<string>() ?? string.Empty,
					Probabilities = ParseProbabilities(answer["probabilities"]),
					Confidence = Number(answer["confidence"]),
				},
				ClassifierScoreQuestion => new ClassifierScoreAnswer
				{
					Score = Number(answer["score"]),
					Confidence = Number(answer["confidence"]),
				},
				_ => new ClassifierBoolAnswer { Probability = Number(answer["probability"]) },
			};
		}

		return answers;
	}

	private static Dictionary<string, double> ParseProbabilities(JsonNode? raw)
	{
		var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
		if (raw is JsonArray array)
		{
			foreach (var item in array)
			{
				if (item?["value"]?.GetValue<string>() is { Length: > 0 } value)
				{
					probabilities[value] = Number(item["probability"]);
				}
			}
		}

		return probabilities;
	}

	private static double Number(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

	private static Usage? ParseUsage(JsonNode? raw, Model model)
	{
		if (raw is not JsonObject usage ||
			(usage["input_tokens"] is null && usage["output_tokens"] is null))
		{
			return null;
		}

		var input = Math.Max(0, ImageJson.Long(usage["input_tokens"]));
		var output = Math.Max(0, ImageJson.Long(usage["output_tokens"]));
		return CostCalculator.Calculate(model, new Usage
		{
			Input = input,
			Output = output,
			TotalTokens = input + output,
		});
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
}
