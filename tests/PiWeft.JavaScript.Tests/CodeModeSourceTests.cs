using Xunit;

namespace PiWeft.JavaScript.Tests;

public sealed class CodeModeSourceTests
{
	[Fact]
	public void Parse_PlainSourceIsUnchanged()
	{
		var parsed = CodeModeSource.Parse("return 1;");

		Assert.Equal("return 1;", parsed.Code);
		Assert.Null(parsed.Options.MaxOutputTokens);
		Assert.Null(parsed.Options.TimeoutMs);
	}

	[Fact]
	public void Parse_OptionsLineIsRemovedButLineNumbersStay()
	{
		var parsed = CodeModeSource.Parse("// @options: {\"max_output_tokens\": 2000, \"timeout_ms\": 30000}\nreturn 1;");

		Assert.Equal("\nreturn 1;", parsed.Code);
		Assert.Equal(2000, parsed.Options.MaxOutputTokens);
		Assert.Equal(30000, parsed.Options.TimeoutMs);
	}

	[Theory]
	[InlineData("   \n", "non-empty")]
	[InlineData("const a = 1;\n// @options: {\"timeout_ms\": 100}\nreturn a;", "must be the first line")]
	[InlineData("// @options: {\"max_tokens\": 10}\nreturn 1;", "max_tokens")]
	[InlineData("// @options: {\"max_output_tokens\": -1}\nreturn 1;", "non-negative")]
	[InlineData("// @options: {\"timeout_ms\": 0}\nreturn 1;", "positive integer")]
	[InlineData("// @options: {\"timeout_ms\": 100}", "must be followed by JavaScript")]
	public void Parse_RejectsInvalidSourceOrOptions(string source, string expectedError)
	{
		var error = Assert.Throws<CodeModeSourceException>(() => CodeModeSource.Parse(source));
		Assert.Contains(expectedError, error.Message);
	}
}
