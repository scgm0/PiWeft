using System.Buffers;
using System.Globalization;
using System.Text;

namespace PiWeft.Desktop;

/// <summary>终端颜色：默认（跟随主题）、256 色调色板索引，或 24 位真彩色。</summary>
internal enum TerminalColorKind : byte
{
	Default,
	Indexed,
	Rgb,
}

internal readonly record struct TerminalColor(TerminalColorKind Kind, byte Index, uint Rgb)
{
	public static readonly TerminalColor Default = default;

	public static TerminalColor Indexed(byte index) => new(TerminalColorKind.Indexed, index, 0);

	public static TerminalColor TrueColor(byte r, byte g, byte b) =>
		new(TerminalColorKind.Rgb, 0, (uint)((r << 16) | (g << 8) | b));
}

/// <summary>
/// 渲染端可绘制的一段同色文本；<see cref="Column"/> 是它起始的单元格列，<see cref="Cells"/> 是它占的格数
/// （宽字符与 emoji 占两格，组合符号零格）。渲染端按格数而不是字符数算列，簇里有多少码元都不影响对齐。
/// </summary>
internal readonly record struct TerminalRun(
	string Text,
	int Column,
	int Cells,
	TerminalColor Foreground,
	TerminalColor Background,
	bool Bold,
	bool Italic,
	bool Underline,
	bool Strikethrough,
	bool Dim,
	bool Hidden,
	bool Reverse,
	bool Link,
	bool Selected,
	bool Cursor);

/// <summary>应用请求的鼠标上报模式（DECSET 1000/1002/1003）。</summary>
internal enum TerminalMouseMode
{
	None,
	X10,
	Normal,
	Button,
	Any,
}

/// <summary>DECSCUSR 光标形态；是否闪烁由 <c>CursorBlink</c> 单独表示。</summary>
internal enum TerminalCursorStyle
{
	Block,
	Underline,
	Bar,
}

/// <summary>
/// 一块终端屏幕：单元格缓冲 + ANSI/VT 解析，参考 xterm.js 的行为实现增量部分。
/// <para>
/// 支持：scrollback（主屏历史行）、备用屏（1049/47/1047）、滚动区域（DECSTBM）、
/// G0/G1 字符集（DEC Special Graphics）、自动换行、原点模式、应用光标键、bracketed paste、
/// 鼠标上报（1000/1002/1003 + SGR 1006/1016 与 urxvt 1015）、文本选择、DSR/DA 应答。
/// </para>
/// <para>不做完整 VT100：少数边界序列按 xterm.js 的行为近似处理。</para>
/// </summary>
internal sealed class TerminalScreen
{
	public const int DefaultScrollback = 2000;

	private const int MinColumns = 2;
	private const int MinRows = 1;

	private readonly List<TerminalCell[]> _primary = [];
	private readonly List<TerminalCell[]> _alternate = [];
	private readonly List<int> _primaryVersions = [];
	private readonly List<int> _alternateVersions = [];

	/// <summary>每行是否“软换行”接到下一行；窗口变宽/变窄时据此把逻辑行拼回去重排。</summary>
	private readonly List<bool> _primaryWrapped = [];
	private readonly List<bool> _alternateWrapped = [];
	private readonly bool[] _graphicsCharset = new bool[2];
	private int _lineVersion;

	private int _columns;
	private int _rows;
	private int _scrollbackLimit = DefaultScrollback;

	private int _cursorRow;
	private int _cursorColumn;
	private bool _wrapPending;
	private bool _cursorVisible = true;
	private bool _autoWrap = true;
	private bool _originMode;
	private bool _alternateActive;
	private int _scrollTop;
	private int _scrollBottom;

	private int _viewOffset;
	private int _activeCharset;
	private int? _singleShift;

	private ParseState _state;
	private readonly List<int> _parameters = [];
	private int _parameter;
	private bool _hasParameter;
	private char _csiMarker;
	private string _osc = string.Empty;

	/// <summary>上一块输出末尾留下的半个代理对：PTY 读块可能把一个码点劈成两半。</summary>
	private char _pendingHighSurrogate;

	/// <summary>上一个写入的码元是 ZWJ：下一个字符并进同一格，不另起一格。</summary>
	private bool _joinNext;

	/// <summary>最近写入的格子（绝对行 + 列），供组合符号、ZWJ 链、区域指示符回填进同一簇。</summary>
	private int _lastCellRow = -1;
	private int _lastCellColumn = -1;

	private (int Row, int Column) _savedCursor;
	private (int Row, int Column) _savedPrimaryCursor;
	private bool _mouseNormal;
	private bool _mouseButton;
	private bool _mouseAny;
	private bool _insertMode;
	private bool _blockSelection;
	private bool _reverseVideo;
	private TerminalCursorStyle _cursorStyle;
	private readonly List<bool> _tabStops = [];
	private readonly List<string> _links = [];
	private readonly Dictionary<string, int> _linkIds = new(StringComparer.Ordinal);
	private int _currentLink;
	private char _csiIntermediate;

	/// <summary>OSC 4 覆盖后的调色板；写入时整块替换，读端拿到的都是稳定快照。</summary>
	private uint?[] _palette = new uint?[256];
	private uint? _defaultForeground;
	private uint? _defaultBackground;
	private uint? _cursorColor;
	private int _colorVersion;

	private (int Row, int Column)? _selectionAnchor;
	private (int Row, int Column)? _selectionFocus;

	public TerminalScreen(int columns, int rows)
	{
		Resize(columns, rows);
	}

	/// <summary>屏幕内容、模式或选择变化时递增，渲染端据此决定是否重绘。</summary>
	public int Version { get; private set; }

	public int Columns => _columns;

	public int Rows => _rows;

	public int CursorRow => _cursorRow;

	public int CursorColumn => _cursorColumn;

	public bool CursorVisible => _cursorVisible && _viewOffset == 0;

	public bool IsAlternateScreen => _alternateActive;

	public TerminalMouseMode MouseMode { get; private set; }

	/// <summary>SGR 1006 扩展鼠标坐标。</summary>
	public bool SgrMouse { get; private set; }

	/// <summary>DECSET 1015（urxvt）：十进制鼠标坐标编码。</summary>
	public bool UrxvtMouse { get; private set; }

	/// <summary>DECSET 1016（SGR-Pixels）：鼠标坐标以像素而非单元格上报。</summary>
	public bool PixelMouse { get; private set; }

	public bool BracketedPaste { get; private set; }

	/// <summary>DECSCUSR（<c>CSI Ps SP q</c>）设置的光标形状。</summary>
	public TerminalCursorStyle CursorStyle => _cursorStyle;

	/// <summary>DECSCUSR 的闪烁位（1/3/5）；默认不闪烁。</summary>
	public bool CursorBlink { get; private set; }

	/// <summary>DECSCNM（模式 5）：整屏反色。</summary>
	public bool ReverseVideo => _reverseVideo;

	/// <summary>调色板快照（null 表示沿用内置颜色）。</summary>
	public uint?[] Palette => Volatile.Read(ref _palette);

	/// <summary>OSC 10 覆盖的默认前景色；null 表示用宿主主题。</summary>
	public uint? DefaultForegroundRgb => _defaultForeground;

	/// <summary>OSC 11 覆盖的默认背景色；null 表示用宿主主题。</summary>
	public uint? DefaultBackgroundRgb => _defaultBackground;

	/// <summary>OSC 12 覆盖的光标颜色；null 表示用强调色。</summary>
	public uint? CursorColorRgb => _cursorColor;

	/// <summary>调色板/默认色版本：渲染端据此丢弃行缓存。</summary>
	public int ColorVersion => Volatile.Read(ref _colorVersion);

	/// <summary>宿主题色，供 OSC 10/11 查询回复默认值。</summary>
	public uint HostForeground { get; set; }

	public uint HostBackground { get; set; }

	public bool ApplicationCursorKeys { get; private set; }

	/// <summary>向 PTY 回写应答（DSR/DA）。由宿主注入。</summary>
	public Action<string>? Respond { get; set; }

	/// <summary>选区变化时触发（含清空），供 PRIMARY selection 同步。</summary>
	public event Action? SelectionChanged;

	/// <summary>OSC 52 写入系统剪贴板（由宿主注入，后台线程调用需自行转到 UI 线程）。</summary>
	public Action<string>? ClipboardWrite { get; set; }

	/// <summary>DECSET 1004：窗口焦点变化时上报 <c>CSI I</c> / <c>CSI O</c>。</summary>
	public bool FocusReporting { get; private set; }

	/// <summary>向上翻看历史时大于 0，0 表示贴底。</summary>
	public int ViewOffset => _viewOffset;

	public int MaxViewOffset => _alternateActive ? 0 : Math.Max(0, _primary.Count - _rows);

	/// <summary>视口第一行对应的缓冲区绝对行。</summary>
	public int ViewTop => Math.Max(0, ActiveLines.Count - _rows - _viewOffset);

	public int ScrollbackLines => _alternateActive ? 0 : Math.Max(0, _primary.Count - _rows);

	public bool HasSelection => _selectionAnchor is not null && _selectionFocus is not null;

	private List<TerminalCell[]> ActiveLines => _alternateActive ? _alternate : _primary;

	private int BufferBase => _alternateActive ? 0 : Math.Max(0, _primary.Count - _rows);

	// ------------------------------------------------------------------ 视口

