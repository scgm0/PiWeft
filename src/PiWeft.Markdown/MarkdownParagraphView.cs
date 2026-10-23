using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;

namespace PiWeft.Markdown;

/// <summary>A realized text element that takes part in the document selection.</summary>
internal interface ISelectableText
{
	int TextUnit { get; set; }
	int TextLength { get; }
	Rect Bounds { get; }
	int OffsetAt(Point windowPoint);
	(int Start, int End) WordAt(int offset);
	void SetSelection(int start, int end);
}

/// <summary>
/// A paragraph laid out in stable chunks. Only the last chunk (the open line) is re-laid out when
/// text is appended; chunks ending on a closed line are frozen and their text layouts are reused.
/// </summary>
internal sealed class MarkdownParagraphView : TextElement, ISelectableText
{
	// The open chunk stays small enough that one append costs a few milliseconds at most.
	private const int MaxOpenChars = 2048;
	private const int MaxOpenLines = 64;
	private const double UnwrappedWidth = 1_000_000;

	private readonly List<Chunk> _chunks = [];
	private readonly List<Rect> _rangeBounds = [];
	private MarkdownBlock _block = null!;
	private string _text = string.Empty;
	private MarkdownRun[] _runs = [];
	private int[] _runStarts = [];
	private LinkRun[] _links = [];
	private MarkdownTheme _theme = new();
	private Action<MarkdownRun>? _activate;
	private Uri? _baseUri;
	private double _width = double.NaN;
	private uint _dpi;
	private TextRunStyle _style;
	private ITextEngine? _engine;
	private int _selectionStart;
	private int _selectionEnd;
	private int _focusedLink;
	private int _pressedLink = -1;
	private bool _plainCode;
	private bool _wrap = true;
	private double _fontScale = 1;

	internal MarkdownParagraphView()
	{
		Focusable = false;
	}

	public int TextUnit { get; set; } = -1;
	public int TextLength => _text.Length;
	internal string Text => _text;
	internal int LinkCount => _links.Length;
	internal int FocusedLink => _focusedLink;
	internal int ChunkCount => _chunks.Count;
	internal MarkdownBlock Block => _block;
	internal void RefreshBlock(MarkdownBlock block)
	{
		if (ReferenceEquals(_block, block))
		{
			return;
		}
		// The model hash matched, so the presentation is identical. Only source offsets may have
		// moved (a block was inserted before this one); shift the existing runs instead of
		// rebuilding metadata and allocating new arrays for every unchanged block each frame.
		int delta = block.SourceStart - _block.SourceStart;
		if (CanShiftSourceOffsets(_block, block, delta))
		{
			_block = block;
			if (delta == 0)
			{
				return;
			}
			for (int index = 0; index < _runs.Length; index++)
			{
				MarkdownRun run = _runs[index];
				if (run.SourceStart >= 0)
				{
					_runs[index] = run with { SourceStart = run.SourceStart + delta };
				}
			}
			for (int index = 0; index < _links.Length; index++)
			{
				LinkRun link = _links[index];
				if (link.Run.SourceStart >= 0)
				{
					_links[index] = link with { Run = link.Run with { SourceStart = link.Run.SourceStart + delta } };
				}
			}
			return;
		}

		_block = block;
		RebuildRuns(block);
	}

