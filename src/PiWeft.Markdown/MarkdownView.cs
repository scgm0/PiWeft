using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;
using Aprillz.MewUI.Text.Editing;

namespace PiWeft.Markdown;

/// <summary>Raised when the user activates a link; the host decides what to open.</summary>
public sealed class MarkdownLinkRequestedEventArgs(
	string url,
	Uri? resolvedUri,
	string? title,
	int sourceStart,
	int sourceLength) : EventArgs
{
	public string Url { get; } = url;
	public Uri? ResolvedUri { get; } = resolvedUri;
	public string? Title { get; } = title;
	public int SourceStart { get; } = sourceStart;
	public int SourceLength { get; } = sourceLength;
}

/// <summary>Raised before selected text is written to the clipboard.</summary>
public sealed class MarkdownCopyingEventArgs(string text) : EventArgs
{
	public string Text { get; set; } = text;
	public bool Cancel { get; set; }
}

/// <summary>
/// Incremental Markdown control. It reparses cheaply, diffs top-level blocks by model hash, reuses
/// unchanged elements and text layouts, and keeps only the blocks near the outer viewport realized.
/// </summary>
public sealed partial class MarkdownView : Control, ISubtreeInvalidationHost, ILogicalTreeHost
{
	private const double FallbackEstimate = 48;

	public static readonly MewProperty<string> SourceProperty = MewProperty<string>.Register<MarkdownView>(
		nameof(Source), string.Empty, MewPropertyOptions.AffectsLayout,
		static (self, _, _) => self.InvalidateSource());

	public static readonly MewProperty<bool> IsStreamingProperty = MewProperty<bool>.Register<MarkdownView>(
		nameof(IsStreaming), false, MewPropertyOptions.AffectsRender,
		static (self, _, _) => self.OnStreamingChanged());

	public static readonly MewProperty<MarkdownOptions> OptionsProperty = MewProperty<MarkdownOptions>.Register<MarkdownView>(
		nameof(Options), new MarkdownOptions(), MewPropertyOptions.AffectsLayout,
		static (self, _, _) => self.InvalidateSource(forceRebuild: true));

	public static readonly MewProperty<MarkdownTheme> MarkdownThemeProperty = MewProperty<MarkdownTheme>.Register<MarkdownView>(
		nameof(MarkdownTheme), new MarkdownTheme(), MewPropertyOptions.AffectsLayout,
		static (self, _, value) => self.OnMarkdownThemeChanged(value));

	private readonly List<BlockSlot> _slots = [];
	private readonly List<Rect> _rangeBounds = [];
	private MarkdownDocument? _document;
	private MarkdownTheme _theme = new();
	private double[] _prefix = [];
	private bool _prefixValid;
	private bool _sourceDirty;
	private bool _forceRebuild;
	private double _measureWidth = double.NaN;
	private MeasureKey _measureKey;
	private ScrollViewer? _scroll;
	private int _visibleFirst = -1;
	private int _visibleLast = -1;
	private bool _disposed;

	private (int Unit, int Offset)? _selectionAnchor;
	private (int Unit, int Offset)? _selectionFocus;
	private bool _dragSelecting;
	private bool _selectionEnabled = true;
	private ContextMenu? _defaultContextMenu;
	private string _selectedText = string.Empty;
	private bool _hasSelection;

	public MarkdownView()
	{
		Focusable = true;
		Cursor = CursorType.IBeam;
		Commands.Register(StandardCommands.Copy, this,
			static view => view.CopySelection(),
			static view => view.IsSelectionEnabled && view.HasSelection);
		Commands.Register(StandardCommands.SelectAll, this,
			static view => view.SelectAll(),
			static view => view.IsSelectionEnabled && view._document?.TextUnits.Count > 0);
	}

	public string Source
	{
		get => GetValue(SourceProperty);
		set => SetValue(SourceProperty, value ?? string.Empty);
	}

	public bool IsStreaming
	{
		get => GetValue(IsStreamingProperty);
		set => SetValue(IsStreamingProperty, value);
	}

	public MarkdownOptions Options
	{
		get => GetValue(OptionsProperty);
		set
		{
			ArgumentNullException.ThrowIfNull(value);
			SetValue(OptionsProperty, value);
		}
	}

	public MarkdownTheme MarkdownTheme
	{
		get => GetValue(MarkdownThemeProperty);
		set
		{
			ArgumentNullException.ThrowIfNull(value);
			SetValue(MarkdownThemeProperty, value);
		}
	}

	public Uri? BaseUri { get; set; }
	public Func<string, string?, FrameworkElement?>? CodeBlockFactory { get; set; }
	public Action<MultiLineTextBox, string?>? CodeTextCreated { get; set; }

	/// <summary>
	/// Called when a paragraph or heading is created. Hosts use this to attach focus guards to
	/// focusable link text; a click can otherwise ask the enclosing scroll viewport to scroll the
	/// whole element into view.
	/// </summary>
	public Action<FrameworkElement>? FocusableElementCreated { get; set; }

	public event Action<MarkdownLinkRequestedEventArgs>? LinkRequested;

	/// <summary>
	/// Raised when a layout pass found nothing to realize although the document is non-empty:
	/// the surface is about to paint empty. Hosts count/log it to capture the geometry of a blank row.
	/// </summary>
	public event Action<string>? NothingRealized;
	public event Action? SelectionChanged;
	public event Action<MarkdownCopyingEventArgs>? Copying;
	public event Action<Exception>? ParseFailed;

	public bool IsSelectionEnabled
	{
		get => _selectionEnabled;
		set
		{
			_selectionEnabled = value;
			Focusable = value;
			Cursor = value ? CursorType.IBeam : null;
			if (!value)
			{
				ClearSelection();
			}
		}
	}

	public bool HasSelection => _hasSelection;
	public string SelectedText => _selectedText;