	public void Resize(int columns, int rows)
	{
		columns = Math.Max(MinColumns, columns);
		rows = Math.Max(MinRows, rows);
		var sizeChanged = columns != _columns || rows != _rows;
		var widthChanged = columns != _columns;
		var oldRows = _rows;

		// 只改高度时视口底部锚定（多出来的历史在顶部长出来），光标要跟着它所在的那一行走，
		// 否则行数一变光标就跳到别的行上——拖分隔条时会看到提示符和光标脱节。
		var oldBase = Math.Max(0, _primary.Count - oldRows);
		var cursorBufferRow = oldBase + _cursorRow;

		_columns = columns;
		_rows = rows;
		ResizeTabStops();

		var reflowCursorRow = -1;
		var reflowCursorColumn = 0;
		var reflowViewTop = 0;
		if (widthChanged)
		{
			if (_alternateActive)
			{
				// 备用屏时主屏暂不重排，只保证宽度一致，退出备用屏后仍可用。
				for (var index = 0; index < _primary.Count; index++)
				{
					var line = _primary[index];
					ResizeLine(ref line, columns);
					_primary[index] = line;
				}
			}
			else
			{
				ReflowPrimary(oldRows, out reflowCursorRow, out reflowCursorColumn, out reflowViewTop);
			}

			for (var index = 0; index < _alternate.Count; index++)
			{
				var line = _alternate[index];
				ResizeLine(ref line, columns);
				_alternate[index] = line;
			}
		}

		EnsurePrimaryHeight(rows);
		if (_alternateActive)
		{
			EnsureAlternateHeight(rows);
		}

		if (reflowCursorRow >= 0)
		{
			_cursorRow = Math.Clamp(reflowCursorRow - BufferBase, 0, rows - 1);
			_cursorColumn = Math.Clamp(reflowCursorColumn, 0, columns - 1);
			_viewOffset = Math.Clamp(_primary.Count - rows - reflowViewTop, 0, MaxViewOffset);
		}
		else if (!widthChanged && !_alternateActive)
		{
			// 行数变化：同一缓冲行在新的视口里往上/往下挪了几行，光标跟着挪。
			_cursorRow = Math.Clamp(cursorBufferRow - BufferBase, 0, rows - 1);
		}

		_scrollTop = 0;
		_scrollBottom = rows - 1;
		_cursorRow = Math.Clamp(_cursorRow, 0, rows - 1);
		_cursorColumn = Math.Clamp(_cursorColumn, 0, columns - 1);
		_wrapPending = false;
		_viewOffset = Math.Clamp(_viewOffset, 0, MaxViewOffset);
		if (sizeChanged)
		{
			// 列宽变化会重排每一行，行缓存必须整体作废；只改高度时行内容没动，
			// 渲染端按「视图行 → 缓冲行」的映射自己发现哪些行挪了位置，不必整屏重建。
			if (widthChanged)
			{
				MarkAllLines();
			}

			Touch();
		}
	}

	/// <summary>向上（正数）或向下（负数）滚动视口；返回是否发生变化。</summary>
	public bool ScrollView(int lines)
	{
		var next = Math.Clamp(_viewOffset + lines, 0, MaxViewOffset);
		if (next == _viewOffset)
		{
			return false;
		}

		_viewOffset = next;
		Touch();
		return true;
	}

	public void ScrollToBottom()
	{
		if (_viewOffset == 0)
		{
			return;
		}

		_viewOffset = 0;
		Touch();
	}

	/// <summary>当前视口的文本（不含历史），用于整屏复制。</summary>
	public string GetText()
	{
		var builder = new StringBuilder();
		for (var row = 0; row < _rows; row++)
		{
			if (row > 0)
			{
				builder.Append('\n');
			}

			AppendLine(builder, ViewTop + row, 0, _columns - 1);
		}

		return builder.ToString().TrimEnd('\n');
	}

	// ------------------------------------------------------------------ 选择

	/// <summary>开始选择；<paramref name="block"/> 为真时是矩形（区块）选择（Alt+拖拽）。</summary>
	public void BeginSelection(int bufferRow, int column, bool block = false)
	{
		_selectionAnchor = (bufferRow, Math.Clamp(column, 0, _columns - 1));
		_selectionFocus = _selectionAnchor;
		_blockSelection = block;
		Touch();
		SelectionChanged?.Invoke();
	}

	public void ExtendSelection(int bufferRow, int column)
	{
		if (_selectionAnchor is null)
		{
			return;
		}

		_selectionFocus = (bufferRow, Math.Clamp(column, 0, _columns - 1));
		Touch();
		SelectionChanged?.Invoke();
	}

	public void ClearSelection()
	{
		if (_selectionAnchor is null)
		{
			return;
		}

		_selectionAnchor = null;
		_selectionFocus = null;
		_blockSelection = false;
		Touch();
		SelectionChanged?.Invoke();
	}

	/// <summary>全选当前活动缓冲（主屏含 scrollback）。</summary>
	public void SelectAll()
	{
		var last = ActiveLines.Count - 1;
		if (last < 0)
		{
			return;
		}

		_selectionAnchor = (0, 0);
		_selectionFocus = (last, _columns - 1);
		_blockSelection = false;
		Touch();
		SelectionChanged?.Invoke();
	}

	/// <summary>双击选词：字母、数字与常见路径字符算一个词。</summary>
	public void SelectWord(int bufferRow, int column)
	{
		var line = BufferLine(bufferRow);
		if (line is null)
		{
			return;
		}

		column = Math.Clamp(column, 0, _columns - 1);
		var start = column;
		var end = column;
		if (IsWord(line[column].Text))
		{
			while (start > 0 && IsWord(line[start - 1].Text))
			{
				start--;
			}

			while (end < _columns - 1 && IsWord(line[end + 1].Text))
			{
				end++;
			}
		}

		_selectionAnchor = (bufferRow, start);
		_selectionFocus = (bufferRow, end);
		_blockSelection = false;
		Touch();
		SelectionChanged?.Invoke();
	}

	/// <summary>三击选行。</summary>
	public void SelectLine(int bufferRow)
	{
		if (BufferLine(bufferRow) is null)
		{
			return;
		}

		_selectionAnchor = (bufferRow, 0);
		_selectionFocus = (bufferRow, _columns - 1);
		_blockSelection = false;
		Touch();
		SelectionChanged?.Invoke();
	}

	/// <summary>选中的文本；没有选区时返回 <c>null</c>。</summary>
	public string? GetSelectionText()
	{
		if (TryGetBlock(out var firstRow, out var lastRow, out var left, out var right))
		{
			var block = new StringBuilder();
			for (var row = firstRow; row <= lastRow; row++)
			{
				if (row > firstRow)
				{
					block.Append('\n');
				}

				AppendLine(block, row, left, right);
			}

			return block.ToString();
		}

		if (!TryGetSelection(out var start, out var end))
		{
			return null;
		}

		var builder = new StringBuilder();
		for (var row = start.Row; row <= end.Row; row++)
		{
			if (row > start.Row)
			{
				builder.Append('\n');
			}

			var from = row == start.Row ? start.Column : 0;
			var to = row == end.Row ? end.Column : _columns - 1;
			AppendLine(builder, row, from, to);
		}

		return builder.ToString();
	}

	/// <summary>某一行在视口中的选区列范围（含端点）；没有选区时返回 <c>null</c>。</summary>
	public (int From, int To)? GetSelectionRange(int viewportRow)
	{
		if (TryGetBlock(out var firstRow, out var lastRow, out var left, out var right))
		{
			var blockRow = ViewTop + viewportRow;
			return blockRow >= firstRow && blockRow <= lastRow ? (left, right) : null;
		}

		if (!TryGetSelection(out var start, out var end))
		{
			return null;
		}

		var row = ViewTop + viewportRow;
		if (row < start.Row || row > end.Row)
		{
			return null;
		}

		var from = row == start.Row ? start.Column : 0;
		var to = row == end.Row ? end.Column : _columns - 1;
		return (from, Math.Min(to, _columns - 1));
	}

	/// <summary>区块选择的矩形边界（左上/右下角按行列各取极值）。</summary>
	private bool TryGetBlock(out int firstRow, out int lastRow, out int left, out int right)
	{
		firstRow = 0;
		lastRow = 0;
		left = 0;
		right = 0;
		if (!_blockSelection || _selectionAnchor is not { } anchor || _selectionFocus is not { } focus)
		{
			return false;
		}

		var max = ActiveLines.Count - 1;
		if (max < 0)
		{
			return false;
		}

		anchor = (Math.Clamp(anchor.Row, 0, max), Math.Clamp(anchor.Column, 0, _columns - 1));
		focus = (Math.Clamp(focus.Row, 0, max), Math.Clamp(focus.Column, 0, _columns - 1));
		firstRow = Math.Min(anchor.Row, focus.Row);
		lastRow = Math.Max(anchor.Row, focus.Row);
		left = Math.Min(anchor.Column, focus.Column);
		right = Math.Max(anchor.Column, focus.Column);
		return true;
	}

	private bool TryGetSelection(out (int Row, int Column) start, out (int Row, int Column) end)
	{
		start = default;
		end = default;
		if (_selectionAnchor is not { } anchor || _selectionFocus is not { } focus)
		{
			return false;
		}

		var max = ActiveLines.Count - 1;
		if (max < 0)
		{
			return false;
		}

		anchor = (Math.Clamp(anchor.Row, 0, max), Math.Clamp(anchor.Column, 0, _columns - 1));
		focus = (Math.Clamp(focus.Row, 0, max), Math.Clamp(focus.Column, 0, _columns - 1));

		if (anchor.Row < focus.Row || (anchor.Row == focus.Row && anchor.Column <= focus.Column))
		{
			start = anchor;
			end = focus;
		}
		else
		{
			start = focus;
			end = anchor;
		}

		return true;
	}

	private static bool IsWord(char c) =>
		char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '\\' or '~' or ':' or '@';

	// ------------------------------------------------------------------ 行缓冲

	private void EnsurePrimaryHeight(int rows)
	{
		while (_primary.Count < rows)
		{
			_primary.Add(NewLine(_columns));
			_primaryVersions.Add(0);
			_primaryWrapped.Add(false);
		}
	}

	private void EnsureAlternateHeight(int rows)
	{
		while (_alternate.Count < rows)
		{
			_alternate.Add(NewLine(_columns));
			_alternateVersions.Add(0);
			_alternateWrapped.Add(false);
		}

		while (_alternate.Count > rows)
		{
			_alternate.RemoveAt(_alternate.Count - 1);
			_alternateVersions.RemoveAt(_alternateVersions.Count - 1);
			_alternateWrapped.RemoveAt(_alternateWrapped.Count - 1);
		}
	}

	/// <summary>某一缓冲行的内容版本：渲染端据此跳过未变化的行。</summary>
	internal int LineVersion(int bufferRow)
	{
		var versions = ActiveVersions;
		return bufferRow >= 0 && bufferRow < versions.Count ? versions[bufferRow] : 0;
	}

