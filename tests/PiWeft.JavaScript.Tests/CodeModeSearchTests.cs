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
	public void Tokenize_KeepsCjkTextSoChineseQueriesCanMatch()
	{
		// pi 用 Intl.Segmenter 按词切；进程内用「单字 + 二元组」兜底，
		// 至少让「截图」这种查询与「截取屏幕截图」共享一个二元组。
		var terms = CodeModeSearch.Tokenize("截取屏幕截图");

		Assert.Contains("截图", terms);
		Assert.Contains("截", terms);
	}

	[Fact]
	public void Rank_FindsToolsDescribedInChinese()
	{
		var screenshot = CodeModeSearch.CreateToolSearchDocument("screenshot", "截取当前屏幕截图", null);
		var read = CodeModeSearch.CreateToolSearchDocument("read", "Read file contents", null);

		var matches = CodeModeSearch.Rank("截图", [screenshot, read], 8);

		Assert.Equal(["screenshot"], matches);
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
	public void Rank_PrefersNameMatchesAndExactLocalNames()
	{
		var byName = CodeModeSearch.CreateToolSearchDocument("window_screenshot", "capture", null);
		var byText = CodeModeSearch.CreateToolSearchDocument(
			"capture",
			"window window window window window window",
			null);

		Assert.Equal(["window_screenshot"], CodeModeSearch.Rank("window", [byName, byText], 1));

		var exact = CodeModeSearch.CreateToolSearchDocument("mcp__app__thread_read", "read a thread", null);
		var other = CodeModeSearch.CreateToolSearchDocument("other", "thread read tool", null);
		Assert.Equal(["mcp__app__thread_read"], CodeModeSearch.Rank("thread_read", [other, exact], 1));
	}

	[Fact]
	public void Rank_PrefersCoverageBeforeRawScore()
	{
		var both = CodeModeSearch.CreateToolSearchDocument("a", "screenshot window", null);
		var repeated = CodeModeSearch.CreateToolSearchDocument("b", "window window window window window", null);

		Assert.Equal(["a"], CodeModeSearch.Rank("screenshot window", [repeated, both], 1));
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