	private static bool CanShiftSourceOffsets(MarkdownBlock oldBlock, MarkdownBlock newBlock, int delta)
	{
		if (oldBlock.Runs.Count != newBlock.Runs.Count)
		{
			return false;
		}
		for (int index = 0; index < oldBlock.Runs.Count; index++)
		{
			MarkdownRun oldRun = oldBlock.Runs[index];
			MarkdownRun newRun = newBlock.Runs[index];
			if (oldRun.SourceLength != newRun.SourceLength)
			{
				return false;
			}
			if (oldRun.SourceStart >= 0 && newRun.SourceStart != oldRun.SourceStart + delta)
			{
				return false;
			}
			if (oldRun.SourceStart < 0 && newRun.SourceStart >= 0)
			{
				return false;
			}
		}
		return true;
	}
	internal (int Start, int End) SelectionRange => (_selectionStart, _selectionEnd);
	internal bool IsStreaming { get; set; }
	internal IReadOnlyList<MarkdownRun> Runs => _runs;
	internal void SetBlock(
		MarkdownBlock block,
		MarkdownTheme theme,
		Action<MarkdownRun>? activate,
		Uri? baseUri,
		bool plainCode,
		bool wrap,
		double fontScale)
	{
		_block = block;
		_theme = theme;
		_activate = activate;
		_baseUri = baseUri;
		_plainCode = plainCode;
		_wrap = wrap;
		_fontScale = fontScale;

		string text = block.Text;
		string oldText = _text;
		MarkdownRun[] oldRuns = _runs;
		int[] oldRunStarts = _runStarts;
		_text = text;
		RebuildRuns(block);

		if (_chunks.Count == 0)
		{
			_chunks.Add(new Chunk(0, _text.Length));
		}
		else
		{
			// Keep every frozen chunk that lies entirely before the first changed offset. The
			// changed tail (new text, a closing delimiter, a restyled run, or a rewrite) is
			// re-laid out from the chunk that contains it, so streaming and edits stay
			// proportional to one chunk instead of the whole paragraph.
			int commonText = CommonPrefixLength(oldText, _text);
			int firstChanged = Math.Min(
				commonText,
				FirstChangedOffset(oldRuns, oldRunStarts, _runs, oldText.Length));
			if (firstChanged <= 0)
			{
				ReleaseChunks();
				_chunks.Clear();
				_chunks.Add(new Chunk(0, _text.Length));
			}
			else
			{
				int keep = 0;
				while (keep < _chunks.Count && _chunks[keep].End <= firstChanged)
				{
					keep++;
				}
				if (keep == _chunks.Count)
				{
					// Pure append (or a run change exactly at the end): extend the open chunk
					// instead of starting a new one at the old end, which would split its last
					// line and add a fake line break.
					Chunk last = _chunks[^1];
					last.End = _text.Length;
					last.Text = null;
					last.Layout = null;
				}
				else
				{
					ReleaseChunksFrom(keep);
					_chunks.RemoveRange(keep, _chunks.Count - keep);
					int start = _chunks.Count == 0 ? 0 : _chunks[^1].End;
					if (start > _text.Length)
					{
						start = 0;
					}
					_chunks.Add(new Chunk(start, _text.Length));
				}
			}
		}
		InvalidateMeasure();
		InvalidateVisual();
	}

	private static int CommonPrefixLength(string oldText, string newText)
	{
		int limit = Math.Min(oldText.Length, newText.Length);
		int index = 0;
		while (index < limit && oldText[index] == newText[index])
		{
			index++;
		}
		return index;
	}

	private void ReleaseChunksFrom(int index)
	{
		if (_engine is null)
		{
			return;
		}
		for (int chunkIndex = index; chunkIndex < _chunks.Count; chunkIndex++)
		{
			_engine.ManagedCache.ReleaseOwner(_chunks[chunkIndex]);
		}
	}

	private static int FirstChangedOffset(
		MarkdownRun[] oldRuns,
		int[] oldStarts,
		MarkdownRun[] newRuns,
		int oldLength)
	{
		if (oldRuns.Length == 0)
		{
			return 0;
		}
		int common = 0;
		while (common < oldRuns.Length && common < newRuns.Length && SamePresentation(oldRuns[common], newRuns[common]))
		{
			common++;
		}
		if (common == oldRuns.Length)
		{
			// All old runs are unchanged; only the tail after them can be new.
			return oldLength;
		}
		return common < oldStarts.Length ? oldStarts[common] : oldLength;
	}

