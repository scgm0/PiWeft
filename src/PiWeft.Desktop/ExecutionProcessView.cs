using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Lazy two-level process folds. Streaming updates retain existing text and tool surfaces.</summary>
internal sealed class ExecutionProcessView
{
	private readonly Func<ChatEntry, UIElement> _create;
	private readonly TextFocusScrollGuard? _focus;
	private readonly TextBlock _header = new();
	private readonly WrapPanel _stats = new();
	private readonly StackPanel _toolStat;
	private readonly StackPanel _thinkingStat;
	private readonly StackPanel _interimStat;
	private readonly StackPanel _errorStat;
	private readonly TextBlock _toolCount = new();
	private readonly TextBlock _thinkingCount = new();
	private readonly TextBlock _interimCount = new();
	private readonly TextBlock _errorCount = new();
	private readonly GlyphElement _ok;
	private readonly TextBlock _running = new();
	private readonly TextBlock _total = new();
	private readonly Expander _fold;
	private readonly StackPanel _body = new();
	private readonly StackPanel _footer;
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
		_header.Text(Strings.ExecutionProcess).FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Medium).PrimaryText();
		_stats.Spacing(8).VerticalAlignment(VerticalAlignment.Center);
		_toolStat = Stat("terminal", AppStyles.RoleSubtle, _toolCount);
		_thinkingStat = Stat("sparkles", AppStyles.RoleThinking, _thinkingCount);
		_interimStat = Stat("message-square", AppStyles.RoleInfo, _interimCount);
		_errorStat = Stat("x", AppStyles.RoleError, _errorCount);
		_stats.Children(_toolStat, _thinkingStat, _interimStat, _errorStat);
		_ok = new GlyphElement().Kind(GlyphKind.CheckMark).GlyphSize(3)
			.WithTheme((theme, glyph) => glyph.Foreground = AppTheme.For(theme).Success)
			.VerticalAlignment(VerticalAlignment.Center);
		_running.Text(Strings.ToolRunning).FontSize(AppTheme.FontSizeMicro).InfoText()
			.VerticalAlignment(VerticalAlignment.Center);
		var headerContent = new StackPanel().Orientation(Orientation.Horizontal).Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center).Children(
				AppIcons.Icon("list-checks", AppStyles.RoleSubtle, 14),
				_header, _stats, _ok, _running);
		var headerPill = new Border().BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 3, AppTheme.Space2 + 2, 3))
			.HorizontalAlignment(HorizontalAlignment.Left)
			.WithTheme((theme, border) =>
			{
				var palette = AppTheme.For(theme);
				border.Background = palette.CardBackgroundAlt;
				border.BorderBrush = palette.BorderSubtle;
			})
			.Child(headerContent);
		_fold = new Expander().Header(headerPill).IsExpanded(false).HoverTip(Strings.ExecutionExpandHint);
		_total.FontSize(AppTheme.FontSizeMicro).SubtleText();
		var collapse = new Button().Ghost().Padding(new Thickness(2, 0, 2, 0)).MinHeight(20)
			.Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(3)
				.VerticalAlignment(VerticalAlignment.Center).Children(
					new GlyphElement().Kind(GlyphKind.ChevronUp).GlyphSize(3)
						.WithTheme((theme, glyph) => glyph.Foreground = AppTheme.For(theme).TextSubtle),
					new TextBlock().Text(Strings.MessageCollapse).FontSize(AppTheme.FontSizeMicro).SubtleText()))
			.OnClick(() => _fold.IsExpanded = false);
		_footer = new StackPanel().Spacing(2).Margin(new Thickness(0, 4, 0, 0))
			.Children(collapse, _total);
		View = new Border().HorizontalAlignment(HorizontalAlignment.Stretch)
			.MaxWidth(AppTheme.MessageMaxWidth).Child(_fold.HorizontalAlignment(HorizontalAlignment.Stretch));
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
		var stats = ExecutionProcess.Count(entry.ProcessEntries);
		_toolCount.Text(stats.Tools.ToString());
		_thinkingCount.Text(stats.Thinking.ToString());
		_interimCount.Text(stats.Interim.ToString());
		_errorCount.Text(stats.Errors.ToString()).ErrorText();
		_toolStat.IsVisible(stats.Tools > 0);
		_thinkingStat.IsVisible(stats.Thinking > 0);
		_interimStat.IsVisible(stats.Interim > 0);
		_errorStat.IsVisible(stats.Errors > 0);
		// 错误已在计数里显示，不再追加第二个红叉；运行中也不能预先打成功勾。
		_ok.IsVisible(!entry.IsStreaming && stats.Errors == 0 && !entry.ProcessEntries.Any(step => step.IsStopped));
		_running.IsVisible(entry.IsStreaming);
		if (_fold.IsExpanded || _fold.Content is not null) Build();
	}

	private void Build()
	{
		if (_fold.Content is null)
		{
			_fold.Content(new Border().Padding(new Thickness(4, AppTheme.Space2, 0, 0))
				.HorizontalAlignment(HorizontalAlignment.Stretch)
				.Child(new StackPanel().Spacing(2).HorizontalAlignment(HorizontalAlignment.Stretch)
					.Children(_body.Spacing(2), _footer)));
		}
		var duration = _entry.DurationMs;
		_total.Text(duration is { } value ? Strings.ExecutionTotal(value) : string.Empty);
		_total.IsVisible(duration is not null);
		var nodes = Nodes(_entry.ProcessEntries);
		// 尾部已经是中间回复/错误时，前一个过程组已经结束，不能仍标「正在…」。
		var latest = nodes.LastOrDefault() is { Group: true } tail ? tail.Key : null;
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
			// 运行态只属于「正在流式的那一轮里最新的过程组」：它给「正在…」组头，其余组给结束态摘要。
			var isRunning = _entry.IsStreaming && node.Group && node.Key == latest;
			if (i < _slots.Count) { _slots[i].Update(node.Entries, isRunning); continue; }
			var slot = node.Group ? Group(node.Key, node.Entries, isRunning) : Leaf(node.Key, node.Entries[0]);
			_slots.Add(slot);
			_body.Children(slot.Host);
		}
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

	private Slot Group(string key, IReadOnlyList<ChatEntry> entries, bool running)
	{
		const int recentSteps = 8;
		var members = entries;
		var slots = new List<Slot>();
		var body = new StackPanel().Spacing(4);
		// 与 PiDeck 一样，连续竖线独占左列；步骤（图标 + 摘要）在右列，不让线从图标背后穿过。
		var bodyRail = new Border().Width(1).VerticalAlignment(VerticalAlignment.Stretch)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).Border);
		var bodyHost = new Grid().Columns("1,*").Children(
			bodyRail.Column(0), body.Column(1).Margin(new Thickness(12, 0, 0, 0)));
		var viewport = new ScrollViewer().Content(bodyHost).AutoVerticalScroll().NoHorizontalScroll()
			.MaxHeight(300).SmoothScrolling();
		var olderBase = -1;
		Expander fold = null!;
		StackPanel olderHost = null!;
		var olderButton = new Button().Ghost().Padding(new Thickness(4, 0, 4, 0)).MinHeight(22)
			.HorizontalAlignment(HorizontalAlignment.Left).IsVisible(false);
		var olderText = new TextBlock().FontSize(AppTheme.FontSizeMicro).MutedText();
		olderButton.Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(3)
			.VerticalAlignment(VerticalAlignment.Center).Children(
				new GlyphElement().Kind(GlyphKind.ChevronUp).GlyphSize(3)
					.WithTheme((theme, glyph) => glyph.Foreground = AppTheme.For(theme).TextSubtle),
				olderText));
		olderButton.OnClick(() =>
		{
			_states[$"older/{key}"] = true;
			olderBase = -1;
			BuildMembers();
		});
		olderHost = new StackPanel().Spacing(2).Children(olderButton, viewport);
		// 组头与 PiDeck 过程组同构：类别图标 + 主文案 + 「·」+ 等宽实时详情；运行时图标走强调色。
		var icon = AppIcons.Icon("terminal", AppStyles.RoleSubtle, 13);
		var label = new TextBlock().FontSize(AppTheme.FontSizeCaption).SubtleText()
			.VerticalAlignment(VerticalAlignment.Center);
		var separator = new TextBlock().Text("·").FontSize(AppTheme.FontSizeMicro).FaintText()
			.VerticalAlignment(VerticalAlignment.Center).IsVisible(false);
		var detail = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText().FontFamily(AppTheme.MonoFont)
			.TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center).IsVisible(false);
		var head = new Grid().Columns("Auto,Auto,Auto,*").HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				icon.Column(0),
				label.Column(1).Margin(new Thickness(5, 0, 0, 0)),
				separator.Column(2).Margin(new Thickness(6, 0, 0, 0)),
				detail.Column(3).Margin(new Thickness(5, 0, 0, 0)));
		fold = new Expander().Header(head)
			.IsExpanded(false).HoverTip(Strings.ExecutionGroupHint);
		fold.ExpandedChanged += expanded => { if (expanded) BuildMembers(); };
		Track(key, fold, false, group: true);
		var host = new Border().Padding(new Thickness(2, 0, 0, 0)).Child(fold);
		var slot = new Slot(key, host, Update);
		Update(entries, running);
		return slot;

		void Update(IReadOnlyList<ChatEntry> next, bool isRunning)
		{
			members = next;
			if (isRunning)
			{
				icon.Source(AppIcons.Get(ExecutionProcess.GroupIcon(next), AppStyles.ColorForRole(AppStyles.RoleAccent)));
				label.Text(ExecutionProcess.RunningLabel(next));
				var runningDetail = ExecutionProcess.RunningDetail(next);
				separator.IsVisible(runningDetail is not null);
				detail.IsVisible(runningDetail is not null);
				if (runningDetail is { } text) detail.Text(text);
			}
			else
			{
				icon.Source(AppIcons.Get(ExecutionProcess.GroupIcon(next), AppStyles.ColorForRole(AppStyles.RoleSubtle)));
				var summary = ExecutionProcess.GroupSummary(next);
				label.Text(summary.Length > 0 ? summary : Strings.ProcessGroupAnalyzed);
				separator.IsVisible(false);
				detail.IsVisible(false);
			}
			if (fold.IsExpanded || fold.Content is not null) BuildMembers();
		}
		void BuildMembers()
		{
			if (fold.Content is null) fold.Content(new Border().Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, 0)).Child(olderHost));
			if (olderBase < 0)
			{
				olderBase = _states.GetValueOrDefault($"older/{key}") ? 0 : Math.Max(0, members.Count - recentSteps);
				for (var i = slots.Count - 1; i >= 0; i--)
				{
					UiLifetime.Release(slots[i].Host);
					body.Remove(slots[i].Host);
					slots.RemoveAt(i);
				}
			}
			olderButton.IsVisible(olderBase > 0);
			if (olderBase > 0) olderText.Text(Strings.ExecutionEarlierSteps(olderBase));
			var visible = members.Count - olderBase;
			for (var i = slots.Count - 1; i >= visible; i--)
			{
				UiLifetime.Release(slots[i].Host);
				body.Remove(slots[i].Host);
				slots.RemoveAt(i);
			}
			for (var i = 0; i < visible; i++)
			{
				var member = members[olderBase + i];
				if (i < slots.Count) slots[i].Update([member], false);
				else
				{
					var leaf = Leaf($"{key}/{Id(member, olderBase + i)}", member);
					slots.Add(leaf);
					body.Children(leaf.Host);
				}
			}
		}
	}

	private Slot Leaf(string key, ChatEntry entry)
	{
		var content = new Border();
		var icon = new StepIcon(entry);
		var host = new Border().Child(new Grid().Columns("22,*")
			.Children(icon.View.Column(0), content.Column(1)));
		ChatEntry? previous = null;
		var slot = new Slot(key, host, (next, _) =>
		{
			var value = next[0];
			if (previous is not null && ConversationTimeline.SameEntry(previous, value)) return;
			if (previous is { IsStreaming: true } && value.Role == ChatRole.Assistant &&
				content.Child is Border { Tag: AssistantMessageView assistant }) assistant.Update(value);
			else
			{
				if (content.Child is { } old) UiLifetime.Release(old);
				content.Child(_create(value));
				var index = 0;
				foreach (var expander in Expanders(host)) Track($"{key}/{index++}", expander, false);
			}
			previous = value;
		});
		slot.Update([entry], false);
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

	private static StackPanel Stat(string icon, string role, TextBlock count)
	{
		count.FontSize(AppTheme.FontSizeMicro).SubtleText().VerticalAlignment(VerticalAlignment.Center);
		return new StackPanel().Orientation(Orientation.Horizontal).Spacing(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(AppIcons.Icon(icon, role, 11), count);
	}

	/// <summary>步骤图标只表示类型，不再承担时间线节点或遮线圆底的职责。</summary>
	private sealed class StepIcon
	{
		public StepIcon(ChatEntry entry)
		{
			View = AppIcons.Icon(IconFor(entry), RoleFor(entry), 14)
				.HorizontalAlignment(HorizontalAlignment.Left).VerticalAlignment(VerticalAlignment.Top)
				.Margin(new Thickness(2, 3, 0, 0));
		}

		public Image View { get; }

		private static string IconFor(ChatEntry entry) => entry.Role switch
		{
			ChatRole.Tool => entry.ToolName switch
			{
				"bash" or "terminal" => "terminal",
				"read" => "file-text",
				"write" or "edit" => "square-pen",
				"grep" or "find" or "ls" => "search",
				"todo" => "list-checks",
				_ => "play",
			},
			ChatRole.Assistant when entry.Thinking.Length > 0 || entry.IsThinking => "sparkles",
			ChatRole.Assistant => "message-square",
			ChatRole.Error => "x",
			_ => "ellipsis",
		};

		private static string RoleFor(ChatEntry entry) => entry.Role switch
		{
			ChatRole.Assistant when entry.Thinking.Length > 0 || entry.IsThinking => AppStyles.RoleThinking,
			ChatRole.Assistant => AppStyles.RoleInfo,
			ChatRole.Error => AppStyles.RoleSubtle,
			_ => AppStyles.RoleSubtle,
		};
	}

	private sealed record Node(string Key, IReadOnlyList<ChatEntry> Entries, bool Group);
	private sealed record Slot(string Key, Border Host, Action<IReadOnlyList<ChatEntry>, bool> Update);
}
