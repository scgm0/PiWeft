using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class SearchTextTests
{
	[Theory]
	[InlineData("gpt-4o", "gpt4o")]
	[InlineData("GPT 4O", "gpt4o")]
	[InlineData("openai/gpt-4o", "openaigpt4o")]
	[InlineData("通义千问·Max", "通义千问max")]
	[InlineData("  --  ", "")]
	public void NormalizeKeepsOnlyLettersAndDigits(string value, string expected) =>
		Assert.Equal(expected, SearchText.Normalize(value));

	[Fact]
	public void WordsSplitsOnAnyWhitespaceAndNormalizesEachWord()
	{
		Assert.Empty(SearchText.Words(null));
		Assert.Empty(SearchText.Words("   "));
		Assert.Empty(SearchText.Words(" . "));
		Assert.Equal(["gpt4o", "reasoning"], SearchText.Words(" GPT-4o\t Reasoning "));
		Assert.Equal(["a", "b", "c"], SearchText.Words("a\n\nb  c"));
	}

	[Fact]
	public void EveryWordMustAppearInTheHaystack()
	{
		var haystack = SearchText.Normalize("local-openai local-code Local Code 128k 图片 推理");

		Assert.True(SearchText.ContainsAll(haystack, SearchText.Words("local code")));
		Assert.True(SearchText.ContainsAll(haystack, SearchText.Words("图片")));
		Assert.True(SearchText.ContainsAll(haystack, SearchText.Words("LOCAL-CODE")));
		Assert.False(SearchText.ContainsAll(haystack, SearchText.Words("local missing")));
		// 空查询不设限：调用方靠这个跳过整轮过滤。
		Assert.True(SearchText.ContainsAll(haystack, SearchText.Words(" ")));
	}
}
