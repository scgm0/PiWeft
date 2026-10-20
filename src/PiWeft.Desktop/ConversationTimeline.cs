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
		Scroll.ScrollChanged += PruneRecycled;
		Scroll.MouseDown += CancelJump;
		Scroll.MouseWheel += CancelJump;
	}

	public ItemsControl View { get; }
	public ScrollViewer Scroll { get; }
	public IEnumerable<UIElement> VisibleRows => _visible.Select(static host => host.Content).OfType<UIElement>();

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
		// A streaming tail changes on every frame. Rebinding just its visible host keeps the
		// virtualized height cache (and therefore the published extent) instead of resetting the
		// row to the generic estimate, which made the viewport clamp short of the real bottom.
		for (var i = 0; i < _inPlaceUpdates.Count; i++)
		{
			var index = _inPlaceUpdates[i];
			if (index < entries.Count && _hostByIndex.TryGetValue(index, out var host)) Bind(host, entries[index], index);
		}
		if (!rebuild) return;
		foreach (var host in _visible.ToArray())
		{
			if (_indexByHost.TryGetValue(host, out var index) && index < entries.Count) Bind(host, entries[index], index);
			else Release(host);
		}
		View.InvalidateMeasure();
	}

	/// <summary>先由虚拟化器实现目标行，再把它对齐到视口上沿；不依赖未测量行的估算高度。</summary>
	public void JumpToRow(int index)
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
				if (_disposed || generation != _jumpGeneration || !_hostByIndex.TryGetValue(index, out var host)) return;
				var delta = host.Bounds.Y - Scroll.Bounds.Y - AppTheme.Space3;
				if (Math.Abs(delta) < 1) return;
				Scroll.SetScrollOffsets(Scroll.HorizontalOffset, Scroll.VerticalOffset + delta);
				if (pass < 2) Align(pass + 1);
			});
		}
	}

	private void CancelJump(MouseEventArgs _) => _jumpGeneration++;

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
		if (_releaseQueued || !Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
		_releaseQueued = true;
		dispatcher.BeginInvoke(() =>
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

	internal static bool SameEntry(ChatEntry left, ChatEntry right) => ReferenceEquals(left, right) || (
		left.Role == right.Role && left.Text == right.Text && left.Thinking == right.Thinking &&
		left.IsThinking == right.IsThinking && left.IsStreaming == right.IsStreaming &&
		left.Timestamp == right.Timestamp && left.EntryId == right.EntryId && left.ToolName == right.ToolName &&
		left.IsError == right.IsError && left.IsStopped == right.IsStopped && left.DurationMs == right.DurationMs && left.Patch == right.Patch && left.ToolArguments == right.ToolArguments &&
		left.Images.SequenceEqual(right.Images) && left.ToolCallId == right.ToolCallId && left.Compact == right.Compact &&
		left.ProcessEntries.Count == right.ProcessEntries.Count && left.ProcessEntries.Zip(right.ProcessEntries).All(pair => SameEntry(pair.First, pair.Second)));

	public void Dispose()
	{
		_disposed = true;
		Scroll.ScrollChanged -= PruneRecycled;
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
				var updatable = true;
				for (var i = unchanged; i < common; i++)
				{
					if (!CanUpdateInPlace(_entries[i], entries[i])) { updatable = false; break; }
				}
				for (var i = unchanged; i < common; i++) _entries[i] = entries[i];
				if (updatable)
				{
					for (var i = unchanged; i < common; i++) inPlace.Add(i);
				}
				else
				{
					Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, unchanged, common - unchanged));
				}
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
