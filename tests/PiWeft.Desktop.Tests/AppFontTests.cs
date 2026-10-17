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
}
