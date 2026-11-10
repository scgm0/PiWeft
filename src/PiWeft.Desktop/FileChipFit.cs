namespace PiWeft.Desktop;

/// 轮末文件 chip 行的「单行装箱」规则。
/// 一轮可能改几十个文件：全铺开会把轮末撑成一坨，横向滚动又会藏起右边的 chip，
/// 所以只放一行，放不下的收进 +N，点 +N 就地展开。
/// 宽度来自运行时测量，「放几个」是产品规则，抽成纯函数才能单测边界。
internal static class FileChipFit
{
	/// 计算一行能放下的 chip 数量。
	/// <param name="widths">每个 chip 的自然宽度。</param>
	/// <param name="spacing">chip 之间的间距。</param>
	/// <param name="available">可用总宽度。</param>
	/// <param name="moreWidth">给定剩余数量时 +N 按钮的宽度；按钮宽度随数字位数变化。</param>
	/// <returns>第一行应显示的 chip 数量；0 表示只显示 +N。</returns>
	public static int Fit(ReadOnlySpan<double> widths, double spacing, double available, Func<int, double> moreWidth)
	{
		var total = widths.Length;
		if (total == 0 || available <= 0) return 0;

		var remaining = total;
		var count = 0;
		// +N 的宽度由剩余数量决定，构成自引用：从「一个都放不下」这个最保守假设出发迭代，
		// 剩余数量不再变化即收敛（实际两轮就稳）。三轮上限防浮点抖动。
		for (var iteration = 0; iteration < 3; iteration++)
		{
			var more = moreWidth(remaining);
			double used = 0;
			var placed = 0;
			for (var index = 0; index < total; index++)
			{
				var next = used + (placed > 0 ? spacing : 0) + widths[index];
				// 放下第 index 个后若还剩别的 chip，就要给 +N 留位置；最后一个不需要。
				var hasRemainder = index < total - 1;
				var budget = hasRemainder ? available - more - spacing : available;
				if (next > budget) break;
				used = next;
				placed++;
			}

			var nextRemaining = total - placed;
			count = placed;
			if (nextRemaining == remaining) break;
			remaining = nextRemaining;
		}

		return count;
	}
}
