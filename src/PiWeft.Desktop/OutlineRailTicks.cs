namespace PiWeft.Desktop;

/// <summary>
/// 会话定位轴（右缘刻度）的刻度规划。PiDeck 的 <c>planRailTicks</c> 同款纯函数：
/// 优先全量展示（间距按可用高度收缩），压到下限仍放不下时均匀抽稀，但首尾刻度强制保留——
/// 最顶刻度必须能回到第一条用户消息，最底刻度必须对应最后一条。
/// </summary>
internal static class OutlineRailTicks
{
	/// <summary>刻度间距上限（每个刻度一行 14px，与 PiDeck 的观感一致）。</summary>
	public const double MaxSize = 14;

	/// <summary>刻度间距下限：再小就只能靠抽稀，保证剩下的刻度仍可悬浮与点击。</summary>
	public const double MinSize = 4;

	public readonly record struct Plan<T>(IReadOnlyList<T> Items, double Size);

	public static Plan<T> Build<T>(IReadOnlyList<T> items, double availableHeight)
	{
		var count = items.Count;
		if (count == 0) return new([], MaxSize);
		// 首帧还没量到高度：先按全量 + 上限间距渲染，测量完再重排。
		if (availableHeight <= 0) return new([.. items], MaxSize);

		var fitted = Math.Floor(availableHeight / count);
		if (fitted >= MaxSize) return new([.. items], MaxSize);
		if (fitted >= MinSize) return new([.. items], fitted);

		var maxCount = Math.Max(2, (int)Math.Floor(availableHeight / MinSize));
		return new(Sample(items, maxCount), MinSize);
	}

	/// <summary>均匀抽稀并强制保留首尾；取样下标严格递增，不会重复。</summary>
	public static IReadOnlyList<T> Sample<T>(IReadOnlyList<T> items, int maxCount)
	{
		var count = items.Count;
		if (count == 0) return [];
		if (count <= maxCount) return [.. items];

		var slots = Math.Max(2, maxCount);
		var sampled = new List<T>(slots);
		for (var slot = 0; slot < slots; slot++)
		{
			sampled.Add(items[(int)Math.Round((double)slot * (count - 1) / (slots - 1), MidpointRounding.AwayFromZero)]);
		}
		return sampled;
	}

	/// <summary>
	/// 当前行对应的用户问题：取最后一个不晚于当前行的项，都不满足时退回第一项。
	/// <paramref name="positions"/> 是按顺序排列的用户消息行下标（不受虚拟化回收影响）。
	/// </summary>
	public static int ActiveIndex(IReadOnlyList<int> positions, int anchor)
	{
		var low = 0;
		var high = positions.Count - 1;
		var active = -1;
		while (low <= high)
		{
			var middle = (low + high) >> 1;
			if (positions[middle] <= anchor)
			{
				active = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}
		return active >= 0 ? active : (positions.Count > 0 ? 0 : -1);
	}

	/// <summary>抽稀后当前项可能没有刻度：取下标最近的已渲染刻度。</summary>
	public static int NearestRendered(IReadOnlyList<int> rendered, int sourceIndex)
	{
		if (rendered.Count == 0) return -1;
		var nearest = 0;
		var distance = int.MaxValue;
		for (var index = 0; index < rendered.Count; index++)
		{
			var current = Math.Abs(rendered[index] - sourceIndex);
			if (current >= distance) continue;
			distance = current;
			nearest = index;
		}
		return nearest;
	}
}
