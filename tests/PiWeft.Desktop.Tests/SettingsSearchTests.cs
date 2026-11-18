using Xunit;

namespace PiWeft.Desktop.Tests;

/// 设置搜索的打分内核：分区入口要能按名字找到，具体设置按标题、别名与分区名匹配，
/// 多词查询逐词 AND，排序稳定到「先分区、后设置」。
public sealed class SettingsSearchTests
{
	[Fact]
	public void EmptyQueryReturnsNoHits()
	{
		Assert.Empty(SettingsSearch.Match(null));
		Assert.Empty(SettingsSearch.Match("   "));
	}

	[Fact]
	public void TitleFindsSectionPage()
	{
		var hits = SettingsSearch.Match(Strings.ConfigAppearance);

		Assert.NotEmpty(hits);
		Assert.Equal("section:appearance", hits[0].Entry.Id);
	}

	[Fact]
	public void ExactSectionNameRanksBeforeSameNamedSetting()
	{
		// 「模型」既是配置管理分区，也是 Git 摘要里的一个字段名。分区应该排在前面。
		var hits = SettingsSearch.Match(Strings.ConfigModels);

		Assert.Equal("section:models", hits[0].Entry.Id);
		Assert.True(hits[0].Entry.Config);
	}

	[Fact]
	public void SectionPrefixRanksBeforeKeywordItems()
	{
		// 「MCP」是分区名的前缀，不是别名的完全命中：先给分区页，再给概览里的 MCP 状态行。
		var hits = SettingsSearch.Match("MCP");

		Assert.Equal("section:mcp", hits[0].Entry.Id);
	}

	[Fact]
	public void KeywordsFindSetting()
	{
		var hits = SettingsSearch.Match("字号");

		Assert.Contains(hits, hit => hit.Entry.Id == "appearance.font");
	}

	[Fact]
	public void SectionNameMatchesAllItsSettings()
	{
		var hits = SettingsSearch.Match("外观");

		Assert.Contains(hits, hit => hit.Entry.Id == "appearance.theme");
		Assert.Contains(hits, hit => hit.Entry.Id == "appearance.font");
		Assert.Equal("section:appearance", hits[0].Entry.Id);
	}

	[Fact]
	public void TokensAreAnded()
	{
		var hits = SettingsSearch.Match("阅读 宽度");

		Assert.Contains(hits, hit => hit.Entry.Id == "appearance.width");
		Assert.Empty(SettingsSearch.Match("阅读 不存在的词"));
	}

	[Fact]
	public void EnglishKeywordFindsSection()
	{
		var byEnglish = SettingsSearch.Match("shortcut");
		var byShortcut = SettingsSearch.Match("快捷键");

		Assert.Contains(byEnglish, hit => hit.Entry.Section == "shortcuts");
		Assert.Contains(byShortcut, hit => hit.Entry.Id == "section:shortcuts");
	}

	[Fact]
	public void ConfigEntriesAreFlagged()
	{
		var config = SettingsSearch.Match("认证").First(hit => hit.Entry.Id == "section:auth");
		var system = SettingsSearch.Match("日志").First(hit => hit.Entry.Id == "section:logs");

		Assert.True(config.Entry.Config);
		Assert.False(system.Entry.Config);
	}

	[Fact]
	public void HighlightIndicesPointIntoTitle()
	{
		var hit = SettingsSearch.Match("模型").First(h => h.Entry.Id == "section:models");

		Assert.Equal(Enumerable.Range(0, 2), hit.TitleIndices);
		Assert.Equal([("模型", true)], SettingsSearch.Segments(hit.Entry.Title, hit.TitleIndices));
	}

	[Fact]
	public void SegmentsSplitAroundMatches()
	{
		var segments = SettingsSearch.Segments("提交摘要模型", [4, 5]);

		Assert.Equal([("提交摘要", false), ("模型", true)], segments);
	}

	[Fact]
	public void SegmentsHighlightNonContiguousRanges()
	{
		var segments = SettingsSearch.Segments("abcdef", [0, 2, 3]);

		Assert.Equal([("a", true), ("b", false), ("cd", true), ("ef", false)], segments);
	}

	[Fact]
	public void SegmentsIgnoresOutOfRangeIndices()
	{
		var segments = SettingsSearch.Segments("abc", [0, 99]);

		Assert.Equal([("a", true), ("bc", false)], segments);
	}

	[Fact]
	public void MultiTokenIndicesAreSortedAndUnique()
	{
		var hit = SettingsSearch.Match("阅读 宽度").First(h => h.Entry.Id == "appearance.width");

		Assert.Equal([0, 1, 4, 5], hit.TitleIndices);
	}

	[Fact]
	public void GroupLabelsFindSectionsInThatGroup()
	{
		var runtime = SettingsSearch.Match(Strings.SettingsGroupRuntime);
		Assert.Contains(runtime, hit => hit.Entry.Id == "section:extensions");
		Assert.Contains(runtime, hit => hit.Entry.Id == "section:shortcuts");

		var config = SettingsSearch.Match(Strings.ConfigurationGroup);
		Assert.Contains(config, hit => hit.Entry.Id == "section:auth");
	}

	[Fact]
	public void LimitCapsResults()
	{
		Assert.Equal(3, SettingsSearch.Match("模型", limit: 3).Count);
	}

	[Fact]
	public void EveryEntryHasKnownSectionAndUniqueId()
	{
		var ids = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in SettingsSearch.Entries)
		{
			Assert.NotNull(SettingsSections.Find(entry.Section));
			Assert.True(ids.Add(entry.Id), "重复的搜索条目：" + entry.Id);
			Assert.False(string.IsNullOrWhiteSpace(entry.Title));
		}
	}

	[Fact]
	public void SectionCatalogCoversBothModes()
	{
		Assert.Contains(SettingsSections.All, section => section.Id == "overview" && !section.Config);
		Assert.Contains(SettingsSections.All, section => section.Id == "models" && section.Config);
		Assert.Equal("models", SettingsSections.SectionOf(ConfigFile.Models));
		Assert.Equal("source", SettingsSections.SectionOf(ConfigFile.Usage));
	}
}
