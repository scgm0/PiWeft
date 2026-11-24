using Aprillz.MewUI;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ToastHostTests
{
	[Fact]
	public void KeepsAtMostThreeAndCollapsesRepeats()
	{
		using var window = new Window();
		var host = new ToastHost(window);

		host.Show("a", ToastKind.Info);
		host.Show("b", ToastKind.Success);
		host.Show("c", ToastKind.Error);
		Assert.Equal(3, host.VisibleCount);
		Assert.Equal(1, window.OverlayLayer.Count);

		// 第四条挤掉最早的一条；重复的最新一条不叠卡，只重置计时。
		host.Show("d", ToastKind.Info);
		host.Show("d", ToastKind.Info);
		Assert.Equal(3, host.VisibleCount);
		Assert.Equal(["b", "c", "d"], host.Texts);

		host.DismissAll();
		Assert.Equal(0, host.VisibleCount);
		Assert.Equal(0, window.OverlayLayer.Count);
	}
}