	/// <summary>Number of realized top-level blocks; exposed for diagnostics and tests.</summary>
	public int RealizedBlockCount
	{
		get
		{
			int count = 0;
			foreach (BlockSlot slot in _slots)
			{
				if (slot.Attached)
				{
					count++;
				}
			}
			return count;
		}
	}

	/// <summary>Total number of top-level blocks in the current document.</summary>
	public int BlockCount => _slots.Count;

	/// <summary>Returns the realized element of a top-level block, or null when it is not created.</summary>
	internal FrameworkElement? GetBlockElement(int index)
		=> index >= 0 && index < _slots.Count ? _slots[index].Element : null;

	internal double GetBlockHeight(int index)
		=> index >= 0 && index < _slots.Count ? _slots[index].Height : double.NaN;

	/// <summary>Exact total height of the document in DIPs.</summary>
	public double TotalHeight
	{
		get
		{
			EnsurePrefix();
			return _slots.Count == 0 ? 0 : _prefix[_slots.Count] - Math.Max(0, _theme.BlockSpacing);
		}
	}

	private static bool ShouldPreserveSelection(string oldSource, string newSource)
	{
		if (oldSource.Length == 0 || newSource.Length == 0)
		{
			return false;
		}
		int common = 0;
		int limit = Math.Min(oldSource.Length, newSource.Length);
		while (common < limit && oldSource[common] == newSource[common])
		{
			common++;
		}
		int shorter = Math.Min(oldSource.Length, newSource.Length);
		return common >= Math.Min(32, shorter) ||
			oldSource.Length == newSource.Length ||
			common >= shorter / 2;
	}

	private void OnMarkdownThemeChanged(MarkdownTheme value)
	{
		_theme = value;
		InvalidateSource(forceRebuild: true);
	}

	private void InvalidateSource(bool forceRebuild = false)
	{
		_sourceDirty = true;
		_forceRebuild |= forceRebuild;
		// Keep the measure key on a plain source change: the next measure still needs to detect a
		// simultaneous font/DPI change, and a reset would hide it.
		if (forceRebuild)
		{
			_measureKey = default;
		}
		InvalidateMeasure();
		InvalidateVisual();
	}

	private void OnStreamingChanged()
	{
		// Finalizing does not rebuild: the tail is already laid out. It only stops treating the
		// open paragraph chunk as transient, so static content can use the backend text caches.
		// Detached elements are included: they may be re-attached after the stream ends.
		foreach (BlockSlot slot in _slots)
		{
			if (slot.Element is not null)
			{
				SetStreamingState(slot.Element);
			}
		}
		InvalidateVisual();
	}

	private void SetStreamingState(Element element)
	{
		if (element is MarkdownParagraphView paragraph)
		{
			paragraph.IsStreaming = IsStreaming;
			paragraph.InvalidateVisual();
		}
		if (element is IVisualTreeHost host)
		{
			host.VisitChildren(child =>
			{
				SetStreamingState(child);
				return true;
			});
		}
	}

	private void EnsureDocument()
	{
		if (_theme != MarkdownTheme)
		{
			_theme = MarkdownTheme;
			_forceRebuild = true;
			_sourceDirty = true;
		}
		if (!_sourceDirty)
		{
			return;
		}
		_sourceDirty = false;
		string source = Source ?? string.Empty;
		MarkdownDocument? previous = _document;
		bool appended = previous is not null && source.Length >= previous.Source.Length &&
			source.StartsWith(previous.Source, StringComparison.Ordinal);
		bool preserveSelection = previous is not null &&
			(appended || ShouldPreserveSelection(previous.Source, source));
		try
		{
			MarkdownDocument document = MarkdownParser.Parse(source, Options, previous);
			ApplyDocument(document, preserveSelection, _forceRebuild);
			_forceRebuild = false;
		}
		catch (Exception error)
		{
			_forceRebuild = false;
			ParseFailed?.Invoke(error);
		}
	}

	private void ApplyDocument(MarkdownDocument document, bool preserveSelection, bool forceRebuild)
	{
		_document = document;
		if (forceRebuild)
		{
			foreach (BlockSlot slot in _slots)
			{
				DisposeSlot(slot);
			}
			_slots.Clear();
		}

		IReadOnlyList<MarkdownBlock> newBlocks = document.Blocks;
		int oldCount = _slots.Count;
		int newCount = newBlocks.Count;

		int prefix = 0;
		while (prefix < oldCount && prefix < newCount && _slots[prefix].Block.Hash == newBlocks[prefix].Hash)
		{
			prefix++;
		}
		int suffix = 0;
		while (suffix < oldCount - prefix && suffix < newCount - prefix &&
			_slots[oldCount - 1 - suffix].Block.Hash == newBlocks[newCount - 1 - suffix].Hash)
		{
			suffix++;
		}

		List<BlockSlot> slots = new(newCount);
		for (int index = 0; index < prefix; index++)
		{
			BlockSlot slot = _slots[index];
			slot.Block = newBlocks[index];
			slots.Add(slot);
		}

		int newMiddleEnd = newCount - suffix;
		int oldMiddleEnd = oldCount - suffix;

		// Changed blocks keep their element when the new block can be presented by it: a streamed
		// tail, a rewritten sentence, or a toggled task. Replacing elements on every change is
		// what made the content flash.
		for (int index = prefix; index < newMiddleEnd; index++)
		{
			BlockSlot? existing = index < oldMiddleEnd ? _slots[index] : null;
			if (existing?.Element is FrameworkElement element &&
				TryUpdateElement(element, existing.Block, newBlocks[index]))
			{
				existing.Block = newBlocks[index];
				existing.Height = double.NaN;
				slots.Add(existing);
				continue;
			}
			slots.Add(new BlockSlot { Block = newBlocks[index] });
		}

		for (int index = 0; index < suffix; index++)
		{
			BlockSlot slot = _slots[oldCount - suffix + index];
			slot.Block = newBlocks[newMiddleEnd + index];
			slots.Add(slot);
		}

		HashSet<BlockSlot> reused = new(ReferenceEqualityComparer.Instance);
		foreach (BlockSlot slot in slots)
		{
			reused.Add(slot);
		}
		foreach (BlockSlot slot in _slots)
		{
			if (!reused.Contains(slot))
			{
				DisposeSlot(slot);
			}
		}

		_slots.Clear();
		_slots.AddRange(slots);
		_prefix = new double[newCount + 1];
		_prefixValid = false;
		_visibleFirst = -1;
		_visibleLast = -1;

		for (int index = 0; index < _slots.Count; index++)
		{
			BlockSlot slot = _slots[index];
			int unit = document.GetTextUnit(slot.Block);
			slot.TextUnit = unit;
			if (slot.Element is not null)
			{
				// Nested paragraphs inside lists, tables and quotes carry their own unit indices;
				// a reused container must refresh them or its text silently stops participating
				// in selection and hit testing.
				AssignTextUnits(slot.Element, slot.Block);
			}
		}

		if (preserveSelection)
		{
			ApplySelectionToRealized();
			RefreshSelectedText();
		}
		else
		{
			ClearSelection();
		}
	}

