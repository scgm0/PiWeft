using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.JavaScript.Tests;

public sealed class CodeModeSearchTests
{
	[Fact]
	public void Tokenize_SplitsCamelCaseDropsStopWordsAndStems()
	{
		Assert.Equal(["read", "file"], CodeModeSearch.Tokenize("ReadFile"));
		Assert.Equal(["xml", "http", "request"], CodeModeSearch.Tokenize("XMLHttpRequest"));
		Assert.Equal(["issue", "search"], CodeModeSearch.Tokenize("issues in the searches"));
		Assert.Empty(CodeModeSearch.Tokenize("the and of"));
	}

	[Fact]
	public void CreateDocument_IncludesSchemaDescriptionsAndPropertyNames()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["pattern"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Regular expression to match",
				},
			},
		};

		var document = CodeModeSearch.CreateDocument("grep", "Search file contents", schema);

		Assert.Contains("pattern", document, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("regular expression", document, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Rank_PrefersTheToolWhoseDescriptionMatches()
	{
		var read = CodeModeSearch.CreateDocument("read", "Read file contents", null);
		var grep = CodeModeSearch.CreateDocument("grep", "Search file contents for a pattern", null);
		(string, string)[] documents = [("read", read), ("grep", grep)];

		Assert.Equal(["grep"], CodeModeSearch.Rank("pattern", documents, 8));
		Assert.Equal(["read"], CodeModeSearch.Rank("read", documents, 8));
		// pi 的朴素词干只处理复数，不做英文词形还原。
		Assert.Empty(CodeModeSearch.Rank("reading", documents, 8));
		Assert.Empty(CodeModeSearch.Rank("   ", documents, 8));
	}

	[Fact]
	public void Rank_HonoursTheLimitAndKeepsOrderOnTies()
	{
		var first = CodeModeSearch.CreateDocument("alpha", "file tool", null);
		var second = CodeModeSearch.CreateDocument("beta", "file tool", null);
		(string, string)[] documents = [("alpha", first), ("beta", second)];

		Assert.Equal(["alpha", "beta"], CodeModeSearch.Rank("file", documents, 2));
		Assert.Equal(["alpha"], CodeModeSearch.Rank("file", documents, 1));
	}
}
