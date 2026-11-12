using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Subagents;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 子代理结构化输出的 schema 子集与结果提取。
public sealed class SubagentSchemaTests
{
	private static JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();

	private static JsonNode? Value(string json) => JsonNode.Parse(json);

	private static JsonObject ReviewSchema => Object("""
		{
		  "type": "object",
		  "properties": {
		    "status": { "type": "string", "enum": ["ok", "blocked"] },
		    "files": { "type": "array", "items": { "type": "string" } },
		    "score": { "type": "integer" }
		  },
		  "required": ["status", "files"],
		  "additionalProperties": false
		}
		""");

	[Fact]
	public void Definition_AcceptsTheSupportedSubset() =>
		Assert.Null(SubagentSchema.ValidateDefinition(ReviewSchema));

	[Fact]
	public void Definition_RejectsUnsupportedKeywords()
	{
		var error = SubagentSchema.ValidateDefinition(Object("""
			{
			  "type": "object",
			  "properties": { "name": { "type": "string", "pattern": "^a" } }
			}
			"""));
		Assert.NotNull(error);
		Assert.Contains("pattern", error);
	}

	[Fact]
	public void Definition_RequiresAnObjectRoot()
	{
		var error = SubagentSchema.ValidateDefinition(Object("""{"type":"array","items":{"type":"string"}}"""));
		Assert.NotNull(error);
		Assert.Contains("root type", error);
	}

	[Fact]
	public void Value_ReportsMissingRequiredAndWrongTypes()
	{
		var missing = SubagentSchema.ValidateValue(Value("""{"status":"ok"}"""), ReviewSchema);
		Assert.NotNull(missing);
		Assert.Contains("files", missing);

		var typeSchema = Object("""{"type":"object","properties":{"name":{"type":"string"}}}""");
		var wrong = SubagentSchema.ValidateValue(Value("""{"name":1}"""), typeSchema);
		Assert.NotNull(wrong);
		Assert.Contains("expected string", wrong);

		var fractional = SubagentSchema.ValidateValue(Value("""{"status":"ok","files":[],"score":1.5}"""), ReviewSchema);
		Assert.NotNull(fractional);
		Assert.Contains("expected integer", fractional);
	}

	[Fact]
	public void Value_EnforcesEnumAndAdditionalProperties()
	{
		var badEnum = SubagentSchema.ValidateValue(Value("""{"status":"done","files":[]}"""), ReviewSchema);
		Assert.NotNull(badEnum);
		Assert.Contains("allowed values", badEnum);

		var extra = SubagentSchema.ValidateValue(Value("""{"status":"ok","files":[],"extra":true}"""), ReviewSchema);
		Assert.NotNull(extra);
		Assert.Contains("additional property", extra);

		Assert.Null(SubagentSchema.ValidateValue(Value("""{"status":"ok","files":[],"score":7}"""), ReviewSchema));
	}

	[Fact]
	public void Value_ChecksArrayItems()
	{
		var error = SubagentSchema.ValidateValue(Value("""{"status":"ok","files":["a",2]}"""), ReviewSchema);
		Assert.NotNull(error);
		Assert.Contains("files[1]", error);
	}

	[Fact]
	public void ParseResult_ExtractsALastFencedJsonBlock()
	{
		var (value, error) = SubagentSchema.ParseResult(
			"""
			审阅完成，结论如下：

			```json
			{"status":"ok","files":["src/Auth.cs"]}
			```
			""",
			ReviewSchema);

		Assert.Null(error);
		Assert.NotNull(value);
		Assert.Equal("ok", value!["status"]!.GetValue<string>());
	}

	[Fact]
	public void ParseResult_RejectsAnOversizedStructuredValue()
	{
		var payload = new string('x', SubagentSchema.MaxStructuredChars + 1);
		var (value, error) = SubagentSchema.ParseResult(
			$$"""{"status":"ok","files":["{{payload}}"]}""",
			ReviewSchema);

		Assert.Null(value);
		Assert.NotNull(error);
		Assert.Contains("too large", error);
	}

	[Fact]
	public void ParseResult_RejectsProseWithoutJson()
	{
		var (value, error) = SubagentSchema.ParseResult("看完了，没有问题。", ReviewSchema);
		Assert.Null(value);
		Assert.NotNull(error);
		Assert.Contains("no JSON", error);
	}
}
