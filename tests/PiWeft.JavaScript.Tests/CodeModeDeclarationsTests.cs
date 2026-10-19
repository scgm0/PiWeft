using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.JavaScript.Tests;

public sealed class CodeModeDeclarationsTests
{
	[Theory]
	[InlineData("read", "read")]
	[InlineData("my-tool", "my_tool")]
	[InlineData("mcp__docs__search", "mcp__docs__search")]
	[InlineData("1abc", "_abc")]
	[InlineData("", "_")]
	public void ToIdentifier_ReplacesInvalidCharacters(string name, string expected) =>
		Assert.Equal(expected, CodeModeIdentifier.ToIdentifier(name));

	[Fact]
	public void RenderSignature_UsesSchemaTypesAndOptionalFields()
	{
		var tool = new CodeModeTool
		{
			Name = "read",
			Description = "Read a file",
			InputSchema = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["path"] = new JsonObject { ["type"] = "string" },
					["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
				},
				["required"] = new JsonArray("path"),
			},
			OutputSchema = new JsonObject { ["type"] = "string" },
			Execute = (_, _) => Task.FromResult<JsonNode?>(null),
		};

		var signature = CodeModeDeclarations.RenderSignature(tool);

		Assert.Contains("read(args:", signature);
		Assert.Contains("path: string;", signature);
		Assert.Contains("// @integer @minimum 1", signature);
		Assert.Contains("limit?: number;", signature);
		Assert.EndsWith("Promise<string>;", signature);
	}

	[Fact]
	public void Render_ListsEveryToolAndItsDescription()
	{
		var tools = new List<CodeModeTool>
		{
			new()
			{
				Name = "grep",
				Description = "Search file contents",
				InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
				Execute = (_, _) => Task.FromResult<JsonNode?>(null),
			},
			new()
			{
				Name = "find",
				InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
				Execute = (_, _) => Task.FromResult<JsonNode?>(null),
			},
		};

		var rendered = CodeModeDeclarations.Render(tools);

		Assert.StartsWith("declare const tools: {", rendered);
		Assert.Contains("/** Search file contents */", rendered);
		Assert.Contains("grep(args:", rendered);
		Assert.Contains("find(args:", rendered);
	}

	[Fact]
	public void SchemaToType_ExpandsLocalReferencesAndEnums()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["mode"] = new JsonObject { ["enum"] = new JsonArray("fast", "slow") },
				["path"] = new JsonObject { ["$ref"] = "#/$defs/path" },
			},
			["$defs"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string" },
			},
		};

		var type = CodeModeDeclarations.SchemaToType(schema);

		Assert.Contains("\"fast\" | \"slow\"", type);
		Assert.Contains("path?: string;", type);
	}

	[Fact]
	public void SchemaToType_FallsBackToUnknownWhenTooLong()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["value"] = new JsonObject
				{
					["type"] = "string",
					["pattern"] = new string('a', 500),
				},
			},
		};

		Assert.Equal("unknown", CodeModeDeclarations.SchemaToType(schema, maxChars: 5));
	}
}
