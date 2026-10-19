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

	[Fact]
	public void Parse_RejectsEmptyInput()
	{
		var error = Assert.Throws<CodeModeSourceException>(() => CodeModeSource.Parse("   \n"));
		Assert.Contains("non-empty", error.Message);
	}

	[Fact]
	public void Parse_RejectsOptionsLineThatIsNotFirst()
	{
		var error = Assert.Throws<CodeModeSourceException>(() =>
			CodeModeSource.Parse("const a = 1;\n// @options: {\"timeout_ms\": 100}\nreturn a;"));

		Assert.Contains("must be the first line", error.Message);
	}

	[Fact]
	public void Parse_RejectsUnknownOptions()
	{
		var error = Assert.Throws<CodeModeSourceException>(() =>
			CodeModeSource.Parse("// @options: {\"max_tokens\": 10}\nreturn 1;"));

		Assert.Contains("max_tokens", error.Message);
	}

	[Fact]
	public void Parse_RejectsNegativeTokenBudget()
	{
		var error = Assert.Throws<CodeModeSourceException>(() =>
			CodeModeSource.Parse("// @options: {\"max_output_tokens\": -1}\nreturn 1;"));

		Assert.Contains("non-negative", error.Message);
	}

	[Fact]
	public void Parse_RejectsZeroTimeout()
	{
		var error = Assert.Throws<CodeModeSourceException>(() =>
			CodeModeSource.Parse("// @options: {\"timeout_ms\": 0}\nreturn 1;"));

		Assert.Contains("positive integer", error.Message);
	}

	[Fact]
	public void Parse_RejectsOptionsWithoutCode()
	{
		var error = Assert.Throws<CodeModeSourceException>(() =>
			CodeModeSource.Parse("// @options: {\"timeout_ms\": 100}"));

		Assert.Contains("must be followed by JavaScript", error.Message);
	}
}