	private void AssignTextUnits(Element? element, MarkdownBlock block)
	{
		if (element is null)
		{
			return;
		}
		switch (block.Kind)
		{
			case MarkdownBlockKind.Paragraph:
			case MarkdownBlockKind.Heading:
				if (element is MarkdownParagraphView paragraph)
				{
					paragraph.RefreshBlock(block);
					paragraph.TextUnit = _document?.GetTextUnit(block) ?? -1;
					ApplySelectionTo(paragraph);
				}
				return;

			case MarkdownBlockKind.Quote:
				if (element is Border { Child: StackPanel quote })
				{
					AssignStackTextUnits(quote, block.Children);
				}
				return;

			case MarkdownBlockKind.Group:
				if (element is StackPanel group)
				{
					AssignStackTextUnits(group, block.Children);
				}
				return;

			case MarkdownBlockKind.List:
				if (element is MarkdownListPanel list)
				{
					for (int item = 0; item < block.Children.Count; item++)
					{
						if (list.ContentAt(item) is StackPanel content)
						{
							AssignStackTextUnits(content, block.Children[item].Children);
						}
					}
				}
				return;

			case MarkdownBlockKind.Table:
				if (element is Border { Child: Grid table })
				{
					int cell = 0;
					foreach (MarkdownBlock row in block.Children)
					{
						foreach (MarkdownBlock tableCell in row.Children)
						{
							if (cell >= table.Children.Count)
							{
								return;
							}
							if (table.Children[cell] is Border { Child: StackPanel content })
							{
								AssignStackTextUnits(content, tableCell.Children);
							}
							cell++;
						}
					}
				}
				return;
		}
	}

	private void AssignStackTextUnits(StackPanel panel, IReadOnlyList<MarkdownBlock> children)
	{
		int count = Math.Min(panel.Children.Count, children.Count);
		for (int index = 0; index < count; index++)
		{
			AssignTextUnits(panel.Children[index], children[index]);
		}
	}

	private bool TryUpdateElement(FrameworkElement element, MarkdownBlock oldBlock, MarkdownBlock newBlock)
	{
		if (oldBlock.Kind != newBlock.Kind)
		{
			return false;
		}
		switch (oldBlock.Kind)
		{
			case MarkdownBlockKind.Paragraph:
			case MarkdownBlockKind.Heading:
				if (element is not MarkdownParagraphView paragraph)
				{
					return false;
				}
				paragraph.SetBlock(
					newBlock,
					_theme,
					ActivateLink,
					BaseUri,
					plainCode: false,
					wrap: true,
					FontScale(newBlock));
				paragraph.IsStreaming = IsStreaming;
				return true;

			case MarkdownBlockKind.Code:
				if (element is not IMarkdownCodeView code ||
					!string.Equals(oldBlock.CodeLanguage, newBlock.CodeLanguage, StringComparison.Ordinal))
				{
					return false;
				}
				// SetCode appends when possible and replaces the document otherwise; the view does
				// not need to be recreated for an edit or a shrinking code block.
				code.SetCode(newBlock.Text, newBlock.CodeLanguage);
				return true;

			case MarkdownBlockKind.Rule:
				return true;

			case MarkdownBlockKind.Quote:
				return element is Border { Child: StackPanel quote } &&
					TryUpdateStack(quote, oldBlock.Children, newBlock.Children);

			case MarkdownBlockKind.Group:
				return element is StackPanel group &&
					TryUpdateStack(group, oldBlock.Children, newBlock.Children);

			case MarkdownBlockKind.List:
				return element is MarkdownListPanel list && TryUpdateList(list, oldBlock, newBlock);

			case MarkdownBlockKind.Table:
				return element is Border { Child: Grid table } && TryUpdateTable(table, oldBlock, newBlock);

			default:
				return false;
		}
	}

	private bool TryUpdateStack(
		StackPanel panel,
		IReadOnlyList<MarkdownBlock> oldChildren,
		IReadOnlyList<MarkdownBlock> newChildren)
	{
		if (newChildren.Count < oldChildren.Count || panel.Children.Count != oldChildren.Count)
		{
			return false;
		}

		for (int index = 0; index < oldChildren.Count; index++)
		{
			if (oldChildren[index].Hash == newChildren[index].Hash)
			{
				continue;
			}
			if (panel.Children[index] is not FrameworkElement child ||
				!TryUpdateElement(child, oldChildren[index], newChildren[index]))
			{
				return false;
			}
		}

		for (int index = oldChildren.Count; index < newChildren.Count; index++)
		{
			panel.Add(CreateBlockElement(newChildren[index]));
		}
		return true;
	}

