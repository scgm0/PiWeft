using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 逐行居中的流式面板：MewUI 的 <see cref="WrapPanel"/> 会把每一行都从左边开始排，
/// 一行放两块、下一行只剩一块时，第二行会贴在左边。空会话的起步建议正好是这种形状，
/// 这里按行计算宽度并把整行居中，宽窗口仍是一行。
internal sealed class CenteredWrapPanel : Panel
{
	public static readonly MewProperty<double> SpacingProperty =
		MewProperty<double>.Register<CenteredWrapPanel>(nameof(Spacing), 0.0, MewPropertyOptions.AffectsLayout);

	public double Spacing
	{
		get => GetValue(SpacingProperty);
		set => SetValue(SpacingProperty, value);
	}

	protected override Size MeasureContent(Size availableSize)
	{
		var available = availableSize.Width;
		double line = 0, widest = 0, height = 0, lineHeight = 0;
		var count = 0;

		foreach (var child in ChildrenList)
		{
			if (child is not UIElement ui || !ui.IsVisible)
			{
				continue;
			}

			ui.Measure(new Size(available, availableSize.Height));
			var size = ui.DesiredSize;
			if (count > 0 && !double.IsPositiveInfinity(available) && line + Spacing + size.Width > available)
			{
				widest = Math.Max(widest, line);
				height += lineHeight + Spacing;
				line = 0;
				lineHeight = 0;
				count = 0;
			}

			line += (count > 0 ? Spacing : 0) + size.Width;
			lineHeight = Math.Max(lineHeight, size.Height);
			count++;
		}

		widest = Math.Max(widest, line);
		height += lineHeight;
		return new Size(double.IsPositiveInfinity(available) ? widest : Math.Min(available, widest), height);
	}

	protected override void ArrangeContent(Rect bounds)
	{
		double line = 0, lineHeight = 0, y = bounds.Y;
		var count = 0;
		var items = new List<(UIElement Child, double Width)>(ChildrenList.Count);

		void Flush()
		{
			if (count == 0)
			{
				return;
			}

			var x = bounds.X + Math.Max(0, (bounds.Width - line) / 2);
			foreach (var (child, width) in items)
			{
				child.Arrange(new Rect(x, y, width, lineHeight));
				x += width + Spacing;
			}

			y += lineHeight + Spacing;
			line = 0;
			lineHeight = 0;
			count = 0;
			items.Clear();
		}

		foreach (var child in ChildrenList)
		{
			if (child is not UIElement ui || !ui.IsVisible)
			{
				continue;
			}

			var size = ui.DesiredSize;
			if (count > 0 && line + Spacing + size.Width > bounds.Width)
			{
				Flush();
			}

			line += (count > 0 ? Spacing : 0) + size.Width;
			lineHeight = Math.Max(lineHeight, size.Height);
			items.Add((ui, size.Width));
			count++;
		}

		Flush();
	}
}
