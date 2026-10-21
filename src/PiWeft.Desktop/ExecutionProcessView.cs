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
	// 组内步骤是虚拟化的：行销毁时按步骤键记住折叠状态，重建后恢复。
	private readonly Dictionary<string, bool[]> _stepStates = [];
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
	public IReadOnlyDictionary<string, bool> State
	{
		get
		{
			var states = new Dictionary<string, bool>(_states);
			// 组内步骤是虚拟化的：它们的展开状态按步骤键单独记着，随消息一起带过回收。
			foreach (var (stepKey, values) in _stepStates)
			{
				for (var index = 0; index < values.Length; index++)
					if (values[index]) states[StepStateKey(stepKey, index)] = true;
			}
			return states;
		}
	}

	public void Restore(IReadOnlyDictionary<string, bool> states)
	{
		_automatic = true;
		try
		{
			foreach (var (key, expanded) in states) _states[key] = expanded;
			foreach (var (key, expanded) in states)
			{
				if (key.StartsWith(StepStatePrefix, StringComparison.Ordinal)) RestoreStepState(key, expanded);
			}
			foreach (var (key, control) in _controls.ToArray())
				if (_states.TryGetValue(key, out var expanded)) control.IsExpanded = expanded;
		}
		finally { _automatic = false; }
	}

	/// <summary>把序列化回来的 <c>step/…/n</c> 状态还原成按步骤键索引的展开表。</summary>
	private void RestoreStepState(string key, bool expanded)
	{
		if (!expanded) return;
		var rest = key[StepStatePrefix.Length..];
		var slash = rest.LastIndexOf('/');
		if (slash <= 0 || !int.TryParse(rest[(slash + 1)..], out var index) || index < 0) return;
		var stepKey = rest[..slash];
		var existing = _stepStates.TryGetValue(stepKey, out var found) ? found : null;
		var length = Math.Max(index + 1, existing?.Length ?? 0);
		var array = new bool[length];
		if (existing is not null) Array.Copy(existing, array, existing.Length);
		array[index] = true;
		_stepStates[stepKey] = array;
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
		var members = entries;
		var rows = new StepRows();
		// 折叠时把组体换成它：Expander.Content 不接受 null，用一个零高度占位保持「已展开才建树」。
		var collapsedBody = new Border { Height = 0 };
		Border? body = null;
		ItemsControl? list = null;
		ScrollViewer? viewport = null;
		// 每个过程组各自持有追帧状态：一个视图里可能有多组（中间回复会把过程切成多段），
		// 共用一份标志会让后一组的重夹/露出被前一组吞掉。
		var reclampQueued = false;
		Grid? revealRow = null;
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
		var fold = new Expander().Header(head)
			.IsExpanded(false).HoverTip(Strings.ExecutionGroupHint);
		fold.ExpandedChanged += expanded =>
		{
			// 展开时按视口建步骤行；折叠时整块释放，不留不可见的视图占内存。
			if (expanded) BuildBody();
			else DestroyBody();
		};
		Track(key, fold, false, group: true);
		var host = new Border().Padding(new Thickness(2, 0, 0, 0)).Child(fold);
		var slot = new Slot(key, host, Update);
		Update(entries, running);
		return slot;

		void BuildBody()
		{
			if (body is not null)
			{
				rows.Set(members);
				return;
			}
			list = new ItemsControl().VariableHeightPresenter().ItemsSource(rows)
				.ItemHeight(28).ItemPadding(Thickness.Zero).BorderThickness(0)
				.Background(AppTheme.Transparent).Padding(Thickness.Zero);
			list.ItemTemplate = new DelegateTemplate<ChatEntry>(
				_ => StepRowHost(),
				(host, entry, index, _) => BindStep((Grid)host, entry, index, key,
					expanded => InvalidateRow((Grid)host, expanded)),
				(host, _, _, _) => UnbindStep((Grid)host, key));
			((IVisualTreeHost)list).VisitChildren(child =>
			{
				viewport ??= child as ScrollViewer;
				return viewport is null;
			});
			// 组体与整段过程一样是「可滚动的小窗」：内层能滚就滚内层，内层到边界（或内容装得下）
			// 才把滚轮交还外层会话——由 SmoothScroll 在视口边界上交接（ScrollCascade）。
			viewport?.MaxHeight(300).SmoothScrolling().NoHorizontalScroll();
			// 与 PiDeck 一样，连续竖线独占左列；步骤（图标 + 摘要）在右列，不让线从图标背后穿过。
			var rail = new Border().Width(1).VerticalAlignment(VerticalAlignment.Stretch)
				.WithTheme((theme, border) => border.Background = AppTheme.For(theme).Border);
			var bodyHost = new Grid().Columns("1,*").Children(
				rail.Column(0), list.Column(1).Margin(new Thickness(12, 0, 0, 0)));
			body = new Border().Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, 0))
				.HorizontalAlignment(HorizontalAlignment.Stretch).Child(bodyHost);
			fold.Content(body);
			rows.Set(members);
		}

		/// <summary>步骤行展开/收起改变了行高：让虚拟化器重测这一行，并刷新组体的可滚范围。</summary>
		void InvalidateRow(Grid row, bool expanded)
		{
			if (body is null) return;
			ConversationTimeline.InvalidateMeasureTree(row);
			list?.InvalidateMeasure();
			// 组体是固定高度的滚动视口：内容变高后要重新发布 extent，否则滚不到新增的部分。
			viewport?.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset);
			QueueReclamp(row, expanded);
		}

		void QueueReclamp(Grid row, bool expanded)
		{
			if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
			// 同一帧里多次展开：记下最后一次要露出的行，只排一次追帧。
			revealRow = expanded ? row : null;
			if (reclampQueued) return;
			reclampQueued = true;
			// 行高只有测完才准：布局落定后再夹一次范围，并把刚展开的内容露出来。
			dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
			{
				reclampQueued = false;
				var reveal = revealRow;
				revealRow = null;
				if (body is null || viewport is null || list is null) return;
				viewport.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset);
				// 期间组体可能被折叠重建：旧行已经释放，不能再拿它的坐标去滚。
				if (reveal is null || reveal.Bounds.Height <= 0 || !reveal.IsDescendantOf(list)) return;
				// 展开出来的内容在视口下方时，组体往下带一段，别让读者自己猜还能不能滚。
				var overflow = reveal.Bounds.Bottom - viewport.Bounds.Bottom;
				if (overflow > 0.5)
					viewport.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset + overflow);
			});
		}

		void DestroyBody()
		{
			if (body is not { } content) return;
			body = null;
			list = null;
			viewport = null;
			rows.Reset();
			fold.Content(collapsedBody);
			UiLifetime.Release(content);
		}

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
			if (body is not null) rows.Set(members);
		}
	}

	/// <summary>
	/// 组内的步骤行：只为视口建摘要，展开时才建具体内容；折叠/滚出视口即释放。
	/// </summary>
	private Grid StepRowHost() => new Grid().Columns("22,*");

	private void BindStep(Grid host, ChatEntry entry, int index, string groupKey, Action<bool> layoutChanged)
	{
		var icon = host.Children.Count > 0 && host.Children[0] is Image existingIcon
			? existingIcon
			: AppIcons.Icon(StepIcon.IconFor(entry), StepIcon.RoleFor(entry), 14)
				.HorizontalAlignment(HorizontalAlignment.Left).VerticalAlignment(VerticalAlignment.Top)
				.Margin(new Thickness(2, 3, 0, 0));
		var content = host.Children.Count > 1 && host.Children[1] is Border existingContent
			? existingContent
			: new Border();
		if (icon.Parent is null) host.Children(icon.Column(0), content.Column(1));
		icon.Source(AppIcons.Get(StepIcon.IconFor(entry), AppStyles.ColorForRole(StepIcon.RoleFor(entry))));

		var stepKey = $"{groupKey}/{Id(entry, index)}";
		if (content.Tag is ChatEntry previous && ConversationTimeline.SameEntry(previous, entry)) return;
		if (content.Tag is ChatEntry { IsStreaming: true } streaming && entry.Role == ChatRole.Assistant &&
			streaming.Timestamp == entry.Timestamp && streaming.Images.SequenceEqual(entry.Images) &&
			content.Child is Border { Tag: AssistantMessageView assistant })
		{
			// 流式思考只更新既有视图，不重建行。
			assistant.Update(entry);
			content.Tag = entry;
			return;
		}
		if (content.Child is { } old) UiLifetime.Release(old);
		var view = _create(entry);
		content.Child(view);
		content.Tag = entry;
		TrackStepStates(stepKey, view, layoutChanged);
	}

	private void UnbindStep(Grid host, string groupKey)
	{
		if (host.Children.Count < 2 || host.Children[1] is not Border content) return;
		if (content.Child is { } old) UiLifetime.Release(old);
		content.Child = null;
		content.Tag = null;
	}

	/// <summary>记住这一步里每个折叠开关的展开状态：行被回收后重建仍能恢复。</summary>
	private void TrackStepStates(string stepKey, Element view, Action<bool>? layoutChanged = null)
	{
		// 恢复状态发生在虚拟化器创建这一行的时候（测量途中）。此时再让上层失效排布会
		// 在测量内部重入，行高缓存可能停在「内容还没建好」的那一版，表现为偶发的高度不对。
		// 行本来就是新建的，这一轮测量一定会带上新内容，所以恢复期间不触发重排。
		var restoring = true;
		var index = 0;
		foreach (var expander in Expanders(view))
		{
			var position = index++;
			if (_stepStates.TryGetValue(stepKey, out var saved) && position < saved.Length && saved[position])
				expander.IsExpanded = true;
			expander.ExpandedChanged += expanded =>
			{
				RecordStepState(stepKey, position, expanded);
				if (restoring) return;
				// 行内容变高/变矮：组体的行高缓存与可滚范围都要重算。
				layoutChanged?.Invoke(expanded);
			};
		}
		restoring = false;
	}

	private void RecordStepState(string stepKey, int index, bool expanded)
	{
		var existing = _stepStates.TryGetValue(stepKey, out var found) ? found : null;
		if (!expanded)
		{
			if (existing is not null && index < existing.Length) existing[index] = false;
			return;
		}
		var length = Math.Max(index + 1, existing?.Length ?? 0);
		var array = new bool[length];
		if (existing is not null) Array.Copy(existing, array, existing.Length);
		array[index] = true;
		_stepStates[stepKey] = array;
	}

	private const string StepStatePrefix = "step/";

	private static string StepStateKey(string stepKey, int index) => $"{StepStatePrefix}{stepKey}/{index}";

	/// <summary>非过程组节点的单行视图（中间回复/错误）：一行一个，不需要虚拟化。</summary>
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

		internal static string IconFor(ChatEntry entry) => entry.Role switch
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

		internal static string RoleFor(ChatEntry entry) => entry.Role switch
		{
			ChatRole.Assistant when entry.Thinking.Length > 0 || entry.IsThinking => AppStyles.RoleThinking,
			ChatRole.Assistant => AppStyles.RoleInfo,
			ChatRole.Error => AppStyles.RoleSubtle,
			_ => AppStyles.RoleSubtle,
		};
	}

	/// <summary>组内步骤的虚拟化数据源：只为视口建行，尾部变化用 Replace 而不是整表重置。</summary>
	private sealed class StepRows : IItemsView
	{
		private IReadOnlyList<ChatEntry> _items = [];

		public int Count => _items.Count;
		public Func<object?, object?>? KeySelector { get; } =
			static item => item is ChatEntry entry ? (object?)entry.EntryId ?? entry : null;
		public event Action<ItemsChange>? Changed;

		public object GetItem(int index) => _items[index];
		public string GetText(int index) => string.Empty;
		public void Invalidate() => Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, Count));

		/// <summary>丢弃内容但不通知：调用方正在销毁整个列表。</summary>
		public void Reset() => _items = [];

		public void Set(IReadOnlyList<ChatEntry> items)
		{
			var previous = _items;
			_items = items;
			if (previous.Count == 0)
			{
				Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, items.Count));
				return;
			}
			if (items.Count == previous.Count)
			{
				// 流式时通常只有尾部一行变化：只重绑这一段，滚动位置与已测高度都留着。
				var first = 0;
				while (first < items.Count && ConversationTimeline.SameEntry(previous[first], items[first])) first++;
				if (first == items.Count) return;
				Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, first, items.Count - first));
				return;
			}
			if (items.Count > previous.Count)
			{
				var prefix = 0;
				while (prefix < previous.Count && ConversationTimeline.SameEntry(previous[prefix], items[prefix])) prefix++;
				if (prefix == previous.Count)
				{
					Changed?.Invoke(new ItemsChange(ItemsChangeKind.Add, prefix, items.Count - prefix));
					return;
				}
			}
			Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, items.Count));
		}
	}

	private sealed record Node(string Key, IReadOnlyList<ChatEntry> Entries, bool Group);
	private sealed record Slot(string Key, Border Host, Action<IReadOnlyList<ChatEntry>, bool> Update);
}
