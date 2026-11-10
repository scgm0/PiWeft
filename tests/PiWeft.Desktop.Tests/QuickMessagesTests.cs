using Xunit;

namespace PiWeft.Desktop.Tests;

/// 快捷消息清单：清洗、插入草稿、搜索与分页都是纯函数，边界在这里钉死。
public sealed class QuickMessagesTests
{
	[Fact]
	public void NormalizeTrimsDropsBlankLinesAndDedupesCaseInsensitively()
	{
		Assert.Equal(["继续", "提交", "Review"], QuickMessages.Normalize(["  继续 ", "", "   ", "提交", "继续", "Review", "review"]));
		Assert.Empty(QuickMessages.Normalize(null));
	}

	[Fact]
	public void NormalizeClipsLongEntriesAndCapsTheList()
	{
		var longEntry = QuickMessages.Normalize([new string('字', QuickMessages.MaxLength + 50)]).Single();
		Assert.Equal(QuickMessages.MaxLength, longEntry.Length);

		// 截断而不是丢弃：用户写的长句保留前缀，只是裁到界面能接受的量级。
		Assert.All(longEntry, character => Assert.Equal('字', character));

		var many = QuickMessages.Normalize(Enumerable.Range(0, QuickMessages.MaxItems + 5).Select(index => $"条目 {index}"));
		Assert.Equal(QuickMessages.MaxItems, many.Count);
	}

	[Fact]
	public void ParseLinesAndToLinesRoundTripTheEditorFormat()
	{
		var items = QuickMessages.ParseLines("继续\r\n\r\n提交\n继续\n");
		Assert.Equal(["继续", "提交"], items);
		Assert.Equal("继续\n提交", QuickMessages.ToLines(items));
	}

	[Fact]
	public void DefaultsKeepTheShippedOrder()
	{
		Assert.Equal(20, QuickMessages.Defaults.Count);
		Assert.Equal(QuickMessages.Defaults.Count, QuickMessages.Normalize(QuickMessages.Defaults).Count);
		Assert.Equal("你好，你是什么模型", QuickMessages.Defaults[0]);
	}

	[Theory]
	[InlineData("", "继续", "继续")]
	[InlineData("先看一下", "继续", "先看一下\n继续")]
	[InlineData("先看一下  \n", "继续", "先看一下\n继续")]
	public void AppendKeepsTheDraftAndAddsANewLine(string draft, string item, string expected) =>
		Assert.Equal(expected, QuickMessages.Append(draft, item));

	[Fact]
	public void FilterIsCaseInsensitiveAndBlankQueryKeepsTheSnapshot()
	{
		string[] items = ["继续", "提交推送", "review 一下这个 commit"];

		Assert.Same(items, QuickMessages.Filter(items, "  "));
		// 全部命中时原样返回，不复制一份。
		string[] allMatch = ["abc", "abd"];
		Assert.Same(allMatch, QuickMessages.Filter(allMatch, "ab"));
		Assert.Equal(["提交推送"], QuickMessages.Filter(items, "推送"));
		Assert.Equal(["review 一下这个 commit"], QuickMessages.Filter(items, "REVIEW"));
		Assert.Empty(QuickMessages.Filter(items, "missing"));
	}

	[Fact]
	public void PaginateClampsOutOfRangePagesAndTreatsEmptyAsOnePage()
	{
		var items = Enumerable.Range(1, 10).Select(index => $"条目 {index}").ToArray();

		var first = QuickMessages.Paginate(items, 1);
		Assert.Equal(8, first.Items.Count);
		// 整份清单都在一页时复用原列表。
		Assert.Same(items, QuickMessages.Paginate(items, 1, pageSize: 10).Items);
		Assert.Equal(2, first.TotalPages);
		Assert.Equal("条目 1", first.Items[0]);

		var second = QuickMessages.Paginate(items, 2);
		Assert.Equal(["条目 9", "条目 10"], second.Items);
		Assert.Equal(2, second.Page);

		// 页码越界是正常操作（搜完清空、设置里删条）导致的，夹紧而不是返回空页。
		Assert.Equal(2, QuickMessages.Paginate(items, 99).Page);
		Assert.Equal(1, QuickMessages.Paginate(items, 0).Page);
		Assert.Equal("条目 1", QuickMessages.Paginate(items, 0).Items[0]);

		var empty = QuickMessages.Paginate([], 3);
		Assert.Empty(empty.Items);
		Assert.Equal(1, empty.Page);
		Assert.Equal(1, empty.TotalPages);
	}
}