	private bool TryUpdateList(MarkdownListPanel list, MarkdownBlock oldList, MarkdownBlock newList)
	{
		int oldCount = oldList.Children.Count;
		int newCount = newList.Children.Count;
		if (newCount < oldCount || list.RowCount != oldCount)
		{
			return false;
		}
		if (oldList.Loose != newList.Loose ||
			oldList.Level != newList.Level ||
			!string.Equals(oldList.Marker, newList.Marker, StringComparison.Ordinal))
		{
			return false;
		}

		// Every existing item is updated in place; a task toggle or a changed bullet only swaps
		// the marker, not the whole list.
		for (int index = 0; index < oldCount; index++)
		{
			MarkdownBlock oldItem = oldList.Children[index];
			MarkdownBlock newItem = newList.Children[index];
			if (oldItem.Hash == newItem.Hash)
			{
				continue;
			}
			if (!TryUpdateListItem(list, index, oldItem, newItem))
			{
				return false;
			}
		}

		for (int index = oldCount; index < newCount; index++)
		{
			AppendListRow(list, newList.Children[index], newList.Loose);
		}
		return true;
	}

	private void AppendListRow(MarkdownListPanel list, MarkdownBlock item, bool loose)
	{
		(FrameworkElement marker, FrameworkElement content) = CreateListRow(item, loose);
		list.AddRow(marker, content);
	}

	private bool TryUpdateListItem(MarkdownListPanel list, int index, MarkdownBlock oldItem, MarkdownBlock newItem)
	{
		if (!TryUpdateListItemMarker(list, index, oldItem, newItem))
		{
			return false;
		}
		return list.ContentAt(index) is StackPanel content &&
			TryUpdateStack(content, oldItem.Children, newItem.Children);
	}

	private bool TryUpdateListItemMarker(MarkdownListPanel list, int index, MarkdownBlock oldItem, MarkdownBlock newItem)
	{
		bool? oldTask = TaskState(oldItem);
		bool? newTask = TaskState(newItem);
		if (oldTask.HasValue != newTask.HasValue)
		{
			// A plain item turned into a task item (or back): the marker kind changes.
			return false;
		}

		FrameworkElement? marker = list.MarkerAt(index);
		if (oldTask.HasValue)
		{
			if (marker is not CheckBox box)
			{
				return false;
			}
			if (oldTask != newTask)
			{
				box.IsChecked = newTask;
			}
			return true;
		}

		string oldMarker = MarkdownDocument.GetListMarkerText(oldItem);
		string newMarker = MarkdownDocument.GetListMarkerText(newItem);
		if (string.Equals(oldMarker, newMarker, StringComparison.Ordinal))
		{
			return true;
		}
		if (marker is not TextBlock text)
		{
			return false;
		}
		// Ordered numbering or a changed bullet: only the marker text changes.
		text.Text = newMarker;
		return true;
	}

	private bool TryUpdateTable(Grid grid, MarkdownBlock oldTable, MarkdownBlock newTable)
	{
		int oldRows = oldTable.Children.Count;
		int newRows = newTable.Children.Count;
		if (newRows < oldRows || oldRows == 0 || grid.RowDefinitions.Count != oldRows)
		{
			return false;
		}
		int oldColumns = grid.ColumnDefinitions.Count;
		int newColumns = newTable.Children.Count == 0 ? 0 : newTable.Children.Max(static row => row.Children.Count);
		if (oldColumns != newColumns)
		{
			return false;
		}

		int cell = 0;
		for (int rowIndex = 0; rowIndex < oldRows; rowIndex++)
		{
			MarkdownBlock oldRow = oldTable.Children[rowIndex];
			MarkdownBlock newRow = newTable.Children[rowIndex];
			if (oldRow.Children.Count != newRow.Children.Count || oldRow.Header != newRow.Header)
			{
				return false;
			}
			for (int cellIndex = 0; cellIndex < oldRow.Children.Count; cellIndex++)
			{
				MarkdownBlock oldCell = oldRow.Children[cellIndex];
				MarkdownBlock newCell = newRow.Children[cellIndex];
				if (oldCell.Alignment != newCell.Alignment)
				{
					return false;
				}
				if (oldCell.Hash != newCell.Hash)
				{
					if (cell >= grid.Children.Count ||
						grid.Children[cell] is not Border { Child: StackPanel content } ||
						!TryUpdateStack(content, oldCell.Children, newCell.Children))
					{
						return false;
					}
				}
				cell++;
			}
		}

		for (int rowIndex = oldRows; rowIndex < newRows; rowIndex++)
		{
			MarkdownBlock row = newTable.Children[rowIndex];
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			for (int cellIndex = 0; cellIndex < row.Children.Count; cellIndex++)
			{
				Border border = CreateTableCellBorder(
					row.Children[cellIndex], row.Header, rowIndex, rowIndex == newRows - 1);
				Grid.SetRow(border, rowIndex);
				Grid.SetColumn(border, cellIndex);
				grid.Add(border);
			}
		}
		if (newRows > oldRows)
		{
			UpdateTableRowBorders(grid, newTable);
		}
		UpdateTableColumnWidths(grid, newTable);

		return true;
	}

	private static void UpdateTableRowBorders(Grid grid, MarkdownBlock table)
	{
		int cell = 0;
		for (int rowIndex = 0; rowIndex < table.Children.Count; rowIndex++)
		{
			bool last = rowIndex == table.Children.Count - 1;
			for (int column = 0; column < table.Children[rowIndex].Children.Count && cell < grid.Children.Count; column++)
			{
				if (grid.Children[cell] is Border border)
				{
					border.NonUniformBorderThickness = new Thickness(0, 0, 0, last ? 0 : 1);
				}
				cell++;
			}
		}
	}

	private static bool? TaskState(MarkdownBlock item)
		=> item.Children.FirstOrDefault()?.Runs.FirstOrDefault(static run => run.TaskChecked.HasValue).TaskChecked;

