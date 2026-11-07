using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ButtonInteractionTests
{
	private sealed class TestButton : Button
	{
		public void RefreshState() => ResolveVisualState(snap: true);
	}

	[Theory]
	[InlineData(null)]
	[InlineData(AppStyles.PrimaryButton)]
	[InlineData(AppStyles.GhostButton)]
	[InlineData(AppStyles.SelectedButton)]
	[InlineData(AppStyles.CardButton)]
	[InlineData(AppStyles.CardMutedButton)]
	public void ButtonsUseHandCursorsOnlyWhileEnabled(string? styleName)
	{
		var button = new TestButton();
		if (styleName is not null) button.StyleName(styleName);
		var root = new Border().StyleSheet(AppStyles.Create()).Child(button);
		button.Measure(new Size(100, 40));
		Assert.Equal(CursorType.Hand, button.Cursor);
		button.IsEnabled(false);
		button.RefreshState();
		Assert.Equal(CursorType.Arrow, button.Cursor);
		button.IsEnabled(true);
		button.RefreshState();
		Assert.Equal(CursorType.Hand, button.Cursor);
		GC.KeepAlive(root);
	}

	[Fact]
	public void PrimaryButtonLabelsFollowTheColorOfTheStateTheyAreIn()
	{
		// 主按钮的标签不写死颜色，继承按钮的 Foreground：可用是反色、禁用是被压暗的次要色。
		// 写死 OnAccentText 的那一版在禁用时会把深色字压在深灰底上（设置页的「保存」、
		// 问询卡的「下一题」都栽在这条上），所以断言「跟着状态变」，不写死固定值。
		var label = new TextBlock().Text("next").FontSize(AppTheme.FontSizeCaption);
		var button = new TestButton().Content(label).StyleName(AppStyles.PrimaryButton);
		var root = new Border().StyleSheet(AppStyles.Create()).Child(button);
		button.Measure(new Size(200, 40));

		Assert.Equal(AppTheme.TextOnAccent, label.Foreground);

		button.IsEnabled(false);
		button.RefreshState();
		Assert.Equal(AppTheme.TextSubtle, label.Foreground);

		button.IsEnabled(true);
		button.RefreshState();
		Assert.Equal(AppTheme.TextOnAccent, label.Foreground);
		GC.KeepAlive(root);
	}

	[Fact]
	public void AccentSegmentKeepsAccentBackgroundAndDimsOnlyTheGlyph()
	{
		var glyph = new GlyphElement();
		var button = new TestButton().Content(new Grid().Children(glyph)).StyleName(AppStyles.AccentSegmentButton);
		var root = new Border().StyleSheet(AppStyles.Create()).Child(button);
		button.Measure(new Size(80, 40));

		Assert.Equal(AppTheme.Accent, button.Background);
		Assert.Equal(AppTheme.TextOnAccent, glyph.Foreground);

		// 禁用时底色仍与主按钮同色，只有箭头降为次要色；换回深灰会在白底主按钮旁切出异色块。
		button.IsEnabled(false);
		button.RefreshState();
		Assert.Equal(AppTheme.Accent, button.Background);
		Assert.Equal(AppTheme.TextSubtle, glyph.Foreground);
		GC.KeepAlive(root);
	}

	[Fact]
	public void ExplicitCursorOverridesTheButtonStyle()
	{
		var button = new Button().Cursor(CursorType.Arrow);
		var root = new Border().StyleSheet(AppStyles.Create()).Child(button);
		button.Measure(new Size(100, 40));
		Assert.Equal(CursorType.Arrow, button.Cursor);
		GC.KeepAlive(root);
	}
}
