using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 窗口级浮层的让位量在窗口状态变化后必须重算：最大化时打开、再还原时漏算，
/// 遮罩会按最大化时的边距铺到透明阴影边上。
[Collection("mewui-controls")]
public sealed class OverlayInsetFollowerTests
{
	private static readonly Thickness Normal = new(12, 54, 12, 14);
	private static readonly Thickness Maximized = new(0, 40, 0, 0);

	private static Thickness InsetFor(Window window) =>
		window.WindowState == WindowState.Maximized ? Maximized : Normal;

	[Fact]
	public void ReappliesInsetWhenTheWindowStateChanges()
	{
		var window = new Window();
		var view = new Border();
		var follower = new OverlayInsetFollower(InsetFor);

		follower.Attach(window, view);
		Assert.Equal(Normal, view.Margin);

		window.WindowState = WindowState.Maximized;
		Assert.Equal(Maximized, view.Margin);

		window.WindowState = WindowState.Normal;
		Assert.Equal(Normal, view.Margin);
	}

	[Fact]
	public void OnlyTheLatestViewFollowsTheWindow()
	{
		var window = new Window();
		var first = new Border();
		var second = new Border();
		var follower = new OverlayInsetFollower(InsetFor);

		follower.Attach(window, first);
		follower.Attach(window, second);
		window.WindowState = WindowState.Maximized;

		Assert.Equal(Normal, first.Margin);
		Assert.Equal(Maximized, second.Margin);
	}

	[Fact]
	public void DetachedViewStaysPut()
	{
		var window = new Window();
		var view = new Border();
		var follower = new OverlayInsetFollower(InsetFor);

		follower.Attach(window, view);
		follower.Detach();
		window.WindowState = WindowState.Maximized;

		Assert.Equal(Normal, view.Margin);
	}

	[Fact]
	public void APlainWindowKeepsTheTitleBarInset()
	{
		var window = new Window();
		var view = new Border();
		var follower = new OverlayInsetFollower();

		follower.Attach(window, view);
		window.WindowState = WindowState.Maximized;

		Assert.Equal(new Thickness(0, AppTheme.TitleBarHeight, 0, 0), view.Margin);
	}
}
