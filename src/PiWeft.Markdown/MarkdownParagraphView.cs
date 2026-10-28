using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Resources;
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
/// Image runs render as scaled images stacked below the text; each is loaded through the host
/// resolver and re-measures the enclosing block when it arrives.
/// </summary>
internal sealed class MarkdownParagraphView : TextElement, ISelectableText, IVisualTreeHost
{
	// The open chunk stays small enough that one append costs a few milliseconds at most.
	private const int MaxOpenChars = 2048;
	private const int MaxOpenLines = 64;
	private const double UnwrappedWidth = 1_000_000;

	private readonly List<Chunk> _chunks = [];
	private readonly List<Rect> _rangeBounds = [];
	private readonly List<Rect> _selectionRects = [];
	private readonly List<ImageSlot> _images = [];
	private readonly Dictionary<string, ImageSource?> _resolvedImages = [];
	private readonly HashSet<string> _pendingImages = [];
	private CancellationTokenSource? _imageCancellation;
	private MarkdownBlock _block = null!;
	private string _text = string.Empty;
	private MarkdownRun[] _runs = [];
	private int[] _runStarts = [];
	private LinkRun[] _links = [];
	private MarkdownTheme _theme = new();
	private Action<MarkdownRun>? _activate;
	private Func<MarkdownImageRequest, CancellationToken, ValueTask<ImageSource?>>? _resolveImage;
	private Action<MarkdownParagraphView>? _imageLayoutChanged;
	private Action<MarkdownRun, ImageSource?>? _activateImage;
	private Uri? _baseUri;
	private double _width = double.NaN;
	private uint _dpi;
	private double _lineSpacing;
	private TextRunStyle _style;
	private ITextEngine? _engine;
	private int _selectionStart;
	private int _selectionEnd;
	private int _focusedLink;
	private int _pressedLink = -1;
	private int _pressedImage = -1;
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
	/// <summary>Layout width assigned by EnsureLayout; NaN until the paragraph was measured, so a
	/// NaN here on a realized element means it would render blank.</summary>
	internal double LayoutWidthForTest => _width;
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
		List<ImageSlot> images = [];
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
			if (run.Image && run.Url is not null && length > 0)
			{
				images.Add(new ImageSlot(run));
			}
			offset += length;
		}
		_links = links.Count == 0 ? [] : links.ToArray();
		RebuildImages(images);
		_focusedLink = _links.Length == 0 ? 0 : Math.Clamp(_focusedLink, 0, _links.Length - 1);
		_pressedLink = -1;
		_pressedImage = -1;
		Focusable = _links.Length > 0;
		BeginImageResolution();
	}

	private void AttachImageElement(ImageSlot slot)
	{
		Image image = new()
		{
			StretchMode = Stretch.Uniform,
			IsVisible = false,
		};
		slot.Element = image;
		AttachChild(image);
		if (_resolvedImages.TryGetValue(slot.Run.Url!, out ImageSource? source))
		{
			ApplyImageSource(slot, source);
		}
	}

	private void ApplyImageSource(ImageSlot slot, ImageSource? source)
	{
		slot.Source = source;
		if (slot.Element is { } image)
		{
			image.Source = source;
			image.IsVisible = source is not null;
		}
	}

	private void ReleaseImages()
	{
		foreach (ImageSlot slot in _images)
		{
			if (slot.Element is { } image)
			{
				slot.Element = null;
				DetachChild(image);
				DisposeTreeOf(image);
			}
		}
		_images.Clear();
	}

	private void DisposeTreeOf(Element element)
	{
		(element as IDisposable)?.Dispose();
	}

	/// <summary>
	/// Rebuilds the image slots after a run remap. Elements for URLs that persist across
	/// revisions are reused — a streaming tail re-maps the paragraph's runs every frame, and the
	/// image element plus its decoded source must survive that.
	/// </summary>
	private void RebuildImages(List<ImageSlot> next)
	{
		Dictionary<string, ImageSlot> reusable = new(StringComparer.Ordinal);
		foreach (ImageSlot slot in _images)
		{
			if (slot.Run.Url is { } url)
			{
				reusable[url] = slot;
			}
		}
		List<ImageSlot> stale = [.. _images];
		_images.Clear();
		foreach (ImageSlot slot in next)
		{
			if (slot.Run.Url is { } url && reusable.TryGetValue(url, out ImageSlot? existing))
			{
				slot.Element = existing.Element;
				slot.Source = existing.Source;
				stale.Remove(existing);
			}
			_images.Add(slot);
		}
		foreach (ImageSlot slot in stale)
		{
			if (slot.Element is { } image)
			{
				slot.Element = null;
				DetachChild(image);
				DisposeTreeOf(image);
			}
		}
		foreach (ImageSlot slot in _images)
		{
			if (slot.Element is null)
			{
				AttachImageElement(slot);
			}
		}
	}

	private void BeginImageResolution()
	{
		if (_resolveImage is null || _images.Count == 0)
		{
			return;
		}
		_imageCancellation ??= new CancellationTokenSource();
		foreach (ImageSlot slot in _images)
		{
			string url = slot.Run.Url!;
			if (slot.Source is not null || _resolvedImages.ContainsKey(url) || !_pendingImages.Add(url))
			{
				continue;
			}
			_ = ResolveImageSlotAsync(slot);
		}
	}

	private async Task ResolveImageSlotAsync(ImageSlot slot)
	{
		string url = slot.Run.Url!;
		CancellationToken cancellation = _imageCancellation?.Token ?? default;
		try
		{
			var request = new MarkdownImageRequest(url, ResolveImageUri(url, _baseUri), _width);
			ImageSource? source = await _resolveImage!(request, cancellation);
			if (cancellation.IsCancellationRequested)
			{
				return;
			}
			_pendingImages.Remove(url);
			_resolvedImages[url] = source;
			bool changed = false;
			foreach (ImageSlot current in _images)
			{
				if (string.Equals(current.Run.Url, url, StringComparison.Ordinal))
				{
					changed |= !ReferenceEquals(current.Source, source);
					ApplyImageSource(current, source);
				}
			}
			if (changed)
			{
				InvalidateMeasure();
				InvalidateVisual();
				_imageLayoutChanged?.Invoke(this);
			}
		}
		catch (OperationCanceledException)
		{
			_pendingImages.Remove(url);
		}
		catch (Exception)
		{
			// Remember the failure so a broken URL is not re-requested on every streaming frame.
			_pendingImages.Remove(url);
			_resolvedImages[url] = null;
		}
	}

	private static Uri? ResolveImageUri(string url, Uri? baseUri)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute))
		{
			return absolute;
		}
		return baseUri?.IsAbsoluteUri == true && Uri.TryCreate(baseUri, url, out Uri? resolved) ? resolved : null;
	}

	private int HitImage(Point position)
	{
		if (!Bounds.Contains(position))
		{
			return -1;
		}
		for (int index = 0; index < _images.Count; index++)
		{
			ImageSlot slot = _images[index];
			if (slot.Source is not null && slot.Bounds.Contains(position))
			{
				return index;
			}
		}
		return -1;
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

	/// <summary>
	/// Wires image loading. Called once when the view creates the element; the delegates survive
	/// every block refresh.
	/// </summary>
	internal void ConfigureImages(
		Func<MarkdownImageRequest, CancellationToken, ValueTask<ImageSource?>>? resolveImage,
		Action<MarkdownParagraphView>? imageLayoutChanged,
		Action<MarkdownRun, ImageSource?>? activateImage)
	{
		_resolveImage = resolveImage;
		_imageLayoutChanged = imageLayoutChanged;
		_activateImage = activateImage;
	}

	bool IVisualTreeHost.VisitChildren(Func<Element, bool> visitor)
	{
		foreach (ImageSlot slot in _images)
		{
			if (slot.Element is { } image && !visitor(image))
			{
				return false;
			}
		}
		return true;
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
		// Every chunk of this paragraph shares one leading value: it is a property of the block's
		// font size, not of the text, so it is resolved once per layout pass.
		_lineSpacing = ResolveLineSpacing(engine, style, dpi);

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
			// A chunk boundary is a line boundary, and the engine only puts leading *between* the
			// lines it knows about: without this the frozen chunk swallows the gap that a single
			// pass would have produced, and a streamed paragraph ends up shorter than the same
			// text laid out at once.
			if (index < _chunks.Count - 1)
			{
				top += _lineSpacing;
			}
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
				Alignment = _block.Alignment,
				LineSpacing = _lineSpacing
			}
		}, TextLayoutCachePolicy.Owner, chunk);
	}

	/// <summary>
	/// Extra leading between the lines of this block, in DIPs, from <see cref="MarkdownMetrics"/>:
	/// the theme's line-height multiplier applied to the rendered font size, minus the height the
	/// font already reserves. Never negative, because a multiplier below the font's natural height
	/// would make lines overlap - CJK fonts ask for more room than Latin ones, so "tighten" stays a
	/// no-op there instead of cutting glyphs. The gap sits between lines only, so a block's top and
	/// bottom edges stay flush with the text and <see cref="MarkdownTheme.BlockSpacing"/> keeps
	/// describing the real distance between blocks.
	/// </summary>
	private double ResolveLineSpacing(ITextEngine engine, TextRunStyle style, uint dpi)
		=> MarkdownMetrics.LineSpacing(
			engine,
			style,
			dpi,
			_block.Kind == MarkdownBlockKind.Heading ? _theme.HeadingLineHeightScale : _theme.LineHeightScale);

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
		bool script = (run.Style & MarkdownRunStyle.Script) != 0;
		double fontSize = style.FontSize;
		if (code)
		{
			fontSize *= _theme.InlineCodeFontScale;
		}
		if (script)
		{
			// Sub/sup from the inline HTML whitelist: the text engine has no baseline shift, so
			// scripts render one step smaller on the shared baseline.
			fontSize *= 0.75;
		}
		return style with
		{
			FontFamily = code ? _theme.CodeFontFamily : style.FontFamily,
			FontSize = fontSize,
			Weight = (run.Style & MarkdownRunStyle.Bold) != 0 ? FontWeight.Bold : style.Weight,
			Italic = (run.Style & MarkdownRunStyle.Italic) != 0 || style.Italic,
			Decoration = ((run.Style & MarkdownRunStyle.Strike) != 0 ? TextDecoration.Strikethrough : TextDecoration.None) |
				((run.Style & MarkdownRunStyle.Inserted) != 0 || (run.Style & MarkdownRunStyle.Underline) != 0
					? TextDecoration.Underline
					: TextDecoration.None)
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
		double imageSpacing = Math.Max(0, _theme.ImageSpacing);
		double availableWidth = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : width;
		foreach (ImageSlot slot in _images)
		{
			if (slot.Source is not { } source)
			{
				continue;
			}
			double naturalWidth = Math.Max(1, source.PixelWidth) / scale;
			double naturalHeight = Math.Max(1, source.PixelHeight) / scale;
			double imageWidth = Math.Min(naturalWidth, availableWidth);
			double imageHeight = imageWidth * naturalHeight / naturalWidth;
			if (slot.Element is { } image)
			{
				image.Width = imageWidth;
				image.Height = imageHeight;
				image.Measure(new Size(imageWidth, double.PositiveInfinity));
			}
			slot.ImageTop = height + (height > 0 ? imageSpacing : 0);
			slot.Bounds = new Rect(Bounds.X, Bounds.Y + slot.ImageTop, imageWidth, imageHeight);
			height = slot.ImageTop + imageHeight;
			width = Math.Max(width, imageWidth);
		}
		double measuredWidth = scale > 0 ? Math.Ceiling(width * scale) / scale + 1 / scale : width;
		return new Size(measuredWidth, height);
	}

	protected override void ArrangeContent(Rect bounds)
	{
		if (_wrap && double.IsFinite(bounds.Width) && Math.Abs(bounds.Width - _width) > 0.01)
		{
			InvalidateMeasure();
		}
		foreach (ImageSlot slot in _images)
		{
			if (slot.Source is not null && slot.Element is { } image)
			{
				slot.Bounds = new Rect(bounds.X, bounds.Y + slot.ImageTop, image.Width, image.Height);
				image.Arrange(slot.Bounds);
			}
		}
	}

	private bool MissingLayout()
	{
		foreach (Chunk chunk in _chunks)
		{
			if (chunk.Layout is null) return true;
		}
		return false;
	}

	protected override void OnRender(IGraphicsContext context)
	{
		if (_chunks.Count == 0)
		{
			return;
		}

		// 流式追加只会把尾块版面置空再 InvalidateVisual：动画脉冲驱动的连续渲染可能先于测量到来，
		// 这时拿到的就是 null 版面，一个异常会让整帧的绘制全部作废（看到的就是丢帧）。
		// 这里按当前 arrange 宽度补一次版面：EnsureLayout 幂等，真正的测量随后只会命中缓存。
		// 还没排过版（宽度不可用）时不补：那种版面只会白建一遍再被测量推翻，留给下一帧。
		if (MissingLayout())
		{
			if (Bounds.Width <= 1) return;
			EnsureLayout(Bounds.Width);
		}

		for (int index = 0; index < _chunks.Count; index++)
		{
			Chunk chunk = _chunks[index];
			ITextLayout layout = chunk.Layout!;
			DrawInlineCodeChips(context, chunk);
			DrawSelection(context, chunk);
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

		foreach (ImageSlot slot in _images)
		{
			slot.Element?.Render(context);
		}
	}

	/// <summary>
	/// 选区底色按「行框」铺，不按「字形带」铺：MewUI 的行范围矩形只有一行的自然高度，
	/// 加了行间距后逐行选区会变成一条条横纹（浏览器/PiDeck 把半行距也涂进去，选区是连续的）。
	/// 上下各扩半个行间距后，行与行之间正好接上；块与块之间只会伸进 BlockSpacing 一半，
	/// 不会盖到相邻块的文字（BlockSpacing 8 ≥ 半行间距 ≤ 3.3 @16px）。
	/// </summary>
	private void DrawSelection(IGraphicsContext context, Chunk chunk)
	{
		Color selection = Theme.Palette.SelectionBackground;
		if (selection.A == 0)
		{
			return;
		}
		_selectionRects.Clear();
		CollectSelectionRects(chunk, _selectionRects);
		foreach (Rect rect in _selectionRects)
		{
			context.FillRectangle(
				new Rect(Bounds.X + rect.X, Bounds.Y + rect.Y, rect.Width, rect.Height),
				selection);
		}
	}

	/// <summary>本段落选区要涂的矩形（坐标相对段落左上，含行间距扩展）。</summary>
	internal IReadOnlyList<Rect> SelectionRectsForTest()
	{
		_selectionRects.Clear();
		foreach (Chunk chunk in _chunks)
		{
			CollectSelectionRects(chunk, _selectionRects);
		}
		return _selectionRects;
	}

	private void CollectSelectionRects(Chunk chunk, List<Rect> into)
	{
		int from = Math.Max(_selectionStart, chunk.Start);
		int to = Math.Min(_selectionEnd, chunk.End);
		if (to <= from || chunk.Layout is null)
		{
			return;
		}
		double inflate = _lineSpacing / 2;
		_rangeBounds.Clear();
		chunk.Layout.GetRangeBounds(from - chunk.Start, to - from, _rangeBounds);
		foreach (Rect rectangle in _rangeBounds)
		{
			if (rectangle.Width <= 0 || rectangle.Height <= 0)
			{
				continue;
			}
			Rect box = InflateLine(rectangle, inflate);
			into.Add(new Rect(box.X, chunk.Top + box.Y, box.Width, box.Height));
		}
	}

	/// <summary>把一行的选区矩形上下各扩 inflate（行间距的一半），使逐行选区连成一片。</summary>
	internal static Rect InflateLine(Rect line, double inflate)
		=> inflate <= 0
			? line
			: new Rect(line.X, line.Y - inflate, line.Width, line.Height + inflate * 2);

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
		Cursor = IsEffectivelyEnabled && (HitLink(WindowPosition(args)) >= 0 || HitImage(WindowPosition(args)) >= 0)
			? CursorType.Hand
			: null;
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
			return;
		}
		_pressedImage = HitImage(WindowPosition(args));
		if (_pressedImage >= 0)
		{
			args.Handled = true;
		}
	}

	protected override void OnMouseUp(MouseEventArgs args)
	{
		base.OnMouseUp(args);
		Point position = WindowPosition(args);
		int pressed = _pressedLink;
		_pressedLink = -1;
		if (!args.Handled && IsEffectivelyEnabled && args.Button == MouseButton.Left &&
			pressed >= 0 && pressed < _links.Length && HitLink(position) == pressed)
		{
			args.Handled = true;
			_activate?.Invoke(_links[pressed].Run);
			return;
		}
		int pressedImage = _pressedImage;
		_pressedImage = -1;
		if (!args.Handled && IsEffectivelyEnabled && args.Button == MouseButton.Left &&
			pressedImage >= 0 && pressedImage < _images.Count && HitImage(position) == pressedImage)
		{
			args.Handled = true;
			ImageSlot image = _images[pressedImage];
			_activateImage?.Invoke(image.Run, image.Source);
		}
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();
		_pressedLink = -1;
		_pressedImage = -1;
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
		_imageCancellation?.Cancel();
		_imageCancellation?.Dispose();
		_imageCancellation = null;
		ReleaseImages();
		_pendingImages.Clear();
		_resolvedImages.Clear();
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

	/// <summary>One image run and its resolved element; kept across run remaps by URL.</summary>
	private sealed class ImageSlot(MarkdownRun run)
	{
		public MarkdownRun Run { get; } = run;
		public ImageSource? Source;
		public Image? Element;
		public double ImageTop;
		public Rect Bounds;
	}

	private readonly record struct LinkRun(int Start, int Length, MarkdownRun Run);
}
