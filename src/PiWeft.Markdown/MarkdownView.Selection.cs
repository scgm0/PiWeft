using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Markdown;

public sealed partial class MarkdownView
{
	/// <summary>Selects the whole document, including blocks that are not realized.</summary>
	public void SelectAll()
	{
		if (!IsSelectionEnabled || _document is null || _document.TextUnits.Count == 0)
		{
			return;
		}
		int last = _document.TextUnits.Count - 1;
		SetSelection((0, 0), (last, _document.TextUnits[last].Text.Length));
	}

	/// <summary>Clears the selection.</summary>
	public void ClearSelection() => SetSelection(null, null);

	/// <summary>Copies the selected text; returns false when there is nothing selected or a handler cancels.</summary>
	public bool CopySelection()
	{
		string text = _selectedText;
		if (text.Length == 0)
		{
			return false;
		}
		MarkdownCopyingEventArgs args = new(text);
		Copying?.Invoke(args);
		if (args.Cancel || string.IsNullOrEmpty(args.Text) || !Application.IsRunning)
		{
			return false;
		}
		return Application.Current?.PlatformServices.Clipboard?.TrySetText(args.Text) == true;
	}

	private void SetSelection((int Unit, int Offset)? anchor, (int Unit, int Offset)? focus)
	{
		if (anchor == _selectionAnchor && focus == _selectionFocus)
		{
			return;
		}
		_selectionAnchor = anchor;
		_selectionFocus = focus;
		ApplySelectionToRealized();
		string text = BuildSelectedText();
		_selectedText = text;
		_hasSelection = text.Length > 0;
		SelectionChanged?.Invoke();
	}

	private ((int Unit, int Offset) Start, (int Unit, int Offset) End)? OrderedSelection()
	{
		if (_selectionAnchor is not (int, int) anchor || _selectionFocus is not (int, int) focus || anchor == focus)
		{
			return null;
		}
		return anchor.CompareTo(focus) <= 0 ? (anchor, focus) : (focus, anchor);
	}

	/// <summary>Recomputes the copied text after the document model changed.</summary>
	private void RefreshSelectedText()
	{
		string text = BuildSelectedText();
		if (string.Equals(text, _selectedText, StringComparison.Ordinal))
		{
			return;
		}
		_selectedText = text;
		_hasSelection = text.Length > 0;
		SelectionChanged?.Invoke();
	}

	private string BuildSelectedText()
	{
		var range = OrderedSelection();
		if (_document is null || range is null)
		{
			return string.Empty;
		}
		var (start, end) = range.Value;
		var units = _document.TextUnits;
		StringBuilder builder = new();
		for (int unit = start.Unit; unit <= end.Unit && unit < units.Count; unit++)
		{
			string text = units[unit].Text;
			int from = unit == start.Unit ? Math.Clamp(start.Offset, 0, text.Length) : 0;
			int to = unit == end.Unit ? Math.Clamp(end.Offset, 0, text.Length) : text.Length;
			if (unit > start.Unit)
			{
				builder.Append(units[unit].Separator);
			}
			if (from == 0)
			{
				builder.Append(units[unit].ListMarkerPrefix);
			}
			builder.Append(text, from, Math.Max(0, to - from));
		}
		// The parser inserts thin spaces outside inline-code runs purely for layout. They are
		// invisible visually but should not leak into copied text.
		return builder.ToString().Replace(
			MarkdownParser.InlineCodeMargin.ToString(), string.Empty, StringComparison.Ordinal);
	}

	private void ApplySelectionTo(ISelectableText element)
	{
		if (element.TextUnit < 0)
		{
			return;
		}
		var range = OrderedSelection();
		if (range is null)
		{
			element.SetSelection(0, 0);
			return;
		}
		var (start, end) = range.Value;
		int unit = element.TextUnit;
		if (unit < start.Unit || unit > end.Unit)
		{
			element.SetSelection(0, 0);
			return;
		}
		int from = unit == start.Unit ? start.Offset : 0;
		int to = unit == end.Unit ? end.Offset : element.TextLength;
		element.SetSelection(from, to);
	}

	private void ApplySelectionToRealized()
	{
		foreach (ISelectableText element in RealizedSelectableElements())
		{
			ApplySelectionTo(element);
		}
	}

