using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 跨会话占用收进工作台后：卡头给数量预览，卡体列出全部会话。
[Collection("mewui-controls")]
public sealed class WorkspaceNoticeTests
{
	[Fact]
	public void MultipleOtherSessionsStayInTheList()
	{
		var view = new WorkspaceNoticeView();
		view.Set(
		[
			new WorkspaceNoticeEntry("a", WorkspaceNoticeKind.Running, "对比 A"),
			new WorkspaceNoticeEntry("b", WorkspaceNoticeKind.Changed, "排查 B", ChangedFiles: 3),
		], _ => { });

		Assert.True(view.IsShown);
		Assert.Equal(2, view.Count);
		Assert.False(view.IsExpanded);
		Assert.Contains("2 个会话", view.Meta);
		Assert.Contains("1 个正在运行", view.Meta);
		Assert.Contains("1 个改过文件", view.Meta);
		Assert.DoesNotContain("「对比 A」", view.Meta);

		view.ExpandForTests();
		ConversationPerformanceTests.Layout(view.View, 900, 420);
		var blocks = Find<TextBlock>(view.View);
		Assert.Contains(blocks, block => block.Text.Contains("「对比 A」", StringComparison.Ordinal));
		Assert.Contains(blocks, block => block.Text.Contains("「排查 B」", StringComparison.Ordinal));
	}

	[Fact]
	public void EmptyEntriesHideTheSection()
	{
		var view = new WorkspaceNoticeView();
		view.Set([], _ => { });

		Assert.False(view.IsShown);
		Assert.Equal(0, view.Count);
		Assert.Equal(string.Empty, view.Meta);
	}
}
