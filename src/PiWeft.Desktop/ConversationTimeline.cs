using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed class ConversationTimeline : IDisposable
{
	private readonly MessageRows _rows = new();
	private readonly ConversationList _list;
	private readonly Func<ChatEntry, UIElement> _create;
	private readonly Dictionary<object, bool[]> _expanded = [];
	private readonly Dictionary<object, IReadOnlyDictionary<string, bool>> _processStates = [];
	private readonly HashSet<ReadingRow> _visible = [];
	private readonly Dictionary<ReadingRow, int> _indexByHost = [];
	private readonly Dictionary<int, ReadingRow> _hostByIndex = [];
	private readonly List<int> _inPlaceUpdates = [];
	private bool _refreshQueued;
	private bool _disposed;

	public ConversationTimeline(Func<ChatEntry, UIElement> create)
	{
		_create = create;
		_list = new ConversationList(this);
		Scroll = new ScrollViewer().Content(_list)
			.AutoVerticalScroll().NoHorizontalScroll()
			.BorderThickness(0).Background(AppTheme.Transparent)
			// 右缘留出定位刻度的命中区，窄阅读列也不会被刻度盖住。
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space3, 44, AppTheme.Space3));
		View = Scroll;
		_list.Scroll = Scroll;
		Scroll.SmoothScrolling();
		// 首次打开的历史行只有估算高度；上滚时实测修正会重写偏移。滑行要跟着内容坐标系一起走。
		SmoothScroll.For(Scroll).CompensateLayoutCorrections = true;
		Scroll.MouseDown += CancelJump;
		Scroll.MouseWheel += CancelJump;
		// 输入→绘制延迟的取样点：指针与滚轮在会话这一层最先被看到（下游控件常把事件处理掉，
		// 冒泡不到窗口根），这里记一次时间戳，下一帧由诊断的帧档算出延迟。
		Scroll.MouseDown += _ => Diagnostics.NoteInput();
		Scroll.MouseWheel += _ => Diagnostics.NoteInput();
	}

	public ScrollViewer View { get; }
	public ScrollViewer Scroll { get; }
	public IItemsView ItemsSource => _rows;
	internal int RowCount => _rows.Count;
	internal Func<bool>? FollowingProvider { get; set; }

	private ReadingRow CreateRow() => new ReadingRow()
		.MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
		.HorizontalAlignment(HorizontalAlignment.Center)
		.Margin(new Thickness(0, 0, 0, AppTheme.Space3));

	public IEnumerable<UIElement> VisibleRows
	{
		get
		{
			foreach (var host in _visible)
			{
				if (host.Content is UIElement content) yield return content;
			}
		}
	}

	internal int VisibleRowCount => _visible.Count;

	internal int RowAtViewportAnchor() => _list.IndexAtViewportAnchor(28);

	public void SetEntries(
		IReadOnlyList<ChatEntry> entries,
		bool rebuild = false,
		int stablePrefix = 0,
		bool prefixReused = false)
	{
		PruneRecycled();
		_inPlaceUpdates.Clear();
		// 行数变化/重建才需要按 key 迁移高度；流式原位更新走原下标，避免每帧复制整张表。
		var oldEntries = rebuild || entries.Count != _rows.Count || (!prefixReused && stablePrefix == 0)
			? _rows.Snapshot()
			: null;
		_rows.Update(entries, stablePrefix, prefixReused, _inPlaceUpdates);
		// churn 计数（零分配）：流式期原位更新的行数，是「重建 vs 复用」最直接的对照量。
		if (_inPlaceUpdates.Count > 0) Diagnostics.Count("row.inplace", _inPlaceUpdates.Count);
		_list.OnEntriesChanged(oldEntries, entries, _inPlaceUpdates);
		// A streaming tail changes on every frame. Rebinding just its visible host keeps the
		// panel's measured height (and therefore the published extent) instead of resetting the
		// row to the coarse estimate, which made the viewport clamp short of the real bottom.
		for (var i = 0; i < _inPlaceUpdates.Count; i++)
		{
			var index = _inPlaceUpdates[i];
			if (index < entries.Count && _hostByIndex.TryGetValue(index, out var host)) Bind(host, entries[index], index);
		}
		// The streaming row invalidates its own measure; the virtualizer only re-measures rows while
		// arranging, and an ancestor that was already measure-dirty skips that arrange. Invalidate the
		// arrange as well so the grown row (expanded thinking, streaming body) is actually re-measured.
		if (_inPlaceUpdates.Count > 0) View.InvalidateArrange();
		if (!rebuild) return;
		foreach (var host in _visible.ToArray())
		{
			if (_indexByHost.TryGetValue(host, out var index) && index < entries.Count) Bind(host, entries[index], index);
			// 索引失效时直接释放；ConversationList 下一轮会把宿主从可视集合里摘掉。
			else Release(host);
		}
		View.InvalidateMeasure();
	}

	internal static double EstimateRowHeight(ChatEntry entry, double width)
	{
		if (entry.Compact) return 28 + AppTheme.Space3;
		// 过程组/工具行默认收起：高度基本是表头，和正文长度无关。
		if (entry.Role == ChatRole.Process) return 72 + AppTheme.Space3;
		if (entry.Role == ChatRole.Tool) return 48 + AppTheme.Space3;

		// 中文和代码混合时一行大约 14 DIP/字符；宁可略估小（实测变大只会扩展 extent）
		// 也不要把长行估得过大，因为收缩才容易触发夹取。
		var charsPerLine = Math.Max(24, width / 14.0);
		var lines = Math.Max(1, entry.TextLength / charsPerLine);
		if (entry.TextLength > 0 && !entry.IsStreaming)
		{
			var text = entry.Text.AsSpan();
			var newlines = 1;
			for (var index = 0; index < text.Length; index++)
			{
				if (text[index] == '\n') newlines++;
			}
			lines = Math.Max(lines, newlines);
		}
		var height = 48 + Math.Ceiling(lines) * 22 + AppTheme.Space3;
		// 思考默认收起成一行表头；展开后的行会被实测替换。
		if (entry.ThinkingLength > 0) height += 44;
		if (entry.Images.Count > 0) height += entry.Images.Count * 240;
		return Math.Clamp(height, 36 + AppTheme.Space3, 12_000);
	}

	/// 折叠开关改变行高后立刻重排一次，并按新内容重算滚动范围、把偏移收回到末尾之内。
	/// 手动滚动或改窗口大小本来会顺手做这件事；不主动做的话，面板会继续用旧的行高，
	/// 表现为折叠内容下方空出一块、直到重排才恢复。
	/// <param name="source">触发重排的元素（折叠开关）；给出时只刷新它所在的那一行。</param>
	public void RefreshLayout(Element? source = null)
	{
		// 只让 ScrollViewer 失效还不够：已实现行的测量缓存仍然有效，element.Measure
		// 会直接复用旧 DesiredSize（旧行高）。窗口大小变化能修好，正是因为它改了测量约束。
		if (HostOf(source) is { } host) InvalidateMeasureTree(host);
		else foreach (var visible in _visible) InvalidateMeasureTree(visible);
		View.InvalidateMeasure();
		_list.InvalidateMeasure();
		// 先按现有指标夹一次，等这一帧布局落定后再夹一次：内容变短只有在重排后才算得准。
		Reclamp();
		View.InvalidateVisual();
		if (_refreshQueued || !Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
		// 一次布局里可能连续恢复多个折叠状态，追帧修正只排一次。
		_refreshQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_refreshQueued = false;
			if (!_disposed) Reclamp();
		});
	}

	/// 把偏移收回当前内容末尾之内。先按上一次布局发布的范围夹一次；
	/// 面板重排后的 Render 回调会再夹一次，避免内容变短后停在旧末尾之外。
	private void Reclamp() => Scroll.SetScrollOffsets(Scroll.HorizontalOffset, Scroll.VerticalOffset);

	private ReadingRow? HostOf(Element? element)
	{
		for (var current = element; current is not null; current = current.Parent)
			if (current is ReadingRow host && _visible.Contains(host)) return host;
		return null;
	}

	internal static void InvalidateMeasureTree(Element element)
	{
		if (element is UIElement ui) ui.InvalidateMeasure();
		if (element is not IVisualTreeHost tree) return;
		tree.VisitChildren(child =>
		{
			InvalidateMeasureTree(child);
			return true;
		});
	}

	public void JumpToRow(int index, double withinRow = 0) => _list.ScrollToIndex(index, withinRow);

	public void ScrollToIndex(int index, double withinRow = 0) => _list.ScrollToIndex(index, withinRow);

	internal void CancelScrollTarget() => _list.CancelScrollTarget();

	private void CancelJump(MouseEventArgs _) => _list.CancelScrollTarget();

	internal readonly record struct ReadingPoint(string? EntryId, double WithinRow);

	internal ReadingPoint CurrentReadingAnchor()
	{
		var point = CaptureReadingPoint();
		return point with { WithinRow = Math.Max(0, point.WithinRow) };
	}

	/// 顶部行的原始阅读位置；WithinRow 是行顶超出视口上沿的距离。
	internal ReadingPoint CaptureReadingPoint() => _list.CaptureReadingPoint();

	/// 把阅读位置映射回当前内容坐标系；面板用 prefix + within 直接落位。
	internal bool RestoreReadingPoint(ReadingPoint point) => _list.RestoreReadingPoint(point);

	internal int IndexOfEntryId(string? entryId)
	{
		if (string.IsNullOrEmpty(entryId)) return -1;
		for (var index = 0; index < _rows.Count; index++)
		{
			if (_rows.GetItem(index) is ChatEntry entry && string.Equals(entry.EntryId, entryId, StringComparison.Ordinal))
				return index;
		}
		return -1;
	}

	public void ApplyReadingWidth()
	{
		foreach (var host in _visible) host.MaxWidth = AppTheme.MessageMaxWidth + AppTheme.Space6;
		_list.InvalidateHeightsForWidth();
		View.InvalidateMeasure();
	}

	private static object Key(ChatEntry entry) => (object?)entry.EntryId ?? entry;

	private void Bind(ReadingRow host, ChatEntry entry, int index)
	{
		TrackIndex(host, index);
		if (host.Tag is ChatEntry previous && SameEntry(previous, entry))
		{
			host.Tag = entry;
			return;
		}
		// Process groups expose their members' arguments in the header. Update the persistent view
		// before considering a top-level metadata-only short-circuit, otherwise a changed tool
		// argument would update the tag while leaving the visible group header stale.
		if (host.Tag is ChatEntry processEntry && entry.Role == ChatRole.Process &&
			processEntry.EntryId == entry.EntryId && host.Content is Border { Tag: ExecutionProcessView process })
		{
			process.Update(entry);
			host.Tag = entry;
			return;
		}
		if (host.Tag is ChatEntry toolEntry && entry.Role == ChatRole.Tool && toolEntry.EntryId == entry.EntryId &&
			host.Content is Border { Tag: ToolMessageView tool } && tool.Update(entry))
		{
			// 运行中的工具每来一段输出就换一个 ChatEntry；整行重建会白送一次 Expander/秒表构造，
			// 还把行高打回虚拟化估算值。表头只依赖参数，多数拍一个字都不用改（见 ToolMessageView.Update）。
			host.Tag = entry;
			return;
		}
		if (host.Tag is ChatEntry metadataEntry && SameMetadataOnly(metadataEntry, entry) &&
			host.Content is not Border { Tag: ToolMessageView })
		{
			// Only non-layout metadata changed: preserve the measured height and existing controls.
			// Replace would reset this row to the virtualizer's estimate.
			host.Tag = entry;
			return;
		}
		if (host.Tag is ChatEntry { IsStreaming: true } streaming && entry.Role == ChatRole.Assistant &&
			streaming.Timestamp == entry.Timestamp && streaming.Images.SequenceEqual(entry.Images) &&
			host.Content is Border { Tag: AssistantMessageView assistant })
		{
			assistant.Update(entry);
			host.Tag = entry;
			return;
		}
		Release(host);
		Diagnostics.Count("row.build");
		host.MaxWidth = AppTheme.MessageMaxWidth + AppTheme.Space6;
		host.Content = _create(entry);
		host.Tag = entry;
		_visible.Add(host);
		// Release above dropped the previous mapping; the recycled host now belongs to this index.
		TrackIndex(host, index);
		if (host.Content is Border { Tag: ExecutionProcessView execution } && _processStates.TryGetValue(Key(entry), out var memory))
		{
			execution.Restore(memory);
			return;
		}
		if (_expanded.TryGetValue(Key(entry), out var states))
		{
			var expanders = FindExpanders(host.Content).ToArray();
			for (var i = 0; i < Math.Min(states.Length, expanders.Length); i++) expanders[i].IsExpanded = states[i];
		}
	}

	private void TrackIndex(ReadingRow host, int index)
	{
		if (_indexByHost.TryGetValue(host, out var previous) && previous != index && _hostByIndex.TryGetValue(previous, out var mapped) && ReferenceEquals(mapped, host))
		{
			_hostByIndex.Remove(previous);
		}
		_indexByHost[host] = index;
		_hostByIndex[index] = host;
	}

	internal void PruneRecycled()
	{
		// ConversationList 在 Arrange 里同步回收离屏宿主，这里保留给测试/旧调用点。
	}

	private void Release(ReadingRow host)
	{
		if (host.Tag is ChatEntry entry && host.Content is { } child)
		{
			if (child is Border { Tag: ExecutionProcessView process }) _processStates[Key(entry)] = process.State;
			var states = FindExpanders(child).Select(static expander => expander.IsExpanded).ToArray();
			if (states.Any(static expanded => expanded)) _expanded[Key(entry)] = states;
			else _expanded.Remove(Key(entry));
			UiLifetime.Release(child);
			Diagnostics.Count("row.release");
		}
		// The framework pools the small host, never the expensive message subtree.
		host.Content = null;
		host.Tag = null;
		_visible.Remove(host);
		if (_indexByHost.Remove(host, out var index) && _hostByIndex.TryGetValue(index, out var mapped) && ReferenceEquals(mapped, host))
			_hostByIndex.Remove(index);
	}

	private static IEnumerable<Expander> FindExpanders(Element root)
	{
		if (root is Expander expander) { yield return expander; yield break; }
		if (root is not IVisualTreeHost tree) yield break;
		var children = new List<Element>();
		tree.VisitChildren(child => { children.Add(child); return true; });
		foreach (var child in children)
			foreach (var found in FindExpanders(child)) yield return found;
	}

	/// Compare fields directly instead of cloning both records with <c>with { ToolArguments = null }</c>:
	/// this runs for every changed row on every streaming frame, and the clones were pure garbage.
	/// Nested process members keep normal argument comparison; only the top-level row is metadata-only.
	internal static bool SameMetadataOnly(ChatEntry left, ChatEntry right) =>
		SameEntryCore(left, right, ignoreToolArguments: true);

	internal static bool SameEntry(ChatEntry left, ChatEntry right) =>
		SameEntryCore(left, right, ignoreToolArguments: false);

	private static bool SameEntryCore(ChatEntry left, ChatEntry right, bool ignoreToolArguments)
	{
		if (ReferenceEquals(left, right)) return true;
		// 流式行按快照比较：为了判断「要不要重绑」而物化整篇正文，等于把省下的拷贝又请回来。
		if (left.Role != right.Role || !left.SameText(right) || !left.SameThinking(right) ||
			left.IsThinking != right.IsThinking || left.IsStreaming != right.IsStreaming ||
			left.ThinkingStartedAt != right.ThinkingStartedAt || left.ThinkingEndedAt != right.ThinkingEndedAt ||
			left.Timestamp != right.Timestamp || left.EntryId != right.EntryId || left.ToolName != right.ToolName ||
			left.IsError != right.IsError || left.IsStopped != right.IsStopped ||
			left.DurationMs != right.DurationMs || left.ToolStartedAt != right.ToolStartedAt || left.Patch != right.Patch ||
			(!ignoreToolArguments && left.ToolArguments != right.ToolArguments) || left.ToolCallId != right.ToolCallId ||
			left.Compact != right.Compact ||
			!left.Images.SequenceEqual(right.Images) || !left.ChangedFiles.SequenceEqual(right.ChangedFiles) ||
			left.ProcessEntries.Count != right.ProcessEntries.Count)
		{
			return false;
		}

		// 过程组要逐成员比较。原来用 Zip(...).All(...)：每帧每行都分配一对枚举器，
		// 流式期（每 40 ms 一次）这些垃圾全进了 GC。改成按下标循环，零分配。
		var count = left.ProcessEntries.Count;
		if (count == 0) return true;
		var leftSteps = left.ProcessEntries;
		var rightSteps = right.ProcessEntries;
		for (var index = 0; index < count; index++)
		{
			if (!SameEntryCore(leftSteps[index], rightSteps[index], ignoreToolArguments: false))
			{
				return false;
			}
		}

		return true;
	}

	public void Dispose()
	{
		_disposed = true;
		Scroll.MouseDown -= CancelJump;
		Scroll.MouseWheel -= CancelJump;
		SmoothScroll.Release(Scroll);
		_list.DisposePanel();
		_expanded.Clear();
		_processStates.Clear();
		_rows.Clear();
	}

	/// <summary>
	/// 会话列表自己的虚拟化面板。替换 MewUI 的 VariableHeightItemsPresenter：
	/// 每行先有自己的估算高度，实测只改这一行，不会因为全局运行平均被一行 10k DIP 的离群行
	/// 牵动而让整段历史的 extent 一起收缩。面板只使用公开的 ScrollViewer/Control API。
	/// </summary>
	private sealed class ConversationList : Panel
	{
		private const double Overscan = 320;
		private readonly ConversationTimeline _owner;
		private readonly Dictionary<int, ReadingRow> _realized = [];
		private readonly Stack<ReadingRow> _pool = new();
		private readonly List<int> _releaseScratch = [];
		private double[] _heights = [];
		private bool[] _measured = [];
		private double[]? _prefix;
		private double _total;
		private double _width;
		private bool _disposed;
		private int _scrollTargetIndex = -1;
		private double _scrollTargetWithin;
		private double _pendingShift;

		public ConversationList(ConversationTimeline owner) => _owner = owner;

		public ScrollViewer? Scroll { get; set; }

		public double TotalHeight => _total;

		protected override Size MeasureContent(Size availableSize)
		{
			var count = _owner._rows.Count;
			EnsureHeights(count);
			if (!double.IsFinite(availableSize.Width) || availableSize.Width <= 0)
			{
				// 无约束测量（弹出层/分栏首轮）只是探自然尺寸，用上一次的视口宽度。
				var fallback = _width > 0 ? _width : Math.Max(280, Scroll?.ViewportWidth ?? 0);
				_width = fallback > 0 ? fallback : AppTheme.MessageMaxWidth;
			}
			else if (Math.Abs(availableSize.Width - _width) > 0.5)
			{
				_width = availableSize.Width;
				for (var index = 0; index < count; index++)
				{
					if (!_measured[index]) _heights[index] = 0;
				}
			}
			RecomputePrefix();
			var total = _total;
			return new Size(
				Math.Max(0, availableSize.Width > 0 && double.IsFinite(availableSize.Width) ? availableSize.Width : _width),
				Math.Max(0, total));
		}

		protected override void ArrangeContent(Rect bounds)
		{
			if (_disposed) return;
			var count = _owner._rows.Count;
			if (count == 0)
			{
				ReleaseAll();
				return;
			}
			EnsureHeights(count);
			var width = bounds.Width;
			if (!double.IsFinite(width) || width <= 0) width = _width;
			if (!double.IsFinite(width) || width <= 0) width = 700;
			if (Math.Abs(width - _width) > 0.5)
			{
				_width = width;
				for (var index = 0; index < count; index++)
				{
					if (!_measured[index]) _heights[index] = 0;
				}
			}
			var scroll = Scroll;
			var offset = scroll?.VerticalOffset ?? Math.Max(0, -bounds.Y);
			var viewport = scroll?.ViewportHeight ?? Math.Max(0, bounds.Height);
			var placementY = bounds.Y;
			var placedOffset = offset;
			if (Math.Abs(_pendingShift) > 0.5 && scroll is not null)
			{
				var shift = _pendingShift;
				_pendingShift = 0;
				scroll.SetScrollOffsets(scroll.HorizontalOffset, scroll.VerticalOffset + shift);
				offset = scroll.VerticalOffset;
				placementY -= offset - placedOffset;
				placedOffset = offset;
			}
			RecomputePrefix();
			var anchorIndex = FindIndexByY(offset);
			var anchorWithin = offset - _prefix![anchorIndex];
			var anchorTopBefore = _prefix![anchorIndex];

			// 先落位到目标行，再按新坐标做一次范围计算；高度刚变化时多走一轮。
			if (_scrollTargetIndex >= 0 && _scrollTargetIndex < count && scroll is not null)
			{
				var desired = Math.Clamp(_prefix![_scrollTargetIndex] + _scrollTargetWithin,
					0, Math.Max(0, _total - viewport));
				if (Math.Abs(desired - offset) > 0.5)
				{
					scroll.SetScrollOffsets(scroll.HorizontalOffset, desired);
					offset = scroll.VerticalOffset;
					placementY -= offset - placedOffset;
					placedOffset = offset;
					RecomputePrefix();
				}
				else
				{
					_scrollTargetIndex = -1;
				}
			}

			var first = FindIndexByY(Math.Max(0, offset - Overscan));
			var last = FindIndexByY(Math.Min(_total, offset + viewport + Overscan));
			ReleaseOutside(first, last);
			var changed = false;
			for (var round = 0; round < 2; round++)
			{
				var roundChanged = ArrangeRange(first, last, width, bounds.X, placementY);
				changed |= roundChanged;
				if (!roundChanged) break;
				RecomputePrefix();
				first = FindIndexByY(Math.Max(0, offset - Overscan));
				last = FindIndexByY(Math.Min(_total, offset + viewport + Overscan));
				ReleaseOutside(first, last);
			}
			if (changed)
			{
				// 高度实测改了内容坐标系：浏览态下把「锚点上方的高度变化」补到当前偏移上。
				// 只用相对量，不用测量时的绝对 offset——动画可能已经又走了一帧。
				if (_owner.FollowingProvider?.Invoke() != true && scroll is not null)
				{
					anchorIndex = Math.Min(anchorIndex, Math.Max(0, count - 1));
					var shift = _prefix![anchorIndex] - anchorTopBefore;
					if (Math.Abs(shift) > 0.5)
					{
						var before = scroll.VerticalOffset;
						scroll.SetScrollOffsets(scroll.HorizontalOffset, before + shift);
						var applied = scroll.VerticalOffset - before;
						_pendingShift = shift - applied;
						offset = scroll.VerticalOffset;
					}
				}
				// ScrollViewer 的 extent 来自 Measure 后的 DesiredSize；这里主动失效一次，
				// 让下一轮把新的总高度发布成滚动条范围。
				InvalidateMeasure();
				scroll?.InvalidateMeasure();
				InvalidateArrange();
			}
		}

		private bool ArrangeRange(int first, int last, double width, double placementX, double placementY)
		{
			var count = _owner._rows.Count;
			var changed = false;
			for (var index = first; index <= last && index < count; index++)
			{
				if (index < 0) continue;
				if (_owner._rows.GetItem(index) is not ChatEntry entry) continue;
				var host = GetOrCreate(index);
				_owner.Bind(host, entry, index);
				host.Measure(new Size(width, double.PositiveInfinity));
				var height = host.DesiredSize.Height;
				if (!double.IsFinite(height) || height <= 0) height = 24;
				if (Math.Abs(height - _heights[index]) > 0.5)
				{
					_heights[index] = height;
					_measured[index] = true;
					changed = true;
				}
				else if (!_measured[index])
				{
					_heights[index] = height;
					_measured[index] = true;
				}
				host.Arrange(new Rect(placementX, placementY + _prefix![index], width, height));
			}
			return changed;
		}

		private ReadingRow GetOrCreate(int index)
		{
			if (_realized.TryGetValue(index, out var host)) return host;
			host = _pool.Count > 0 ? _pool.Pop() : _owner.CreateRow();
			Add(host);
			_realized[index] = host;
			return host;
		}

		private void ReleaseOutside(int first, int last)
		{
			_releaseScratch.Clear();
			foreach (var (index, _) in _realized)
			{
				if (index < first || index > last) _releaseScratch.Add(index);
			}
			for (var i = 0; i < _releaseScratch.Count; i++)
			{
				if (!_realized.Remove(_releaseScratch[i], out var host)) continue;
				// 焦点还在这一行里时先留着：滚动离开视口不应把正在选中的文本焦点一起回收。
				if (ContainsFocus(host))
				{
					_realized[_releaseScratch[i]] = host;
					continue;
				}
				_owner.Release(host);
				Remove(host);
				_pool.Push(host);
			}
		}

		private static bool ContainsFocus(ReadingRow host)
		{
			if (host.FindVisualRoot() is not Window window) return false;
			var focused = window.FocusManager.FocusedElement;
			return focused is not null && VisualTree.IsInSubtreeOf(focused, host);
		}

		public void CancelScrollTarget() => _scrollTargetIndex = -1;

		private void ReleaseAll()
		{
			foreach (var host in _realized.Values) _owner.Release(host);
			_realized.Clear();
			Clear();
			_pool.Clear();
		}

		public void OnEntriesChanged(
			IReadOnlyList<ChatEntry>? oldEntries,
			IReadOnlyList<ChatEntry> entries,
			IReadOnlyList<int> inPlaceUpdates)
		{
			var count = entries.Count;
			if (oldEntries is null)
			{
				EnsureHeights(count);
				InvalidateMeasure();
				return;
			}

			var inPlace = new HashSet<int>(inPlaceUpdates);
			var oldByKey = new Dictionary<object, (double Height, bool Measured, ChatEntry Entry)>();
			for (var index = 0; index < oldEntries.Count && index < _heights.Length; index++)
			{
				var entry = oldEntries[index];
				oldByKey[Key(entry)] = (_heights[index], _measured[index], entry);
			}
			var heights = new double[count];
			var measured = new bool[count];
			for (var index = 0; index < count; index++)
			{
				var entry = entries[index];
				if (!oldByKey.TryGetValue(Key(entry), out var previous)) continue;
				if (inPlace.Contains(index) || SameEntry(previous.Entry, entry))
				{
					heights[index] = previous.Height;
					measured[index] = previous.Measured;
				}
			}
			_heights = heights;
			_measured = measured;
			_prefix = null;
			InvalidateMeasure();
		}

		public void InvalidateHeightsForWidth()
		{
			for (var index = 0; index < _heights.Length; index++)
			{
				if (!_measured[index]) _heights[index] = 0;
			}
			_prefix = null;
			InvalidateMeasure();
		}

		public ReadingPoint CaptureReadingPoint()
		{
			var count = _owner._rows.Count;
			if (count == 0) return new ReadingPoint(null, 0);
			EnsureHeights(count);
			RecomputePrefix();
			var offset = Scroll?.VerticalOffset ?? 0;
			var index = FindIndexByY(offset);
			if (_owner._rows.GetItem(index) is not ChatEntry entry) return new ReadingPoint(null, 0);
			return new ReadingPoint(entry.EntryId, offset - _prefix![index]);
		}

		public bool RestoreReadingPoint(ReadingPoint point)
		{
			var count = _owner._rows.Count;
			if (count == 0 || string.IsNullOrEmpty(point.EntryId)) return false;
			var index = _owner.IndexOfEntryId(point.EntryId);
			if (index < 0) return false;
			ScrollToIndex(index, point.WithinRow);
			return true;
		}

		public void ScrollToIndex(int index, double withinRow = 0)
		{
			var count = _owner._rows.Count;
			if (index < 0 || index >= count || Scroll is null) return;
			EnsureHeights(count);
			RecomputePrefix();
			var viewport = Math.Max(0, Scroll.ViewportHeight);
			var target = Math.Clamp(_prefix![index] + withinRow, 0, Math.Max(0, _total - viewport));
			_scrollTargetIndex = index;
			_scrollTargetWithin = withinRow;
			Scroll.SetScrollOffsets(Scroll.HorizontalOffset, target);
			InvalidateArrange();
		}

		public int IndexAtViewportAnchor(double withinViewport)
		{
			var count = _owner._rows.Count;
			if (count == 0) return -1;
			EnsureHeights(count);
			RecomputePrefix();
			return FindIndexByY((Scroll?.VerticalOffset ?? 0) + withinViewport);
		}

		private void EnsureHeights(int count)
		{
			if (_heights.Length == count && _measured.Length == count) return;
			var heights = new double[count];
			var measured = new bool[count];
			Array.Copy(_heights, heights, Math.Min(_heights.Length, count));
			Array.Copy(_measured, measured, Math.Min(_measured.Length, count));
			_heights = heights;
			_measured = measured;
			_prefix = null;
		}

		private void RecomputePrefix()
		{
			var count = _owner._rows.Count;
			if (_heights.Length != count) EnsureHeights(count);
			if (_prefix is null || _prefix.Length != count + 1) _prefix = new double[count + 1];
			var sum = 0.0;
			_prefix[0] = 0;
			for (var index = 0; index < count; index++)
			{
				var height = _heights[index] > 0 ? _heights[index] : EstimateHeight(index);
				_heights[index] = height;
				sum += height;
				_prefix[index + 1] = sum;
			}
			_total = sum;
		}

		private double EstimateHeight(int index)
		{
			if (_owner._rows.GetItem(index) is not ChatEntry entry) return 48;
			var width = _width > 0 ? _width : 700;
			var height = ConversationTimeline.EstimateRowHeight(entry, width);
			return double.IsFinite(height) && height > 0 ? height : 48;
		}

		private int FindIndexByY(double y)
		{
			var count = _owner._rows.Count;
			if (count <= 0) return 0;
			y = Math.Clamp(y, 0, Math.Max(0, _total));
			var prefix = _prefix!;
			int low = 0, high = count;
			while (low < high)
			{
				var middle = low + (high - low) / 2;
				if (prefix[middle + 1] <= y) low = middle + 1;
				else high = middle;
			}
			return Math.Min(low, count - 1);
		}

		public void DisposePanel()
		{
			if (_disposed) return;
			_disposed = true;
			ReleaseAll();
			_heights = [];
			_measured = [];
			_prefix = null;
			_total = 0;
		}
	}

	private sealed class ReadingRow : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var measured = base.MeasureContent(availableSize);
			return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : measured.Width, measured.Height);
		}
	}

	internal sealed class MessageRows : IItemsView
	{
		private readonly List<ChatEntry> _entries = [];
		public int Count => _entries.Count;
		public Func<object?, object?> KeySelector { get; } = static item => item is ChatEntry entry ? Key(entry) : null;
		public event Action<ItemsChange>? Changed;
		public object GetItem(int index) => _entries[index];
		public string GetText(int index) => string.Empty;
		public ChatEntry[] Snapshot() => [.. _entries];
		public void Invalidate() => Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, Count));
		public void Clear() => Update([], 0, false, []);

		/// Streaming and process rows have a persistent view that can absorb new data without
		/// rebuilding. Other replacements change identity or presentation and still need a rebind.
		/// 流式行落盘时 EntryId 会从 live-assistant/… 变成会话条目 id（时间戳与图片不变）：
		/// 这仍是一次原位收尾——发 Replace 会把该行已测高度作废成估算值，长回复定稿的一瞬
		/// 滚动范围先缩后涨，跟底读者看到一次跳动（组内步骤列表见 StepRows 的同一处理）。
		private static bool CanUpdateInPlace(ChatEntry current, ChatEntry next) =>
			(Equals(Key(current), Key(next)) && next.Role == ChatRole.Process) ||
			(current.IsStreaming && next.Role == ChatRole.Assistant && current.Timestamp == next.Timestamp &&
				current.Images.SequenceEqual(next.Images)) ||
			// 工具行的流式输出：同一次调用每来一段就换一条 ChatEntry，只有正文在长；
			// 表头只依赖参数（见 ToolMessageView.Update），原地重绑才能保住行高与秒表。
			(current.Role == ChatRole.Tool && current.IsStreaming && next.Role == ChatRole.Tool &&
				next.IsStreaming && Equals(Key(current), Key(next)) && current.ToolCallId == next.ToolCallId) ||
			SameMetadataOnly(current, next);

		public void Update(IReadOnlyList<ChatEntry> entries, int stablePrefix, bool prefixReused, List<int> inPlace)
		{
			var common = Math.Min(Count, entries.Count);
			// The projector reuses every row before the current turn by reference. Start the
			// comparison there instead of walking the whole history on every streaming frame.
			var unchanged = prefixReused ? Math.Min(stablePrefix, common) : 0;
			while (unchanged < common && SameEntry(_entries[unchanged], entries[unchanged])) unchanged++;
			if (unchanged < common)
			{
				// Notify only the rows that really need a new view. A single Replace spanning to the
				// end would drop the virtualizer's measured heights for every unchanged row below,
				// collapsing the extent to an estimate and shifting a browsing reader. In-place rows
				// are rebound by the host below, so they interrupt a Replace run instead of joining it.
				var runStart = -1;
				for (var i = unchanged; i < common; i++)
				{
					var current = _entries[i];
					var next = entries[i];
					if (SameEntry(current, next))
					{
						if (runStart >= 0)
						{
							Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, i - runStart));
							runStart = -1;
						}
						continue;
					}
					_entries[i] = next;
					if (CanUpdateInPlace(current, next))
					{
						inPlace.Add(i);
						if (runStart >= 0)
						{
							Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, i - runStart));
							runStart = -1;
						}
					}
					else if (runStart < 0)
					{
						runStart = i;
					}
				}
				if (runStart >= 0) Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, common - runStart));
			}
			if (entries.Count < Count)
			{
				var removed = Count - entries.Count;
				_entries.RemoveRange(entries.Count, removed);
				Changed?.Invoke(new ItemsChange(ItemsChangeKind.Remove, entries.Count, removed));
			}
			else if (entries.Count > Count)
			{
				var start = Count;
				for (var i = start; i < entries.Count; i++) _entries.Add(entries[i]);
				Changed?.Invoke(new ItemsChange(ItemsChangeKind.Add, start, Count - start));
			}
		}
	}
}
