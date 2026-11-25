using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 单列容器：宽度最多 <see cref="MaxContentWidth"/>，窗口再宽就左右留白，窗口变窄就跟着收。
/// 不能只用 <see cref="FrameworkElement.MaxWidth"/>：MewUI 在 arrange 阶段让 Stretch 直接吃掉槽宽，
/// 最多约束期望尺寸；这里把排布宽度也夹一次。
internal sealed class CenteredWidthPanel : Panel
{
	public static readonly MewProperty<double> MaxContentWidthProperty =
		MewProperty<double>.Register<CenteredWidthPanel>(
			nameof(MaxContentWidth), double.PositiveInfinity, MewPropertyOptions.AffectsLayout);

	public double MaxContentWidth
	{
		get => GetValue(MaxContentWidthProperty);
		set => SetValue(MaxContentWidthProperty, value);
	}

	protected override Size MeasureContent(Size availableSize)
	{
		var width = Math.Min(availableSize.Width, MaxContentWidth);
		double height = 0;
		foreach (var child in ChildrenList)
		{
			if (child is not UIElement ui || !ui.IsVisible)
			{
				continue;
			}

			ui.Measure(new Size(width, availableSize.Height));
			height = Math.Max(height, ui.DesiredSize.Height);
		}

		return new Size(width, height);
	}

	protected override void ArrangeContent(Rect bounds)
	{
		var width = Math.Min(bounds.Width, MaxContentWidth);
		var x = bounds.X + Math.Max(0, (bounds.Width - width) / 2);
		foreach (var child in ChildrenList)
		{
			if (child is not UIElement ui || !ui.IsVisible)
			{
				continue;
			}

			ui.Arrange(new Rect(x, bounds.Y, width, bounds.Height));
		}
	}
}
