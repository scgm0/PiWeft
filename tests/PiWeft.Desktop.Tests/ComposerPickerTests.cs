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

	private static void Press(UIElement element, Key key) =>
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(element, [new KeyEventArgs(key, 0)]);
}