	private static bool SamePresentation(MarkdownRun left, MarkdownRun right)
		=> string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
			left.Style == right.Style &&
			string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
			string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
			left.Image == right.Image &&
			left.TaskChecked == right.TaskChecked;

	private void RebuildRuns(MarkdownBlock block)
	{
		_runs = block.Runs.Count == 0 ? [] : block.Runs.ToArray();
		_runStarts = new int[_runs.Length];
		List<LinkRun> links = [];
		int offset = 0;
		for (int index = 0; index < _runs.Length; index++)
		{
			_runStarts[index] = offset;
			MarkdownRun run = _runs[index];
			int length = VisualText(run).Length;
			if (length > 0 && run.Url is not null)
			{
				links.Add(new LinkRun(offset, length, run));
			}
			offset += length;
		}
		_links = links.Count == 0 ? [] : links.ToArray();
		_focusedLink = _links.Length == 0 ? 0 : Math.Clamp(_focusedLink, 0, _links.Length - 1);
		_pressedLink = -1;
		Focusable = _links.Length > 0;
	}

	private static string VisualText(MarkdownRun run) => run.Image && run.Text.Length == 0 ? "\uFFFC" : run.Text;

	private void ReleaseChunks()
	{
		if (_engine is not null)
		{
			foreach (Chunk chunk in _chunks)
			{
				_engine.ManagedCache.ReleaseOwner(chunk);
			}
		}
	}

	internal void FocusLink(int index)
	{
		if (_links.Length == 0)
		{
			return;
		}
		_focusedLink = Math.Clamp(index, 0, _links.Length - 1);
		Focus();
		InvalidateVisual();
	}

	public int OffsetAt(Point windowPoint)
	{
		if (_chunks.Count == 0)
		{
			return 0;
		}
		double localY = windowPoint.Y - Bounds.Y;
		if (localY <= 0)
		{
			return 0;
		}
		for (int index = 0; index < _chunks.Count; index++)
		{
			Chunk chunk = _chunks[index];
			ITextLayout layout = chunk.Layout!;
			if (localY < chunk.Top + layout.ContentHeight || index == _chunks.Count - 1)
			{
				double within = Math.Clamp(localY - chunk.Top, 0, Math.Max(0, layout.ContentHeight - 0.01));
				CharacterHit hit = layout.HitTestPoint(new Point(windowPoint.X - Bounds.X, within));
				return Math.Clamp(chunk.Start + hit.InsertionIndex, chunk.Start, chunk.End);
			}
		}
		return _text.Length;
	}

	public (int Start, int End) WordAt(int offset)
	{
		offset = Math.Clamp(offset, 0, _text.Length);
		if (_text.Length == 0)
		{
			return (0, 0);
		}
		int probe = Math.Min(offset, _text.Length - 1);
		if (!IsWordCharacter(_text[probe]))
		{
			int end = probe + 1;
			if (end < _text.Length && char.IsSurrogatePair(_text[probe], _text[end]))
			{
				end++;
			}
			return (probe, end);
		}
		int start = probe;
		while (start > 0 && IsWordCharacter(_text[start - 1]))
		{
			start--;
		}
		int finish = probe + 1;
		while (finish < _text.Length && IsWordCharacter(_text[finish]))
		{
			finish++;
		}
		return (start, finish);
	}

	private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

	public void SetSelection(int start, int end)
	{
		start = Math.Clamp(start, 0, _text.Length);
		end = Math.Clamp(end, 0, _text.Length);
		if (end <= start)
		{
			start = 0;
			end = 0;
		}
		if (start == _selectionStart && end == _selectionEnd)
		{
			return;
		}
		_selectionStart = start;
		_selectionEnd = end;
		InvalidateVisual();
	}

