using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed class ReadingColumn : StackPanel
{
	public ReadingColumn()
	{
		MaxWidth = AppTheme.MessageMaxWidth + AppTheme.Space6;
		HorizontalAlignment = HorizontalAlignment.Center;
	}

	protected override Size MeasureContent(Size availableSize)
	{
		var measured = base.MeasureContent(availableSize);
		return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : measured.Width, measured.Height);
	}
}