	private List<int> ActiveVersions => _alternateActive ? _alternateVersions : _primaryVersions;

	private List<bool> ActiveWrapped => _alternateActive ? _alternateWrapped : _primaryWrapped;

	private void MarkLine(int bufferRow)
	{
		var versions = ActiveVersions;
		if (bufferRow >= 0 && bufferRow < versions.Count)
		{
			versions[bufferRow] = ++_lineVersion;
		}
	}

	private void MarkAllLines()
	{
		var versions = ActiveVersions;
		for (var index = 0; index < versions.Count; index++)
		{
			versions[index] = ++_lineVersion;
		}

		// 整屏行被搬动/重排：上一格的位置已经不可信，组合符号不要再回填到那里。
		InvalidateLastCell();
	}

	/// <summary>丢弃「最近写入的格子」：行被搬动、清屏或重排后，续接码元必须重新开始。</summary>
	private void InvalidateLastCell()
	{
		_lastCellRow = -1;
		_lastCellColumn = -1;
	}

	/// <summary>行缓冲与行版本必须同步增删，否则行级缓存会串行。</summary>
	private void InsertBufferLine(int index, TerminalCell[] line)
	{
		ActiveLines.Insert(index, line);
		ActiveVersions.Insert(index, 0);
		ActiveWrapped.Insert(index, false);
	}

	private void RemoveBufferLine(int index)
	{
		ActiveLines.RemoveAt(index);
		ActiveVersions.RemoveAt(index);
		ActiveWrapped.RemoveAt(index);
	}

	private bool LineWrapped(int bufferRow)
	{
		var wrapped = ActiveWrapped;
		return bufferRow >= 0 && bufferRow < wrapped.Count && wrapped[bufferRow];
	}

	private void SetLineWrapped(int bufferRow, bool value)
	{
		var wrapped = ActiveWrapped;
		if (bufferRow >= 0 && bufferRow < wrapped.Count)
		{
			wrapped[bufferRow] = value;
		}
	}

	private static void ResizeLine(ref TerminalCell[] line, int columns)
	{
		if (line.Length != columns)
		{
			var previous = line.Length;
			Array.Resize(ref line, columns);
			// 新长出来的格子必须是空格：默认 struct 的 '\0' 是宽字符右半的占位标记，不能拿来做空。
			for (var index = previous; index < columns; index++)
			{
				line[index] = TerminalCell.Blank;
			}
		}
	}

	/// <summary>
	/// 主屏重排：把软换行的行拼回逻辑行，再按新列宽重新切分。
	/// 光标、视口与软换行标记一起迁移，缩放窗口不再截断长命令行。
	/// </summary>
	private void ReflowPrimary(int oldRows, out int cursorRow, out int cursorColumn, out int viewTopRow)
	{
		var source = _primary;
		var sourceWrapped = _primaryWrapped;
		var cursorSourceRow = source.Count - oldRows + _cursorRow;

		var rebuilt = new List<TerminalCell[]>(source.Count + 8);
		var rebuiltWrapped = new List<bool>(source.Count + 8);
		var startMap = new int[source.Count];
		cursorRow = -1;
		cursorColumn = 0;
		viewTopRow = Math.Max(0, source.Count - oldRows - _viewOffset);

		// 光标下方的空白行是屏幕填充，不应参与重排，否则内容会凭空挤出一行回滚。
		var effectiveCount = source.Count;
		while (effectiveCount > 0 &&
			effectiveCount - 1 > cursorSourceRow &&
			!sourceWrapped[effectiveCount - 1] &&
			LineContentLength(source[effectiveCount - 1]) == 0)
		{
			effectiveCount--;
		}

		var row = 0;
		while (row < effectiveCount)
		{
			var groupStart = rebuilt.Count;
			var last = row;
			while (last + 1 < effectiveCount && last < sourceWrapped.Count && sourceWrapped[last])
			{
				last++;
			}

			var current = NewLine(_columns);
			var used = 0;
			for (var index = row; index <= last; index++)
			{
				var line = source[index];
				var length = index == last ? LineContentLength(line) : line.Length;
				for (var column = 0; column < length; column++)
				{
					var cell = line[column];
					if (cell.Text == '\0')
					{
						// 宽字符右半格由左半格一起搬。
						continue;
					}

					var width = CellWidthAt(line, column);
					if (used + width > _columns && used > 0)
					{
						rebuilt.Add(current);
						rebuiltWrapped.Add(true);
						current = NewLine(_columns);
						used = 0;
					}

					if (index == cursorSourceRow && column == _cursorColumn)
					{
						cursorRow = rebuilt.Count;
						cursorColumn = used;
					}

					current[used] = cell;
					if (width == 2 && used + 1 < _columns)
					{
						// 右半格必须是占位格：写成普通空格会被当成真字符输出，整行多一个空格。
						current[used + 1] = cell with { Text = '\0', Trailers = null };
					}

					used += width;
				}
			}

			// 光标停在逻辑行末尾（后面没有字符）时落在已用宽度处。
			if (cursorRow < 0 && cursorSourceRow >= row && cursorSourceRow <= last &&
				_cursorColumn >= LineContentLength(source[cursorSourceRow]))
			{
				cursorRow = rebuilt.Count;
				cursorColumn = Math.Min(used, _columns - 1);
			}

			rebuilt.Add(current);
			rebuiltWrapped.Add(false);
			for (var index = row; index <= last; index++)
			{
				startMap[index] = groupStart;
			}

			row = last + 1;
		}

		_primary.Clear();
		_primary.AddRange(rebuilt);
		_primaryWrapped.Clear();
		_primaryWrapped.AddRange(rebuiltWrapped);
		_primaryVersions.Clear();
		for (var index = 0; index < rebuilt.Count; index++)
		{
			_primaryVersions.Add(0);
		}

		viewTopRow = effectiveCount == 0
			? 0
			: startMap[Math.Clamp(viewTopRow, 0, effectiveCount - 1)];
	}

	/// <summary>行内最后一个有内容的单元格之后（行尾默认空格是填充，不算内容）。</summary>
	private static int LineContentLength(TerminalCell[] line)
	{
		var length = line.Length;
		while (length > 0 && IsDefaultCell(line[length - 1]))
		{
			length--;
		}

		return length;
	}

	/// <summary>某格的显示宽度：右半占位格为 0，宽字符（下一格是占位）为 2，其余为 1。</summary>
	private static int CellWidthAt(TerminalCell[] line, int column) =>
		line[column].Text == '\0'
			? 0
			: column + 1 < line.Length && line[column + 1].Text == '\0' ? 2 : 1;

	private static bool IsDefaultCell(in TerminalCell cell) =>
		cell.Text is ' ' or '\0' &&
		cell.Trailers is null &&
		cell.Foreground.Kind == TerminalColorKind.Default &&
		cell.Background.Kind == TerminalColorKind.Default &&
		!cell.Bold && !cell.Italic && !cell.Underline && !cell.Strikethrough &&
		!cell.Dim && !cell.Hidden && !cell.Reverse && cell.Link == 0;

	/// <summary>列数变化时同步制表位：已有位保留，新列沿用默认的每 8 列一个。</summary>
	private void ResizeTabStops()
	{
		while (_tabStops.Count > _columns)
		{
			_tabStops.RemoveAt(_tabStops.Count - 1);
		}

		for (var column = _tabStops.Count; column < _columns; column++)
		{
			_tabStops.Add(column > 0 && column % 8 == 0);
		}
	}

	/// <summary>下一个制表位；没有就用最后一列（与 xterm 一致）。</summary>
	private int NextTabStop(int from)
	{
		for (var column = from + 1; column < _columns; column++)
		{
			if (_tabStops[column])
			{
				return column;
			}
		}

		return _columns - 1;
	}

	private TerminalCell[]? BufferLine(int bufferRow)
	{
		var lines = ActiveLines;
		return bufferRow >= 0 && bufferRow < lines.Count ? lines[bufferRow] : null;
	}

	private int AbsoluteRow => BufferBase + _cursorRow;

	private void Touch() => Version++;

	private void TrimScrollback()
	{
		if (_alternateActive)
		{
			return;
		}

		var excess = _primary.Count - (_rows + _scrollbackLimit);
		if (excess <= 0)
		{
			return;
		}

		_primary.RemoveRange(0, excess);
		_primaryVersions.RemoveRange(0, excess);
		_primaryWrapped.RemoveRange(0, excess);
		MarkAllLines();
		if (_viewOffset > 0)
		{
			_viewOffset = Math.Max(0, _viewOffset - excess);
		}

		if (_selectionAnchor is { } anchor)
		{
			_selectionAnchor = (Math.Max(0, anchor.Row - excess), anchor.Column);
		}

		if (_selectionFocus is { } focus)
		{
			_selectionFocus = (Math.Max(0, focus.Row - excess), focus.Column);
		}

		if (_selectionAnchor is not null)
		{
			SelectionChanged?.Invoke();
		}
	}

	// ------------------------------------------------------------------ 解析

	/// <summary>
	/// 喂入一段（可跨块的）终端输出。普通文本用 <see cref="SearchValues{T}"/> 批量跳过，
	/// 版本号每块只递增一次：高频输出时不再为每个字符做一次失效标记。
	/// </summary>
	public void Feed(ReadOnlySpan<char> text)
	{
		if (text.IsEmpty)
		{
			return;
		}

		while (!text.IsEmpty)
		{
			if (_state != ParseState.Ground)
			{
				Feed(text[0]);
				text = text[1..];
				continue;
			}

			var index = text.IndexOfAny(GroundSpecials);
			if (index < 0)
			{
				WriteSpan(text);
				break;
			}

			if (index > 0)
			{
				WriteSpan(text[..index]);
			}

			Feed(text[index]);
			text = text[(index + 1)..];
		}

		Touch();
	}

	/// <summary>C0 控制字符（含 ESC）：Ground 状态下唯一需要退出快速路径的字符。</summary>
	private static readonly SearchValues<char> GroundSpecials = CreateGroundSpecials();

	private static SearchValues<char> CreateGroundSpecials()
	{
		var chars = new char[32];
		for (var index = 0; index < chars.Length; index++)
		{
			chars[index] = (char)index;
		}

		return SearchValues.Create(chars);
	}