	private void DisposeSlot(BlockSlot slot)
	{
		DetachSlot(slot, dispose: true);
		slot.Element = null;
	}

	private void EnsureElement(BlockSlot slot)
	{
		if (slot.Element is not null)
		{
			return;
		}
		slot.Element = CreateBlockElement(slot.Block);
		// A re-created element has to be measured again even when its cached height is known.
		slot.Height = double.NaN;
		_prefixValid = false;
		slot.TextUnit = _document?.GetTextUnit(slot.Block) ?? -1;
		if (slot.Element is ISelectableText selectable)
		{
			selectable.TextUnit = slot.TextUnit;
			ApplySelectionTo(selectable);
		}
	}

	private FrameworkElement CreateBlockElement(MarkdownBlock block)
	{
		switch (block.Kind)
		{
			case MarkdownBlockKind.Paragraph:
			case MarkdownBlockKind.Heading:
			{
				MarkdownParagraphView paragraph = new();
				paragraph.SetBlock(
					block,
					_theme,
					ActivateLink,
					BaseUri,
					plainCode: false,
					wrap: true,
					FontScale(block));
				if (block.Kind == MarkdownBlockKind.Heading)
				{
					paragraph.FontWeight = FontWeight.Bold;
				}
				paragraph.IsStreaming = IsStreaming;
				paragraph.TextUnit = _document?.GetTextUnit(block) ?? -1;
				ApplySelectionTo(paragraph);
				FocusableElementCreated?.Invoke(paragraph);
				return paragraph;
			}

			case MarkdownBlockKind.Code:
			{
				FrameworkElement? custom = CodeBlockFactory?.Invoke(block.Text, block.CodeLanguage);
				if (custom is not null)
				{
					if (custom is IMarkdownCodeView codeView)
					{
						codeView.SetCode(block.Text, block.CodeLanguage);
					}
					return custom;
				}

				Func<Theme, Color> background = _theme.CodeBackgroundSelector
					?? (_theme.CodeBackground is { } fixedColor
						? _ => fixedColor
						: static theme => theme.Palette.ControlBackground);
				MarkdownCodeBlockView code = new(
					_theme,
					FontSize,
					Foreground,
					background(Theme),
					background);
				CodeTextCreated?.Invoke(code.TextView, block.CodeLanguage);
				code.SetCode(block.Text, block.CodeLanguage);
				return code;
			}

			case MarkdownBlockKind.Rule:
				return new Border { Height = 1 }
					.WithTheme(static (theme, border) => border.Background = theme.Palette.ControlBorder);

			case MarkdownBlockKind.Quote:
				return new Border
				{
					Padding = new Thickness(12, 4),
					NonUniformBorderThickness = new Thickness(4, 0, 0, 0),
					Child = CreateChildStack(block.Children, _theme.BlockSpacing)
				}.WithTheme(static (theme, border) =>
				{
					border.BorderBrush = theme.Palette.ControlBorder;
					border.Background = theme.Palette.ButtonFace;
				});

			case MarkdownBlockKind.List:
				return CreateList(block);

			case MarkdownBlockKind.Table:
				return CreateTable(block);

			default:
				return CreateChildStack(block.Children, _theme.BlockSpacing);
		}
	}

	private FrameworkElement CreateChildStack(IReadOnlyList<MarkdownBlock> children, double spacing)
	{
		StackPanel panel = new() { Spacing = Math.Max(0, spacing) };
		foreach (MarkdownBlock child in children)
		{
			panel.Add(CreateBlockElement(child));
		}
		return panel;
	}

	private FrameworkElement CreateList(MarkdownBlock block)
	{
		MarkdownListPanel panel = new(_theme.ListIndent);
		foreach (MarkdownBlock item in block.Children)
		{
			(FrameworkElement marker, FrameworkElement content) = CreateListRow(item, block.Loose);
			panel.AddRow(marker, content);
		}
		return panel;
	}

	private (FrameworkElement Marker, FrameworkElement Content) CreateListRow(MarkdownBlock item, bool loose)
	{
		// FirstOrDefault over MarkdownRun returns default(MarkdownRun), not null: a non-null
		// nullable struct would turn every ordinary list item into an unchecked CheckBox.
		bool? taskChecked = item.Children.FirstOrDefault()?.Runs
			.FirstOrDefault(static run => run.TaskChecked.HasValue).TaskChecked;
		FrameworkElement marker;
		if (taskChecked.HasValue)
		{
			marker = new CheckBox
			{
				IsChecked = taskChecked,
				IsEnabled = false,
				IsHitTestVisible = false,
				Width = 14,
				Height = 14,
				MinWidth = 14,
				MinHeight = 14,
				Padding = Thickness.Zero,
				Margin = new Thickness(0, 0, 8, 0),
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top
			};
		}
		else
		{
			marker = new TextBlock
			{
				Text = MarkdownDocument.GetListMarkerText(item),
				FontSize = FontSize,
				Foreground = Foreground,
				Margin = new Thickness(0, 0, 8, 0),
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				VerticalTextAlignment = TextAlignment.Top
			};
		}
		FrameworkElement content = CreateChildStack(item.Children, loose ? _theme.BlockSpacing : 0);
		content.Margin = new Thickness(0, 0, 0, loose ? 4 : 0);
		return (marker, content);
	}

	private FrameworkElement CreateTable(MarkdownBlock block)
	{
		Grid grid = new();
		int columns = block.Children.Count == 0 ? 0 : block.Children.Max(static row => row.Children.Count);
		for (int column = 0; column < columns; column++)
		{
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
		}
		for (int rowIndex = 0; rowIndex < block.Children.Count; rowIndex++)
		{
			MarkdownBlock row = block.Children[rowIndex];
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			for (int column = 0; column < row.Children.Count; column++)
			{
				Border border = CreateTableCellBorder(
					row.Children[column], row.Header, rowIndex, rowIndex == block.Children.Count - 1);
				Grid.SetRow(border, rowIndex);
				Grid.SetColumn(border, column);
				grid.Add(border);
			}
		}
		UpdateTableRowBorders(grid, block);
		UpdateTableColumnWidths(grid, block);
		return new Border
		{
			Child = grid,
			Padding = Thickness.Zero,
			BorderThickness = 1,
			CornerRadius = _theme.CardCornerRadius,
			ClipToBounds = true,
		}.WithTheme(static (theme, element) =>
		{
			element.Background = theme.Palette.ControlBackground;
			element.BorderBrush = theme.Palette.ControlBorder;
		});
	}

