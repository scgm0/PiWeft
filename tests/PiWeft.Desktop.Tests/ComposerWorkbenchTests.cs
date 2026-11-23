using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 工作台摘要行：默认收起，选中一节才挂内容；所有节隐藏时整条不占位。
[Collection("mewui-controls")]
public sealed class ComposerWorkbenchTests
{
	private static (ComposerWorkbench Workbench, ComposerWidgetCard Card) Build(bool shown = true)
	{
		var card = new ComposerWidgetCard("list-checks", "待办");
		card.Body.Add(new TextBlock().Text("一行待办"));
		var workbench = new ComposerWorkbench(
		[
			new WorkbenchSection("todo", "待办", card.View, () => shown, () => "9 完成"),
		]);
		return (workbench, card);
	}

	[Fact]
	public void StartsCollapsedAndDoesNotHostTheSection()
	{
		var (workbench, card) = Build();
		workbench.Refresh();

		Assert.True(workbench.IsShown);
		Assert.False(workbench.IsExpanded);
		Assert.False(card.IsExpanded);
	}

	[Fact]
	public void SelectingAChipShowsTheCollapsedCardPreview()
	{
		var (workbench, card) = Build();
		workbench.Refresh();
		workbench.SelectForTests("todo");

		Assert.True(workbench.IsExpanded);
		// 选中给的是预览卡头；卡体仍收起，第二次点击才展开。
		Assert.False(card.IsExpanded);
	}

	[Fact]
	public void HiddenSectionsHideTheWholeWorkbench()
	{
		var (workbench, _) = Build(shown: false);
		workbench.Refresh();

		Assert.False(workbench.IsShown);
	}
}
