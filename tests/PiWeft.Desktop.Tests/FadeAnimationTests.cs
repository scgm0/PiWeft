using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 弹层动效的收尾契约：无后端时不能卡住关闭流程，原生弹层的内容要在关闭后交还给生命周期管理。
[Collection("mewui-controls")]
public sealed class FadeAnimationTests
{
	[Fact]
	public void FadePopupReleasesContentWhenItCannotOpen()
	{
		using var popup = new FadePopup();
		var content = new Border();

		popup.Show(content, new Border(), new Rect(0, 0, 80, 24), PopupAnchorSide.Above);

		Assert.False(popup.IsOpen);
		Assert.Same(content, popup.Content);
		popup.Close();
		Assert.Null(popup.Content);
	}

	[Fact]
	public void OverlayFadeClosesSynchronouslyWithoutABackend()
	{
		var view = new Grid();
		var fade = new OverlayFade(view);
		var closed = false;

		fade.Close(() => closed = true);

		Assert.True(closed);
		Assert.Equal(1, view.Opacity);
		Assert.False(fade.IsClosing);
	}
}
