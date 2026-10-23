using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// <summary>
/// 帧时间条：把最近若干帧的间隔画成柱子，一眼看出「什么时候掉的帧、掉多深」。
/// 这是数据可视化（不是图标）：柱形与参考线只表达数值，任何图形元素都来自主题色。
/// </summary>
internal sealed class FrameTimeStrip : FrameworkElement
{
	/// <summary>一帧的时间预算：60 FPS。</summary>
	private const double BudgetMs = 16.7;

	private const double BarGap = 1;
	private double[] _samples = [];

	public FrameTimeStrip()
	{
		Height = 44;
		HorizontalAlignment = HorizontalAlignment.Stretch;
		VerticalAlignment = VerticalAlignment.Center;
	}

	public void SetSamples(IReadOnlyList<double> samples)
	{
		_samples = samples.Count == 0 ? [] : [.. samples];
		InvalidateVisual();
	}

	protected override Size MeasureContent(Size availableSize) =>
		new(double.IsFinite(availableSize.Width) ? availableSize.Width : 320, Height);

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnRender(IGraphicsContext context)
	{
		var palette = AppTheme.For(Theme);
		var box = Bounds;
		if (box.Width < 8 || box.Height < 12)
		{
			return;
		}

		// 底：极淡的面板底 + 1px 发丝线，让柱子有参照。
		context.FillRoundedRectangle(box, AppTheme.RadiusXs, AppTheme.RadiusXs, palette.CardBackgroundAlt.WithAlpha(0x66));
		var baseline = box.Bottom - 1;
		context.DrawLine(new Point(box.X + 1, baseline), new Point(box.Right - 1, baseline), palette.BorderSubtle, 1);

		if (_samples.Length == 0)
		{
			return;
		}

		// 纵轴按「最矮也看得见预算线」来定标：峰值再高，16.7 ms 的参考线也始终在 1/3 高度以上。
		var peak = BudgetMs * 1.5;
		foreach (var sample in _samples)
		{
			peak = Math.Max(peak, sample);
		}

		var usable = box.Height - 6;
		double Y(double ms) => box.Bottom - 3 - Math.Min(1, ms / peak) * usable;
		var budget = Y(BudgetMs);
		context.DrawLine(new Point(box.X + 1, budget), new Point(box.Right - 1, budget),
			palette.Info.WithAlpha(0x66), 1, pixelSnap: true);

		var slot = box.Width / _samples.Length;
		var width = Math.Max(1, slot - BarGap);
		for (var index = 0; index < _samples.Length; index++)
		{
			var top = Y(_samples[index]);
			var rect = new Rect(box.X + index * slot + BarGap / 2, top, width, box.Bottom - 3 - top);
			var color = _samples[index] >= Diagnostics.LongFrameReportMs
				? palette.Error
				: _samples[index] > BudgetMs
					? palette.Accent
					: palette.Success;
			context.FillRectangle(rect, color.WithAlpha((byte)(_samples[index] > BudgetMs ? 0xAA : 0x6E)));
		}
	}
}
