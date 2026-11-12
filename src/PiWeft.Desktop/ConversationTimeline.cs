using System.Runtime.InteropServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed class ConversationTimeline : IDisposable
{
	private readonly MessageRows _rows = new();
	private readonly VirtualRowsPanel<ChatEntry, ReadingRow> _list;
	private readonly Func<ChatEntry, UIElement> _create;
	private readonly Dictionary<object, bool[]> _expanded = [];
	private readonly Dictionary<object, IReadOnlyDictionary<string, bool>> _processStates = [];
	private readonly HashSet<ReadingRow> _visible = [];
	private readonly Dictionary<ReadingRow, int> _indexByHost = [];
	private readonly Dictionary<int, ReadingRow> _hostByIndex = [];
	private readonly List<int> _inPlaceUpdates = [];
	private readonly List<int> _keptUpdates = [];
	private readonly List<ReadingRow> _rebindScratch = [];
	private readonly List<Expander> _expanderScratch = [];
	private readonly List<Element> _treeScratch = [];
	private bool _refreshQueued;
	private bool _disposed;

	public ConversationTimeline(Func<ChatEntry, UIElement> create)
	{
		_create = create;
		_list = new VirtualRowsPanel<ChatEntry, ReadingRow>(
			() => _rows.Count,
			_rows.EntryAt,
			CreateRow,
			(host, entry, index) => Bind(host, entry, index),
			Release)
		{
			Key = static entry => (object?)entry.EntryId ?? entry,
			Same = SameEntry,
			Estimate = EstimateRowHeight,
			DefaultEstimate = 120,
		};
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
		// 输入→绘制延迟的取样点：指针与滚轮在会话这一层最先被看到，下游控件常把事件处理掉，
		// 冒泡不到窗口根。这里记一次时间戳，下一帧由帧档算出延迟。
		Scroll.MouseDown += _ => Diagnostics.NoteInput();
		Scroll.MouseWheel += _ => Diagnostics.NoteInput();
	}

	public ScrollViewer View { get; }
	public ScrollViewer Scroll { get; }
	public IItemsView ItemsSource => _rows;
	internal int RowCount => _rows.Count;
	internal void SetFollowingProvider(Func<bool>? following) =>
		_list.PreserveReadingAnchor = following is null ? null : () => !following();

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

	/// <summary>结构变化触发整视口重画的次数，供测试确认「行离场会要求重画」这条策略。</summary>
	internal int RepaintOnStructuralChangeCount { get; private set; }

	internal int RowAtViewportAnchor() => _list.IndexAtViewportAnchor(28);

	public void SetEntries(
		IReadOnlyList<ChatEntry> entries,
		bool rebuild = false,
		int stablePrefix = 0,
		bool prefixReused = false)
	{
		_inPlaceUpdates.Clear();
		_keptUpdates.Clear();
		// 结构变化才需要按行身份迁移高度；流式原位更新走原下标，避免每帧复制整张表。
		// 全量重投（prefixReused=false）会换掉行实例，必须迁移；否则只按引用前缀判断即可。
		var structural = rebuild || entries.Count != _rows.Count || !prefixReused;
		var oldEntries = structural ? _rows.Snapshot() : default;
		// 迁移/重排会把行整体平移：先按顶部行记住阅读位置，布局前后补偿同样的量。
		var anchor = structural ? _list.CaptureShiftAnchor() : default;
		_rows.Update(entries, stablePrefix, prefixReused, _inPlaceUpdates, _keptUpdates, structural, out var rowsReplaced);
		// 行被移除或替换时，离开的元素里溢出的绘制（正文越过自己排布矩形的那部分）不属于任何
		// 场景节点：部分重绘只覆盖行矩形与新露出的带，滚动也不会带走这些像素，于是留下贴在旧屏幕
		// 位置、滚动不变的残影，直到下一块更大的重绘盖上去。这里整视口重画一次清掉它们；
		// 流式原位更新不离开任何元素，不付这次重画。
		if (rebuild || rowsReplaced || !prefixReused)
		{
			View.InvalidateVisual();
			RepaintOnStructuralChangeCount++;
		}
		// churn 计数（零分配）：流式期原位更新的行数，是「重建 vs 复用」最直接的对照量。
		if (_inPlaceUpdates.Count > 0) Diagnostics.Count("row.inplace", _inPlaceUpdates.Count);
		// 行数/顺序没变时不用碰高度缓存：流式行的增长由 Arrange 重测，再经「总高度变化」发布 extent。
		// 顺手省掉一条每帧都会作废测量的路径（否则流式期每帧多跑一整轮 measure）。
		if (structural) _list.ApplyRows(oldEntries, entries.Count, _keptUpdates, anchor);
		// A streaming tail changes on every frame. Rebinding its visible host keeps the panel's
		// measured height (and therefore the published extent) instead of resetting the row to the
		// coarse estimate, which made the viewport clamp short of the real bottom. Rows inside the
		// realized range are rebound again by Arrange, but that second visit is a reference-equal
		// early-out; rows pinned outside it (focus) are only rebound here.
		foreach (var index in CollectionsMarshal.AsSpan(_inPlaceUpdates))
		{
			if (index < entries.Count && _hostByIndex.TryGetValue(index, out var host)) Bind(host, entries[index], index);
		}
		// The streaming row invalidates its own measure; the panel only re-measures rows on arrange,
		// and an ancestor that was already measure-dirty skips that arrange. Invalidate the
		// arrange as well so the grown row (expanded thinking, streaming body) is actually re-measured.
		if (_inPlaceUpdates.Count > 0) View.InvalidateArrange();
		if (!rebuild) return;
		_rebindScratch.Clear();
		_rebindScratch.AddRange(_visible);
		foreach (var host in CollectionsMarshal.AsSpan(_rebindScratch))
		{
			if (_indexByHost.TryGetValue(host, out var index) && index < entries.Count) Bind(host, entries[index], index);
			// 索引失效时直接释放；面板下一轮会把宿主从可视集合里摘掉。
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

		// 中文和代码混合时一行约 14 DIP/字符。宁可略估小：实测变大只会扩展 extent，
		// 估得过大则会在收缩时触发夹取。
		var charsPerLine = Math.Max(24, width / 14.0);
		var lines = (double)entry.TextLength / charsPerLine;
		// 流式行的正文是增量快照，materialize 整篇只为数个换行不划算：只按字符数粗估。
		if (entry.TextLength > 0 && !entry.IsStreaming && entry.StreamedText is null)
		{
			// MemoryExtensions.Count 是向量化实现：整段正文扫一遍比逐字符快一个量级。
			var newlines = entry.Text.AsSpan().Count('\n') + 1;
			if (newlines > lines) lines = newlines;
		}
		var height = 48 + Math.Ceiling(Math.Max(1, lines)) * 22 + AppTheme.Space3;
		// 思考默认收起成一行表头；展开后的行会被实测替换。
		if (entry.ThinkingLength > 0) height += 44;
		if (entry.Images.Count > 0) height += entry.Images.Count * 240;
		return Math.Clamp(height, 36 + AppTheme.Space3, 12_000);
	}

	/// 折叠开关改变行高后立刻重排一次，按新内容重算滚动范围，把偏移收回到末尾。
	/// 不主动做的话，面板会继续用旧行高，折叠内容下方空出一块，直到下次重排才恢复。
	/// <param name="source">触发重排的元素（折叠开关）；给出时只刷新它所在的那一行。</param>
	public void RefreshLayout(Element? source = null)
	{
		// 只让 ScrollViewer 失效还不够：已实现行的测量缓存仍有效，element.Measure 会复用旧 DesiredSize。
		// 窗口大小变化能修好，是因为它改了测量约束。
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
	internal ReadingPoint CaptureReadingPoint()
	{
		var (key, within) = _list.CaptureReadingPoint();
		return new ReadingPoint(key as string, within);
	}

	/// 把阅读位置映射回当前内容坐标系；面板用 prefix + within 直接落位。
	internal bool RestoreReadingPoint(ReadingPoint point) => _list.RestoreReadingPoint(point.EntryId, point.WithinRow);

	internal int IndexOfEntryId(string? entryId)
	{
		if (string.IsNullOrEmpty(entryId)) return -1;
		for (var index = 0; index < _rows.Count; index++)
		{
			if (_rows.EntryAt(index) is { } entry && string.Equals(entry.EntryId, entryId, StringComparison.Ordinal))
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
		if (host.Tag is ChatEntry previous && SameEntry(previous, entry))
		{
			// 引用相等时连字典都不用动：流式期每帧都会走到这里。
			if (ReferenceEquals(previous, entry)) return;
			TrackIndex(host, index);
			host.Tag = entry;
			return;
		}
		TrackIndex(host, index);
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
			// Replace would reset this row to the estimator's value.
			host.Tag = entry;
			return;
		}
		if (host.Tag is ChatEntry { IsStreaming: true } streaming && entry.Role == ChatRole.Assistant &&
			streaming.Timestamp == entry.Timestamp && SameItems(streaming.Images, entry.Images) &&
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
			var expanders = FindExpanders(host.Content);
			for (var i = 0; i < Math.Min(states.Length, expanders.Count); i++) expanders[i].IsExpanded = states[i];
		}
	}

	private void TrackIndex(ReadingRow host, int index)
	{
		if (_indexByHost.TryGetValue(host, out var previous))
		{
			if (previous == index && _hostByIndex.TryGetValue(index, out var current) && ReferenceEquals(current, host)) return;
			if (_hostByIndex.TryGetValue(previous, out var mapped) && ReferenceEquals(mapped, host)) _hostByIndex.Remove(previous);
		}
		_indexByHost[host] = index;
		_hostByIndex[index] = host;
	}

	internal void PruneRecycled()
	{
		// 面板在 Arrange 里同步回收离屏宿主，这里保留给测试/旧调用点。
	}

	private void Release(ReadingRow host)
	{
		if (host.Tag is ChatEntry entry && host.Content is { } child)
		{
			if (child is Border { Tag: ExecutionProcessView process }) _processStates[Key(entry)] = process.State;
			var expanders = FindExpanders(child);
			if (expanders.Count == 0)
			{
				_expanded.Remove(Key(entry));
			}
			else
			{
				var states = new bool[expanders.Count];
				var any = false;
				for (var slot = 0; slot < states.Length; slot++)
				{
					states[slot] = expanders[slot].IsExpanded;
					any |= states[slot];
				}
				if (any) _expanded[Key(entry)] = states;
				else _expanded.Remove(Key(entry));
			}
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

	/// 迭代式遍历并用复用缓冲收集 <see cref="Expander"/>：行回收在滚动热路径上，
	/// 递归 + 每层 List 分配会变成每帧几十次小对象。
	private List<Expander> FindExpanders(Element root)
	{
		_expanderScratch.Clear();
		_treeScratch.Clear();
		_treeScratch.Add(root);
		while (_treeScratch.Count > 0)
		{
			var element = _treeScratch[^1];
			_treeScratch.RemoveAt(_treeScratch.Count - 1);
			if (element is Expander expander)
			{
				_expanderScratch.Add(expander);
				continue;
			}
			if (element is IVisualTreeHost tree) tree.VisitChildren(child => { _treeScratch.Add(child); return true; });
		}
		return _expanderScratch;
	}

	/// Compare fields directly instead of cloning both records with <c>with { ToolArguments = null }</c>:
	/// this runs for every changed row on every streaming frame, and the clones were pure garbage.
	/// Nested process members keep normal argument comparison; only the top-level row is metadata-only.
	internal static bool SameMetadataOnly(ChatEntry left, ChatEntry right) =>
		SameEntryCore(left, right, ignoreToolArguments: true);

	internal static bool SameEntry(ChatEntry left, ChatEntry right) =>
		SameEntryCore(left, right, ignoreToolArguments: false);

	/// 按下标比较两个列表。`IReadOnlyList<T>.SequenceEqual` 会为两侧各取一次接口枚举器，
	/// 流式期每帧每行都会走到这里；空列表（图片/改动汇总的常态）应当直接命中引用相等。
	internal static bool SameItems<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
	{
		if (ReferenceEquals(left, right)) return true;
		var count = left.Count;
		if (count != right.Count) return false;
		var comparer = EqualityComparer<T>.Default;
		for (var index = 0; index < count; index++)
			if (!comparer.Equals(left[index], right[index])) return false;
		return true;
	}

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
			!SameItems(left.Images, right.Images) || !SameItems(left.ChangedFiles, right.ChangedFiles) ||
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
		private ChatEntry[] _snapshot = [];
		public int Count => _entries.Count;
		public Func<object?, object?> KeySelector { get; } = static item => item is ChatEntry entry ? Key(entry) : null;
		public event Action<ItemsChange>? Changed;
		public object GetItem(int index) => _entries[index];
		public ChatEntry? EntryAt(int index) => (uint)index < (uint)_entries.Count ? _entries[index] : null;
		public string GetText(int index) => string.Empty;
		public void Invalidate() => Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, Count));
		public void Clear() => Update([], 0, false, [], [], false, out _);

		/// 复用同一块快照缓冲：结构变化（追加一行、过程组重排）在流式期每回合会发生几十次，
		/// 每次 <c>[.. _entries]</c> 都会分配一个与历史行数等长的数组。
		public RowsSnapshot<ChatEntry> Snapshot()
		{
			if (_snapshot.Length < _entries.Count) _snapshot = new ChatEntry[Math.Max(16, _entries.Count)];
			CollectionsMarshal.AsSpan(_entries).CopyTo(_snapshot);
			return new RowsSnapshot<ChatEntry>(_snapshot, _entries.Count);
		}

		/// Streaming and process rows have a persistent view that can absorb new data without
		/// rebuilding. Other replacements change identity or presentation and still need a rebind.
		/// 流式行落盘时 EntryId 会从 live-assistant/… 变成会话条目 id（时间戳与图片不变）：
		/// 这仍是一次原位收尾：发 Replace 会把该行已测高度作废成估算值，长回复定稿的一瞬
		/// 滚动范围先缩后涨，跟底读者看到一次跳动（组内步骤列表见 StepRows 的同一处理）。
		private static bool CanUpdateInPlace(ChatEntry current, ChatEntry next) =>
			(Equals(Key(current), Key(next)) && next.Role == ChatRole.Process) ||
			(current.IsStreaming && next.Role == ChatRole.Assistant && current.Timestamp == next.Timestamp &&
				SameItems(current.Images, next.Images)) ||
			// 工具行的流式输出：同一次调用每来一段就换一条 ChatEntry，只有正文在长；
			// 表头只依赖参数（见 ToolMessageView.Update），原地重绑才能保住行高与秒表。
			(current.Role == ChatRole.Tool && current.IsStreaming && next.Role == ChatRole.Tool &&
				next.IsStreaming && Equals(Key(current), Key(next)) && current.ToolCallId == next.ToolCallId) ||
			SameMetadataOnly(current, next);

		/// <param name="inPlace">可以只重绑、不必重建视图的行（流式尾巴、过程组）。</param>
		/// <param name="kept">行高可以沿用的行：内容未变，或可原位更新。<paramref name="collectKept"/> 为 false 时不填。</param>
		/// <param name="collectKept">调用方是否需要 <paramref name="kept"/>（只有结构变化才需要）。</param>
		public void Update(
			IReadOnlyList<ChatEntry> entries,
			int stablePrefix,
			bool prefixReused,
			List<int> inPlace,
			List<int> kept,
			bool collectKept,
			out bool replaced)
		{
			replaced = false;
			var common = Math.Min(Count, entries.Count);
			// The projector reuses every row before the current turn by reference. Start the
			// comparison there instead of walking the whole history on every streaming frame.
			var unchanged = prefixReused ? Math.Min(stablePrefix, common) : 0;
			while (unchanged < common && SameEntry(_entries[unchanged], entries[unchanged])) unchanged++;
			// 前缀行无论后面有没有变化都要记进 kept：全量重投但内容未变时 unchanged == common，
			// 漏记会让面板把整段历史的行高当成未知，重新估算后 extent 大幅跳动。
			if (collectKept)
			{
				kept.Clear();
				for (var index = 0; index < unchanged; index++) kept.Add(index);
			}
			if (unchanged < common)
			{
				// Notify only the rows that really need a new view. A single Replace spanning to the
				// end would drop the measured heights for every unchanged row below, collapsing the
				// extent to an estimate and shifting a browsing reader. In-place rows are rebound by
				// the host below, so they interrupt a Replace run instead of joining it.
				var runStart = -1;
				for (var i = unchanged; i < common; i++)
				{
					var current = _entries[i];
					var next = entries[i];
					if (SameEntry(current, next))
					{
						if (collectKept) kept.Add(i);
						if (runStart >= 0)
						{
							Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, i - runStart));
							replaced = true;
							runStart = -1;
						}
						continue;
					}
					_entries[i] = next;
					if (CanUpdateInPlace(current, next))
					{
						inPlace.Add(i);
						if (collectKept) kept.Add(i);
						if (runStart >= 0)
						{
							Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, i - runStart));
							replaced = true;
							runStart = -1;
						}
					}
					else if (runStart < 0)
					{
						runStart = i;
					}
				}
				if (runStart >= 0)
				{
					Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, common - runStart));
					replaced = true;
				}
			}
			if (entries.Count < Count)
			{
				var removed = Count - entries.Count;
				_entries.RemoveRange(entries.Count, removed);
				Changed?.Invoke(new ItemsChange(ItemsChangeKind.Remove, entries.Count, removed));
				replaced = true;
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
