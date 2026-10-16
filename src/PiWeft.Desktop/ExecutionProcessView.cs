using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Lazy two-level process folds. Streaming updates retain existing text and tool surfaces.</summary>
internal sealed class ExecutionProcessView
{
	private readonly Func<ChatEntry, UIElement> _create;
	private readonly TextFocusScrollGuard? _focus;
	private readonly TextBlock _header = new();
	private readonly Expander _fold;
	private readonly StackPanel _body = new();
	private readonly Dictionary<string, bool> _states = [];
	private readonly Dictionary<string, Expander> _controls = [];
	private readonly List<Slot> _slots = [];
	private ChatEntry _entry;
	private string? _latestGroup;
	private bool _automatic;

	public ExecutionProcessView(ChatEntry entry, Func<ChatEntry, UIElement> create, TextFocusScrollGuard? focus = null)
	{
		_create = create;
		_focus = focus;
		_entry = entry;
		_fold = new Expander().Header(_header.FontSize(AppTheme.FontSizeCaption).MutedText())
			.IsExpanded(false).HoverTip(Strings.ExecutionExpandHint);
		// 参照 Codex 风格：执行过程是通栏色调条，不随折叠改变宽度。
		View = new Border().StyleName(AppStyles.ToolMessage).CornerRadius(AppTheme.RadiusSm)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space2, AppTheme.Space1))
			.MaxWidth(AppTheme.MessageMaxWidth).Child(_fold);
		View.Tag = this;
		_fold.ExpandedChanged += expanded =>
		{
			if (expanded) Build();
		};
		Track("run", _fold, entry.IsStreaming);
		Update(entry);
	}

	public Border View { get; }
	public IReadOnlyDictionary<string, bool> State => new Dictionary<string, bool>(_states);

	public void Restore(IReadOnlyDictionary<string, bool> states)
	{
		_automatic = true;
		try
		{
			foreach (var (key, expanded) in states) _states[key] = expanded;
			foreach (var (key, control) in _controls.ToArray())
				if (_states.TryGetValue(key, out var expanded)) control.IsExpanded = expanded;
		}
		finally { _automatic = false; }
	}

	private void Track(string key, Expander expander, bool initial, bool group = false)
	{
		_controls[key] = expander;
		if (key == "run" || group) _focus?.TrackFold(expander);
		expander.IsExpanded = _states.GetValueOrDefault(key, initial);
		_states[key] = expander.IsExpanded;
		expander.ExpandedChanged += expanded =>
		{
			_states[key] = expanded;
			if (!_automatic && group)
				_states[$"manual/{key}"] = true;
		};
	}

	public void Update(ChatEntry entry)
	{
		_entry = entry;
		_header.Text($"{Strings.ExecutionProcess}  ·  {ExecutionProcess.Summary(entry.ProcessEntries)}");
		if (_fold.IsExpanded || _fold.Content is not null) Build();
	}

	private void Build()
	{
		if (_fold.Content is null)
			_fold.Content(new Border().Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, 0)).Child(_body.Spacing(AppTheme.Space2)));
		var nodes = Nodes(_entry.ProcessEntries);
		var common = Math.Min(nodes.Count, _slots.Count);
		var reset = 0;
		while (reset < common && _slots[reset].Key == nodes[reset].Key) reset++;
		for (var i = _slots.Count - 1; i >= reset; i--)
		{
			var slot = _slots[i];
			UiLifetime.Release(slot.Host);
			_body.Remove(slot.Host);
			_slots.RemoveAt(i);
			foreach (var key in _controls.Keys.Where(key => key.StartsWith(slot.Key + "/", StringComparison.Ordinal) || key == slot.Key).ToArray())
				_controls.Remove(key);
		}
		for (var i = 0; i < nodes.Count; i++)
		{
			var node = nodes[i];
			if (i < _slots.Count) { _slots[i].Update(node.Entries); continue; }
			var slot = node.Group ? Group(node.Key, node.Entries) : Leaf(node.Key, node.Entries[0]);
			_slots.Add(slot);
			_body.Children(slot.Host);
		}
		var latest = nodes.LastOrDefault(node => node.Group)?.Key;
		if (_entry.IsStreaming && _fold.IsExpanded && latest != _latestGroup && latest is not null)
		{
			_automatic = true;
			try
			{
				if (_latestGroup is { } previous && !_states.GetValueOrDefault($"manual/{previous}") && _controls.TryGetValue(previous, out var old))
					old.IsExpanded = false;
				if (!_states.GetValueOrDefault($"manual/{latest}") && _controls.TryGetValue(latest, out var current)) current.IsExpanded = true;
			}
			finally { _automatic = false; }
		}
		_latestGroup = latest;
	}

	private Slot Group(string key, IReadOnlyList<ChatEntry> entries)
	{
		var members = entries;
		var slots = new List<Slot>();
		var body = new StackPanel().Spacing(4);
		var header = new TextBlock().FontSize(AppTheme.FontSizeCaption).SubtleText();
		var fold = new Expander().Header(header).IsExpanded(false).HoverTip(Strings.ExecutionGroupHint);
		fold.ExpandedChanged += expanded => { if (expanded) BuildMembers(); };
		Track(key, fold, false, group: true);
		var host = new Border().WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(new Thickness(1, 0, 0, 0))
			.Padding(new Thickness(AppTheme.Space2, 0, 0, 0)).Child(fold);
		var slot = new Slot(key, host, Update);
		Update(entries);
		return slot;

		void Update(IReadOnlyList<ChatEntry> next)
		{
			members = next;
			header.Text(ExecutionProcess.GroupSummary(next));
			if (fold.IsExpanded || fold.Content is not null) BuildMembers();
		}
		void BuildMembers()
		{
			if (fold.Content is null) fold.Content(new Border().Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, 0)).Child(body));
			for (var i = slots.Count - 1; i >= members.Count; i--)
			{
				UiLifetime.Release(slots[i].Host);
				body.Remove(slots[i].Host);
				slots.RemoveAt(i);
			}
			for (var i = 0; i < members.Count; i++)
			{
				if (i < slots.Count) slots[i].Update([members[i]]);
				else
				{
					var leaf = Leaf($"{key}/{Id(members[i], i)}", members[i]);
					slots.Add(leaf);
					body.Children(leaf.Host);
				}
			}
		}
	}

	private Slot Leaf(string key, ChatEntry entry)
	{
		var host = new Border();
		ChatEntry? previous = null;
		var slot = new Slot(key, host, next =>
		{
			var value = next[0];
			if (previous is not null && ConversationTimeline.SameEntry(previous, value)) return;
			if (previous is { IsStreaming: true } && value.Role == ChatRole.Assistant &&
				host.Child is Border { Tag: AssistantMessageView assistant }) assistant.Update(value);
			else
			{
				if (host.Child is { } old) UiLifetime.Release(old);
				host.Child(_create(value));
				var index = 0;
				foreach (var expander in Expanders(host)) Track($"{key}/{index++}", expander, false);
			}
			previous = value;
		});
		slot.Update([entry]);
		return slot;
	}

	private static IEnumerable<Expander> Expanders(Element element)
	{
		if (element is Expander expander) { yield return expander; yield break; }
		if (element is not IVisualTreeHost tree) yield break;
		var children = new List<Element>();
		tree.VisitChildren(child => { children.Add(child); return true; });
		foreach (var child in children)
			foreach (var found in Expanders(child)) yield return found;
	}

	private static string Id(ChatEntry entry, int index) => entry.EntryId ?? $"{entry.Role}/{entry.Timestamp}/{index}";
	private static List<Node> Nodes(IReadOnlyList<ChatEntry> entries)
	{
		var nodes = new List<Node>();
		var members = new List<ChatEntry>();
		for (var i = 0; i < entries.Count; i++)
		{
			var entry = entries[i];
			if (entry.Role == ChatRole.Tool || entry.Thinking.Length > 0 || entry.IsThinking) members.Add(entry);
			else
			{
				Flush();
				nodes.Add(new Node($"interim/{Id(entry, i)}", [entry], false));
			}
		}
		Flush();
		return nodes;
		void Flush()
		{
			if (members.Count == 0) return;
			nodes.Add(new Node($"group/{Id(members[0], nodes.Count)}", members.ToArray(), true));
			members.Clear();
		}
	}

	private sealed record Node(string Key, IReadOnlyList<ChatEntry> Entries, bool Group);
	private sealed record Slot(string Key, Border Host, Action<IReadOnlyList<ChatEntry>> Update);
}
