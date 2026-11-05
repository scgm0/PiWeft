using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.Ai.Tests;

/// 受约束采样：工具只表达「希望严格」，provider 与模型能力决定能不能真严格。
/// 这里锁住与 pi `constrained-sampling.ts` 对齐的几件事：可选属性补 null、全部进 required、
/// 关闭 additionalProperties、无法表达的形状必须抛（好让 prefer 静默回落）。
public class ConstrainedSamplingTests
{
	private static Tool ToolWith(JsonObject parameters, string? strict = "prefer") => new()
	{
		Name = "probe",
		Description = "probe",
		Parameters = parameters,
		ConstrainedSampling = strict is null ? null : new JsonObject { ["type"] = "json_schema", ["strict"] = strict },
	};

	private static JsonObject ReadSchema() => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["path"] = new JsonObject { ["type"] = "string" },
			["offset"] = new JsonObject { ["type"] = "number" },
		},
		["required"] = new JsonArray("path"),
	};

	[Fact]
	public void OptionalPropertiesBecomeNullableAndEveryPropertyIsRequired()
	{
		var strict = ConstrainedSampling.MakeStrictJsonSchema(ReadSchema());

		Assert.Equal(["path", "offset"], strict["required"]!.AsArray().Select(node => node!.GetValue<string>()));
		Assert.False(strict["additionalProperties"]!.GetValue<bool>());

		var offset = strict["properties"]!["offset"]!.AsObject();
		Assert.Equal("number", offset["anyOf"]![0]!["type"]!.GetValue<string>());
		Assert.Equal("null", offset["anyOf"]![1]!["type"]!.GetValue<string>());
		// 必填属性原样保留，不再套一层 anyOf。
		Assert.Equal("string", strict["properties"]!["path"]!["type"]!.GetValue<string>());
	}

	[Fact]
	public void PropertiesThatAlreadyAllowNullAreNotWrappedTwice()
	{
		var schema = ReadSchema();
		schema["properties"]!["offset"] = new JsonObject
		{
			["anyOf"] = new JsonArray(new JsonObject { ["type"] = "number" }, new JsonObject { ["type"] = "null" }),
		};

		var strict = ConstrainedSampling.MakeStrictJsonSchema(schema);

		Assert.Equal(2, strict["properties"]!["offset"]!["anyOf"]!.AsArray().Count);
		Assert.Contains("offset", strict["required"]!.AsArray().Select(node => node!.GetValue<string>()));
	}

	[Fact]
	public void NestedObjectsAndArrayItemsAreMadeStrictToo()
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
						["properties"] = new JsonObject
						{
							["oldText"] = new JsonObject { ["type"] = "string" },
							["note"] = new JsonObject { ["type"] = "string" },
						},
						["required"] = new JsonArray("oldText"),
					},
				},
			},
		};

		var strict = ConstrainedSampling.MakeStrictJsonSchema(schema);
		// 根部没列 required：整个 edits 会被包成 anyOf[数组, null]，严格版在里面那个分支上。
		var edits = strict["properties"]!["edits"]!.AsObject();
		var item = edits["anyOf"]![0]!["items"]!.AsObject();

		Assert.False(item["additionalProperties"]!.GetValue<bool>());
		Assert.Equal(["oldText", "note"], item["required"]!.AsArray().Select(node => node!.GetValue<string>()));
		Assert.NotNull(item["properties"]!["note"]!["anyOf"]);
	}

	[Theory]
	[InlineData("allOf")]
	[InlineData("oneOf")]
	[InlineData("$ref")]
	[InlineData("definitions")]
	[InlineData("patternProperties")]
	[InlineData("if")]
	public void ShapesTheProvidersCannotExpressAreRejected(string keyword)
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
			[keyword] = new JsonObject(),
		};

		Assert.Throws<UnsupportedStrictJsonSchemaException>(() => ConstrainedSampling.MakeStrictJsonSchema(schema));
	}

	[Fact]
	public void TupleItemsAndBooleanSchemasAreRejected()
	{
		Assert.Throws<UnsupportedStrictJsonSchemaException>(() => ConstrainedSampling.MakeStrictJsonSchema(
			new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["pair"] = new JsonObject
					{
						["type"] = "array",
						["items"] = new JsonArray(new JsonObject { ["type"] = "string" }),
					},
				},
			}));

		Assert.Throws<UnsupportedStrictJsonSchemaException>(() => ConstrainedSampling.MakeStrictJsonSchema(
			new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject { ["anything"] = true },
			}));
	}

	[Fact]
	public void RequiredMustNameAKnownProperty()
	{
		var schema = ReadSchema();
		schema["required"] = new JsonArray("path", "missing");

		Assert.Throws<UnsupportedStrictJsonSchemaException>(() => ConstrainedSampling.MakeStrictJsonSchema(schema));
	}

	[Fact]
	public void PreferToolsFallBackSilentlyWhenStrictIsUnavailableOrTheSchemaIsTooRich()
	{
		Assert.Null(ConstrainedSampling.ResolveStrictJsonSchema(ToolWith(ReadSchema()), supportsStrictMode: false));
		Assert.True(ConstrainedSampling.ResolveStrictJsonSchema(ToolWith(ReadSchema()), supportsStrictMode: true));

		var rich = ReadSchema();
		rich["oneOf"] = new JsonArray(new JsonObject { ["type"] = "string" });
		Assert.Null(ConstrainedSampling.ResolveStrictJsonSchema(ToolWith(rich), supportsStrictMode: true));
	}

	[Fact]
	public void RequireToolsRefuseToDowngradeSilently()
	{
		Assert.Throws<InvalidOperationException>(() => ConstrainedSampling.ResolveStrictJsonSchema(
			ToolWith(ReadSchema(), strict: "require"), supportsStrictMode: false));

		var rich = ReadSchema();
		rich["oneOf"] = new JsonArray(new JsonObject { ["type"] = "string" });
		Assert.Throws<InvalidOperationException>(() => ConstrainedSampling.ResolveStrictJsonSchema(
			ToolWith(rich, strict: "require"), supportsStrictMode: true));
	}

	[Fact]
	public void ToolsWithoutADeclarationNeverGoStrict()
	{
		Assert.Null(ConstrainedSampling.ResolveStrictJsonSchema(ToolWith(ReadSchema(), strict: null), supportsStrictMode: true));
		Assert.False(ConstrainedSampling.PrefersStrictJsonSchema(ToolWith(ReadSchema(), strict: null)));
		Assert.True(ConstrainedSampling.PrefersStrictJsonSchema(ToolWith(ReadSchema())));
		Assert.False(ConstrainedSampling.PrefersStrictJsonSchema(ToolWith(ReadSchema(), strict: "require")));
	}

	[Fact]
	public void AnthropicRejectsBoundsAndUnknownFormatsButAllowsTheDocumentedOnes()
	{
		// 0/1 是 minItems 的合法值，更大的会被拒（Claude 只能表达「至少 0 个 / 至少 1 个」）。
		Assert.False(ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword("minItems", 1));
		Assert.False(ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword("minItems", 0));
		Assert.True(ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword("minItems", 2));

		Assert.False(ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword("format", "uuid"));
		Assert.True(ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword("format", "custom"));

		Assert.True(ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword("minimum", 1));
		Assert.True(ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword("maxProperties", 3));
		Assert.False(ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword("description", "hello"));
	}

	[Fact]
	public void StrictToolLimitErrorsAreRecognisedSoTheRequestCanBeSentAgain()
	{
		Assert.True(ConstrainedSampling.IsStrictToolLimitError(
			"Too many strict tools (limit 20)"));
		Assert.True(ConstrainedSampling.IsStrictToolLimitError(
			"Schemas contains too many parameters with union types (17)"));
		Assert.True(ConstrainedSampling.IsStrictToolLimitError("The compiled grammar is too large"));
		Assert.False(ConstrainedSampling.IsStrictToolLimitError("invalid request: bad model"));
		Assert.False(ConstrainedSampling.IsStrictToolLimitError(null));
	}

	[Fact]
	public void StrictTransformLeavesTheDeclaredSchemaUntouched()
	{
		var schema = ReadSchema();
		ConstrainedSampling.MakeStrictJsonSchema(schema);

		Assert.Null(schema["additionalProperties"]);
		Assert.Single(schema["required"]!.AsArray());
	}
}