	private Border CreateTableCellBorder(MarkdownBlock cell, bool header, int rowIndex, bool lastRow)
	{
		FrameworkElement content = CreateChildStack(cell.Children, 0);
		SetFontSize(content, Math.Max(11, FontSize * _theme.TableFontSizeScale));
		if (header)
		{
			SetBold(content);
		}
		bool alternate = !header && rowIndex > 0 && rowIndex % 2 == 0;
		return new Border
		{
			Padding = new Thickness(10, 7, 10, 7),
			CornerRadius = 0,
			NonUniformBorderThickness = new Thickness(0, 0, 0, lastRow ? 0 : 1),
			Child = content
		}.WithTheme((theme, element) =>
		{
			element.BorderBrush = theme.Palette.ControlBorder;
			element.Background = header
				? TableHeaderBackground(theme)
				: alternate
					? TableAlternateBackground(theme)
					: Color.Transparent;
		});
	}

	private static void UpdateTableColumnWidths(Grid grid, MarkdownBlock table)
	{
		int columns = grid.ColumnDefinitions.Count;
		if (columns == 0)
		{
			return;
		}
		double[] weights = new double[columns];
		foreach (MarkdownBlock row in table.Children)
		{
			for (int column = 0; column < row.Children.Count && column < columns; column++)
			{
				// Long prose in one column should get more room than a short numeric column,
				// but the square root keeps one huge cell from starving the rest of the table.
				double width = Math.Min(48, CellTextLength(row.Children[column]));
				weights[column] = Math.Max(weights[column], width);
			}
		}
		double total = 0;
		for (int column = 0; column < columns; column++)
		{
			weights[column] = Math.Sqrt(Math.Max(4, weights[column]));
			total += weights[column];
		}
		if (total <= 0)
		{
			return;
		}
		for (int column = 0; column < columns; column++)
		{
			grid.ColumnDefinitions[column].Width = GridLength.Stars(weights[column]);
		}
	}

	private static int CellTextLength(MarkdownBlock block)
	{
		int length = block.Text.Length;
		foreach (MarkdownBlock child in block.Children)
		{
			length += CellTextLength(child);
		}
		return length;
	}

	private static Color TableHeaderBackground(Theme theme)
		=> theme.Palette.ControlBackground.Lerp(theme.Palette.ButtonFace, 0.7);

	private static Color TableAlternateBackground(Theme theme)
		=> theme.Palette.ControlBackground.Lerp(theme.Palette.ButtonFace, 0.5);

	private static void SetFontSize(FrameworkElement element, double size)
	{
		if (element is TextElement text)
		{
			text.FontSize = size;
			return;
		}
		if (element is IVisualTreeHost host)
		{
			host.VisitChildren(child =>
			{
				if (child is FrameworkElement framework)
				{
					SetFontSize(framework, size);
				}
				return true;
			});
		}
	}

	private static void SetBold(FrameworkElement element)
	{
		if (element is TextElement text)
		{
			text.FontWeight = FontWeight.Bold;
			return;
		}
		if (element is IVisualTreeHost host)
		{
			host.VisitChildren(child =>
			{
				if (child is FrameworkElement framework)
				{
					SetBold(framework);
				}
				return true;
			});
		}
	}

	private static double FontScale(MarkdownBlock block)
		=> block.Kind == MarkdownBlockKind.Heading
			? Math.Max(1.05, 2.0 - (block.Level - 1) * 0.18)
			: 1;

	protected override Size MeasureContent(Size availableSize)
	{
		EnsureDocument();
		EnsureScrollSubscription();

		double width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 1_000_000;
		var measureKey = new MeasureKey(FontFamily, FontSize, FontWeight, FontStyle, GetDpi(), _theme, Options);
		bool fontChanged = _measureKey != default && (
			!string.Equals(_measureKey.FontFamily, measureKey.FontFamily, StringComparison.Ordinal) ||
			_measureKey.FontSize != measureKey.FontSize ||
			_measureKey.Weight != measureKey.Weight ||
			_measureKey.Style != measureKey.Style ||
			_measureKey.Dpi != measureKey.Dpi);
		if (fontChanged)
		{
			// Code blocks and list markers bake the font into their child elements; rebuild the
			// whole document instead of trying to patch every nested view.
			_forceRebuild = true;
			_sourceDirty = true;
			EnsureDocument();
			measureKey = new MeasureKey(FontFamily, FontSize, FontWeight, FontStyle, GetDpi(), _theme, Options);
		}
		if (_measureKey != measureKey || Math.Abs(_measureWidth - width) > 0.01)
		{
			if (_slots.Count > 0 && (_measureKey != default || Math.Abs(_measureWidth - width) > 0.01))
			{
				// Font, DPI, theme metrics or wrap width changed: every cached height is stale.
				foreach (BlockSlot slot in _slots)
				{
					slot.Height = double.NaN;
				}
			}
			_measureKey = measureKey;
			_measureWidth = width;
		}

		double dpiScale = GetDpi() / 96.0;
		for (int index = 0; index < _slots.Count; index++)
		{
			BlockSlot slot = _slots[index];
			if (!double.IsNaN(slot.Height))
			{
				continue;
			}
			EnsureElement(slot);
			// Attach before measuring so inherited font/foreground/theme resolve from this view.
			if (!slot.Attached)
			{
				AttachChild(slot.Element!);
				slot.Attached = true;
			}
			slot.Element!.Measure(new Size(width, double.PositiveInfinity));
			slot.Height = LayoutRounding.RoundToPixel(Math.Max(0, slot.Element.DesiredSize.Height), dpiScale);
			_prefixValid = false;
		}

		EnsurePrefix();
		(double visibleTop, double visibleBottom) = GetVisibleRange();
		RealizeRange(visibleTop, visibleBottom, width, dpiScale);
		EnsurePrefix();
		return new Size(width, TotalHeight);
	}

