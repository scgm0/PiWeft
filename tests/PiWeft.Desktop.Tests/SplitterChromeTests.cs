using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class SplitterChromeTests
{
	[Fact]
	public void SplitterHasNoRestingLineAndHighlightsOnHover()
	{
		var split = new SplitPanel().Horizontal().SplitterThickness(4)
			.First(new Border()).Second(new Border())
			.FlatSplitter();

		var thumb = split.Children.OfType<Control>()
			.Single(control => control.Cursor is CursorType.SizeWE or CursorType.SizeNS);

		// 静止时没有 30 DIP 的居中短线，也没有底色。
		Assert.Equal(AppTheme.Transparent, thumb.BorderBrush);
		Assert.Equal(AppTheme.Transparent, thumb.Background);

		Raise(thumb, "MouseEnter");
		Assert.Equal(AppTheme.HoverBackground, thumb.Background);

		Raise(thumb, "MouseLeave");
		Assert.Equal(AppTheme.Transparent, thumb.Background);
	}

	// MewUI 的 IsMouseOver 只由输入路由写入；测试里直接触发事件背后的委托。
	private static void Raise(Control control, string eventName)
	{
		var field = typeof(UIElement).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
		var handler = field?.GetValue(control) as Action;
		Assert.NotNull(handler);
		handler();
	}
}
