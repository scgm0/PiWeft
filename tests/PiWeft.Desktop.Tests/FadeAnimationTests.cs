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
	public void KeepAlivePopupUnloadsContentWithoutReleasingIt()
	{
		var content = new ReleaseProbe();
		using var popup = new FadePopup { ReleaseContentOnClose = false };

		popup.Show(content, new Border(), new Rect(0, 0, 80, 24), PopupAnchorSide.Above);
		popup.Close();

		// 常驻面板（会话统计）关掉时只从浮层卸载，下一次打开复用同一棵树。
		Assert.Null(popup.Content);
		Assert.False(content.Released);
		popup.Show(content, new Border(), new Rect(0, 0, 80, 24), PopupAnchorSide.Above);
		Assert.Same(content, popup.Content);

		popup.Dispose();
		Assert.False(content.Released);
		UiLifetime.Release(content);
		Assert.True(content.Released);
	}

	[Fact]
	public void ReopenedContentAcceptsInputAgain()
	{
		var content = new ReleaseProbe();
		using var popup = new FadePopup { ReleaseContentOnClose = false };

		// 淡出会关掉复用内容的命中；无后端时直接同步关闭，这里复现重开前的状态。
		content.IsHitTestVisible = false;
		popup.Show(content, new Border(), new Rect(0, 0, 80, 24), PopupAnchorSide.Above);

		Assert.True(content.IsHitTestVisible);
	}

	private sealed class ReleaseProbe : FrameworkElement
	{
		public bool Released { get; private set; }

		protected override void OnDispose()
		{
			Released = true;
			base.OnDispose();
		}
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

	[Fact]
	public void OverlayFadeOpensSynchronouslyWithoutABackend()
	{
		var view = new Grid();
		var fade = new OverlayFade(view);

		fade.Open();

		Assert.Equal(1, view.Opacity);
		Assert.False(fade.IsClosing);
	}
}
