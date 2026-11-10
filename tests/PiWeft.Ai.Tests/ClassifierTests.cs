using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Providers.Classifiers;
using PiWeft.Ai.Providers.OpenAi;
using Xunit;

namespace PiWeft.Ai.Tests;

public class ClassifierTests
{
	private const string ResponseBody = """
		{
		  "answers": [
		    {
		      "name": "sentiment",
		      "type": "choice",
		      "choice": "positive",
		      "probabilities": [
		        { "value": "positive", "probability": 0.9 },
		        { "value": "negative", "probability": 0.1 }
		      ],
		      "confidence": 0.8
		    },
		    { "name": "quality", "type": "score", "score": 4, "confidence": 0.75 },
		    { "name": "spam", "type": "predicate", "probability": 0.05 }
		  ],
		  "usage": { "input_tokens": 100, "output_tokens": 10 }
		}
		""";

	private static Model Model() => new()
	{
		Id = "gpt-5-decisions",
		Provider = "openai",
		Api = OpenAiDecisionsProvider.ApiId,
		Type = ModelType.Classifier,
		BaseUrl = "https://api.openai.example/v1",
		Input = ["text", "image"],
		Cost = new ModelCost { Input = 1, Output = 2 },
	};

	[Fact]
	public async Task DecisionsParsesAllQuestionTypes()
	{
		var handler = new FakeHttpHandler
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
			},
		};
		var provider = new OpenAiDecisionsProvider(new HttpClient(handler));
		var context = new ClassifierContext
		{
			State = new JsonObject { ["text"] = "great product" },
			Images = [new ImageContent { Data = "aW1n", MimeType = "image/png" }],
			Questions = new Dictionary<string, ClassifierQuestion>
			{
				["sentiment"] = new ClassifierChoiceQuestion
				{
					Instructions = "Classify sentiment",
					Criteria = new Dictionary<string, string> { ["positive"] = "happy", ["negative"] = "unhappy" },
				},
				["quality"] = new ClassifierScoreQuestion
				{
					Instructions = "Rate quality",
					Criteria = ["bad", "ok", "good", "great", "best"],
				},
				["spam"] = new ClassifierBoolQuestion
				{
					Instructions = "Is it spam?",
					TrueMeaning = "unsolicited",
					FalseMeaning = "wanted",
				},
			},
		};

		var result = await provider.ClassifyAsync(
			Model(),
			context,
			new ProviderRequestOptions { ApiKey = "key" });

		Assert.Equal(StopReason.Stop, result.StopReason);
		var choice = Assert.IsType<ClassifierChoiceAnswer>(result.Answers["sentiment"]);
		Assert.Equal("positive", choice.Choice);
		Assert.Equal(0.9, choice.Probabilities["positive"]);
		Assert.Equal(0.8, choice.Confidence);
		Assert.Equal(4, Assert.IsType<ClassifierScoreAnswer>(result.Answers["quality"]).Score);
		Assert.Equal(0.05, Assert.IsType<ClassifierBoolAnswer>(result.Answers["spam"]).Probability);
		Assert.Equal(100, result.Usage!.Input);
		Assert.Equal(10, result.Usage.Output);

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;
		Assert.Equal("gpt-5-decisions", root.GetProperty("model").GetString());
		Assert.Equal("predicate", root.GetProperty("questions")[2].GetProperty("type").GetString());
		Assert.Contains("unsolicited", root.GetProperty("questions")[2].GetProperty("instructions").GetString());
		Assert.Equal("input_image", root.GetProperty("input")[0].GetProperty("content")[1].GetProperty("type").GetString());
	}

	private const string SystemOneResponse = """
		{
		  "answers": {
		    "sentiment": { "type": "choice", "choice": "positive", "probabilities": { "positive": 0.9, "negative": 0.1 }, "confidence": 0.8 },
		    "quality": { "type": "score", "score": 4, "confidence": 0.75 },
		    "spam": { "type": "noul", "noul": 0.05 }
		  },
		  "usage": { "input_tokens": 100, "output_tokens": 10 }
		}
		""";

	private static Model SystemOneModel(string provider, ModelType type = ModelType.Classifier) => new()
	{
		Id = "jev-1.13",
		Provider = provider,
		Api = SystemOneClassifierProvider.TypesafeApiId,
		Type = type,
		BaseUrl = "https://api.typesafe.example/v1",
		Input = ["text", "image"],
		Cost = new ModelCost { Input = 1, Output = 2 },
	};

	private static ClassifierContext SystemOneContext() => new()
	{
		State = new JsonObject { ["text"] = "great product" },
		Images = [new ImageContent { Data = "aW1n", MimeType = "image/png" }],
		Questions = new Dictionary<string, ClassifierQuestion>
		{
			["sentiment"] = new ClassifierChoiceQuestion
			{
				Instructions = "Classify sentiment",
				Criteria = new Dictionary<string, string> { ["positive"] = "happy", ["negative"] = "unhappy" },
			},
			["quality"] = new ClassifierScoreQuestion { Instructions = "Rate quality", Criteria = ["bad", "ok", "good", "great", "best"] },
			["spam"] = new ClassifierBoolQuestion { Instructions = "Is it spam?", TrueMeaning = "unsolicited", FalseMeaning = "wanted" },
		},
	};

	[Fact]
	public async Task SystemOneParsesAnswersAndUsesOptInImageAssets()
	{
		var handler = new FakeHttpHandler
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(SystemOneResponse, Encoding.UTF8, "application/json"),
			},
		};
		var provider = new SystemOneClassifierProvider(cloudflare: false, new HttpClient(handler));

		var result = await provider.ClassifyAsync(SystemOneModel("typesafe"), SystemOneContext(), new ProviderRequestOptions { ApiKey = "key" });

		Assert.True(result.StopReason == StopReason.Stop, result.ErrorMessage);
		Assert.Equal("positive", Assert.IsType<ClassifierChoiceAnswer>(result.Answers["sentiment"]).Choice);
		Assert.Equal(4, Assert.IsType<ClassifierScoreAnswer>(result.Answers["quality"]).Score);
		Assert.Equal(0.05, Assert.IsType<ClassifierBoolAnswer>(result.Answers["spam"]).Probability);
		Assert.Equal(100, result.Usage!.Input);
		Assert.Equal("/v1/systemone", handler.LastRequest!.RequestUri!.AbsolutePath);
		Assert.Equal("true", handler.LastRequest.Headers.GetValues("TypeSafe-Danger-Zone-Images").Single());
		using var document = JsonDocument.Parse(handler.LastBody!);
		Assert.Equal("noul", document.RootElement.GetProperty("questions").GetProperty("spam").GetProperty("type").GetString());
		Assert.Equal("image", document.RootElement.GetProperty("assets").GetProperty("<image-1>").GetProperty("type").GetString());
		Assert.Equal("<image-1>", document.RootElement.GetProperty("state")[0].GetString());
	}

	[Fact]
	public async Task CloudflareSystemOneUnwrapsRunEnvelope()
	{
		var handler = new FakeHttpHandler
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					"{\"success\":true,\"result\":{\"state\":\"Completed\",\"result\":" + SystemOneResponse + "}}",
					Encoding.UTF8,
					"application/json"),
			},
		};
		var model = SystemOneModel("cloudflare-workers-ai") with
		{
			Api = SystemOneClassifierProvider.CloudflareApiId,
			Id = "@cf/cloudflare/clef",
			BaseUrl = "https://api.cloudflare.example/ai",
		};
		var provider = new SystemOneClassifierProvider(cloudflare: true, new HttpClient(handler));

		var result = await provider.ClassifyAsync(model, SystemOneContext(), new ProviderRequestOptions { ApiKey = "key" });

		Assert.Equal(StopReason.Stop, result.StopReason);
		Assert.Equal("positive", Assert.IsType<ClassifierChoiceAnswer>(result.Answers["sentiment"]).Choice);
		Assert.Equal("/ai/run", handler.LastRequest!.RequestUri!.AbsolutePath);
		using var document = JsonDocument.Parse(handler.LastBody!);
		Assert.Equal("@cf/cloudflare/clef", document.RootElement.GetProperty("model").GetString());
		Assert.Equal("great product", document.RootElement.GetProperty("input").GetProperty("state").GetProperty("text").GetString());
	}

	[Fact]
	public async Task GatewayReportsMissingKey()
	{
		var gateway = new ClassifiersGateway();
		gateway.Register(new OpenAiDecisionsProvider(new HttpClient(new FakeHttpHandler())));
		var result = await gateway.ClassifyAsync(Model(), new ClassifierContext(), new ProviderRequestOptions());

		Assert.Equal(StopReason.Error, result.StopReason);
		Assert.Contains("No API key", result.ErrorMessage);
	}
}
