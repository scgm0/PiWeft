using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 浮层遮罩：档位越深 alpha 越高，点空白关闭，滚轮不落到后面的正文。
[Collection("mewui-controls")]
public sealed class OverlayScrimTests
{
	[Fact]
	public void StrengthsDarkenInOrderAndNoneStaysTransparent()
	{
		var none = AppTheme.Scrim(ScrimStrength.None);
		var light = AppTheme.Scrim(ScrimStrength.Light);
		var regular = AppTheme.Scrim(ScrimStrength.Regular);
		var strong = AppTheme.Scrim(ScrimStrength.Strong);
		var heavy = AppTheme.Scrim(ScrimStrength.Heavy);

		Assert.Equal(0, none.A);
		Assert.True(light.A < regular.A);
		Assert.True(regular.A < strong.A);
		Assert.True(strong.A < heavy.A);
	}

	[Fact]
	public void ClickingTheBlankAreaCloses()
	{
		var closed = 0;
		var scrim = OverlayScrim.Create(() => closed++);

		Click(scrim);

		Assert.Equal(1, closed);
		Assert.False(scrim.Focusable);
	}

	private static void Click(Button button) =>
		typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
}