	private void EnsureLayout(double width)
	{
		double targetWidth = _wrap && double.IsFinite(width) ? Math.Max(1, width) : UnwrappedWidth;
		var engine = GetGraphicsFactory().TextEngine;
		// FontWeight is the inherited property: headings and table headers set it on the element,
		// so a header cell actually renders bold instead of silently using Normal.
		var style = new TextRunStyle(
			FontFamily,
			FontSize * _fontScale,
			FontWeight,
			FontStyle == FontStyle.Italic);
		uint dpi = GetDpi();

		if (_engine is not null && !ReferenceEquals(_engine, engine))
		{
			foreach (Chunk chunk in _chunks)
			{
				_engine.ManagedCache.ReleaseOwner(chunk);
			}
		}
		if (_width != targetWidth || _dpi != dpi || _style != style || !ReferenceEquals(_engine, engine))
		{
			_width = targetWidth;
			_dpi = dpi;
			_style = style;
			_engine = engine;
			foreach (Chunk chunk in _chunks)
			{
				engine.ManagedCache.ReleaseOwner(chunk);
				chunk.Layout = null;
			}
		}
		_engine = engine;

		if (_chunks.Count == 0)
		{
			_chunks.Add(new Chunk(0, _text.Length));
		}

		for (int index = 0; index < _chunks.Count; index++)
		{
			Chunk chunk = _chunks[index];
			if (chunk.Layout is null)
			{
				chunk.Text ??= _text.Substring(chunk.Start, chunk.End - chunk.Start);
				chunk.Layout = CreateLayout(chunk, engine, style, dpi, targetWidth);
			}
		}

		// Freeze closed lines when the open chunk grows past the budget. Splitting at the last
		// line's start keeps the open chunk to exactly the line that can still change.
		while (TrySplitOpenChunk(engine, style, dpi, targetWidth))
		{
		}

		double top = 0;
		for (int index = 0; index < _chunks.Count; index++)
		{
			Chunk chunk = _chunks[index];
			chunk.Top = top;
			top += chunk.Layout!.ContentHeight;
		}
	}

	private ITextLayout CreateLayout(Chunk chunk, ITextEngine engine, TextRunStyle style, uint dpi, double width)
	{
		List<GeometryStyleRun> runs = [];
		for (int index = 0; index < _runs.Length; index++)
		{
			int runStart = _runStarts[index];
			int runLength = VisualText(_runs[index]).Length;
			int runEnd = runStart + runLength;
			int from = Math.Max(runStart, chunk.Start);
			int to = Math.Min(runEnd, chunk.End);
			if (to > from)
			{
				runs.Add(new GeometryStyleRun(
					from - chunk.Start,
					to - from,
					ResolveStyle(style, _runs[index])));
			}
		}

		return engine.GetOrCreateLayout(new TextLayoutRequest
		{
			Text = chunk.Text.AsMemory(),
			DefaultStyle = style,
			Dpi = dpi,
			Runs = runs,
			Paragraph = new TextParagraphStyle
			{
				MaxWidth = width,
				Wrapping = _wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
				Alignment = _block.Alignment
			}
		}, TextLayoutCachePolicy.Owner, chunk);
	}

	private bool TrySplitOpenChunk(ITextEngine engine, TextRunStyle style, uint dpi, double width)
	{
		if (_chunks.Count == 0)
		{
			return false;
		}
		Chunk last = _chunks[^1];
		ITextLayout layout = last.Layout!;
		if (last.End - last.Start <= MaxOpenChars && layout.Lines.Count <= MaxOpenLines)
		{
			return false;
		}
		if (layout.Lines.Count <= 1)
		{
			return false;
		}
		TextLayoutLineMetrics lastLine = layout.Lines[^1];
		int frozenEnd;
		int openStart;
		if (lastLine.TextLength == 0 && layout.Lines.Count >= 2)
		{
			// The open chunk is the empty line after a trailing newline. Freeze the previous
			// line without its newline, and start the open chunk after it, so neither chunk
			// grows a synthetic empty line.
			TextLayoutLineMetrics previous = layout.Lines[^2];
			frozenEnd = last.Start + previous.TextEnd;
			openStart = frozenEnd + previous.NewLineLength;
		}
		else
		{
			frozenEnd = last.Start + lastLine.TextStart;
			openStart = frozenEnd;
		}
		if (frozenEnd <= last.Start || openStart >= last.End)
		{
			return false;
		}

		Chunk frozen = new(last.Start, frozenEnd);
		frozen.Text = _text.Substring(frozen.Start, frozen.End - frozen.Start);
		frozen.Layout = CreateLayout(frozen, engine, style, dpi, width);
		Chunk open = new(openStart, last.End);
		open.Text = _text.Substring(open.Start, open.End - open.Start);
		open.Layout = CreateLayout(open, engine, style, dpi, width);
		engine.ManagedCache.ReleaseOwner(last);
		_chunks[^1] = frozen;
		_chunks.Add(open);
		return true;
	}