	/// <summary>
	/// 批量写入一段可打印文本。按码点而不是按 char 走：代理对、组合符号、ZWJ 链都归一个簇、占一格。
	/// 末尾那个孤立高代理留到下一块再拼——PTY 读块会把它劈开。
	/// </summary>
	private void WriteSpan(ReadOnlySpan<char> text)
	{
		if (_pendingHighSurrogate != '\0')
		{
			var high = _pendingHighSurrogate;
			_pendingHighSurrogate = '\0';
			if (!text.IsEmpty && char.IsLowSurrogate(text[0]))
			{
				WriteRune(high, text[0]);
				text = text[1..];
			}
			else
			{
				WriteRune(high, '\0');
			}
		}

		while (!text.IsEmpty)
		{
			var first = text[0];
			if (char.IsHighSurrogate(first))
			{
				if (text.Length < 2)
				{
					_pendingHighSurrogate = first;
					return;
				}

				WriteRune(first, text[1]);
				text = text[2..];
				continue;
			}

			WriteRune(first, '\0');
			text = text[1..];
		}
	}

	private void Feed(char c)
	{
		switch (_state)
		{
			case ParseState.Ground:
				FeedGround(c);
				break;

			case ParseState.Escape:
				FeedEscape(c);
				break;

			case ParseState.Csi:
				FeedCsi(c);
				break;

			case ParseState.Osc:
				if (c == '\a')
				{
					HandleOsc();
				}
				else if (c == '\u001b')
				{
					_state = ParseState.OscEscape;
				}
				else if (_osc.Length < 512)
				{
					_osc += c;
				}

				break;

			case ParseState.OscEscape:
				if (c == '\\')
				{
					HandleOsc();
				}
				else
				{
					_state = ParseState.Osc;
				}

				break;

			case ParseState.Charset:
				FeedCharset(c);
				break;

			case ParseState.Hash:
				if (c == '8')
				{
					FillScreen('E');
				}

				_state = ParseState.Ground;
				break;
		}
	}

	/// <summary>DECALN（<c>ESC # 8</c>）：整屏填 'E'，光标回原点，用于检查对齐。</summary>
	private void FillScreen(char c)
	{
		var cell = TerminalCell.Blank;
		cell.Text = c;
		var lines = ActiveLines;
		for (var row = 0; row < _rows; row++)
		{
			Array.Fill(lines[BufferBase + row], cell);
		}

		(_cursorRow, _cursorColumn) = (0, 0);
		_wrapPending = false;
		MarkAllLines();
		Touch();
	}

	private void FeedGround(char c)
	{
		switch (c)
		{
			case '\u001b':
				_state = ParseState.Escape;
				return;

			case '\r':
				_cursorColumn = 0;
				_wrapPending = false;
				return;

			case '\n':
			case '\v':
			case '\f':
				LineFeed();
				return;

			case '\b':
				_cursorColumn = Math.Max(0, _cursorColumn - 1);
				_wrapPending = false;
				return;

			case '\t':
				_cursorColumn = NextTabStop(_cursorColumn);
				_wrapPending = false;
				return;

			case '\u000e':
				_activeCharset = 1;
				return;

			case '\u000f':
				_activeCharset = 0;
				return;

			case '\a':
				return;
		}

		if (c < ' ')
		{
			return;
		}

		WriteRune(c, '\0');
	}

	private void FeedEscape(char c)
	{
		switch (c)
		{
			case '[':
				BeginCsi();
				return;

			case ']':
				_state = ParseState.Osc;
				_osc = string.Empty;
				return;

			case '(':
			case ')':
				// 下一字符决定 G0/G1 是 ASCII 还是 DEC Special Graphics。
				_state = ParseState.Charset;
				PendingCharset = c == '(' ? 0 : 1;
				return;

			case '7':
				_savedCursor = (_cursorRow, _cursorColumn);
				_state = ParseState.Ground;
				return;

			case '8':
				(_cursorRow, _cursorColumn) = _savedCursor;
				ClampCursor();
				_state = ParseState.Ground;
				Touch();
				return;

			case 'D':
				LineFeed();
				_state = ParseState.Ground;
				return;

			case 'E':
				// NEL：另起一行（列归零，底部滚动）。
				_cursorColumn = 0;
				LineFeed();
				_state = ParseState.Ground;
				return;

			case 'M':
				ReverseIndex();
				_state = ParseState.Ground;
				return;

			case 'c':
				ResetAll();
				_state = ParseState.Ground;
				return;

			case 'N':
				_singleShift = 1;
				_state = ParseState.Ground;
				return;

			case 'O':
				_singleShift = 0;
				_state = ParseState.Ground;
				return;

			case 'H':
				// HTS：在当前列设置制表位。
				if (_cursorColumn < _tabStops.Count)
				{
					_tabStops[_cursorColumn] = true;
				}

				_state = ParseState.Ground;
				return;

			case '#':
				// ESC # 8 = DECALN（下一字符决定）。
				_state = ParseState.Hash;
				return;
		}

		_state = ParseState.Ground;
	}

	/// <summary>等待 <c>ESC (</c> / <c>ESC )</c> 之后决定 G0 还是 G1 的下标。</summary>
	private int PendingCharset { get; set; }

	private void FeedCharset(char c)
	{
		if (PendingCharset is >= 0 and <= 1 && c is '0' or 'B' or '<' or '>')
		{
			_graphicsCharset[PendingCharset] = c == '0';
		}

		_state = ParseState.Ground;
	}

	private void BeginCsi()
	{
		_state = ParseState.Csi;
		_parameters.Clear();
		_parameter = 0;
		_hasParameter = false;
		_csiMarker = '\0';
		_csiIntermediate = '\0';
	}

	private void FeedCsi(char c)
	{
		if (c is >= '0' and <= '9')
		{
			_parameter = Math.Min(100_000, (_parameter * 10) + (c - '0'));
			_hasParameter = true;
			return;
		}

		if (c is >= ' ' and <= '/')
		{
			// 中间字节（如 DECSCUSR 的 SP、DECSTR 的 !）。
			_csiIntermediate = c;
			return;
		}

		if (c == ';')
		{
			_parameters.Add(_hasParameter ? _parameter : 0);
			_parameter = 0;
			_hasParameter = false;
			return;
		}

		if (c is >= '<' and <= '?')
		{
			_csiMarker = c;
			return;
		}

		if (c is >= '@' and <= '~')
		{
			if (_hasParameter || _parameters.Count > 0)
			{
				_parameters.Add(_hasParameter ? _parameter : 0);
			}

			_state = ParseState.Ground;
			HandleCsi(c);
		}
	}

	private void HandleCsi(char final)
	{
		var first = Parameter(0, 1);
		switch (final)
		{
			case 'm' when _csiMarker == '\0':
				HandleSgr();
				return;

			case 'H':
			case 'f':
				MoveTo(Parameter(0, 1) - 1, Parameter(1, 1) - 1);
				return;

			case 'A':
				MoveTo(_cursorRow - first, _cursorColumn);
				return;

			case 'B':
				MoveTo(_cursorRow + first, _cursorColumn);
				return;

			case 'C':
				MoveTo(_cursorRow, _cursorColumn + first);
				return;

			case 'D':
				MoveTo(_cursorRow, _cursorColumn - first);
				return;

			case 'E':
				MoveTo(_cursorRow + first, 0);
				return;

			case 'F':
				MoveTo(_cursorRow - first, 0);
				return;

			case 'G':
				MoveTo(_cursorRow, first - 1);
				return;

			case 'd':
				MoveTo(first - 1, _cursorColumn);
				return;

			case 'J':
				EraseDisplay(_parameters.Count > 0 ? _parameters[0] : 0);
				return;

			case 'K':
				EraseLine(_parameters.Count > 0 ? _parameters[0] : 0);
				return;

			case 'P':
				DeleteCharacters(first);
				return;

			case '@':
				InsertCharacters(first);
				return;

			case 'L':
				InsertLines(first);
				return;

			case 'M':
				DeleteLines(first);
				return;

			case 'X':
				EraseCharacters(first);
				return;

			case 'S':
				ScrollUpRegion(_scrollTop, _scrollBottom, first);
				return;

			case 'T':
				ScrollDownRegion(_scrollTop, _scrollBottom, first);
				return;

			case 'r':
				SetScrollRegion(first - 1, Parameter(1, _rows) - 1);
				return;

			case 's':
				_savedCursor = (_cursorRow, _cursorColumn);
				return;

			case 'u':
				(_cursorRow, _cursorColumn) = _savedCursor;
				ClampCursor();
				Touch();
				return;

			case 'h':
			case 'l':
				HandleMode(final == 'h');
				return;

			case 'g' when _csiMarker == '\0':
				ClearTabStops(_parameters.Count > 0 ? _parameters[0] : 0);
				return;

			case 'q' when _csiIntermediate is '\0' or ' ' && _csiMarker == '\0':
			{
				// DECSCUSR：1/2 块、3/4 下划线、5/6 竖线，奇数位闪烁；0 回默认（实心块）。
				// 规范写法是 CSI Ps SP q，但 vim/kitty 这类也常发不带空格的 CSI Ps q，一并受理。
				var shape = _parameters.Count > 0 ? _parameters[0] : 0;
				_cursorStyle = shape switch
				{
					3 or 4 => TerminalCursorStyle.Underline,
					5 or 6 => TerminalCursorStyle.Bar,
					_ => TerminalCursorStyle.Block,
				};
				CursorBlink = shape is 1 or 3 or 5;
				Touch();
				return;
			}

			case 'p' when _csiIntermediate == '!':
				SoftReset();
				return;

			case 'p' when _csiIntermediate == '$':
				ReportMode(_csiMarker == '?', _parameters.Count > 0 ? _parameters[0] : 0);
				return;

			case 'n' when _csiMarker == '\0':
				HandleDeviceStatus(first);
				return;

			case 'c' when _csiMarker == '\0':
				Respond?.Invoke("\u001b[?1;2c");
				return;

			case 'c' when _csiMarker == '>':
				Respond?.Invoke("\u001b[>0;276;0c");
				return;
		}
	}

