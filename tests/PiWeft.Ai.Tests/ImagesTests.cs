using System.Net;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Providers.OpenAi;
using Xunit;

namespace PiWeft.Ai.Tests;

public class ImagesTests
{
	private const string ResponseBody = """
		{
		  "id": "img_1",
		  "choices": [
		    {
		      "message": {
		        "content": "here is a cat",
		        "images": [
		          { "image_url": "data:image/png;base64,Y2F0" }
		        ]
		      }
		    }
		  ],
		  "usage": {
		    "prompt_tokens": 120,
		    "completion_tokens": 30,
		    "prompt_tokens_details": { "cached_tokens": 20 }
		  }
		}
		""";

	private static Model Model() => new()
	{
		Id = "black-forest-labs/flux.2-pro",
		Provider = "openrouter",
		Api = OpenRouterImagesProvider.ApiId,
		Type = ModelType.Image,
		BaseUrl = "https://openrouter.example/api/v1",
		Input = ["text", "image"],
		Output = ["image", "text"],
		Cost = new ModelCost { Input = 1, Output = 2, CacheRead = 0.1 },
	};

	[Fact]
	public async Task OpenRouterImageGenerationParsesImagesAndUsage()
	{
		var handler = new FakeHttpHandler
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
			},
		};
		var provider = new OpenRouterImagesProvider(new HttpClient(handler));
		var result = await provider.GenerateAsync(
			Model(),
			new ImageGenerationRequest
			{
				Input =
				[
					new TextContent { Text = "draw a cat" },
					new ImageContent { Data = "cmVm", MimeType = "image/png" },
				],
			},
			new ProviderRequestOptions { ApiKey = "key" });

		Assert.Equal(StopReason.Stop, result.StopReason);
		Assert.Equal("img_1", result.ResponseId);
		Assert.Equal("here is a cat", Assert.IsType<TextContent>(result.Output[0]).Text);
		var image = Assert.IsType<ImageContent>(result.Output[1]);
		Assert.Equal("Y2F0", image.Data);
		Assert.Equal("image/png", image.MimeType);
		Assert.Equal(100, result.Usage.Input);
		Assert.Equal(20, result.Usage.CacheRead);
		Assert.Equal(30, result.Usage.Output);

		using var body = JsonDocument.Parse(handler.LastBody!);
		Assert.Equal("black-forest-labs/flux.2-pro", body.RootElement.GetProperty("model").GetString());
		Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
		Assert.Equal(2, body.RootElement.GetProperty("modalities").GetArrayLength());
		var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
		Assert.Equal("image_url", content[1].GetProperty("type").GetString());
	}

	[Fact]
	public async Task MissingImageKeyReturnsAnErrorResult()
	{
		var gateway = new ImagesGateway();
		gateway.Register(new OpenRouterImagesProvider(new HttpClient(new FakeHttpHandler())));
		var result = await gateway.GenerateAsync(Model(), new ImageGenerationRequest(), new ProviderRequestOptions());

		Assert.Equal(StopReason.Error, result.StopReason);
		Assert.Contains("No API key", result.ErrorMessage);
	}

	[Fact]
	public void RegistrySeparatesImageAndChatModels()
	{
		var registry = new ModelRegistry();
		registry.Add(Model());
		registry.Add(new Model { Id = "chat", Provider = "openrouter", Api = "openai-completions" });

		Assert.Single(registry.ImageModels());
		Assert.Single(registry.ChatModels());
	}
}
