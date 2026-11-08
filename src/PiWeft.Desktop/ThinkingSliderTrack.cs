using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// 盖在 <see cref="Slider"/> 上的自绘轨道：整条轨道渐变、档位刻度点、白圆钮。
/// 为什么不用 Slider 自己的渲染：它只画实心强调色与方轨，也没有「整条轨道固定渐变、
/// 填充段只是裁出来一段」的入口；但 Slider 的离散手感、捕获与键盘步进都值钱，
/// 所以保留它做输入层，视觉层盖在上面（命中穿透），只按它的 Value 画。
internal sealed class ThinkingSliderTrack : FrameworkElement
{
	private const double TrackThickness = 10;
	private const double ThumbDiameter = 16;
	private const double DotDiameter = 3;

	/// 圆钮中心的行程端点：等于半个圆钮宽，白钮在两端正好压进轨道圆角里。
	internal const double TrackInset = ThumbDiameter / 2;

	private readonly Slider _slider;
	private LinearGradientBrush? _gradient;
	private Color _gradientStart;
	private Color _gradientEnd;
	private Rect _gradientTrack;

	public ThinkingSliderTrack(Slider slider)
	{
		_slider = slider;
		IsHitTestVisible = false;
		slider.GotFocus += InvalidateVisual;
		slider.LostFocus += InvalidateVisual;
		// Slider 的值变化只重画它自己；视觉层是另一个元素，必须自己跟着重画。
		slider.ValueChanged += _ => InvalidateVisual();
	}

	protected override Size MeasureContent(Size availableSize) => Size.Empty;

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme)
	{
		base.OnThemeChanged(oldTheme, newTheme);
		_gradient = null;
		InvalidateVisual();
	}

	protected override void OnRender(IGraphicsContext context)
	{
		var bounds = Bounds;
		if (bounds.Width <= 0 || bounds.Height <= 0) return;

		var palette = AppTheme.For(Theme);
		var content = bounds.Deflate(_slider.Padding);
		var track = new Rect(
			content.X,
			content.Y + ((content.Height - TrackThickness) / 2),
			content.Width,
			TrackThickness);
		if (track.Width <= 0) return;

		var fraction = Fraction();
		var travelStart = content.X + TrackInset;
		var travelSpan = Math.Max(0, content.Width - (TrackInset * 2));
		var centerX = travelStart + (travelSpan * fraction);
		var radius = TrackThickness / 2;
		context.FillRoundedRectangle(track, radius, radius, palette.HoverBackground);

		var fillWidth = Math.Max(0, centerX - track.X);
		if (fillWidth > 0)
		{
			// 裁出填充段再画整条渐变：渐变坐标系锚在整条轨道上，右端变色位置不跟进度走。
			var filled = new Rect(track.X, track.Y, fillWidth, track.Height);
			context.Save();
			try
			{
				context.SetClip(filled);
				context.FillRoundedRectangle(track, radius, radius, GradientFor(palette, track));
			}
			finally
			{
				context.Restore();
			}
		}

		DrawTicks(context, palette, track, travelStart, travelSpan);
		DrawThumb(context, palette, track, centerX);
	}

	private double Fraction()
	{
		var range = _slider.Maximum - _slider.Minimum;
		return range <= 0 ? 0 : Math.Clamp((_slider.Value - _slider.Minimum) / range, 0, 1);
	}

	private void DrawTicks(IGraphicsContext context, AppPalette palette, Rect track, double travelStart, double travelSpan)
	{
		var count = Math.Max(1, (int)Math.Round(_slider.Maximum - _slider.Minimum) + 1);
		var reached = Math.Clamp((int)Math.Round(_slider.Value - _slider.Minimum), 0, count - 1);
		for (var index = 0; index < count; index++)
		{
			var x = count == 1 ? travelStart : travelStart + (travelSpan * index / (count - 1));
			var dot = new Rect(
				x - (DotDiameter / 2),
				track.Y + (TrackThickness / 2) - (DotDiameter / 2),
				DotDiameter,
				DotDiameter);
			// 选中段的白点浮在彩色填充上；未选段的点必须比灰轨亮一档，否则深色主题里消失。
			context.FillEllipse(dot, index <= reached ? Color.White.WithAlpha(0xB4) : palette.TextPrimary.WithAlpha(0x40));
		}
	}

	private void DrawThumb(IGraphicsContext context, AppPalette palette, Rect track, double centerX)
	{
		var centerY = track.Y + (track.Height / 2);
		var thumb = new Rect(centerX - (ThumbDiameter / 2), centerY - (ThumbDiameter / 2), ThumbDiameter, ThumbDiameter);

		// 一层偏移的暗影代替真实阴影：白钮压在浅灰轨道上时仍能看出边界。
		context.FillEllipse(new Rect(thumb.X, thumb.Y + 1, thumb.Width, thumb.Height), Color.Black.WithAlpha(0x30));
		context.FillEllipse(thumb, Color.White);

		if (!_slider.IsFocused) return;
		// 焦点环画在圆钮外圈，留 2 的间隙；白钮与白环靠间隙分开，深色主题下才看得出来。
		var ring = new Rect(thumb.X - 2, thumb.Y - 2, ThumbDiameter + 4, ThumbDiameter + 4);
		context.DrawEllipse(ring, palette.Accent.WithAlpha(0xC0), 1.5);
	}

	private LinearGradientBrush GradientFor(AppPalette palette, Rect track)
	{
		if (_gradient is null || _gradientStart != palette.Success || _gradientEnd != palette.Info || _gradientTrack != track)
		{
			_gradientStart = palette.Success;
			_gradientEnd = palette.Info;
			_gradientTrack = track;
			_gradient = new LinearGradientBrush(
				new Point(track.X, track.Y),
				new Point(track.Right, track.Y),
				[
					new GradientStop(0, _gradientStart),
					new GradientStop(1, _gradientEnd),
				]);
		}

		return _gradient;
	}
}