	/// <summary>TBC（<c>CSI Ps g</c>）：0 清当前列，3 清全部。</summary>
	private void ClearTabStops(int mode)
	{
		if (mode == 3)
		{
			for (var index = 0; index < _tabStops.Count; index++)
			{
				_tabStops[index] = false;
			}

			return;
		}

		if (mode == 0 && _cursorColumn < _tabStops.Count)
		{
			_tabStops[_cursorColumn] = false;
		}
	}

	/// <summary>DECSTR（<c>CSI ! p</c>）：软复位，保留屏幕内容与回滚缓冲。</summary>
	private void SoftReset()
	{
		_pen = TerminalCell.Blank;
		_currentLink = 0;
		_cursorStyle = TerminalCursorStyle.Block;
		CursorBlink = false;
		_autoWrap = true;
		_originMode = false;
		_insertMode = false;
		_cursorVisible = true;
		_scrollTop = 0;
		_scrollBottom = _rows - 1;
		_wrapPending = false;
		Touch();
	}

	private int Parameter(int index, int fallback)
	{
		if (index >= _parameters.Count)
		{
			return fallback;
		}

		var value = _parameters[index];
		return value == 0 ? fallback : value;
	}

	private void HandleDeviceStatus(int mode)
	{
		switch (mode)
		{
			case 5:
				Respond?.Invoke("\u001b[0n");
				break;

			case 6:
				Respond?.Invoke($"\u001b[{_cursorRow + 1};{_cursorColumn + 1}R");
				break;
		}
	}

	/// <summary>DECRQM：1 = 已设置，2 = 已复位，0 = 不支持（xterm 约定）。</summary>
	private void ReportMode(bool privateMode, int mode)
	{
		var state = privateMode
			? mode switch
			{
				1 => ApplicationCursorKeys ? 1 : 2,
				5 => _reverseVideo ? 1 : 2,
				6 => _originMode ? 1 : 2,
				7 => _autoWrap ? 1 : 2,
				12 => CursorBlink ? 1 : 2,
				25 => _cursorVisible ? 1 : 2,
				47 or 1047 or 1049 => _alternateActive ? 1 : 2,
				1000 => MouseMode == TerminalMouseMode.Normal ? 1 : 2,
				1002 => MouseMode == TerminalMouseMode.Button ? 1 : 2,
				1003 => MouseMode == TerminalMouseMode.Any ? 1 : 2,
				1004 => FocusReporting ? 1 : 2,
				1006 => SgrMouse ? 1 : 2,
				1015 => UrxvtMouse ? 1 : 2,
				1016 => PixelMouse ? 1 : 2,
				2004 => BracketedPaste ? 1 : 2,
				_ => 0,
			}
			: mode switch
			{
				4 => _insertMode ? 1 : 2,
				_ => 0,
			};

		Respond?.Invoke($"\u001b[{(privateMode ? "?" : string.Empty)}{mode};{state}$y");
	}

	private void HandleMode(bool enabled)
	{
		if (_csiMarker != '?')
		{
			// 非私有模式：4 = IRM（插入模式）。
			foreach (var mode in _parameters)
			{
				if (mode == 4)
				{
					_insertMode = enabled;
				}
			}

			return;
		}

		foreach (var mode in _parameters)
		{
			switch (mode)
			{
				case 1:
					ApplicationCursorKeys = enabled;
					break;

				case 5:
					_reverseVideo = enabled;
					Touch();
					break;

				case 6:
					_originMode = enabled;
					(_cursorRow, _cursorColumn) = (enabled ? _scrollTop : 0, 0);
					_wrapPending = false;
					Touch();
					break;

				case 7:
					_autoWrap = enabled;
					_wrapPending = false;
					break;

				case 12:
					// DECSET 12：全局光标闪烁开关，与 DECSCUSR 的闪烁位共用同一状态。
					CursorBlink = enabled;
					break;

				case 25:
					_cursorVisible = enabled;
					Touch();
					break;

				case 47:
				case 1047:
					SwitchAlternate(enabled, clearOnEnter: false);
					break;

				case 1049:
					SwitchAlternate(enabled, clearOnEnter: true);
					break;

				case 1000:
					_mouseNormal = enabled;
					UpdateMouseMode();
					break;

				case 1002:
					_mouseButton = enabled;
					UpdateMouseMode();
					break;

				case 1003:
					_mouseAny = enabled;
					UpdateMouseMode();
					break;

				case 1006:
					SgrMouse = enabled;
					break;

				case 1015:
					UrxvtMouse = enabled;
					break;

				case 1016:
					PixelMouse = enabled;
					break;

				case 1004:
					FocusReporting = enabled;
					break;

				case 2004:
					BracketedPaste = enabled;
					break;
			}
		}
	}

	private void UpdateMouseMode() =>
		MouseMode = _mouseAny ? TerminalMouseMode.Any
			: _mouseButton ? TerminalMouseMode.Button
			: _mouseNormal ? TerminalMouseMode.Normal
			: TerminalMouseMode.None;

	private void SwitchAlternate(bool enabled, bool clearOnEnter)
	{
		if (enabled == _alternateActive)
		{
			return;
		}

		if (enabled)
		{
			_savedPrimaryCursor = (_cursorRow, _cursorColumn);
			_alternateActive = true;
			EnsureAlternateHeight(_rows);
			if (clearOnEnter)
			{
				for (var index = 0; index < _alternate.Count; index++)
				{
					Array.Fill(_alternate[index], TerminalCell.Blank);
				}

				for (var index = 0; index < _alternateWrapped.Count; index++)
				{
					_alternateWrapped[index] = false;
				}
			}

			_cursorRow = 0;
			_cursorColumn = 0;
			_viewOffset = 0;
		}
		else
		{
			_alternateActive = false;
			(_cursorRow, _cursorColumn) = _savedPrimaryCursor;
		}

		_scrollTop = 0;
		_scrollBottom = _rows - 1;
		_wrapPending = false;
		MarkAllLines();
		ClearSelection();
		Touch();
	}

	private void HandleSgr()
	{
		if (_parameters.Count == 0)
		{
			ResetPen();
			return;
		}

		for (var index = 0; index < _parameters.Count; index++)
		{
			var code = _parameters[index];
			switch (code)
			{
				case 0:
					ResetPen();
					break;
				case 1:
					_pen.Bold = true;
					break;
				case 2:
					_pen.Dim = true;
					break;
				case 3:
					_pen.Italic = true;
					break;
				case 4:
					_pen.Underline = true;
					break;
				case 7:
					_pen.Reverse = true;
					break;
				case 8:
					_pen.Hidden = true;
					break;
				case 9:
					_pen.Strikethrough = true;
					break;
				case 22:
					_pen.Bold = false;
					_pen.Dim = false;
					break;
				case 23:
					_pen.Italic = false;
					break;
				case 24:
					_pen.Underline = false;
					break;
				case 27:
					_pen.Reverse = false;
					break;
				case 28:
					_pen.Hidden = false;
					break;
				case 29:
					_pen.Strikethrough = false;
					break;
				case 39:
					_pen.Foreground = TerminalColor.Default;
					break;
				case 49:
					_pen.Background = TerminalColor.Default;
					break;

				case >= 30 and <= 37:
					_pen.Foreground = TerminalColor.Indexed((byte)(code - 30));
					break;

				case >= 90 and <= 97:
					_pen.Foreground = TerminalColor.Indexed((byte)(code - 90 + 8));
					break;

				case >= 40 and <= 47:
					_pen.Background = TerminalColor.Indexed((byte)(code - 40));
					break;

				case >= 100 and <= 107:
					_pen.Background = TerminalColor.Indexed((byte)(code - 100 + 8));
					break;

				case 38:
				case 48:
					index = ReadExtendedColor(code == 38, index);
					break;
			}
		}
	}

	private int ReadExtendedColor(bool foreground, int index)
	{
		if (index + 1 >= _parameters.Count)
		{
			return index;
		}

		var kind = _parameters[index + 1];
		if (kind == 5 && index + 2 < _parameters.Count)
		{
			var color = TerminalColor.Indexed((byte)Math.Clamp(_parameters[index + 2], 0, 255));
			Assign(foreground, color);
			return index + 2;
		}

		if (kind == 2 && index + 4 < _parameters.Count)
		{
			var color = TerminalColor.TrueColor(
				(byte)Math.Clamp(_parameters[index + 2], 0, 255),
				(byte)Math.Clamp(_parameters[index + 3], 0, 255),
				(byte)Math.Clamp(_parameters[index + 4], 0, 255));
			Assign(foreground, color);
			return index + 4;
		}

		return index + 1;
	}

	private void Assign(bool foreground, TerminalColor color)
	{
		if (foreground)
		{
			_pen.Foreground = color;
		}
		else
		{
			_pen.Background = color;
		}
	}

	private void ResetPen() => _pen = TerminalCell.Blank;

	private TerminalCell _pen = TerminalCell.Blank;

	// ------------------------------------------------------------------ 单元格写入

	/// <summary>
	/// 把一个簇写进当前格；宽字符再占一格右半占位。<paramref name="second"/> 为 <c>\0</c> 表示单字符。
	/// </summary>
	private void WriteCell(char first, char second, int width)
	{
		if (_wrapPending || (_cursorColumn + width > _columns && _autoWrap))
		{
			// 自动换行是软换行：记下来，窗口缩放时才能把长行拼回去。
			SetLineWrapped(AbsoluteRow, true);
			LineFeed();
			_cursorColumn = 0;
			_wrapPending = false;
		}

		if (_cursorColumn + width > _columns)
		{
			return;
		}

		// 输出时用户多半想看最新内容；贴底状态保持，历史视口保持不动由 ScrollUp 处理。
		var line = ActiveLines[AbsoluteRow];
		if (_insertMode)
		{
			// IRM：把光标起的单元格右移，再写入新字符。
			var shift = Math.Min(width, _columns - _cursorColumn);
			Array.Copy(line, _cursorColumn, line, _cursorColumn + shift, _columns - _cursorColumn - shift);
		}

		line[_cursorColumn] = new TerminalCell
		{
			Text = first,
			Trailers = second == '\0' ? null : second.ToString(),
			Foreground = _pen.Foreground,
			Background = _pen.Background,
			Bold = _pen.Bold,
			Italic = _pen.Italic,
			Underline = _pen.Underline,
			Strikethrough = _pen.Strikethrough,
			Dim = _pen.Dim,
			Hidden = _pen.Hidden,
			Reverse = _pen.Reverse,
			Link = _pen.Link,
		};

		if (width == 2)
		{
			line[_cursorColumn + 1] = line[_cursorColumn] with { Text = '\0', Trailers = null };
		}

		_lastCellRow = AbsoluteRow;
		_lastCellColumn = _cursorColumn;
		_cursorColumn += width;
		if (_cursorColumn >= _columns)
		{
			_cursorColumn = _columns - 1;
			_wrapPending = true;
		}

		MarkLine(AbsoluteRow);
	}

