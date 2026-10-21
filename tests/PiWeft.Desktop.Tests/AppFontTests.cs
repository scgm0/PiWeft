using Aprillz.MewUI;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>等宽字体用 Maple Mono NF CN：Nerd Font 图标（powerline 提示符）才能显示。</summary>
public sealed class AppFontTests
{
	[Fact]
	public void EmbeddedMonoFontIsTheNerdFontVariant()
	{
		var names = typeof(AppFonts).Assembly.GetManifestResourceNames();

		Assert.Contains("PiWeft.Desktop.Resources.fonts.MapleMono-NF-CN-Regular.ttf", names);
		Assert.DoesNotContain("PiWeft.Desktop.Resources.fonts.MapleMono-CN-Regular.ttf", names);
		Assert.Equal("Maple Mono NF CN", AppFonts.MonoFamily);
	}

	[Fact]
	public void WheelStepMatchesBrowserDistancePerNotch()
	{
		// MewUI 默认一格 50 DIP，浏览器（Chromium/Electron）约 100 DIP；步长减半会让滚动显得拖。
		Assert.Equal(100, AppFonts.ScrollWheelStepDip);
		Assert.True(AppFonts.ScrollWheelStepDip > ThemeMetrics.Default.ScrollWheelStep);
	}

	[Fact]
	public void StartupMetricsApplyTheWheelStep()
	{
		AppFonts.Register();
		Assert.Equal(AppFonts.ScrollWheelStepDip, ThemeManager.DefaultMetrics.ScrollWheelStep);
		Assert.Equal(ThemeMetrics.Default.ScrollBarThickness, ThemeManager.DefaultMetrics.ScrollBarThickness);
	}
}