	private IEnumerable<ISelectableText> RealizedSelectableElements()
	{
		Stack<Element> pending = new();
		for (int index = _slots.Count - 1; index >= 0; index--)
		{
			BlockSlot slot = _slots[index];
			if (slot.Attached && slot.Element is not null)
			{
				pending.Push(slot.Element);
			}
		}
		List<Element> children = [];
		while (pending.Count > 0)
		{
			Element current = pending.Pop();
			if (current is ISelectableText selectable && selectable.TextUnit >= 0)
			{
				yield return selectable;
			}
			if (current is IVisualTreeHost host)
			{
				children.Clear();
				host.VisitChildren(child =>
				{
					children.Add(child);
					return true;
				});
				for (int index = children.Count - 1; index >= 0; index--)
				{
					pending.Push(children[index]);
				}
			}
		}
	}

	private (int Unit, int Offset, ISelectableText Element)? ResolveSelectionPoint(Point windowPoint)
	{
		ISelectableText? best = null;
		double bestDistance = double.PositiveInfinity;
		foreach (ISelectableText element in RealizedSelectableElements())
		{
			Rect bounds = element.Bounds;
			if (bounds.Contains(windowPoint))
			{
				// Table cells and list columns share a row; the exact bounds decide before any
				// distance heuristic can pick the neighbouring cell.
				return (element.TextUnit, element.OffsetAt(windowPoint), element);
			}

			double vertical = windowPoint.Y < bounds.Y ? bounds.Y - windowPoint.Y
				: windowPoint.Y > bounds.Bottom ? windowPoint.Y - bounds.Bottom : 0;
			double horizontal = windowPoint.X < bounds.X ? bounds.X - windowPoint.X
				: windowPoint.X > bounds.Right ? windowPoint.X - bounds.Right : 0;
			// Vertical distance dominates, but a point in the same row as two table cells must
			// resolve to the cell it is horizontally closest to.
			double distance = vertical * 1000 + horizontal;
			if (distance < bestDistance)
			{
				best = element;
				bestDistance = distance;
			}
		}
		if (best is null)
		{
			return null;
		}
		return (best.TextUnit, best.OffsetAt(windowPoint), best);
	}

	internal (int Unit, int Offset)? SelectionAnchorForTest => _selectionAnchor;
	internal (int Unit, int Offset)? SelectionFocusForTest => _selectionFocus;

	internal bool BeginSelectionAtForTest(Point windowPoint, int clickCount = 1)
		=> BeginSelectionAt(windowPoint, clickCount);

	internal void ExtendSelectionToForTest(Point windowPoint)
		=> ExtendSelectionTo(windowPoint);

	private bool BeginSelectionAt(Point windowPoint, int clickCount = 1)
	{
		var hit = ResolveSelectionPoint(windowPoint);
		if (hit is null)
		{
			return false;
		}
		var (unit, offset, element) = hit.Value;
		if (clickCount >= 3)
		{
			SetSelection((unit, 0), (unit, element.TextLength));
		}
		else if (clickCount == 2)
		{
			var (start, end) = element.WordAt(offset);
			SetSelection((unit, start), (unit, end));
		}
		else
		{
			SetSelection((unit, offset), (unit, offset));
		}
		return true;
	}

	private void ExtendSelectionTo(Point windowPoint)
	{
		if (_selectionAnchor is null)
		{
			return;
		}
		var hit = ResolveSelectionPoint(windowPoint);
		if (hit is not null)
		{
			SetSelection(_selectionAnchor, (hit.Value.Unit, hit.Value.Offset));
		}
	}

	protected override void OnMouseDown(MouseEventArgs args)
	{
		base.OnMouseDown(args);
		if (args.Handled || !IsSelectionEnabled || !IsEffectivelyEnabled)
		{
			return;
		}
		if (args.Button == MouseButton.Right)
		{
			ShowDefaultContextMenu(args);
			return;
		}
		if (args.Button != MouseButton.Left)
		{
			return;
		}
		Point point = WindowPoint(args);
		if (args.ClickCount == 1 && args.Modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchor is not null)
		{
			ExtendSelectionTo(point);
		}
		else if (!BeginSelectionAt(point, args.ClickCount))
		{
			return;
		}
		Focus();
		_dragSelecting = args.ClickCount == 1;
		if (_dragSelecting && FindVisualRoot() is Window window)
		{
			window.CaptureMouse(this);
		}
		args.Handled = true;
	}