	/// <summary>
	/// 写入一个码点。零宽续接码元（组合符号、变体选择符、ZWJ 链、肤色修饰、Hangul 中/终声、
	/// 区域指示符的第二半）并进上一格而不开新格——列号不再因为“一个簇好几个码元”而漂移。
	/// </summary>
	private void WriteRune(char first, char second)
	{
		var mapped = MapCharset(first);
		int codepoint;
		if (second == '\0')
		{
			codepoint = mapped;
		}
		else if (char.IsHighSurrogate(mapped) && char.IsLowSurrogate(second))
		{
			codepoint = char.ConvertToUtf32(mapped, second);
		}
		else
		{
			// 代理对不完整（孤立的半对）：按替换字符写一格，不把后面的文本带偏。
			mapped = '\uFFFD';
			second = '\0';
			codepoint = mapped;
		}

		if ((_joinNext || TerminalText.IsExtend(codepoint) || TerminalText.IsRegionalIndicator(codepoint)) &&
			TryJoinLastCell(codepoint, mapped, second))
		{
			_joinNext = codepoint == TerminalText.Zwj;
			return;
		}

		_joinNext = false;
		var width = TerminalText.RuneWidth(codepoint);
		if (width <= 0)
		{
			// 行首孤立的组合符号没有可附着的格子：丢掉比占一格更接近 xterm 的行为。
			return;
		}

		WriteCell(mapped, second, width);
	}

	/// <summary>
	/// 把续接码元并进最近写入的格子。光标必须还紧挨着那一格：<c>\r</c>、CSI 定位、滚动都会让光标跑掉，
	/// 这时宁可当新字符写，也不能回填到别处去。
	/// </summary>
	private bool TryJoinLastCell(int codepoint, char first, char second)
	{
		if (_lastCellRow < 0 || _lastCellRow != AbsoluteRow)
		{
			return false;
		}

		var lines = ActiveLines;
		if (_lastCellRow >= lines.Count)
		{
			return false;
		}

		var line = lines[_lastCellRow];
		var column = _lastCellColumn;
		if (column < 0 || column >= line.Length)
		{
			return false;
		}

		// 光标要正好停在这一格右边；宽字符是两格。
		var cellWidth = column + 1 < line.Length && line[column + 1].Text == '\0' ? 2 : 1;
		if (_wrapPending || _cursorColumn != column + cellWidth)
		{
			return false;
		}

		// 区域指示符只在「上一格是一个孤立指示符」时配对，否则 🇨🇳🇯🇵 会挤成一格。
		if (TerminalText.IsRegionalIndicator(codepoint) && !IsLoneRegionalIndicator(line, column))
		{
			return false;
		}

		Span<char> units = stackalloc char[2];
		units[0] = first;
		var length = 1;
		if (second != '\0')
		{
			units[1] = second;
			length = 2;
		}

		var trailers = line[column].Trailers;
		line[column].Trailers = trailers is null
			? new string(units[..length])
			: string.Concat(trailers, units[..length]);
		MarkLine(_lastCellRow);
		return true;
	}

	/// <summary>这一格是不是一个还没配对的区域指示符（国旗的左半）。</summary>
	private static bool IsLoneRegionalIndicator(TerminalCell[] line, int column)
	{
		var cell = line[column];
		if (cell.Trailers is { Length: > 1 })
		{
			return false;
		}

		if (!char.IsHighSurrogate(cell.Text))
		{
			return false;
		}

		if (cell.Trailers is not { Length: 1 } trailer || !char.IsLowSurrogate(trailer[0]))
		{
			return false;
		}

		return TerminalText.IsRegionalIndicator(char.ConvertToUtf32(cell.Text, trailer[0]));
	}

	/// <summary>DEC Special Graphics：只有 G0/G1 被 <c>ESC ( 0</c> 切换时生效。</summary>
	private char MapCharset(char c)
	{
		var index = _singleShift ?? _activeCharset;
		_singleShift = null;
		if (!_graphicsCharset[index])
		{
			return c;
		}

		return c switch
		{
			'`' => '◆', 'a' => '▒', 'b' => '␉', 'c' => '␌', 'd' => '␍', 'e' => '␊',
			'f' => '°', 'g' => '±', 'h' => '␤', 'i' => '␋', 'j' => '┘', 'k' => '┐',
			'l' => '┌', 'm' => '└', 'n' => '┼', 'o' => '⎺', 'p' => '⎻', 'q' => '─',
			'r' => '⎼', 's' => '⎽', 't' => '├', 'u' => '┤', 'v' => '┴', 'w' => '┬',
			'x' => '│', 'y' => '≤', 'z' => '≥', '{' => 'π', '|' => '≠', '}' => '£',
			'~' => '·',
			_ => c,
		};
	}

	private void LineFeed()
	{
		_wrapPending = false;
		if (_cursorRow == _scrollBottom)
		{
			ScrollUpRegion(_scrollTop, _scrollBottom, 1);
		}
		else if (_cursorRow < _rows - 1)
		{
			_cursorRow++;
		}

		Touch();
	}

	private void ReverseIndex()
	{
		_wrapPending = false;
		if (_cursorRow == _scrollTop)
		{
			ScrollDownRegion(_scrollTop, _scrollBottom, 1);
		}
		else if (_cursorRow > 0)
		{
			_cursorRow--;
		}

		Touch();
	}

	private void ScrollUpRegion(int top, int bottom, int count)
	{
		var lines = ActiveLines;
		var baseIndex = BufferBase;
		var fullScreen = !_alternateActive && top == 0 && bottom == _rows - 1;
		for (var index = 0; index < count; index++)
		{
			if (fullScreen)
			{
				// 主屏全屏滚动：顶部行进入历史，追加新行；视口跟随内容。
				_primary.Add(NewLine(_columns));
				_primaryVersions.Add(0);
				_primaryWrapped.Add(false);
				if (_viewOffset > 0)
				{
					_viewOffset++;
				}
			}
			else
			{
				RemoveBufferLine(baseIndex + Math.Clamp(top, 0, lines.Count - 1));
				InsertBufferLine(
					Math.Clamp(baseIndex + bottom, 0, lines.Count),
					NewLine(_columns));
			}
		}

		TrimScrollback();
		MarkAllLines();
		_viewOffset = Math.Clamp(_viewOffset, 0, MaxViewOffset);
		Touch();
	}

	private void ScrollDownRegion(int top, int bottom, int count)
	{
		var lines = ActiveLines;
		var baseIndex = BufferBase;
		for (var index = 0; index < count; index++)
		{
			var bottomIndex = Math.Clamp(baseIndex + bottom, 0, lines.Count - 1);
			RemoveBufferLine(bottomIndex);
			InsertBufferLine(Math.Clamp(baseIndex + top, 0, lines.Count), NewLine(_columns));
		}

		MarkAllLines();
		_viewOffset = Math.Clamp(_viewOffset, 0, MaxViewOffset);
		Touch();
	}

	private void SetScrollRegion(int top, int bottom)
	{
		if (top < 0)
		{
			top = 0;
		}

		if (bottom >= _rows)
		{
			bottom = _rows - 1;
		}

		if (bottom <= top)
		{
			return;
		}

		_scrollTop = top;
		_scrollBottom = bottom;
		(_cursorRow, _cursorColumn) = (_originMode ? top : 0, 0);
		_wrapPending = false;
		Touch();
	}

	private void MoveTo(int row, int column)
	{
		if (_originMode)
		{
			row += _scrollTop;
			_cursorRow = Math.Clamp(row, _scrollTop, _scrollBottom);
		}
		else
		{
			_cursorRow = Math.Clamp(row, 0, _rows - 1);
		}

		_cursorColumn = Math.Clamp(column, 0, _columns - 1);
		_wrapPending = false;
		Touch();
	}

	private void ClampCursor()
	{
		_cursorRow = Math.Clamp(_cursorRow, 0, _rows - 1);
		_cursorColumn = Math.Clamp(_cursorColumn, 0, _columns - 1);
		_wrapPending = false;
	}

	private void EraseLine(int mode)
	{
		var line = ActiveLines[AbsoluteRow];
		var (start, end) = mode switch
		{
			1 => (0, _cursorColumn + 1),
			2 => (0, _columns),
			_ => (_cursorColumn, _columns),
		};

		Array.Fill(line, TerminalCell.Blank, start, Math.Max(0, end - start));
		_wrapPending = false;
		MarkLine(AbsoluteRow);
		Touch();
	}

	private void EraseDisplay(int mode)
	{
		switch (mode)
		{
			case 1:
				EraseLine(1);
				for (var row = 0; row < _cursorRow; row++)
				{
					Array.Fill(ActiveLines[BufferBase + row], TerminalCell.Blank);
				}

				break;

			case 2:
				for (var row = 0; row < _rows; row++)
				{
					Array.Fill(ActiveLines[BufferBase + row], TerminalCell.Blank);
					SetLineWrapped(BufferBase + row, false);
				}

				break;

			case 3:
				// 3J：清 scrollback（xterm 语义），主屏历史一并丢弃。
				EraseDisplay(2);
				if (!_alternateActive)
				{
					var history = Math.Max(0, _primary.Count - _rows);
					_primary.RemoveRange(0, history);
					_primaryVersions.RemoveRange(0, history);
					_primaryWrapped.RemoveRange(0, history);
					_viewOffset = 0;
				}

				break;

			default:
				EraseLine(0);
				for (var row = _cursorRow + 1; row < _rows; row++)
				{
					Array.Fill(ActiveLines[BufferBase + row], TerminalCell.Blank);
				}

				break;
		}

		MarkAllLines();
		Touch();
	}

