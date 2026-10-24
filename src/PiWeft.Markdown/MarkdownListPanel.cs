using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Markdown;

/// <summary>
/// Vertical list rows with a fixed marker column. Row heights are cached; a streamed last item only
/// re-measures its own row instead of the whole list, which a star-column Grid would do on every
/// pass.
/// </summary>
internal sealed class MarkdownListPanel : Panel
{
	private readonly List<ListRow> _rows = [];
	private readonly double _markerWidth;
	private double _width = double.NaN;

	internal MarkdownListPanel(double markerWidth)
	{
		_markerWidth = Math.Max(0, markerWidth);
	}

	internal int RowCount => _rows.Count;

	internal void AddRow(FrameworkElement marker, FrameworkElement content, double leading = 0)
	{
		Add(marker);
		Add(content);
		_rows.Add(new ListRow(marker, content, Math.Max(0, leading)));
	}

	internal StackPanel? ContentAt(int index)
		=> index >= 0 && index < _rows.Count ? _rows[index].Content as StackPanel : null;

	internal FrameworkElement? MarkerAt(int index)
		=> index >= 0 && index < _rows.Count ? _rows[index].Marker : null;

	protected override Size MeasureContent(Size availableSize)
	{
		double width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 1_000_000;
		if (Math.Abs(width - _width) > 0.01)
		{
			_width = width;
			foreach (ListRow row in _rows)
			{
				row.Height = double.NaN;
			}
		}

		double contentWidth = Math.Max(0, width - _markerWidth);
		double total = 0;
		foreach (ListRow row in _rows)
		{
			if (double.IsNaN(row.Height) || row.Marker.IsMeasureDirty || row.Content.IsMeasureDirty)
			{
				row.Marker.Measure(new Size(_markerWidth, double.PositiveInfinity));
				row.Content.Measure(new Size(contentWidth, double.PositiveInfinity));
				// 单行条目自身拿不到行间距（段落里只有一行，行距只存在于行与行之间），所以条目这一行
				// 的行高由宿主补上：自然行高 + 一个行间距 == 正文行距，列表才不会比正文挤。
				row.Height = Math.Max(row.Marker.DesiredSize.Height, row.Content.DesiredSize.Height) + row.Leading;
			}
			total += row.Height;
		}
		return new Size(width, total);
	}

	protected override void ArrangeContent(Rect bounds)
	{
		double contentWidth = Math.Max(0, bounds.Width - _markerWidth);
		double y = bounds.Y;
		foreach (ListRow row in _rows)
		{
			double height = double.IsNaN(row.Height) ? 0 : row.Height;
			// 补出来的行间距上下均分：条目文字落在自己那一行的行框里，标记也跟第一行对齐，
			// 而不是把整段推到行框下沿。
			double offset = Math.Min(row.Leading / 2, Math.Max(0, height - row.Leading));
			double childHeight = Math.Max(0, height - row.Leading);
			row.Marker.Arrange(new Rect(bounds.X, y + offset, _markerWidth, childHeight));
			row.Content.Arrange(new Rect(bounds.X + _markerWidth, y + offset, contentWidth, childHeight));
			y += height;
		}
	}

	private sealed class ListRow(FrameworkElement marker, FrameworkElement content, double leading)
	{
		public FrameworkElement Marker { get; } = marker;
		public FrameworkElement Content { get; } = content;
		public double Leading { get; } = leading;
		public double Height { get; set; } = double.NaN;
	}
}