	private void RealizeRange(double visibleTop, double visibleBottom, double width, double dpiScale)
	{
		if (_slots.Count == 0)
		{
			_visibleFirst = _visibleLast = -1;
			return;
		}

		int first = Math.Max(0, FindIndexByY(visibleTop));
		int last = Math.Min(_slots.Count - 1, FindIndexByY(visibleBottom));
		bool attachedAny = false;
		// 新窗口与上一次完全不相交 = 几何跳变（外层已滚走而本控件还没重新 arrange，或反过来）。
		// 这一帧把上一次的窗口也留在附着集合里，避免中间出现一个谁都不覆盖的空档。
		bool geometryJumped = _visibleLast >= _visibleFirst && (last < _visibleFirst || first > _visibleLast);
		int holdFirst = geometryJumped ? Math.Max(0, _visibleFirst - 2) : -1;
		int holdLast = geometryJumped ? Math.Min(_slots.Count - 1, _visibleLast + 2) : -1;

		// Keep a couple of blocks attached beyond the viewport so a sub-pixel/one-block
		// oscillation at the boundary does not detach and re-attach the same element every
		// frame (which readers see as flashing).
		int attachFirst = Math.Max(0, first - 2);
		int attachLast = Math.Min(_slots.Count - 1, last + 2);
		for (int index = attachFirst; index <= attachLast; index++)
		{
			BlockSlot slot = _slots[index];
			EnsureElement(slot);
			if (!slot.Attached)
			{
				AttachChild(slot.Element!);
				slot.Attached = true;
			}
			attachedAny = true;
			if (double.IsNaN(slot.Height))
			{
				slot.Element!.Measure(new Size(width, double.PositiveInfinity));
				slot.Height = LayoutRounding.RoundToPixel(Math.Max(0, slot.Element.DesiredSize.Height), dpiScale);
				_prefixValid = false;
			}
		}

		// Keep a larger detached cache around the viewport; the streaming tail is always kept so
		// its frozen chunks survive while the reader is scrolled up. Short documents keep every
		// measured element: disposing and re-creating them costs a full text layout each, and the
		// document was already measured in one pass anyway.
		int keepFirst = Math.Max(0, first - 12);
		int keepLast = Math.Min(_slots.Count - 1, last + 12);
		bool keepAll = _slots.Count <= 256;
		for (int index = 0; index < _slots.Count; index++)
		{
			BlockSlot slot = _slots[index];
			if (slot.Element is null || (index >= attachFirst && index <= attachLast))
			{
				continue;
			}
			if (slot.Attached && IsFocusedSubtree(slot.Element))
			{
				continue;
			}
			bool keep = keepAll ||
				index >= keepFirst && index <= keepLast ||
				index >= holdFirst && index <= holdLast ||
				(IsStreaming && index == _slots.Count - 1);
			if (slot.Attached)
			{
				DetachSlot(slot, dispose: !keep);
			}
			else if (!keep)
			{
				DetachSlot(slot, dispose: true);
			}
		}

		_visibleFirst = first;
		_visibleLast = last;
		if (!attachedAny)
		{
			// 只在「文档非空但一个块都没附着」时上报：这是正常回收永远不走的分支，
			// 成本是一次字符串拼接，发生在即将出现空白的罕见帧上。
			NothingRealized?.Invoke(
				$"blocks={_slots.Count} window=({visibleTop:0},{visibleBottom:0}) " +
				$"bounds={Bounds} scroll=({_scroll?.VerticalOffset:0},{_scroll?.ViewportHeight:0}) " +
				$"height={TotalHeight:0}");
		}
	}

	private void EnsurePrefix()
	{
		if (_prefixValid)
		{
			return;
		}
		if (_prefix.Length != _slots.Count + 1)
		{
			_prefix = new double[_slots.Count + 1];
		}
		double spacing = Math.Max(0, _theme.BlockSpacing);
		double sum = 0;
		_prefix[0] = 0;
		for (int index = 0; index < _slots.Count; index++)
		{
			double height = double.IsNaN(_slots[index].Height) ? FallbackEstimate : _slots[index].Height;
			_slots[index].Top = sum;
			sum += height + spacing;
			_prefix[index + 1] = sum;
		}
		_prefixValid = true;
	}

	private int FindIndexByY(double offset)
	{
		int count = _slots.Count;
		if (count == 0)
		{
			return 0;
		}
		if (double.IsPositiveInfinity(offset))
		{
			return count - 1;
		}
		offset = Math.Clamp(offset, 0, _prefix[count]);
		int low = 0;
		int high = count - 1;
		while (low < high)
		{
			int middle = low + (high - low + 1) / 2;
			if (_prefix[middle] <= offset)
			{
				low = middle;
			}
			else
			{
				high = middle - 1;
			}
		}
		return low;
	}

