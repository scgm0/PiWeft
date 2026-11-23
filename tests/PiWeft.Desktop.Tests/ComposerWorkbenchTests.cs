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
			new WorkbenchSection("todo", "待办", card.View, () => shown, () => "9 完成",
				() => card.IsExpanded = true),
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
		Assert.Null(workbench.SelectedKeyForTests);
		Assert.False(card.IsExpanded);
	}

	[Fact]
	public void SelectingAChipExpandsTheSectionBody()
	{
		var (workbench, card) = Build();
		workbench.Refresh();
		workbench.SelectForTests("todo");

		Assert.True(workbench.IsExpanded);
		Assert.Equal("todo", workbench.SelectedKeyForTests);
		Assert.True(card.IsExpanded);
	}

	[Fact]
	public void TitleAreaCollapsesAndClearsTheSelection()
	{
		var (workbench, card) = Build();
		workbench.Refresh();
		ImagePreviewTests.Click(workbench.TitleButtonForTests);
		Assert.False(workbench.IsExpanded);
		Assert.Null(workbench.SelectedKeyForTests);

		workbench.SelectForTests("todo");
		Assert.True(workbench.IsExpanded);
		Assert.True(card.IsExpanded);

		ImagePreviewTests.Click(workbench.TitleButtonForTests);
		Assert.False(workbench.IsExpanded);
		Assert.Null(workbench.SelectedKeyForTests);
	}

	[Fact]
	public void SelectingAChipKeepsTheButtonInstance()
	{
		var (workbench, _) = Build();
		workbench.Refresh();
		var before = workbench.ChipButtonForTests("todo");
		workbench.SelectForTests("todo");
		Assert.Same(before, workbench.ChipButtonForTests("todo"));
	}

	[Fact]
	public void ClickingTheSelectedChipCollapsesAndClears()
	{
		var (workbench, _) = Build();
		workbench.Refresh();
		workbench.SelectForTests("todo");
		workbench.SelectForTests("todo");

		Assert.False(workbench.IsExpanded);
		Assert.Null(workbench.SelectedKeyForTests);
	}

	[Fact]
	public void HiddenSectionsHideTheWholeWorkbench()
	{
		var (workbench, _) = Build(shown: false);
		workbench.Refresh();

		Assert.False(workbench.IsShown);
	}
}
