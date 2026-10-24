using Aprillz.MewUI;
using Aprillz.MewUI.Text;

namespace PiWeft.Markdown;

/// <summary>
/// 行距换算的公共口径：<see cref="MarkdownTheme"/> 用「字号倍率」描述行高（CSS/PiDeck 的习惯），
/// MewUI 的段落样式只接受「附加行间距」（DIP）。要把倍率落到附加间距上，就得先知道字体自己
/// 为一行留了多少高度，所以这里按 字族/字号/字重/斜体/DPI 探一次量并缓存（一个进程内不会变）。
/// </summary>
internal static class MarkdownMetrics
{
	private const double UnwrappedWidth = 1_000_000;

	// 探针结果的键空间本来很小（字族 × 字号档 × 字重 × DPI），但宿主连续缩放字号时仍可能不断引入新键：
	// 所以用 LRU 淘汰而不是「满了就清盘」——清盘会把已有答案全丢了，下一帧又得重新探，
	// 把一次性成本变成每帧成本。
	private const int Capacity = 128;

	private static readonly object Lock = new();
	private static readonly Dictionary<FontKey, double> NaturalLineHeights = [];
	private static readonly LinkedList<FontKey> Lru = [];

	private readonly record struct FontKey(string? Family, double Size, FontWeight Weight, bool Italic, uint Dpi);

	/// <summary>
	/// 每行需要补的附加行间距（DIP）：<c>字号 × 倍率</c> 与字体自然行高之差。
	/// 差为负时返回 0——倍率低于自然行高就不压缩，否则上下行会咬在一起（CJK 字体自然行高本来就大）。
	/// 倍率非有限或 ≤ 0 视为关闭行距。
	/// </summary>
	public static double LineSpacing(ITextEngine engine, TextRunStyle style, uint dpi, double scale)
	{
		if (!double.IsFinite(scale) || scale <= 0)
		{
			return 0;
		}
		if (!double.IsFinite(style.FontSize) || style.FontSize <= 0)
		{
			return 0;
		}
		return Math.Max(0, style.FontSize * scale - NaturalLineHeight(engine, style, dpi));
	}

	/// <summary>字体为一行预留的高度（不含任何附加行距）。</summary>
	public static double NaturalLineHeight(ITextEngine engine, TextRunStyle style, uint dpi)
	{
		FontKey key = new(style.FontFamily, style.FontSize, style.Weight, style.Italic, dpi);
		lock (Lock)
		{
			if (NaturalLineHeights.TryGetValue(key, out double cached))
			{
				// 命中也要刷新 LRU 位置，否则热键会被冷键顶掉，反复重新探针。
				Lru.Remove(key);
				Lru.AddLast(key);
				return cached;
			}
		}

		// 一次性的探针布局：MewUI 自己也用 CreateLayout 做这类临时测量，布局对象不需要显式释放。
		// 探针在锁外跑：引擎内部有自己的缓存，拿住锁去调它只会把串行风险放大。
		double height = engine.CreateLayout(new TextLayoutRequest
		{
			Text = "Hx".AsMemory(),
			DefaultStyle = style,
			Dpi = dpi,
			Paragraph = new TextParagraphStyle
			{
				MaxWidth = UnwrappedWidth,
				Wrapping = TextWrapping.NoWrap
			}
		}).ContentHeight;
		if (!double.IsFinite(height) || height <= 0)
		{
			// 后端给出不可用的度量时，宁可不加行距，也不要到处凭空撑高。
			return 0;
		}
		lock (Lock)
		{
			if (!NaturalLineHeights.TryAdd(key, height))
			{
				Lru.Remove(key);
			}
			Lru.AddLast(key);
			while (Lru.Count > Capacity)
			{
				FontKey oldest = Lru.First!.Value;
				Lru.RemoveFirst();
				NaturalLineHeights.Remove(oldest);
			}
		}
		return height;
	}
}
