using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>居中的阅读列占满可用宽度直到上限，短消息和折叠工具不会让整列忽宽忽窄。</summary>
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