	private TextRunStyle ResolveStyle(TextRunStyle style, MarkdownRun run)
	{
		bool code = (run.Style & MarkdownRunStyle.Code) != 0;
		return style with
		{
			FontFamily = code ? _theme.CodeFontFamily : style.FontFamily,
			FontSize = code ? style.FontSize * _theme.InlineCodeFontScale : style.FontSize,
			Weight = (run.Style & MarkdownRunStyle.Bold) != 0 ? FontWeight.Bold : style.Weight,
			Italic = (run.Style & MarkdownRunStyle.Italic) != 0 || style.Italic,
			Decoration = ((run.Style & MarkdownRunStyle.Strike) != 0 ? TextDecoration.Strikethrough : TextDecoration.None) |
				((run.Style & MarkdownRunStyle.Inserted) != 0 ? TextDecoration.Underline : TextDecoration.None)
		};
	}

	protected override Size MeasureContent(Size availableSize)
	{
		EnsureLayout(availableSize.Width);
		double width = 0;
		double height = 0;
		foreach (Chunk chunk in _chunks)
		{
			width = Math.Max(width, chunk.Layout!.MeasuredSize.Width);
			height = chunk.Top + chunk.Layout.ContentHeight;
		}
		double scale = GetDpi() / 96.0;
		double measuredWidth = scale > 0 ? Math.Ceiling(width * scale) / scale + 1 / scale : width;
		return new Size(measuredWidth, height);
	}

	protected override void ArrangeContent(Rect bounds)
	{
		if (_wrap && double.IsFinite(bounds.Width) && Math.Abs(bounds.Width - _width) > 0.01)
		{
			InvalidateMeasure();
		}
	}

	protected override void OnRender(IGraphicsContext context)
	{
		if (_chunks.Count == 0)
		{
			return;
		}

		for (int index = 0; index < _chunks.Count; index++)
		{
			Chunk chunk = _chunks[index];
			ITextLayout layout = chunk.Layout!;
			DrawInlineCodeChips(context, chunk);
			List<TextPaintSpan> paints = [];
			for (int runIndex = 0; runIndex < _runs.Length; runIndex++)
			{
				MarkdownRun run = _runs[runIndex];
				int runStart = _runStarts[runIndex];
				int runLength = VisualText(run).Length;
				int from = Math.Max(runStart, chunk.Start);
				int to = Math.Min(runStart + runLength, chunk.End);
				if (to <= from)
				{
					continue;
				}
				bool link = run.Url is not null && !run.Image;
				// Inline code gets a padded, rounded chip drawn behind the text instead of a
				// per-glyph rectangular background.
				Color? background = (run.Style & MarkdownRunStyle.Marked) != 0
					? _theme.MarkedBackground ?? Theme.Palette.Accent.WithAlpha(64)
					: null;
				paints.Add(new TextPaintSpan(
					new TextRange(from - chunk.Start, to - from),
					link ? _theme.LinkForeground ?? Theme.Palette.Accent : null,
					background,
					link ? TextDecoration.Underline : TextDecoration.None));
			}

			if (_selectionEnd > _selectionStart)
			{
				int from = Math.Max(_selectionStart, chunk.Start);
				int to = Math.Min(_selectionEnd, chunk.End);
				if (to > from)
				{
					paints.Add(new TextPaintSpan(
						new TextRange(from - chunk.Start, to - from),
						null,
						Theme.Palette.SelectionBackground));
				}
			}

			var options = new TextDrawOptions(
				Foreground,
				paints.Count == 0 ? default : paints.ToArray(),
				Owner: chunk,
				Transient: IsStreaming && index == _chunks.Count - 1);
			context.Text.Draw(layout, new Point(Bounds.X, Bounds.Y + chunk.Top), in options);
		}

		if (IsFocused && _links.Length > 0)
		{
			DrawFocusedLink(context);
		}
	}

