using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Utils;
using Xunit;

namespace PiWeft.Ai.Tests;

public class JsonSchemaValidatorTests
{
	private static Tool ToolWith(JsonObject schema, string name = "demo") => new()
	{
		Name = name,
		Description = "demo tool",
		Parameters = schema,
	};

	private static JsonObject ObjectSchema(params (string Name, JsonObject Schema)[] properties) => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject(properties.Select(static p => KeyValuePair.Create<string, JsonNode?>(p.Name, p.Schema))),
		["required"] = new JsonArray([.. properties.Select(static p => (JsonNode?)JsonValue.Create(p.Name))]),
	};

	[Fact]
	public void ValidArgumentsPassThrough()
	{
		var tool = ToolWith(ObjectSchema(("path", new JsonObject { ["type"] = "string" })));
		var result = JsonSchemaValidator.ValidateToolArguments(tool, new JsonObject { ["path"] = "a.txt" });

		Assert.Null(result.Error);
		Assert.Equal("a.txt", result.Value!["path"]!.GetValue<string>());
	}

	[Fact]
	public void MissingRequiredPropertyReportsItsPath()
	{
		var tool = ToolWith(ObjectSchema(("path", new JsonObject { ["type"] = "string" })));
		var result = JsonSchemaValidator.ValidateToolArguments(tool, new JsonObject());

		Assert.NotNull(result.Error);
		Assert.Contains("- path: Expected required property", result.Error);
		Assert.Contains("Received arguments:", result.Error);
		Assert.Contains("Validation failed for tool \"demo\"", result.Error);
	}

	[Fact]
	public void NestedRequiredPropertyReportsADottedPath()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["edit"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject { ["oldText"] = new JsonObject { ["type"] = "string" } },
					["required"] = new JsonArray("oldText"),
				},
			},
			["required"] = new JsonArray("edit"),
		};

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject { ["edit"] = new JsonObject() });

		Assert.Contains("- edit.oldText: Expected required property", result.Error);
	}

	[Fact]
	public void TypeMismatchReportsTheExpectedType()
	{
		var tool = ToolWith(ObjectSchema(("limit", new JsonObject { ["type"] = "number" })));
		var result = JsonSchemaValidator.ValidateToolArguments(tool, new JsonObject { ["limit"] = new JsonObject() });

		Assert.Contains("- limit: Expected number", result.Error);
	}

	[Fact]
	public void CoercesPrimitivesToTheDeclaredType()
	{
		var schema = ObjectSchema(
			("limit", new JsonObject { ["type"] = "number" }),
			("count", new JsonObject { ["type"] = "integer" }),
			("flag", new JsonObject { ["type"] = "boolean" }),
			("text", new JsonObject { ["type"] = "string" }));

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["limit"] = "2.5",
			["count"] = "7",
			["flag"] = "true",
			["text"] = 42,
		});

		Assert.Null(result.Error);
		Assert.Equal(2.5, result.Value!["limit"]!.GetValue<double>());
		Assert.Equal(7, result.Value["count"]!.GetValue<long>());
		Assert.True(result.Value["flag"]!.GetValue<bool>());
		Assert.Equal("42", result.Value["text"]!.GetValue<string>());
	}

	[Fact]
	public void OptionalNullsAreRemovedAndCoerced()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string" },
				["limit"] = new JsonObject { ["type"] = "number" },
			},
			["required"] = new JsonArray("path"),
		};

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["path"] = "a.txt",
			["limit"] = null,
		});

		Assert.Null(result.Error);
		Assert.False(result.Value!.ContainsKey("limit"));
	}

	[Fact]
	public void ArrayItemsAreValidatedWithIndexedPaths()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["edits"] = new JsonObject
				{
					["type"] = "array",
					["items"] = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject { ["oldText"] = new JsonObject { ["type"] = "string" } },
						["required"] = new JsonArray("oldText"),
					},
				},
			},
			["required"] = new JsonArray("edits"),
		};

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["edits"] = new JsonArray(new JsonObject(), new JsonObject { ["oldText"] = "x" }),
		});

		Assert.Contains("- edits[0].oldText: Expected required property", result.Error);
	}

	[Fact]
	public void EnumPatternAndRangeConstraintsAreEnforced()
	{
		var schema = ObjectSchema(
			("mode", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("fast", "slow") }),
			("name", new JsonObject { ["type"] = "string", ["pattern"] = "^[a-z]+$" }),
			("limit", new JsonObject { ["type"] = "number", ["minimum"] = 1 }));

		var bad = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["mode"] = "medium",
			["name"] = "ABC",
			["limit"] = 0,
		});

		Assert.Contains("Expected one of", bad.Error);
		Assert.Contains("Expected string to match", bad.Error);
		Assert.Contains("Expected number greater than or equal to 1", bad.Error);

		var good = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["mode"] = "fast",
			["name"] = "abc",
			["limit"] = 1,
		});
		Assert.Null(good.Error);
	}

	[Fact]
	public void AnyOfAcceptsAMatchingBranch()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject
				{
					["anyOf"] = new JsonArray(
						new JsonObject { ["type"] = "string" },
						new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }),
				},
			},
			["required"] = new JsonArray("path"),
		};

		Assert.Null(JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject { ["path"] = "a" }).Error);
		Assert.Null(JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["path"] = new JsonArray("a", "b"),
		}).Error);

		// An object cannot be coerced into either branch.
		var bad = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject { ["path"] = new JsonObject() });
		Assert.Contains("Expected value to match at least one of the allowed schemas", bad.Error);
	}

	[Fact]
	public void AdditionalPropertiesFalseRejectsUnknownKeys()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
			["required"] = new JsonArray("path"),
			["additionalProperties"] = false,
		};

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["path"] = "a",
			["surprise"] = true,
		});

		Assert.Contains("- surprise: Unexpected property", result.Error);
	}

	[Fact]
	public void LocalRefPropertiesAreValidatedAndCoerced()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["model"] = new JsonObject { ["$ref"] = "#/$defs/model" },
			},
			["required"] = new JsonArray("model"),
			["$defs"] = new JsonObject
			{
				["model"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["count"] = new JsonObject { ["type"] = "integer" },
						["tag"] = new JsonObject { ["type"] = "string" },
					},
					["required"] = new JsonArray("count"),
				},
			},
		};

		var missing = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["model"] = new JsonObject { ["tag"] = "a" },
		});
		Assert.Contains("- model.count: Expected required property", missing.Error);

		var coerced = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["model"] = new JsonObject { ["count"] = "7", ["tag"] = 42 },
		});
		Assert.Null(coerced.Error);
		Assert.Equal(7, coerced.Value!["model"]!["count"]!.GetValue<long>());
		Assert.Equal("42", coerced.Value["model"]!["tag"]!.GetValue<string>());
	}

	[Fact]
	public void RecursiveRefDoesNotLoop()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["root"] = new JsonObject { ["$ref"] = "#/$defs/node" } },
			["required"] = new JsonArray("root"),
			["$defs"] = new JsonObject
			{
				["node"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject { ["next"] = new JsonObject { ["$ref"] = "#/$defs/node" } },
				},
			},
		};

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["root"] = new JsonObject { ["next"] = new JsonObject { ["next"] = new JsonObject() } },
		});
		Assert.Null(result.Error);
	}

	[Fact]
	public void PatternPropertiesAreValidatedAndCoerced()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["patternProperties"] = new JsonObject
			{
				["^x-"] = new JsonObject { ["type"] = "integer" },
				["^y-"] = new JsonObject { ["type"] = "string" },
			},
			["additionalProperties"] = false,
		};

		var coerced = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["x-a"] = "3",
			["y-b"] = 4,
		});
		Assert.Null(coerced.Error);
		Assert.Equal(3, coerced.Value!["x-a"]!.GetValue<long>());
		Assert.Equal("4", coerced.Value["y-b"]!.GetValue<string>());

		var unknown = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject { ["z-c"] = 1 });
		Assert.Contains("- z-c: Unexpected property", unknown.Error);
	}

	[Fact]
	public void PrefixItemsAreValidatedAndTheRestUsesItems()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["args"] = new JsonObject
				{
					["type"] = "array",
					["prefixItems"] = new JsonArray(
						new JsonObject { ["type"] = "string" },
						new JsonObject { ["type"] = "integer" }),
					["items"] = new JsonObject { ["type"] = "number" },
				},
			},
			["required"] = new JsonArray("args"),
		};

		var coerced = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["args"] = new JsonArray("a", "2", "3.5"),
		});
		Assert.Null(coerced.Error);
		Assert.Equal("a", coerced.Value!["args"]![0]!.GetValue<string>());
		Assert.Equal(2, coerced.Value["args"]![1]!.GetValue<long>());
		Assert.Equal(3.5, coerced.Value["args"]![2]!.GetValue<double>());

		var bad = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["args"] = new JsonArray(new JsonObject()),
		});
		Assert.Contains("- args[0]: Expected string", bad.Error);
	}

	[Fact]
	public void SingleValueIsWrappedForArraySchemas()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["questions"] = new JsonObject
				{
					["type"] = "array",
					["items"] = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject { ["question"] = new JsonObject { ["type"] = "string" } },
						["required"] = new JsonArray("question"),
					},
				},
			},
			["required"] = new JsonArray("questions"),
		};

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject
		{
			["questions"] = new JsonObject { ["question"] = "继续？" },
		});

		Assert.Null(result.Error);
		Assert.IsType<JsonArray>(result.Value!["questions"]);
		Assert.Equal("继续？", result.Value["questions"]![0]!["question"]!.GetValue<string>());
	}

	[Fact]
	public void MalformedRefIsIgnoredInsteadOfThrowing()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["value"] = new JsonObject { ["$ref"] = "#/$defs/%E0" } },
		};

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject { ["value"] = 1 });
		Assert.Null(result.Error);
	}

	[Fact]
	public void PropertiesAndPatternPropertiesBothApply()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["x-a"] = new JsonObject { ["type"] = "string", ["minLength"] = 3 },
			},
			["patternProperties"] = new JsonObject
			{
				["^x-"] = new JsonObject { ["type"] = "string", ["pattern"] = "^x" },
			},
		};

		// 命中 properties 的属性也要过 patternProperties。
		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject { ["x-a"] = "x" });
		Assert.Contains("- x-a: Expected string length greater than or equal to 3", result.Error);
	}

	[Fact]
	public void NullIsNotWrappedIntoAnArray()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
			},
			["required"] = new JsonArray("items"),
		};

		var result = JsonSchemaValidator.ValidateToolArguments(ToolWith(schema), new JsonObject { ["items"] = null });
		Assert.Contains("- items: Expected array", result.Error);
	}

	[Fact]
	public void EmptySchemaAcceptsAnything()
	{
		var tool = ToolWith(new JsonObject { ["type"] = "object" });
		var result = JsonSchemaValidator.ValidateToolArguments(tool, new JsonObject { ["anything"] = 1 });

		Assert.Null(result.Error);
	}

	[Fact]
	public void BuiltinToolSchemasValidateTheirOwnDocumentedArguments()
	{
		// The schemas this port ships must accept the arguments its own tests and docs use.
		var readSchema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["path"] = new JsonObject { ["type"] = "string" },
				["offset"] = new JsonObject { ["type"] = "number" },
				["limit"] = new JsonObject { ["type"] = "number" },
			},
			["required"] = new JsonArray("path"),
		};

		Assert.Null(JsonSchemaValidator.ValidateToolArguments(
			ToolWith(readSchema, "read"),
			new JsonObject { ["path"] = "a", ["offset"] = 1, ["limit"] = 10 }).Error);

		Assert.Contains(
			"read",
			JsonSchemaValidator.ValidateToolArguments(ToolWith(readSchema, "read"), new JsonObject()).Error);
	}
}