	private void EraseCharacters(int count)
	{
		var line = ActiveLines[AbsoluteRow];
		Array.Fill(line, TerminalCell.Blank, _cursorColumn, Math.Min(count, _columns - _cursorColumn));
		MarkLine(AbsoluteRow);
		Touch();
	}

	private void InsertCharacters(int count)
	{
		var line = ActiveLines[AbsoluteRow];
		count = Math.Min(count, _columns - _cursorColumn);
		Array.Copy(line, _cursorColumn, line, _cursorColumn + count, _columns - _cursorColumn - count);
		Array.Fill(line, TerminalCell.Blank, _cursorColumn, count);
		MarkLine(AbsoluteRow);
		Touch();
	}

	private void DeleteCharacters(int count)
	{
		var line = ActiveLines[AbsoluteRow];
		count = Math.Min(count, _columns - _cursorColumn);
		Array.Copy(line, _cursorColumn + count, line, _cursorColumn, _columns - _cursorColumn - count);
		Array.Fill(line, TerminalCell.Blank, _columns - count, count);
		MarkLine(AbsoluteRow);
		Touch();
	}

	private void InsertLines(int count)
	{
		if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
		{
			return;
		}

		var lines = ActiveLines;
		var baseIndex = BufferBase;
		count = Math.Min(count, _scrollBottom - _cursorRow + 1);
		for (var index = 0; index < count; index++)
		{
			RemoveBufferLine(baseIndex + _scrollBottom);
			InsertBufferLine(baseIndex + _cursorRow, NewLine(_columns));
		}

		MarkAllLines();
		Touch();
	}

	private void DeleteLines(int count)
	{
		if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
		{
			return;
		}

		var lines = ActiveLines;
		var baseIndex = BufferBase;
		count = Math.Min(count, _scrollBottom - _cursorRow + 1);
		for (var index = 0; index < count; index++)
		{
			RemoveBufferLine(baseIndex + _cursorRow);
			InsertBufferLine(baseIndex + _scrollBottom, NewLine(_columns));
		}

		MarkAllLines();
		Touch();
	}

	private void ResetAll()
	{
		InvalidateLastCell();
		foreach (var line in _primary)
		{
			Array.Fill(line, TerminalCell.Blank);
		}

		_primary.Clear();
		_primaryVersions.Clear();
		_primaryWrapped.Clear();
		_alternate.Clear();
		_alternateVersions.Clear();
		_alternateWrapped.Clear();
		_alternateActive = false;
		_cursorRow = 0;
		_cursorColumn = 0;
		_wrapPending = false;
		_cursorVisible = true;
		_autoWrap = true;
		_originMode = false;
		_scrollTop = 0;
		_scrollBottom = _rows - 1;
		_viewOffset = 0;
		_activeCharset = 0;
		_singleShift = null;
		_graphicsCharset[0] = false;
		_graphicsCharset[1] = false;
		_mouseNormal = false;
		_mouseButton = false;
		_mouseAny = false;
		_insertMode = false;
		_reverseVideo = false;
		_cursorStyle = TerminalCursorStyle.Block;
		CursorBlink = false;
		_currentLink = 0;
		_links.Clear();
		_linkIds.Clear();
		Volatile.Write(ref _palette, new uint?[256]);
		_defaultForeground = null;
		_defaultBackground = null;
		_cursorColor = null;
		ResizeTabStops();
		FocusReporting = false;
		UrxvtMouse = false;
		PixelMouse = false;
		MouseMode = TerminalMouseMode.None;
		SgrMouse = false;
		BracketedPaste = false;
		ApplicationCursorKeys = false;
		_pen = TerminalCell.Blank;
		_selectionAnchor = null;
		_selectionFocus = null;
		EnsurePrimaryHeight(_rows);
		MarkAllLines();
		Touch();
	}

	private void HandleOsc()
	{
		var osc = _osc;
		_state = ParseState.Ground;
		_osc = string.Empty;

		// OSC 4;index;spec[;index;spec...]：改写调色板；spec 为 ? 时回报当前值。
		if (osc.StartsWith("4;", StringComparison.Ordinal))
		{
			ApplyPaletteOsc(osc[2..]);
			return;
		}

		// OSC 10/11/12;spec：默认前景/背景/光标色。
		if (osc.Length > 2 && osc[2] == ';' && osc[0] == '1' && osc[1] is '0' or '1' or '2')
		{
			ApplyDefaultColorOsc(osc);
			return;
		}

		// OSC 104/110/111/112：颜色复位。
		if (osc == "104" || osc.StartsWith("104;", StringComparison.Ordinal) || osc is "110" or "111" or "112")
		{
			ResetColorOsc(osc);
			return;
		}

		// OSC 8;params;URI：给后续字符打超链接标记；空 URI 表示结束。
		if (osc.StartsWith("8;", StringComparison.Ordinal))
		{
			var parts = osc.Split(';', 3);
			if (parts.Length == 3)
			{
				_currentLink = parts[2].Length == 0 ? 0 : RegisterLink(parts[2]);
				_pen.Link = _currentLink;
			}

			return;
		}

		// OSC 52;c;base64：把文本写进系统剪贴板（tmux/ssh 复制依赖它）。
		if (osc.StartsWith("52;", StringComparison.Ordinal))
		{
			var parts = osc.Split(';', 3);
			if (parts.Length == 3 && parts[2] is { Length: > 0 } payload && payload != "?" &&
				ClipboardWrite is { } write)
			{
				try
				{
					var text = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
					if (text.Length > 0)
					{
						write(text);
					}
				}
				catch (FormatException)
				{
					// 非法 base64：忽略，不影响后续输出。
				}
			}
		}
	}

