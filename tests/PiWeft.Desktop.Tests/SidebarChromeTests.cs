using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class SidebarChromeTests
{
	[Fact]
	public void NoticeShowsOnlyWhileItHasTextAndCanBeDismissed()
	{
		var notice = new SidebarNotice();
		Assert.False(notice.View.IsVisible);

		notice.Text("模型目录已更新。");
		Assert.True(notice.View.IsVisible);

		var close = Find<Button>(notice.View).Single();
		ImagePreviewTests.Click(close);
		Assert.False(notice.View.IsVisible);

		// 下一条提示要能重新占位：关闭只收起当前这一条。
		notice.Text("配置已应用。");
		Assert.True(notice.View.IsVisible);
	}

	[Fact]
	public void EmptyHintKeepsTitleAndHintOnSeparateLines()
	{
		var hint = new SidebarEmptyHint(Strings.ChatsEmptyTitle, Strings.ChatsEmptyHint, "message-square");
		hint.Show(Strings.ChatsEmptyTitle, Strings.ChatsEmptyHint);
		var texts = Find<TextBlock>(hint.View).Select(static block => block.Text).ToArray();

		Assert.Contains(Strings.ChatsEmptyTitle, texts);
		Assert.Contains(Strings.ChatsEmptyHint, texts);
		// 说明里带硬换行：窄侧栏折行时不会把逗号留在下一行行首。
		Assert.Contains('\n', Strings.ChatsEmptyHint);
	}

	[Fact]
	public void EmptyHintUsesTheWholeSidebarWidth()
	{
		var hint = new SidebarEmptyHint(Strings.ChatsEmptyTitle, Strings.ChatsEmptyHint, "message-square");
		hint.Show(Strings.ChatsEmptyTitle, Strings.ChatsEmptyHint);
		var grid = new Grid().Children(hint.View);
		grid.Measure(new Size(280, 600));
		grid.Arrange(new Rect(0, 0, 280, 600));
		var blocks = Find<TextBlock>(hint.View).ToArray();
		foreach (var block in blocks) Console.WriteLine($"text=[{block.Text}] bounds={block.Bounds} desired={block.DesiredSize}");
		Assert.True(blocks[^1].Bounds.Width > 200, $"hint width {blocks[^1].Bounds.Width}");
	}

	[Fact]
	public void UnnamedTextInputsUseTheQuietFieldSurface()
	{
		var box = new TextBox();
		var root = new Border().StyleSheet(AppStyles.Create()).Child(box);
		box.Measure(new Size(200, 40));

		// 设置/供应商表单里的输入框靠「内嵌表面 + 发丝线」区分，而不是框架默认的亮描边。
		Assert.Equal(AppTheme.CardBackgroundAlt, box.Background);
		Assert.Equal(AppTheme.Border, box.BorderBrush);
		Assert.Equal(AppTheme.FontSizeCaption, box.FontSize, 0.5);
		// 聚焦只提亮描边，别让骨架默认的亮框在未聚焦时就出现。
		var style = AppStyles.Create().GetByType(typeof(TextBox));
		Assert.NotNull(style);
		Assert.Contains(style!.Triggers, trigger => trigger.Matches(VisualStateFlags.Focused | VisualStateFlags.Enabled));
		GC.KeepAlive(root);
	}

	[Theory]
	[InlineData(AppStyles.CardButton)]
	[InlineData(AppStyles.CardMutedButton)]
	[InlineData(AppStyles.SelectedButton)]
	[InlineData(AppStyles.GhostButton)]
	[InlineData(AppStyles.PrimaryButton)]
	[InlineData(AppStyles.SegmentButton)]
	[InlineData(AppStyles.SegmentButtonActive)]
	[InlineData(AppStyles.TitleBarTab)]
	[InlineData(AppStyles.TitleBarTabActive)]
	[InlineData(AppStyles.TitleBarIconButton)]
	[InlineData(AppStyles.TitleBarIconButtonActive)]
	[InlineData(AppStyles.TitleBarButton)]
	[InlineData(AppStyles.TitleBarCloseButton)]
	[InlineData(AppStyles.TitleBarTabClose)]
	[InlineData(AppStyles.ChangedFileChip)]
	[InlineData(AppStyles.ChangedFilesMore)]
	[InlineData(AppStyles.CodeButton)]
	[InlineData(AppStyles.AccentSegmentButton)]
	public void ClickableSurfacesHaveHoverAndFocusFeedback(string styleName)
	{
		var style = AppStyles.Create().Get(styleName);

		Assert.NotNull(style);
		Assert.Contains(style!.Triggers, trigger => trigger.Matches(VisualStateFlags.Hot | VisualStateFlags.Enabled));
		Assert.Contains(style.Triggers, trigger => trigger.Matches(VisualStateFlags.Focused | VisualStateFlags.Enabled));
	}
}
