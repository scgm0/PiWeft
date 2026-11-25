using Aprillz.MewUI;
using Aprillz.MewUI.Svg;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class AppIconTests
{
	// 资源名与 Window.Icon 的设置共用一个字面量；拼错时启动会抛异常，这里先把问题挡在测试里。
	[Fact]
	public void EmbeddedIconCoversTaskbarSizes()
	{
		var icon = IconSource.FromResource<MainWindow>("PiWeft.Desktop.Resources.appicon.piweft.ico");

		foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
		{
			Assert.NotNull(icon.Pick(size));
		}
	}

	// 起始页的品牌标直接取同一份 SVG：资源名写错会让欢迎页构建时抛异常，先把名字挡在测试里。
	[Fact]
	public void BrandMarkSvgIsEmbeddedAndParses()
	{
		using var source = SvgImageSource.FromResource(
			typeof(MainWindow).Assembly, "PiWeft.Desktop.Resources.appicon.piweft.svg");

		Assert.Equal(1024, source.IntrinsicSize.Width, 1);
		Assert.Equal(1024, source.IntrinsicSize.Height, 1);
	}
}
