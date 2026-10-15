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
	public void ExplicitCursorOverridesTheButtonStyle()
	{
		var button = new Button().Cursor(CursorType.Arrow);
		var root = new Border().StyleSheet(AppStyles.Create()).Child(button);
		button.Measure(new Size(100, 40));
		Assert.Equal(CursorType.Arrow, button.Cursor);
		GC.KeepAlive(root);
	}
}
