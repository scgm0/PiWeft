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

	internal void AddRow(FrameworkElement marker, FrameworkElement content)
	{
		Add(marker);
		Add(content);
		_rows.Add(new ListRow(marker, content));
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
				row.Height = Math.Max(row.Marker.DesiredSize.Height, row.Content.DesiredSize.Height);
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
			row.Marker.Arrange(new Rect(bounds.X, y, _markerWidth, height));
			row.Content.Arrange(new Rect(bounds.X + _markerWidth, y, contentWidth, height));
			y += height;
		}
	}

	private sealed class ListRow(FrameworkElement marker, FrameworkElement content)
	{
		public FrameworkElement Marker { get; } = marker;
		public FrameworkElement Content { get; } = content;
		public double Height { get; set; } = double.NaN;
	}
}