	private void DrawInlineCodeChips(IGraphicsContext context, Chunk chunk)
	{
		if (_runs.Length == 0)
		{
			return;
		}
		Color background = default;
		Color border = default;
		var colorsReady = false;
		double padX = Math.Max(0, _theme.InlineCodePadding);
		double padY = Math.Max(0, _theme.InlineCodeVerticalPadding);
		double radius = Math.Max(0, _theme.InlineCodeCornerRadius);
		double thickness = LayoutRounding.SnapThicknessToPixels(1, context.DpiScale, 1);
		for (int runIndex = 0; runIndex < _runs.Length; runIndex++)
		{
			MarkdownRun run = _runs[runIndex];
			if ((run.Style & MarkdownRunStyle.Code) == 0)
			{
				continue;
			}
			int runStart = _runStarts[runIndex];
			int runLength = VisualText(run).Length;
			int from = Math.Max(runStart, chunk.Start);
			int to = Math.Min(runStart + runLength, chunk.End);
			if (to <= from)
			{
				continue;
			}
			if (!colorsReady)
			{
				background = _theme.InlineCodeBackgroundSelector?.Invoke(Theme)
					?? _theme.InlineCodeBackground
					?? _theme.CodeBackground
					?? Theme.Palette.ButtonFace;
				border = _theme.InlineCodeBorderSelector?.Invoke(Theme)
					?? _theme.InlineCodeBorder
					?? Theme.Palette.ControlBorder;
				colorsReady = true;
			}
			_rangeBounds.Clear();
			chunk.Layout!.GetRangeBounds(from - chunk.Start, to - from, _rangeBounds);
			foreach (Rect rectangle in _rangeBounds)
			{
				if (rectangle.Width <= 0 || rectangle.Height <= 0)
				{
					continue;
				}
				Rect chip = new(
					Bounds.X + rectangle.X - padX,
					Bounds.Y + chunk.Top + rectangle.Y - padY,
					rectangle.Width + padX * 2,
					rectangle.Height + padY * 2);
				double chipRadius = Math.Min(radius, Math.Min(chip.Width, chip.Height) / 2);
				if (background.A > 0)
				{
					context.FillRoundedRectangle(chip, chipRadius, chipRadius, background);
				}
				if (border.A > 0)
				{
					context.DrawRoundedRectangle(chip, chipRadius, chipRadius, border, thickness, strokeInset: true);
				}
			}
		}
	}

	private void DrawFocusedLink(IGraphicsContext context)
	{
		LinkRun link = _links[Math.Clamp(_focusedLink, 0, _links.Length - 1)];
		double thickness = LayoutRounding.SnapThicknessToPixels(1, context.DpiScale, 1);
		foreach (Chunk chunk in _chunks)
		{
			int from = Math.Max(link.Start, chunk.Start);
			int to = Math.Min(link.Start + link.Length, chunk.End);
			if (to <= from)
			{
				continue;
			}
			_rangeBounds.Clear();
			chunk.Layout!.GetRangeBounds(from - chunk.Start, to - from, _rangeBounds);
			foreach (Rect rectangle in _rangeBounds)
			{
				Rect focus = LayoutRounding.SnapBoundsRectToPixels(
					new Rect(Bounds.X + rectangle.X, Bounds.Y + chunk.Top + rectangle.Y, rectangle.Width, rectangle.Height),
					context.DpiScale);
				context.DrawRectangle(focus, Theme.Palette.Focus, thickness, strokeInset: true);
			}
		}
	}

