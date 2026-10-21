using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>toast 统一入口的记录语义；呈现由框架的 ToastService 负责，需要真实窗口验证。</summary>
public sealed class ToastsTests
{
	[Fact]
	public void ShowingRecordsTheMessageWithItsKind()
	{
		Toasts.ClearRecent();
		Toasts.Show(null, "已保存", ToastKind.Success);
		Toasts.Error(new TextBlock(), "加载失败");

		Assert.Equal(new[]
		{
			(Message: "已保存", Kind: ToastKind.Success),
			(Message: "加载失败", Kind: ToastKind.Error),
		}, Toasts.Recent.Select(item => (item.Message, item.Kind)));
	}

	[Fact]
	public void EmptyMessagesAreIgnoredAndTheRecordStaysBounded()
	{
		Toasts.ClearRecent();
		Toasts.Show(null, "   ");
		Assert.Empty(Toasts.Recent);

		for (var index = 0; index < 12; index++) Toasts.Show(null, $"第 {index} 条");
		Assert.Equal(8, Toasts.Recent.Count);
		Assert.Equal("第 11 条", Toasts.Recent[^1].Message);
	}

	[Fact]
	public void ShowingWithoutAWindowIsSilentlyIgnored()
	{
		// 无窗口的布局测试与退出阶段都会走到这里：不能抛异常，也不能新建浮层。
		Toasts.ClearRecent();
		Toasts.Show(null, "hello");
		Toasts.Show(new TextBlock(), "hello");
		Toasts.Success(new TextBlock(), "saved");

		Assert.Equal(new[] { "hello", "hello", "saved" }, Toasts.Recent.Select(item => item.Message));
		Assert.Equal(ToastKind.Success, Toasts.Recent[^1].Kind);
	}
}
