using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 技能与提示词选择器：一次打开取一份快照，搜索只读快照；选中只回调插入命令。
[Collection("mewui-controls")]
public sealed class ComposerPickerTests
{
	private static ComposerPicker.Item Item(string name, string description = "", string origin = "项目") =>
		new("sparkles", name, name, description, origin);

	[Fact]
	public void FilterMatchesNameDescriptionAndOrigin()
	{
		var items = new[]
		{
			Item("deploy", "把构建发布到测试环境", "项目"),
			Item("review", "只读审查改动", "用户"),
		};

		Assert.Same(items, ComposerPicker.Filter(items, "  "));
		Assert.Equal(["deploy"], ComposerPicker.Filter(items, "发布").Select(item => item.Name));
		Assert.Equal(["review"], ComposerPicker.Filter(items, "用户").Select(item => item.Name));
		Assert.Equal(["deploy"], ComposerPicker.Filter(items, "/deploy").Select(item => item.Name));
		Assert.Empty(ComposerPicker.Filter(items, "missing"));
	}

	[Theory]
	[InlineData("", "skill:deploy", "/skill:deploy ")]
	[InlineData("先看一下", "skill:deploy", "先看一下 /skill:deploy ")]
	[InlineData("/", "skill:deploy", "/skill:deploy ")]
	[InlineData("先看一下 /sk", "skill:deploy", "先看一下 /skill:deploy ")]
	[InlineData("先看一下\n", "deploy", "先看一下 /deploy ")]
	public void AppendCommandKeepsTheDraftAndReplacesAnUnfinishedSlashWord(string draft, string token, string expected) =>
		Assert.Equal(expected, ComposerPicker.AppendCommand(draft, token));

	[Fact]
	public void EnterPicksTheFirstMatchAndClosesTheOverlay()
	{
		var deploy = Item("deploy", "把构建发布到测试环境");
		var review = Item("review", "只读审查改动", "用户");
		var picker = new ComposerPicker();
		var window = new Window();
		ComposerPicker.Item? picked = null;
		picker.Show(window, Strings.SkillPickerTitle, Strings.SkillPickerPlaceholder,
			Strings.SkillPickerEmpty, Strings.SkillPickerNoMatch, [deploy, review], item => picked = item);

		Assert.True(picker.IsOpen);
		Assert.Same(window, picker.View.Parent);
		Assert.Same(deploy, picker.Matches[0]);

		var query = Find<TextBox>(picker.View).Single();
		query.Text = "审查";
		Assert.Equal([review], picker.Matches);

		Press(query, Key.Enter);
		Assert.Same(review, picked);
		Assert.False(picker.IsOpen);
		Assert.Null(picker.View.Parent);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void EmptyStateExplainsWhereTheDataComesFromAndEscapeCloses()
	{
		var picker = new ComposerPicker();
		var window = new Window();
		picker.Show(window, Strings.PromptPickerTitle, Strings.PromptPickerPlaceholder,
			Strings.PromptPickerEmpty, Strings.PromptPickerNoMatch, [], _ => throw new InvalidOperationException());

		Assert.Contains(Find<TextBlock>(picker.View), block => block.IsVisible && block.Text == Strings.PromptPickerEmpty);
		Assert.Contains(Find<TextBlock>(picker.View), block => block.Text == Strings.ComposerInsertHint);

		Press(picker.View, Key.Escape);
		Assert.False(picker.IsOpen);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void ReopeningWithAnotherContentSwapsTheListAndClearsTheQuery()
	{
		var picker = new ComposerPicker();
		var window = new Window();
		var skills = new[] { Item("deploy") };
		var prompts = new[] { Item("review") };

		picker.Show(window, Strings.SkillPickerTitle, Strings.SkillPickerPlaceholder,
			Strings.SkillPickerEmpty, Strings.SkillPickerNoMatch, skills, _ => { });
		var query = Find<TextBox>(picker.View).Single();
		query.Text = "deploy";

		// 「+」菜单还在时直接选另一类条目：已打开的选择器必须换内容，不能静默忽略。
		picker.Show(window, Strings.PromptPickerTitle, Strings.PromptPickerPlaceholder,
			Strings.PromptPickerEmpty, Strings.PromptPickerNoMatch, prompts, _ => { });

		Assert.True(picker.IsOpen);
		Assert.Equal(["review"], picker.Matches.Select(item => item.Name));
		Assert.Equal(string.Empty, query.Text);
		Assert.Contains(Find<TextBlock>(picker.View), block => block.Text == Strings.PromptPickerTitle);
	}

	[Fact]
	public void FilteringReusesTheRowAlreadyBuiltForAnItem()
	{
		var deploy = Item("deploy", "把构建发布到测试环境");
		var review = Item("review", "只读审查改动", "用户");
		var picker = new ComposerPicker();
		var window = new Window();
		picker.Show(window, Strings.SkillPickerTitle, Strings.SkillPickerPlaceholder,
			Strings.SkillPickerEmpty, Strings.SkillPickerNoMatch, [deploy, review], _ => { });

		var row = RowFor(picker, deploy);
		var query = Find<TextBox>(picker.View).Single();
		query.Text = "deploy";

		Assert.Same(row, RowFor(picker, deploy));
		picker.Hide();
	}

	[Fact]
	public void RefilteringLeavesExactlyOneHighlightedRow()
	{
		var alpha = Item("alpha", "第一项");
		var beta = Item("beta", "第二项");
		var picker = new ComposerPicker();
		var window = new Window();
		picker.Show(window, Strings.SkillPickerTitle, Strings.SkillPickerPlaceholder,
			Strings.SkillPickerEmpty, Strings.SkillPickerNoMatch, [alpha, beta], _ => { });

		var query = Find<TextBox>(picker.View).Single();
		// 先把高亮移到第二行，再收窄到只剩第一行，最后放开：复用行不能残留第二枚高亮。
		Press(query, Key.Down);
		Assert.Equal("/beta", Highlighted(picker));
		query.Text = "alph";
		Assert.Equal("/alpha", Highlighted(picker));
		query.Text = string.Empty;
		Assert.Equal("/alpha", Highlighted(picker));
		Assert.Single(PickerRows(picker), row => row.StyleName == AppStyles.SelectedButton);
		picker.Hide();
	}

	private static IEnumerable<Button> PickerRows(ComposerPicker picker) =>
		Find<Button>(picker.View).Where(button =>
			button.Content is Grid grid &&
			Find<TextBlock>(grid).Any(block => block.Text.StartsWith('/')));

	private static string? Highlighted(ComposerPicker picker) =>
		PickerRows(picker)
			.FirstOrDefault(row => row.StyleName == AppStyles.SelectedButton) is { Content: Grid grid }
			? Find<TextBlock>(grid).First(block => block.Text.StartsWith('/')).Text
			: null;

	private static Button RowFor(ComposerPicker picker, ComposerPicker.Item item) =>
		Find<Button>(picker.View).Single(button =>
			button.Content is Grid grid &&
			Find<TextBlock>(grid).Any(block => block.Text == item.Title));

	private static void Press(UIElement element, Key key) =>
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(element, [new KeyEventArgs(key, 0)]);
}