	internal int HitLink(Point position)
	{
		if (!Bounds.Contains(position))
		{
			return -1;
		}
		var local = new Point(position.X - Bounds.X, position.Y - Bounds.Y);
		for (int linkIndex = 0; linkIndex < _links.Length; linkIndex++)
		{
			LinkRun link = _links[linkIndex];
			foreach (Chunk chunk in _chunks)
			{
				int from = Math.Max(link.Start, chunk.Start);
				int to = Math.Min(link.Start + link.Length, chunk.End);
				if (to <= from)
				{
					continue;
				}
				_rangeBounds.Clear();
				chunk.Layout!.GetRangeBounds(from - chunk.Start, to - from, _rangeBounds);
				foreach (Rect rectangle in _rangeBounds)
				{
					if (new Rect(rectangle.X, rectangle.Y + chunk.Top, rectangle.Width, rectangle.Height).Contains(local))
					{
						return linkIndex;
					}
				}
			}
		}
		return -1;
	}

	protected override void OnMouseMove(MouseEventArgs args)
	{
		base.OnMouseMove(args);
		Cursor = IsEffectivelyEnabled && HitLink(WindowPosition(args)) >= 0 ? CursorType.Hand : null;
	}

	protected override void OnMouseDown(MouseEventArgs args)
	{
		base.OnMouseDown(args);
		if (args.Handled || !IsEffectivelyEnabled || args.Button != MouseButton.Left)
		{
			return;
		}
		_pressedLink = HitLink(WindowPosition(args));
		if (_pressedLink >= 0)
		{
			_focusedLink = _pressedLink;
			Focus();
			InvalidateVisual();
			args.Handled = true;
		}
	}

	protected override void OnMouseUp(MouseEventArgs args)
	{
		base.OnMouseUp(args);
		int pressed = _pressedLink;
		_pressedLink = -1;
		if (!args.Handled && IsEffectivelyEnabled && args.Button == MouseButton.Left &&
			pressed >= 0 && pressed < _links.Length && HitLink(WindowPosition(args)) == pressed)
		{
			args.Handled = true;
			_activate?.Invoke(_links[pressed].Run);
		}
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();
		_pressedLink = -1;
		Cursor = null;
	}

	private Point WindowPosition(MouseEventArgs args)
	{
		var local = args.GetPosition(this);
		return new Point(Bounds.X + local.X, Bounds.Y + local.Y);
	}

	protected override void OnKeyDown(KeyEventArgs args)
	{
		base.OnKeyDown(args);
		if (args.Handled || !IsEffectivelyEnabled || _links.Length == 0)
		{
			return;
		}
		if (args.Key == Key.Enter || args.Key == Key.Space)
		{
			args.Handled = true;
			if (!args.IsRepeat)
			{
				_activate?.Invoke(_links[_focusedLink].Run);
			}
		}
		else if (args.Key == Key.Tab)
		{
			int next = _focusedLink + (args.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
			if (next >= 0 && next < _links.Length)
			{
				_focusedLink = next;
				args.Handled = true;
				InvalidateVisual();
			}
		}
	}

	protected override void OnLostFocus()
	{
		base.OnLostFocus();
		_focusedLink = 0;
		_pressedLink = -1;
	}

	protected override void OnDispose()
	{
		ReleaseChunks();
		_chunks.Clear();
		_engine = null;
		base.OnDispose();
	}

	private sealed class Chunk(int start, int end)
	{
		public int Start { get; } = start;
		public int End { get; set; } = end;
		public string? Text { get; set; }
		public ITextLayout? Layout { get; set; }
		public double Top { get; set; }
	}

	private readonly record struct LinkRun(int Start, int Length, MarkdownRun Run);
}
