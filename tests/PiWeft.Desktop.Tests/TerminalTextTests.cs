using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>终端码点宽度：一个簇占几格决定列号，错一格整行就偏。</summary>
public sealed class TerminalTextTests
{
	[Theory]
	[InlineData("a", 1)]
	[InlineData("你", 2)]
	[InlineData("e\u0301", 1)]
	[InlineData("\U0001F600", 2)]
	[InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467", 2)]
	[InlineData("\U0001F1E8\U0001F1F3", 2)]
	[InlineData("\U0001F44D\U0001F3FD", 2)]
	[InlineData("\u1112\u1161\u11AB", 2)]
	[InlineData("\u0301", 0)]
	// 键帽：字体上画成两格宽的 emoji，但 wcwidth 只算一格，宽度跟随 wcwidth 才和 shell 的列号一致。
	[InlineData("1\uFE0F\u20E3", 1)]
	public void ClusterWidthMatchesTheCellCount(string cluster, int expected) =>
		Assert.Equal(expected, TerminalText.ClusterWidth(cluster));

	[Fact]
	public void IsExtendCoversExactlyTheZeroWidthCodeUnits()
	{
		Assert.True(TerminalText.IsExtend(0x0301));  // 组合重音
		Assert.True(TerminalText.IsExtend(0x200D));  // ZWJ
		Assert.True(TerminalText.IsExtend(0xFE0F));  // emoji 变体选择符
		Assert.True(TerminalText.IsExtend(0x1F3FD)); // 肤色修饰
		Assert.True(TerminalText.IsExtend(0x1161));  // Hangul 中声
		Assert.False(TerminalText.IsExtend('a'));
		Assert.False(TerminalText.IsExtend('你'));
		Assert.False(TerminalText.IsExtend(0x1F600));
	}

	[Fact]
	public void IsWideCoversEastAsianAndEmoji()
	{
		Assert.True(TerminalText.IsWide('你'));
		Assert.True(TerminalText.IsWide(0x1F600));
		Assert.True(TerminalText.IsWide(0x1F1E8));
		Assert.True(TerminalText.IsWide(0x20000));
		Assert.False(TerminalText.IsWide('a'));
		Assert.False(TerminalText.IsWide(0x0301));
		Assert.False(TerminalText.IsWide(0x200D));
	}
}