	protected override void OnMouseMove(MouseEventArgs args)
	{
		base.OnMouseMove(args);
		if (!_dragSelecting || !args.LeftButton)
		{
			return;
		}
		Point point = WindowPoint(args);
		AutoScrollTowards(point);
		ExtendSelectionTo(point);
		args.Handled = true;
	}

	protected override void OnMouseUp(MouseEventArgs args)
	{
		base.OnMouseUp(args);
		if (args.Button == MouseButton.Left && _dragSelecting)
		{
			_dragSelecting = false;
			if (FindVisualRoot() is Window window)
			{
				window.ReleaseMouseCapture();
			}
		}
	}

	protected override void OnKeyDown(KeyEventArgs args)
	{
		base.OnKeyDown(args);
		if (args.Handled)
		{
			return;
		}
		if (IsSelectionEnabled && args.Key == Key.Escape && _hasSelection)
		{
			ClearSelection();
			args.Handled = true;
		}
	}

	private void ShowDefaultContextMenu(MouseEventArgs args)
	{
		if (ContextMenu is not null)
		{
			return;
		}
		// Focus first so the menu's commands resolve to this view's selection.
		Focus();
		var menu = _defaultContextMenu ??= new ContextMenu();
		menu.Items.Clear();
		menu.AddItem(StandardCommands.Copy);
		menu.AddSeparator();
		menu.AddItem(StandardCommands.SelectAll);
		menu.Show(this, WindowPoint(args));
		args.Handled = true;
	}

	private Point WindowPoint(MouseEventArgs args)
	{
		Point local = args.GetPosition(this);
		return new Point(Bounds.X + local.X, Bounds.Y + local.Y);
	}

	private void AutoScrollTowards(Point windowPoint)
	{
		if (_scroll is null)
		{
			return;
		}
		double top = _scroll.Bounds.Y;
		double bottom = top + _scroll.ViewportHeight;
		if (windowPoint.Y < top)
		{
			_scroll.SetScrollOffsets(_scroll.HorizontalOffset, _scroll.VerticalOffset + windowPoint.Y - top);
		}
		else if (windowPoint.Y > bottom)
		{
			_scroll.SetScrollOffsets(_scroll.HorizontalOffset, _scroll.VerticalOffset + windowPoint.Y - bottom);
		}
	}

	internal void RaiseLinkForTest(string url, string? title = null)
		=> LinkRequested?.Invoke(new MarkdownLinkRequestedEventArgs(url, ResolveUri(url, BaseUri), title, 0, 0));

	private void ActivateLink(MarkdownRun run)
	{
		string url = run.Url ?? string.Empty;
		if (url.StartsWith('#'))
		{
			NavigateToAnchor(Uri.UnescapeDataString(url[1..]));
		}
		LinkRequested?.Invoke(new MarkdownLinkRequestedEventArgs(
			url,
			ResolveUri(url, BaseUri),
			run.Title,
			run.SourceStart,
			run.SourceLength));
	}

	private bool NavigateToAnchor(string anchor)
	{
		if (_document is null || _scroll is null || !_document.AnchorBlocks.TryGetValue(anchor, out int index))
		{
			return false;
		}
		if (index < 0 || index >= _slots.Count)
		{
			return false;
		}
		double viewportTop = _scroll.Bounds.Y + _scroll.Padding.Top;
		double target = _scroll.VerticalOffset + (_slots[index].Top + Bounds.Y - viewportTop);
		_scroll.SetScrollOffsets(_scroll.HorizontalOffset, Math.Max(0, target));
		InvalidateMeasure();
		return true;
	}

	private static Uri? ResolveUri(string url, Uri? baseUri)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute))
		{
			return absolute;
		}
		if (baseUri?.IsAbsoluteUri == true && Uri.TryCreate(baseUri, url, out Uri? resolved))
		{
			return resolved;
		}
		return null;
	}
}
