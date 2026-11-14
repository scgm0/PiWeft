using System.Text;
using PiWeft.Markdown;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 流式追加的文档簿记：每帧开销不随已闭合块数增长。
[Collection("mewui-controls")]
public sealed class MarkdownAppendCostTests
{
	private const string ClosedParagraph = "A closed paragraph with **bold** and `code`.\n\n";

	[Fact]
	public void TextUnitIndicesSurviveIncrementalAppends()
	{
		var builder = new StringBuilder();
		MarkdownDocument? document = null;
		for (var index = 0; index < 12; index++)
		{
			builder.Append(ClosedParagraph);
			document = MarkdownParser.Parse(builder, previous: document, streaming: true);
		}

		Assert.NotNull(document);
		// 块自己记下标：每块一个文本单元，增量追加不能让它偏移。
		for (var index = 0; index < document.TextUnits.Count; index++)
		{
			var unit = document.TextUnits[index];
			Assert.Equal(index, document.GetTextUnit(unit.Block));
			Assert.Equal(unit.Block.Text, unit.Text);
		}
	}

	[Fact]
	public void AnchorTableIsSharedUntilTheTailAddsOne()
	{
		var builder = new StringBuilder("first paragraph\n\nsecond paragraph\n\n");
		var first = MarkdownParser.Parse(builder, streaming: true);
		builder.Append(ClosedParagraph);
		var second = MarkdownParser.Parse(builder, previous: first, streaming: true);

		// 尾块没带锚点：整张表按引用共享，不逐帧复制。
		Assert.Same(first.AnchorBlocks, second.AnchorBlocks);

		builder.Append("## A heading\n\n");
		var third = MarkdownParser.Parse(builder, previous: second, streaming: true);
		Assert.NotSame(second.AnchorBlocks, third.AnchorBlocks);
		Assert.True(third.AnchorBlocks.ContainsKey("a-heading"));

		// 复制后的表与从头解析一致。
		var fresh = MarkdownParser.Parse(builder.ToString());
		Assert.Equal(fresh.AnchorBlocks.Count, third.AnchorBlocks.Count);
		foreach (var (name, topIndex) in fresh.AnchorBlocks)
		{
			Assert.Equal(topIndex, third.AnchorBlocks[name]);
		}
	}

	[Fact]
	public void AppendingClosedBlocksDoesNotCopyTheWholeDocumentPerFrame()
	{
		var builder = new StringBuilder();
		MarkdownDocument? document = null;
		long early = 0;
		long late = 0;
		for (var index = 0; index < 400; index++)
		{
			builder.Append(ClosedParagraph);
			var before = GC.GetAllocatedBytesForCurrentThread();
			document = MarkdownParser.Parse(builder, previous: document, streaming: true);
			var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			if (index == 100)
			{
				early = allocated;
			}
			if (index == 399)
			{
				late = allocated;
			}
		}

		Assert.NotNull(document);
		Assert.True(document.Blocks.Count > 300, $"blocks={document.Blocks.Count}");
		// 块数翻两番，单帧分配不跟着翻：剩下的只有文本单元表与前缀数组的线性复制（每块几十字节）。
		Assert.True(late < early * 3, $"early={early:N0} B late={late:N0} B");
	}
}
