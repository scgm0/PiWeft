using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// 行内容在自己的 arrange 里长高：面板必须同一帧把行高补齐，否则下一行还停在旧位置，
/// 长出来的正文就压在它上面（读者看到的是「正文错位重叠」）。Markdown 的 arrange 会在滚动中
/// 补实现新块并就地测量，这条路径在真实会话里每滚一下都会走到。
[Collection("mewui-controls")]
public sealed class RowArrangeGrowthTests(ITestOutputHelper output)
{
	private sealed class GrowRow : ContentControl
	{
		public string? Item;
		public double ContentHeight;
		public bool GrowOnArrange;
		private bool _grown;

		protected override Size MeasureContent(Size availableSize)
		{
			base.MeasureContent(availableSize);
			return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 400, ContentHeight);
		}

		protected override void ArrangeContent(Rect bounds)
		{
			base.ArrangeContent(bounds);
			if (!GrowOnArrange || _grown) return;
			_grown = true;
			ContentHeight += 120;
			InvalidateMeasure();
		}
	}

	private static VirtualRowsPanel<string, GrowRow> Build(bool growFirst)
	{
		var items = new List<string> { "first", "second", "third" };
		return new VirtualRowsPanel<string, GrowRow>(
			() => items.Count,
			index => index < items.Count ? items[index] : null,
			() => new GrowRow(),
			(host, item, index) =>
			{
				if (host.Item == item) return;
				host.Item = item;
				host.ContentHeight = 40;
				host.GrowOnArrange = growFirst && index == 0;
			},
			_ => { });
	}

	[Fact]
	public void ARowThatGrowsWhileArrangingMovesTheNextRowInTheSamePass()
	{
		var panel = Build(growFirst: true);
		panel.Measure(new Size(400, 400));
		panel.Arrange(new Rect(0, 0, 400, 400));

		var first = Assert.IsType<GrowRow>(panel.HostAt(0));
		var second = Assert.IsType<GrowRow>(panel.HostAt(1));
		output.WriteLine($"first={first.Bounds} second={second.Bounds} total={panel.TotalHeight}");
		Assert.Equal(160, first.Bounds.Height, 1);
		Assert.Equal(240, panel.TotalHeight, 1);
		// 下一行要落在长高后的底边之下，不能还压在旧位置。
		Assert.True(second.Bounds.Y + 0.5 >= first.Bounds.Bottom,
			$"second row at {second.Bounds.Y:0.#} overlaps the first row bottom {first.Bounds.Bottom:0.#}");
	}

	[Fact]
	public void RowsThatKeepTheirHeightAreMeasuredOnce()
	{
		var panel = Build(growFirst: false);
		panel.Measure(new Size(400, 400));
		panel.Arrange(new Rect(0, 0, 400, 400));

		for (var index = 0; index < 3; index++)
		{
			var host = Assert.IsType<GrowRow>(panel.HostAt(index));
			Assert.Equal(40, host.Bounds.Height, 1);
		}
		Assert.Equal(120, panel.TotalHeight, 1);
	}
}