	/// <summary>OSC 4：按 index/spec 成对处理，spec 为 <c>?</c> 时按 xterm 格式回报。</summary>
	private void ApplyPaletteOsc(string specs)
	{
		var parts = specs.Split(';');
		for (var index = 0; index + 1 < parts.Length; index += 2)
		{
			if (!byte.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
			{
				continue;
			}

			var spec = parts[index + 1];
			if (spec == "?")
			{
				Respond?.Invoke($"\u001b]4;{slot};{FormatColor(Palette[slot] ?? TerminalPalette.Rgb(slot))}\u001b\\");
				continue;
			}

			if (TryParseColor(spec, out var rgb))
			{
				SetPaletteColor(slot, rgb);
			}
		}
	}

	/// <summary>OSC 10/11/12：设置（或查询）默认前景、背景、光标颜色。</summary>
	private void ApplyDefaultColorOsc(string osc)
	{
		var separator = osc.IndexOf(';');
		var target = osc[..separator];
		var spec = osc[(separator + 1)..];
		if (spec == "?")
		{
			var current = target switch
			{
				"10" => _defaultForeground ?? HostForeground,
				"11" => _defaultBackground ?? HostBackground,
				_ => _cursorColor ?? HostForeground,
			};
			Respond?.Invoke($"\u001b]{target};{FormatColor(current)}\u001b\\");
			return;
		}

		if (!TryParseColor(spec, out var rgb))
		{
			return;
		}

		switch (target)
		{
			case "10":
				_defaultForeground = rgb;
				break;
			case "11":
				_defaultBackground = rgb;
				break;
			default:
				_cursorColor = rgb;
				break;
		}

		BumpColors();
	}

	/// <summary>OSC 104 复位单个/全部调色板项，OSC 110/111/112 复位默认色。</summary>
	private void ResetColorOsc(string osc)
	{
		if (osc == "104")
		{
			Volatile.Write(ref _palette, new uint?[256]);
		}
		else if (osc.StartsWith("104;", StringComparison.Ordinal))
		{
			var next = (uint?[])_palette.Clone();
			foreach (var part in osc[4..].Split(';'))
			{
				if (byte.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
				{
					next[slot] = null;
				}
			}

			Volatile.Write(ref _palette, next);
		}
		else
		{
			switch (osc)
			{
				case "110":
					_defaultForeground = null;
					break;
				case "111":
					_defaultBackground = null;
					break;
				case "112":
					_cursorColor = null;
					break;
				default:
					return;
			}
		}

		BumpColors();
	}

	private void SetPaletteColor(byte slot, uint rgb)
	{
		var next = (uint?[])_palette.Clone();
		next[slot] = rgb;
		Volatile.Write(ref _palette, next);
		BumpColors();
	}

	private void BumpColors()
	{
		Interlocked.Increment(ref _colorVersion);
		MarkAllLines();
		Touch();
	}

	/// <summary>解析 <c>rgb:r/g/b</c>、<c>#rgb</c>、<c>#rrggbb</c>、<c>#rrrrggggbbbb</c>。</summary>
	private static bool TryParseColor(string spec, out uint rgb)
	{
		rgb = 0;
		byte r;
		byte g;
		byte b;
		if (spec.StartsWith("rgb:", StringComparison.Ordinal))
		{
			var channels = spec[4..].Split('/');
			if (channels.Length != 3 ||
				!TryParseChannel(channels[0], out r) ||
				!TryParseChannel(channels[1], out g) ||
				!TryParseChannel(channels[2], out b))
			{
				return false;
			}

			rgb = Pack(r, g, b);
			return true;
		}

		if (!spec.StartsWith('#'))
		{
			return false;
		}

		var hex = spec[1..];
		if (hex.Length == 3)
		{
			if (!TryParseNibble(hex[0], out var rn) ||
				!TryParseNibble(hex[1], out var gn) ||
				!TryParseNibble(hex[2], out var bn))
			{
				return false;
			}

			rgb = Pack((byte)(rn * 17), (byte)(gn * 17), (byte)(bn * 17));
			return true;
		}

		if (hex.Length == 6)
		{
			return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
		}

		if (hex.Length == 12 &&
			TryParseChannel(hex[..4], out r) &&
			TryParseChannel(hex[4..8], out g) &&
			TryParseChannel(hex[8..], out b))
		{
			rgb = Pack(r, g, b);
			return true;
		}

		return false;
	}

	private static uint Pack(byte r, byte g, byte b) => ((uint)r << 16) | ((uint)g << 8) | b;

	private static bool TryParseNibble(char c, out int value)
	{
		value = c switch
		{
			>= '0' and <= '9' => c - '0',
			>= 'a' and <= 'f' => c - 'a' + 10,
			>= 'A' and <= 'F' => c - 'A' + 10,
			_ => -1,
		};
		return value >= 0;
	}

	/// <summary>颜色分量允许 1/2/3/4 位十六进制，统一缩放到 8 位。</summary>
	private static bool TryParseChannel(string text, out byte value)
	{
		value = 0;
		if (text.Length is < 1 or > 4 ||
			!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
		{
			return false;
		}

		value = text.Length switch
		{
			1 => (byte)(hex * 17),
			2 => (byte)hex,
			3 => (byte)(hex >> 4),
			_ => (byte)(hex >> 8),
		};
		return true;
	}

	private static string FormatColor(uint rgb)
	{
		var r = (rgb >> 16) & 0xFF;
		var g = (rgb >> 8) & 0xFF;
		var b = rgb & 0xFF;
		return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
	}

	private int RegisterLink(string uri)
	{
		if (_linkIds.TryGetValue(uri, out var id))
		{
			return id;
		}

		// 链接表只增不减会随 ls --hyperlink 无限长；上限到了就整体换代，旧单元格退化为无链接。
		if (_links.Count >= 4096)
		{
			_links.Clear();
			_linkIds.Clear();
		}

		id = _links.Count + 1;
		_links.Add(uri);
		_linkIds[uri] = id;
		return id;
	}

	/// <summary>视口某格上的超链接地址；没有则返回 <c>null</c>。</summary>
	public string? LinkAt(int viewportRow, int column)
	{
		var bufferRow = ViewTop + viewportRow;
		if (column < 0 || BufferLine(bufferRow) is not { } line || column >= line.Length)
		{
			return null;
		}

		var id = line[column].Link;
		return id >= 1 && id <= _links.Count ? _links[id - 1] : null;
	}

	/// <summary>新行：全部填成空格。<c>\0</c> 只用来标记宽字符右半格，不能当空单元格。</summary>
	private static TerminalCell[] NewLine(int columns)
	{
		var line = new TerminalCell[Math.Max(MinColumns, columns)];
		Array.Fill(line, TerminalCell.Blank);
		return line;
	}

	/// <summary>某一行按颜色与选区合并后的 runs；宽字符右半（<c>\0</c>）不单独输出，但计入格数。</summary>
	public List<TerminalRun> GetRuns(int viewportRow, List<TerminalRun> runs) =>
		GetRuns(viewportRow, runs, new StringBuilder(), -1);

	/// <summary>同上，并把 <paramref name="cursorColumn"/> 那一格单独切出来（<see cref="TerminalRun.Cursor"/>）。</summary>
	public List<TerminalRun> GetRuns(int viewportRow, List<TerminalRun> runs, int cursorColumn) =>
		GetRuns(viewportRow, runs, new StringBuilder(), cursorColumn);

	/// <summary>
	/// 同上，但复用调用方的 <see cref="StringBuilder"/>，渲染循环里每行零额外分配。
	/// <paramref name="cursorColumn"/> 是要单独切出来的光标格（-1 表示不切）：渲染端不必再按文本元素
	/// 自己算簇边界，光标格永远落在一个完整簇上。
	/// </summary>
	public List<TerminalRun> GetRuns(int viewportRow, List<TerminalRun> runs, StringBuilder builder, int cursorColumn = -1)
	{
		runs.Clear();
		builder.Clear();
		if (viewportRow < 0 || viewportRow >= _rows)
		{
			return runs;
		}

		var bufferRow = ViewTop + viewportRow;
		if (BufferLine(bufferRow) is not { } line)
		{
			return runs;
		}

		var selection = GetSelectionRange(viewportRow);
		// 光标落在宽字符右半格时归到左半：整个字符一起高亮，不把一个簇切开。
		if (cursorColumn > 0 && cursorColumn < line.Length && line[cursorColumn].Text == '\0')
		{
			cursorColumn--;
		}

		var start = 0;
		var cells = 0;

		// 行尾的空白是填充，不参与渲染：否则每行都会多出一段无意义的空格 run。
		var end = LineContentLength(line);
		if (end < line.Length && line[end].Text == '\0')
		{
			// 末格是宽字符时，右半占位格也属于这一行。
			end++;
		}

		void Flush()
		{
			if (cells == 0)
			{
				return;
			}

			var first = line[start];
			runs.Add(new TerminalRun(
				builder.ToString(),
				start,
				cells,
				first.Foreground,
				first.Background,
				first.Bold,
				first.Italic,
				first.Underline,
				first.Strikethrough,
				first.Dim,
				first.Hidden,
				first.Reverse,
				first.Link != 0,
				selection is { } range && start >= range.From && start <= range.To,
				start == cursorColumn));
			builder.Clear();
			cells = 0;
		}

		for (var column = 0; column < end; column++)
		{
			var cell = line[column];
			if (cell.Text == '\0')
			{
				// 宽字符右半格：并进当前 run 的格数，但不重复输出文本。
				cells++;
				continue;
			}

			// 每个格子正好是一个 Unicode 文本元素（簇），所以格边界就是合法的 run 边界。
			if (cells > 0)
			{
				var previous = line[start];
				var sameSelection = selection is not { } r || (start >= r.From && start <= r.To) == (column >= r.From && column <= r.To);
				var cursorBoundary = column == cursorColumn || (start == cursorColumn && column > cursorColumn);
				if (!SameStyle(previous, cell) || !sameSelection || cursorBoundary)
				{
					Flush();
				}
			}

			if (cells == 0)
			{
				start = column;
			}

			builder.Append(cell.Text);
			if (cell.Trailers is { } trailers)
			{
				builder.Append(trailers);
			}

			cells++;
		}

		Flush();
		return runs;
	}

	private static bool SameStyle(in TerminalCell left, in TerminalCell right) =>
		left.Bold == right.Bold &&
		left.Italic == right.Italic &&
		left.Underline == right.Underline &&
		left.Strikethrough == right.Strikethrough &&
		left.Dim == right.Dim &&
		left.Hidden == right.Hidden &&
		left.Reverse == right.Reverse &&
		left.Link == right.Link &&
		left.Foreground.Equals(right.Foreground) &&
		left.Background.Equals(right.Background);

	private void AppendLine(StringBuilder builder, int bufferRow, int from, int to)
	{
		if (BufferLine(bufferRow) is not { } line)
		{
			return;
		}

		from = Math.Clamp(from, 0, _columns - 1);
		to = Math.Clamp(to, 0, _columns - 1);
		var end = to + 1;
		while (end > from && (line[end - 1].Text is '\0' or ' ') && line[end - 1].Trailers is null)
		{
			end--;
		}

		for (var column = from; column < end; column++)
		{
			if (line[column].Text != '\0')
			{
				builder.Append(line[column].Text);
				if (line[column].Trailers is { } trailers)
				{
					builder.Append(trailers);
				}
			}
		}
	}

	private enum ParseState
	{
		Ground,
		Escape,
		Csi,
		Osc,
		OscEscape,
		Charset,
		Hash,
	}

	private struct TerminalCell
	{
		public static readonly TerminalCell Blank = new()
		{
			Text = ' ',
			Foreground = TerminalColor.Default,
			Background = TerminalColor.Default,
		};

		/// <summary>簇的首个 UTF-16 码元；<c>\0</c> 表示宽字符的右半占位。</summary>
		public char Text;

		/// <summary>
		/// 同一簇里跟在首码元后面的码元：低代理、组合符号、变体选择符、ZWJ 链、肤色修饰……
		/// 整簇只占一格（宽字符两格），渲染时拼回去。null 表示就是一个字符，这是绝大多数格子的情形。
		/// </summary>
		public string? Trailers;

		public TerminalColor Foreground;
		public TerminalColor Background;
		public bool Bold;
		public bool Italic;
		public bool Underline;
		public bool Strikethrough;
		public bool Dim;
		public bool Hidden;
		public bool Reverse;

		/// <summary>OSC 8 超链接 id（0 表示没有链接），与 <c>_links</c> 一起解析成 URI。</summary>
		public int Link;
	}
}

/// <summary>终端 256 色调色板（16 个系统色 + 6×6×6 立方 + 灰阶）。</summary>
internal static class TerminalPalette
{
	private static readonly uint[] Colors = Create();

	// 浅色终端下 16 个系统色换成深色版本：白/亮白不能再是近白色，否则在白底上看不见。
	private static readonly uint[] LightColors =
	[
		0x24292f, 0xcf222e, 0x116329, 0x9a6700, 0x0969da, 0x8250df, 0x1b7c83, 0x6e7781,
		0x57606a, 0xa40e26, 0x1a7f37, 0x7d4e00, 0x0550ae, 0x6639ba, 0x0f6e75, 0x24292f,
	];

	public static uint Rgb(byte index) => Colors[index];

	/// <summary>按终端表面明暗取系统色；16 色以后的立方体/灰阶保持 xterm 标准。</summary>
	public static uint Rgb(byte index, bool light) =>
		light && index < LightColors.Length ? LightColors[index] : Colors[index];

	private static uint[] Create()
	{
		var colors = new uint[256];
		uint[] system =
		[
			0x000000, 0xcd3131, 0x0dbc79, 0xe5e510, 0x2472c8, 0xbc3fbc, 0x11a8cd, 0xe5e5e5,
			0x666666, 0xf14c4c, 0x23d18b, 0xf5f543, 0x3b8eea, 0xd670d6, 0x29b8db, 0xffffff,
		];
		Array.Copy(system, colors, system.Length);

		for (var index = 0; index < 216; index++)
		{
			var r = index / 36;
			var g = (index / 6) % 6;
			var b = index % 6;
			colors[16 + index] = (uint)((Channel(r) << 16) | (Channel(g) << 8) | Channel(b));
		}

		for (var index = 0; index < 24; index++)
		{
			var level = (uint)(8 + (index * 10));
			colors[232 + index] = (level << 16) | (level << 8) | level;
		}

		return colors;
	}

	private static uint Channel(int value) => (uint)(value == 0 ? 0 : 55 + (value * 40));
}
