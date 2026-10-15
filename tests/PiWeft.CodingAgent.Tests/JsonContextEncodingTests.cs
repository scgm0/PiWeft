using System.Text.Json;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public class JsonContextEncodingTests
{
	[Theory]
	[InlineData("中")]
	[InlineData("<tag> & 'quote'")]
	[InlineData("\"quoted\"")]
	public void SettingsContextPreservesGeneratedOptionsAndReadableText(string text)
	{
		var context = CodingAgentJsonContext.Default;
		var settings = new SettingsFile { DefaultModel = text };

		var json = JsonSerializer.Serialize(settings, context.SettingsFile);

		Assert.DoesNotContain("\\u", json);
		Assert.Contains("\"defaultModel\"", json);
		Assert.DoesNotContain("defaultProvider", json);
		Assert.Contains('\n', json);
		Assert.Equal(text, JsonSerializer.Deserialize(json, context.SettingsFile)!.DefaultModel);
	}

	[Theory]
	[InlineData("中")]
	[InlineData("<tag> & 'quote'")]
	[InlineData("\"quoted\"")]
	public void ModelsContextKeepsNestedValuesReadable(string text)
	{
		var context = ModelsJsonContext.Default;
		var models = new ModelsFile
		{
			Providers = new Dictionary<string, ModelsProviderEntry>
			{
				["test"] = new() { ApiKey = text },
			},
		};

		var json = JsonSerializer.Serialize(models, context.ModelsFile);

		Assert.DoesNotContain("\\u", json);
		Assert.Equal(text, JsonSerializer.Deserialize(json, context.ModelsFile)!.Providers!["test"].ApiKey);
	}
}