	private (double Top, double Bottom) GetVisibleRange()
	{
		double top;
		double bottom;
		if (_scroll is not null && _scroll.ViewportHeight > 0)
		{
			top = _scroll.Bounds.Y + _scroll.Padding.Top - Bounds.Y;
			bottom = top + _scroll.ViewportHeight;
		}
		else if (Bounds.Height > 0)
		{
			top = 0;
			bottom = Bounds.Height;
		}
		else
		{
			top = 0;
			bottom = 400;
		}
		double height = Math.Max(1, bottom - top);
		double overscan = Math.Max(height, 600);
		top -= overscan;
		bottom += overscan;

		// 布局中间态保护：外层视口或本控件的位置只有一边是新的（滚动已生效、本控件还没重新
		// arrange，或反过来），于是算出的窗口会整体落在文档之外。照那种窗口 realize 会把
		// 可见块换成文档一端的少数块，正文瞬间空白。这里沿用上一次的窗口，等本轮 arrange
		// 把几何对齐后，ArrangeContent 会察觉窗口变化并再测一次。
		double total = _prefixValid && _slots.Count > 0 ? _prefix[_slots.Count] : 0;
		if (total > 0 && _visibleFirst >= 0 && (bottom < 0 || top > total))
		{
			return (_slots[_visibleFirst].Top,
				_slots[_visibleLast].Top + _slots[_visibleLast].Height);
		}

		return (top, bottom);
	}

	protected override void ArrangeContent(Rect bounds)
	{
		foreach (BlockSlot slot in _slots)
		{
			if (slot.Attached && slot.Element is not null)
			{
				slot.Element.Arrange(new Rect(bounds.X, bounds.Y + slot.Top, bounds.Width, slot.Height));
			}
		}

		if (_slots.Count == 0)
		{
			return;
		}
		(double top, double bottom) = GetVisibleRange();
		int first = Math.Max(0, FindIndexByY(top));
		int last = Math.Min(_slots.Count - 1, FindIndexByY(bottom));
		if (first != _visibleFirst || last != _visibleLast)
		{
			// Scrolling only arranges; the next measure realizes what the viewport now covers.
			InvalidateMeasure();
		}
	}

	protected override void RenderSubtree(IGraphicsContext context)
	{
		foreach (BlockSlot slot in _slots)
		{
			if (slot.Attached && slot.Element is not null)
			{
				slot.Element.Render(context);
			}
		}
	}

	bool IVisualTreeHost.VisitChildren(Func<Element, bool> visitor)
	{
		foreach (BlockSlot slot in _slots)
		{
			if (slot.Attached && slot.Element is not null && !visitor(slot.Element))
			{
				return false;
			}
		}
		return true;
	}

	bool ILogicalTreeHost.VisitLogicalChildren(Func<Element, bool> visitor) => ((IVisualTreeHost)this).VisitChildren(visitor);

	private void EnsureScrollSubscription()
	{
		ScrollViewer? scroll = FindScrollViewer();
		if (ReferenceEquals(scroll, _scroll))
		{
			return;
		}
		if (_scroll is not null)
		{
			_scroll.ScrollChanged -= OnScrollChanged;
		}
		_scroll = scroll;
		if (_scroll is not null)
		{
			_scroll.ScrollChanged += OnScrollChanged;
		}
	}

	private ScrollViewer? FindScrollViewer()
	{
		for (Element? current = Parent; current is not null; current = current.Parent)
		{
			if (current is ScrollViewer scroll)
			{
				return scroll;
			}
		}
		return null;
	}

	private void OnScrollChanged()
	{
		if (_disposed)
		{
			return;
		}
		InvalidateMeasure();
	}

	private bool IsFocusedSubtree(FrameworkElement element)
	{
		return FindVisualRoot() is Window window &&
			window.FocusManager.FocusedElement is UIElement focused &&
			VisualTree.IsInSubtreeOf(focused, element);
	}

	private void DetachSlot(BlockSlot slot, bool dispose)
	{
		if (slot.Attached && slot.Element is not null)
		{
			DetachChild(slot.Element);
			slot.Attached = false;
		}
		if (dispose && slot.Element is not null)
		{
			DisposeTree(slot.Element);
			slot.Element = null;
		}
	}

	internal static void DisposeTree(Element element)
	{
		if (element is IVisualTreeHost host)
		{
			List<Element> children = [];
			host.VisitChildren(child =>
			{
				children.Add(child);
				return true;
			});
			foreach (Element child in children)
			{
				DisposeTree(child);
			}
		}
		(element as IDisposable)?.Dispose();
	}

	protected override void OnParentChanged()
	{
		base.OnParentChanged();
		RestoreAfterReattach(Parent is not null);
	}

	protected override void OnVisualRootChanged(Element? oldRoot, Element? newRoot)
	{
		base.OnVisualRootChanged(oldRoot, newRoot);
		RestoreAfterReattach(newRoot is not null);
	}

	private void RestoreAfterReattach(bool attached)
	{
		if (!attached || _disposed || _slots.Count == 0)
		{
			return;
		}
		// A virtualized row may be detached and reattached with the view intact. While it was
		// offscreen RealizeRange may have detached its visible blocks; a cached measure would
		// skip the realize pass and paint an empty Markdown surface until the next scroll.
		InvalidateMeasure();
		InvalidateVisual();
	}

	protected override void OnDispose()
	{
		_disposed = true;
		if (_scroll is not null)
		{
			_scroll.ScrollChanged -= OnScrollChanged;
			_scroll = null;
		}
		foreach (BlockSlot slot in _slots)
		{
			DisposeSlot(slot);
		}
		_slots.Clear();
		_document = null;
		_defaultContextMenu?.Dispose();
		_defaultContextMenu = null;
		FocusableElementCreated = null;
		CodeTextCreated = null;
		CodeBlockFactory = null;
		LinkRequested = null;
		SelectionChanged = null;
		Copying = null;
		ParseFailed = null;
		base.OnDispose();
	}

	private sealed class BlockSlot
	{
		public MarkdownBlock Block = null!;
		public double Height = double.NaN;
		public double Top;
		public FrameworkElement? Element;
		public bool Attached;
		public int TextUnit = -1;
	}

	private readonly record struct MeasureKey(
		string FontFamily,
		double FontSize,
		FontWeight Weight,
		FontStyle Style,
		uint Dpi,
		MarkdownTheme Theme,
		MarkdownOptions Options);
}
