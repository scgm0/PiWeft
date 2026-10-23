using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>可变高度的消息时间线，只为视口创建内容；回收时释放正文、图片和 Markdown。</summary>
internal sealed class ConversationTimeline : IDisposable
{
	private readonly MessageRows _rows = new();
	private readonly Func<ChatEntry, UIElement> _create;
	private readonly Dictionary<object, bool[]> _expanded = [];
	private readonly Dictionary<object, IReadOnlyDictionary<string, bool>> _processStates = [];
	private readonly HashSet<ReadingRow> _visible = [];
	private readonly Dictionary<ReadingRow, int> _indexByHost = [];
	private readonly Dictionary<int, ReadingRow> _hostByIndex = [];
	private readonly HashSet<ReadingRow> _pendingRelease = [];
	private readonly List<int> _inPlaceUpdates = [];
	private bool _releaseQueued;
	private bool _refreshQueued;
	private bool _disposed;
	private int _jumpGeneration;

	public ConversationTimeline(Func<ChatEntry, UIElement> create)
	{
		_create = create;
		View = new ItemsControl().VariableHeightPresenter().ItemsSource(_rows)
			.ItemHeight(120).ItemPadding(Thickness.Zero).BorderThickness(0).Background(AppTheme.Transparent)
			// 右缘留出定位刻度的命中区，窄阅读列也不会被刻度盖住。
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space3, 44, AppTheme.Space3));
		View.ItemTemplate = new DelegateTemplate<ChatEntry>(
			_ => new ReadingRow().MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
				.HorizontalAlignment(HorizontalAlignment.Center).Margin(new Thickness(0, 0, 0, AppTheme.Space3)),
			(view, entry, index, _) => Bind((ReadingRow)view, entry, index),
			(view, _, _, _) => QueueRelease((ReadingRow)view));
		ScrollViewer? scroll = null;
		((IVisualTreeHost)View).VisitChildren(child => { scroll = child as ScrollViewer; return false; });
		Scroll = scroll ?? throw new InvalidOperationException("The timeline requires a scroll viewport.");
		Scroll.SmoothScrolling().NoHorizontalScroll();
		Scroll.ScrollChanged += RequestPrune;
		Scroll.MouseDown += CancelJump;
		Scroll.MouseWheel += CancelJump;
		// 输入→绘制延迟的取样点：指针与滚轮在会话这一层最先被看到（下游控件常把事件处理掉，
		// 冒泡不到窗口根），这里记一次时间戳，下一帧由诊断的帧档算出延迟。
		Scroll.MouseDown += _ => Diagnostics.NoteInput();
		Scroll.MouseWheel += _ => Diagnostics.NoteInput();
	}

	public ItemsControl View { get; }
	public ScrollViewer Scroll { get; }
	public IEnumerable<UIElement> VisibleRows => _visible.Select(static host => host.Content).OfType<UIElement>();

	/// <summary>当前视口里真正建了内容的行数；诊断页用它区分「行多」与「行贵」。</summary>
	internal int VisibleRowCount => _visible.Count;

	/// <summary>视口顶部下方 28 DIP 的实际行；只检查已实现容器，不测量整份历史。</summary>
	internal int RowAtViewportAnchor()
	{
		var anchor = Scroll.Bounds.Y + 28;
		var active = -1;
		var first = int.MaxValue;
		foreach (var host in _visible)
		{
			if (host.Tag is not ChatEntry || host.Bounds.Height <= 0 || !host.IsDescendantOf(View) ||
				!_indexByHost.TryGetValue(host, out var index)) continue;
			first = Math.Min(first, index);
			if (host.Bounds.Y <= anchor) active = Math.Max(active, index);
		}
		return active >= 0 ? active : first == int.MaxValue ? -1 : first;
	}

	public void SetEntries(
		IReadOnlyList<ChatEntry> entries,
		bool rebuild = false,
		int stablePrefix = 0,
		bool prefixReused = false)
	{
		PruneRecycled();
		_inPlaceUpdates.Clear();
		_rows.Update(entries, stablePrefix, prefixReused, _inPlaceUpdates);
		// churn 计数（零分配）：流式期原位更新的行数，是「重建 vs 复用」最直接的对照量。
		if (_inPlaceUpdates.Count > 0) Diagnostics.Count("row.inplace", _inPlaceUpdates.Count);
		// A streaming tail changes on every frame. Rebinding just its visible host keeps the
		// virtualized height cache (and therefore the published extent) instead of resetting the
		// row to the generic estimate, which made the viewport clamp short of the real bottom.
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
			// 索引失效时先挂起释放：宿主可能还是容器正在显示的那一行，
			// 立刻清空内容会让它变成一条空白行，只能等下次滚动重绑才恢复。
			else QueueRelease(host);
		}
		View.InvalidateMeasure();
	}

	/// <summary>
	/// 折叠开关改变行高后立刻重排一次，并按新内容重算滚动范围、把偏移收回到末尾之内。
	/// 手动滚动或改窗口大小本来会顺手做这件事；不主动做的话，虚拟化器会继续用旧的行高，
	/// 表现为折叠内容下方空出一块、直到重排才恢复。
	/// </summary>
	/// <param name="source">触发重排的元素（折叠开关）；给出时只刷新它所在的那一行。</param>
	public void RefreshLayout(Element? source = null)
	{
		// 只让 ItemsControl 失效还不够：已实现行的测量缓存仍然有效，虚拟化器的 element.Measure
		// 会直接复用旧 DesiredSize（旧行高）。窗口大小变化能修好，正是因为它改了测量约束。
		if (HostOf(source) is { } host) InvalidateMeasureTree(host);
		else foreach (var visible in _visible) InvalidateMeasureTree(visible);
		View.InvalidateMeasure();
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

	/// <summary>
	/// 把偏移收回当前内容末尾之内。SetScrollOffsets 会先从滚动内容取一次最新范围再夹取，
	/// 和用户滚一下触发的修正完全同一条路径。
	/// </summary>
	private void Reclamp() => Scroll.SetScrollOffsets(Scroll.HorizontalOffset, Scroll.VerticalOffset);

	/// <summary>元素所在的、当前已实现的行；不在时间线里时返回 <c>null</c>。</summary>
	private ReadingRow? HostOf(Element? element)
	{
		for (var current = element; current is not null; current = current.Parent)
			if (current is ReadingRow host && _visible.Contains(host)) return host;
		return null;
	}

	/// <summary>让一行（含子树）重新测量：折叠改变了内部高度的元素不一定把失效传到行根。</summary>
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

	/// <summary>先由虚拟化器实现目标行，再把它对齐到视口上沿；不依赖未测量行的估算高度。</summary>
	/// <param name="index">目标行。</param>
	/// <param name="withinRow">恢复阅读位置时，让目标行顶部比视口上沿高出多少 DIP。</param>
	public void JumpToRow(int index, double withinRow = 0)
	{
		if (index < 0 || index >= _rows.Count) return;
		var generation = ++_jumpGeneration;
		View.ScrollIntoView(index);
		Align(0);
		void Align(int pass)
		{
			if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
			dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
			{
				if (_disposed || generation != _jumpGeneration) return;
				if (!_hostByIndex.TryGetValue(index, out var host))
				{
					// 行高刚变化时虚拟化器偶尔会丢掉一次 ScrollIntoView；重发一次，别让刻度点击没有反应。
					if (pass < 2)
					{
						View.ScrollIntoView(index);
						Align(pass + 1);
					}
					return;
				}
				var delta = host.Bounds.Y - Scroll.Bounds.Y - AppTheme.Space3 + withinRow;
				if (Math.Abs(delta) < 1) return;
				Scroll.SetScrollOffsets(Scroll.HorizontalOffset, Scroll.VerticalOffset + delta);
				if (pass < 2) Align(pass + 1);
			});
		}
	}

	private void CancelJump(MouseEventArgs _) => _jumpGeneration++;

	/// <summary>正在阅读的条目与视口进入它的深度；用于搜索结束后恢复原位置。</summary>
	internal readonly record struct ReadingPoint(string? EntryId, double WithinRow);

	internal ReadingPoint CurrentReadingAnchor()
	{
		ReadingRow? top = null;
		var topIndex = int.MaxValue;
		foreach (var host in _visible)
		{
			if (host.Tag is not ChatEntry || host.Bounds.Height <= 0 || !host.IsDescendantOf(View) ||
				!_indexByHost.TryGetValue(host, out var index) || host.Bounds.Bottom <= Scroll.Bounds.Top)
			{
				continue;
			}
			if (index >= topIndex) continue;
			top = host;
			topIndex = index;
		}
		if (top?.Tag is not ChatEntry entry) return new ReadingPoint(null, 0);
		return new ReadingPoint(entry.EntryId, Math.Max(0, Scroll.Bounds.Top - top.Bounds.Top));
	}

	/// <summary>Current index of an entry id, or -1 when the entry left the projection.</summary>
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
		View.InvalidateMeasure();
	}

	private static object Key(ChatEntry entry) => (object?)entry.EntryId ?? entry;

	private void Bind(ReadingRow host, ChatEntry entry, int index)
	{
		_pendingRelease.Remove(host);
		TrackIndex(host, index);
		if (host.Tag is ChatEntry previous && SameEntry(previous, entry)) return;
		if (host.Tag is ChatEntry processEntry && entry.Role == ChatRole.Process &&
			processEntry.EntryId == entry.EntryId && host.Content is Border { Tag: ExecutionProcessView process })
		{
			process.Update(entry);
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

	/// <summary>Keeps a cheap host-to-index map so an in-place update can find its realized row.</summary>
	private void TrackIndex(ReadingRow host, int index)
	{
		if (_indexByHost.TryGetValue(host, out var previous) && previous != index && _hostByIndex.TryGetValue(previous, out var mapped) && ReferenceEquals(mapped, host))
		{
			_hostByIndex.Remove(previous);
		}
		_indexByHost[host] = index;
		_hostByIndex[index] = host;
	}

	/// <summary>Unbind also runs immediately before a rebind. Release only rows actually detached after layout.</summary>
	private void QueueRelease(ReadingRow host)
	{
		_pendingRelease.Add(host);
		RequestPrune();
	}

	/// <summary>
	/// 释放检查只能放在本次布局之后：虚拟化器在一次布局里会先摘下容器、随后又按 key 收回
	/// （Reset 的 hold/回挂），此刻 <c>Parent</c> 为空并不代表这行真的离开了视图。
	/// 在布局中途同步释放会把仍要显示的行清空，表现就是一条只占了位置、没有内容的空白行。
	/// </summary>
	private void RequestPrune()
	{
		if (_pendingRelease.Count == 0) return;
		if (_releaseQueued || !Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
		_releaseQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_releaseQueued = false;
			if (!_disposed) PruneRecycled();
		});
	}

	internal void PruneRecycled()
	{
		foreach (var host in _pendingRelease.ToArray())
		{
			var attached = false;
			for (Element? element = host; element is not null; element = element.Parent)
			{
				if (ReferenceEquals(element, View)) { attached = true; break; }
			}
			if (!attached) Release(host);
		}
	}

	private void Release(ReadingRow host)
	{
		_pendingRelease.Remove(host);
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

	internal static bool SameEntry(ChatEntry left, ChatEntry right)
	{
		if (ReferenceEquals(left, right)) return true;
		if (left.Role != right.Role || left.Text != right.Text || left.Thinking != right.Thinking ||
			left.IsThinking != right.IsThinking || left.IsStreaming != right.IsStreaming ||
			left.Timestamp != right.Timestamp || left.EntryId != right.EntryId || left.ToolName != right.ToolName ||
			left.IsError != right.IsError || left.IsStopped != right.IsStopped ||
			left.DurationMs != right.DurationMs || left.Patch != right.Patch ||
			left.ToolArguments != right.ToolArguments || left.ToolCallId != right.ToolCallId ||
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
			if (!SameEntry(leftSteps[index], rightSteps[index])) return false;
		}

		return true;
	}

	public void Dispose()
	{
		_disposed = true;
		Scroll.ScrollChanged -= RequestPrune;
		Scroll.MouseDown -= CancelJump;
		Scroll.MouseWheel -= CancelJump;
		SmoothScroll.Release(Scroll);
		View.ItemsSource = ItemsView.Empty;
		foreach (var host in _visible.ToArray()) Release(host);
		_expanded.Clear();
		_processStates.Clear();
		_rows.Clear();
	}

	/// <summary>Short messages occupy the same reading column as long replies.</summary>
	private sealed class ReadingRow : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var measured = base.MeasureContent(availableSize);
			return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : measured.Width, measured.Height);
		}
	}

	/// <summary>Granular tail changes retain measured heights and existing selections during streaming.</summary>
	private sealed class MessageRows : IItemsView
	{
		private readonly List<ChatEntry> _entries = [];
		public int Count => _entries.Count;
		public Func<object?, object?> KeySelector { get; } = static item => item is ChatEntry entry ? Key(entry) : null;
		public event Action<ItemsChange>? Changed;
		public object GetItem(int index) => _entries[index];
		public string GetText(int index) => string.Empty;
		public void Invalidate() => Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, Count));
		public void Clear() => Update([], 0, false, []);

		/// <summary>
		/// Streaming and process rows have a persistent view that can absorb new data without
		/// rebuilding. Other replacements change identity or presentation and still need a rebind.
		/// </summary>
		private static bool CanUpdateInPlace(ChatEntry current, ChatEntry next) =>
			Equals(Key(current), Key(next)) && (next.Role == ChatRole.Process ||
				(current.IsStreaming && next.Role == ChatRole.Assistant && current.Timestamp == next.Timestamp &&
					current.Images.SequenceEqual(next.Images)));

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
