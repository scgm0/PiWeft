using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 快捷消息弹层的内容：搜索过滤、翻页、行内两个动作（插入草稿 / 直发）与空态。
/// 原生 Popup 的开关与定位由框架负责，这里只测内容与回调。
[Collection("mewui-controls")]
public sealed class ComposerQuickMessagesTests
{
	[Fact]
	public void EnterInsertsTheFirstMatch()
	{
		string? inserted = null;
		string? sent = null;
		var content = new ComposerQuickMessages().BuildForTests(
			["继续", "提交推送"], canSend: true, text => inserted = text, text => sent = text, () => { });

		var query = Find<TextBox>(content).Single();
		query.Text = "推送";
		Press(query, Key.Enter);

		Assert.Equal("提交推送", inserted);
		Assert.Null(sent);
	}

	[Fact]
	public void ClickingARowInsertsTheOriginalEntryAndTheTrailingButtonSendsIt()
	{
		string? inserted = null;
		string? sent = null;
		var actions = new List<string>();
		var content = new ComposerQuickMessages().BuildForTests(
			["继续", "提交推送"], canSend: true,
			text => { inserted = text; actions.Add("insert"); },
			text => { sent = text; actions.Add("send"); },
			() => actions.Add("manage"));

		ImagePreviewTests.Click(RowFor(content, "继续"));
		Assert.Equal("继续", inserted);
		Assert.Null(sent);

		ImagePreviewTests.Click(SendFor(content, "提交推送"));
		Assert.Equal("提交推送", sent);
		Assert.Equal(["insert", "send"], actions);
	}

	[Fact]
	public void DirectSendIsDisabledWhileStreamingOrWithAttachments()
	{
		var content = new ComposerQuickMessages().BuildForTests(
			["继续"], canSend: false, _ => { }, _ => { }, () => { });

		var send = SendFor(content, "继续");
		Assert.False(send.IsEnabled);
		Assert.Equal(Strings.QuickMessagesSendUnavailable, HoverHints.Text(send));

		// 插入草稿不受直发可用性影响。
		Assert.True(RowFor(content, "继续").IsEnabled);
	}

	[Fact]
	public void PagesShowEightItemsAndClampWhenTheFilterShrinks()
	{
		var overlay = new ComposerQuickMessages();
		var content = overlay.BuildForTests(
			[.. Enumerable.Range(1, 10).Select(index => $"条目 {index}")], canSend: true,
			_ => { }, _ => { }, () => { });

		Assert.Equal(8, overlay.Matches.Count);
		Assert.Equal(1, overlay.Page);

		ImagePreviewTests.Click(PageButton(content, "2"));
		Assert.Equal(2, overlay.Page);
		Assert.Equal(["条目 9", "条目 10"], overlay.Matches);

		// 搜索把结果收窄到一页：页码自动夹紧，不留在不存在的第 2 页。
		var query = Find<TextBox>(content).Single();
		query.Text = "条目 1";
		Assert.Equal(1, overlay.Page);
		Assert.Equal(["条目 1", "条目 10"], overlay.Matches);
	}

	[Fact]
	public void RowsAndPageButtonsAreReusedWhenTheVisiblePageDoesNotChange()
	{
		var items = Enumerable.Range(1, 10).Select(index => $"review {index:00}").ToArray();
		var overlay = new ComposerQuickMessages();
		var content = overlay.BuildForTests(items, canSend: true, _ => { }, _ => { }, () => { });

		var row = RowFor(content, "review 01");
		var query = Find<TextBox>(content).Single();
		query.Text = "review";

		// 命中集合没变时不能重建行：连续输入时每一击都重造图标与按钮是白付的。
		Assert.Same(row, RowFor(content, "review 01"));

		// 翻页只重建列表、不重建页码按钮：在按钮自己的点击回调里把它摘掉会丢掉这次点击。
		query.Text = string.Empty;
		var pageTwo = PageButton(content, "2");
		ImagePreviewTests.Click(pageTwo);
		Assert.Same(pageTwo, PageButton(content, "2"));
	}

	[Fact]
	public void EmptyStateSaysWhereToAddEntriesAndNoMatchIsVisible()
	{
		var emptyContent = new ComposerQuickMessages().BuildForTests(
			[], canSend: true, _ => { }, _ => { }, () => { });
		Assert.Contains(Find<TextBlock>(emptyContent), block => block.Text == Strings.QuickMessagesEmpty);

		var overlay = new ComposerQuickMessages();
		var content = overlay.BuildForTests(["继续"], canSend: true, _ => { }, _ => { }, () => { });
		var query = Find<TextBox>(content).Single();
		query.Text = "missing";
		Assert.Empty(overlay.Matches);
		Assert.Contains(Find<TextBlock>(content), block => block.IsVisible && block.Text == Strings.QuickMessagesNoMatch);
	}

	private static Button RowFor(UIElement content, string item) =>
		Find<Button>(content).Single(button =>
			button.Content is TextBlock label && label.Text == item);

	private static Button SendFor(UIElement content, string item)
	{
		var grid = Assert.IsType<Grid>(RowFor(content, item).Parent);
		return Assert.IsType<Button>(grid.Children[1]);
	}

	private static Button PageButton(UIElement content, string page) =>
		Find<Button>(content).Single(button =>
			button.Content is TextBlock label && label.Text == page);

	private static void Press(UIElement element, Key key) =>
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(element, [new KeyEventArgs(key, 0)]);
}
