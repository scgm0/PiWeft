using System.Text.Json;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

public class JsonContextEncodingTests
{
	[Theory]
	[InlineData("中")]
	[InlineData("<tag> & 'quote'")]
	[InlineData("\"quoted\"")]
	public void McpContextKeepsNestedValuesReadable(string text)
	{
		var context = McpJsonContext.Default;
		var config = new McpConfigFile
		{
			McpServers = new Dictionary<string, McpServerEntry>
			{
				["test"] = new() { Description = text, Args = [text] },
			},
		};

		var json = JsonSerializer.Serialize(config, context.McpConfigFile);

		Assert.DoesNotContain("\\u", json);
		var entry = JsonSerializer.Deserialize(json, context.McpConfigFile)!.McpServers!["test"];
		Assert.Equal(text, entry.Description);
		Assert.Equal(text, Assert.Single(entry.Args!));
	}
}
