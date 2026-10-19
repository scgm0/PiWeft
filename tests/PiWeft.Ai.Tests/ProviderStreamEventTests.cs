using System.Text.Json.Nodes;
using System.Text.Json;
using PiWeft.Ai;
using Xunit;

namespace PiWeft.Ai.Tests;

public sealed class ProviderStreamEventTests
{
	[Fact]
	public void CallbackReceivesTheParsedPayload()
	{
		var model = new Model { Id = "m", Provider = "p", Api = "test" };
		JsonNode? seen = null;
		var options = new StreamOptions
		{
			OnProviderStreamEvent = (node, callbackModel) =>
			{
				Assert.Same(model, callbackModel);
				seen = node;
			},
		};

		options.NotifyStreamEvent("""{"type":"delta","text":"hi"}""", model);

		Assert.NotNull(seen);
		Assert.Equal("delta", seen!["type"]!.GetValue<string>());
	}

	[Fact]
	public void NoCallbackDoesNotParseOrThrow()
	{
		var model = new Model { Id = "m", Provider = "p", Api = "test" };
		var options = new StreamOptions();
		options.NotifyStreamEvent("""{"type":"delta"}""", model);
	}

	[Fact]
	public void ElementOverloadPassesTheElement()
	{
		var model = new Model { Id = "m", Provider = "p", Api = "test" };
		string? type = null;
		var options = new StreamOptions
		{
			OnProviderStreamEvent = (node, _) => type = node["type"]?.GetValue<string>(),
		};
		using var document = JsonDocument.Parse("""{"type":"message","value":1}""");
		options.NotifyStreamEvent(document.RootElement, model);
		Assert.Equal("message", type);
	}
}
