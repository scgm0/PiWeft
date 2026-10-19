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
	private readonly HashSet<ReadingRow> _pendingRelease = [];
	private bool _releaseQueued;
	private bool _disposed;

	public ConversationTimeline(Func<ChatEntry, UIElement> create)
	{
		_create = create;
		View = new ItemsControl().VariableHeightPresenter().ItemsSource(_rows)
			.ItemHeight(120).ItemPadding(Thickness.Zero).BorderThickness(0).Background(AppTheme.Transparent)
			.Padding(new Thickness(AppTheme.Space3));
		View.ItemTemplate = new DelegateTemplate<ChatEntry>(
			_ => new ReadingRow().MaxWidth(AppTheme.MessageMaxWidth + AppTheme.Space6)
				.HorizontalAlignment(HorizontalAlignment.Center).Margin(new Thickness(0, 0, 0, AppTheme.Space3)),
			(view, entry, _, _) => Bind((ReadingRow)view, entry),
			(view, _, _, _) => QueueRelease((ReadingRow)view));
		ScrollViewer? scroll = null;
		((IVisualTreeHost)View).VisitChildren(child => { scroll = child as ScrollViewer; return false; });
		Scroll = scroll ?? throw new InvalidOperationException("The timeline requires a scroll viewport.");
		Scroll.SmoothScrolling().NoHorizontalScroll();
		Scroll.ScrollChanged += PruneRecycled;
	}

	public ItemsControl View { get; }
	public ScrollViewer Scroll { get; }
	public IEnumerable<UIElement> VisibleRows => _visible.Select(static host => host.Content).OfType<UIElement>();

	public void SetEntries(
		IReadOnlyList<ChatEntry> entries,
		bool rebuild = false,
		int stablePrefix = 0,
		bool prefixReused = false)
	{
		PruneRecycled();
		_rows.Update(entries, stablePrefix, prefixReused);
		if (!rebuild) return;
		foreach (var host in _visible.ToArray())
		{
			var entry = (ChatEntry)host.Tag!;
			Release(host);
			Bind(host, entry);
		}
		View.InvalidateMeasure();
	}

	public void ApplyReadingWidth()
	{
		foreach (var host in _visible) host.MaxWidth = AppTheme.MessageMaxWidth + AppTheme.Space6;
		View.InvalidateMeasure();
	}

	private static object Key(ChatEntry entry) => (object?)entry.EntryId ?? entry;

	private void Bind(ReadingRow host, ChatEntry entry)
	{
		_pendingRelease.Remove(host);
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
		left.IsError == right.IsError && left.DurationMs == right.DurationMs && left.Patch == right.Patch && left.ToolArguments == right.ToolArguments &&
		left.Images.SequenceEqual(right.Images) && left.ToolCallId == right.ToolCallId &&
		left.ProcessEntries.Count == right.ProcessEntries.Count && left.ProcessEntries.Zip(right.ProcessEntries).All(pair => SameEntry(pair.First, pair.Second)));

	public void Dispose()
	{
		_disposed = true;
		Scroll.ScrollChanged -= PruneRecycled;
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
		public void Clear() => Update([]);

		public void Update(IReadOnlyList<ChatEntry> entries, int stablePrefix = 0, bool prefixReused = false)
		{
			var common = Math.Min(Count, entries.Count);
			// The projector reuses every row before the current turn by reference. Start the
			// comparison there instead of walking the whole history on every streaming frame.
			var unchanged = prefixReused ? Math.Min(stablePrefix, common) : 0;
			while (unchanged < common && SameEntry(_entries[unchanged], entries[unchanged])) unchanged++;
			if (unchanged < common)
			{
				for (var i = unchanged; i < common; i++) _entries[i] = entries[i];
				Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, unchanged, common - unchanged));
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
