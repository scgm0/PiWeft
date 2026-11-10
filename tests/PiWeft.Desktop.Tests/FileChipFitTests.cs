using Xunit;

namespace PiWeft.Desktop.Tests;

/// 轮末 chip 的装箱规则：一行放不下就收进 +N，最后一块不需要给 +N 留位置。
public sealed class FileChipFitTests
{
	private static readonly double[] ThreeChips = [100, 100, 100];

	private static int Fit(double[] widths, double available, double more = 30)
		=> FileChipFit.Fit(widths, 4, available, _ => more);

	[Fact]
	public void FitsEverythingWhenThereIsRoom()
	{
		Assert.Equal(3, Fit(ThreeChips, 1000));
	}

	[Fact]
	public void ReservesRoomForTheMoreButton()
	{
		// 两块 chip 占 204；再放 +1（4 间距 + 30）共 238，240 放得下。
		Assert.Equal(2, Fit(ThreeChips, 240));
		// 200 只够一块 chip：两块 + +1 会溢出。
		Assert.Equal(1, Fit(ThreeChips, 200));
	}

	[Fact]
	public void LastChipDoesNotReserveMoreWidth()
	{
		// 两块 100 + 40，间距 4：正好 144。若最后一块也要给 +N 留位置，这一格就放不下。
		Assert.Equal(2, Fit([100, 40], 144, more: 10));
	}

	[Fact]
	public void ReturnsZeroWhenNothingFits()
	{
		Assert.Equal(0, Fit(ThreeChips, 40));
		Assert.Equal(0, Fit(ThreeChips, 0));
		Assert.Equal(0, FileChipFit.Fit([], 4, 500, _ => 30));
	}

	[Fact]
	public void MoreWidthMayDependOnRemainingCount()
	{
		var widths = new double[] { 100, 100, 100 };
		// 剩余越多，「+N」越宽；迭代两轮后稳定在同一结果。
		var count = FileChipFit.Fit(widths, 4, 240, remaining => 10 + remaining * 10);
		Assert.Equal(2, count);
	}
}
