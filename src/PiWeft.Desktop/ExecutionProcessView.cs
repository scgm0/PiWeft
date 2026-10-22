using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Lazy two-level process folds. Streaming updates retain existing text and tool surfaces.</summary>
internal sealed class ExecutionProcessView
{
	private readonly Func<ChatEntry, UIElement> _create;
	private readonly TextFocusScrollGuard? _focus;
	private bool _metricsQueued;
	// 展开/收起步骤后"把预览行钉在原位"的收尾动作：必须在本帧布局之后立刻执行，
	// 不能等到下一次无关的 ScrollChanged —— 那时行已经移位，读者会看到一次迟到跳动。
	private readonly List<Action> _pendingClamps = [];
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
	// Nodes() 的复用缓冲：流式期每帧都会重建节点列表，不能每次都新建对象。
	private readonly List<Node> _nodes = [];
	private readonly List<ChatEntry> _memberScratch = [];
	private int _nodeCount;
	private ChatEntry _entry;
	private string? _latestGroup;
	private bool _automatic;
	// 只在「流式最新组自动展开」这一条通道上跟随末尾；手动展开/恢复展开不跟随。
	private bool _autoExpandingGroup;

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

	/// <summary>
	/// The step list only re-measures rows while arranging, and an ancestor that was already
	/// measure-dirty leaves the arrange clean — a row that grows (expanded step, streaming thinking)
	/// would then keep its old height and the body would neither grow nor scroll to it. Invalidate the
	/// arrange of every open body and republish the virtualized extent once layout has settled.
	/// </summary>
	private void KickBodyLayout()
	{
		foreach (var viewport in BodyViewports())
		{
			viewport.InvalidateArrange();
			// ScrollViewer itself is not an invalidation host: the presenter (virtualized) or the
			// stack panel inside it must be arrange-dirty too, or it keeps its previous arrange.
			viewport.Content?.InvalidateArrange();
		}
		if (_metricsQueued || !Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
		_metricsQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_metricsQueued = false;
			SyncBodyMetrics();
		});
	}

	/// <summary>Republishes each open body's extent and keeps a growth-following body pinned to its end.</summary>
	internal void SyncBodyMetrics()
	{
		foreach (var viewport in BodyViewports())
		{
			viewport.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset);
			if (SmoothScroll.Find(viewport) is { IsFollowingGrowth: true } smooth) smooth.FollowGrowth();
		}
		RunPendingClamps();
	}

	/// <summary>布局落定后执行待收尾的阅读位置修正；每个动作只跑一次。</summary>
	private void RunPendingClamps()
	{
		if (_pendingClamps.Count == 0) return;
		var pending = _pendingClamps.ToArray();
		_pendingClamps.Clear();
		foreach (var apply in pending) apply();
	}

	private void QueuePendingClamp(Action apply)
	{
		if (!_pendingClamps.Contains(apply)) _pendingClamps.Add(apply);
		KickBodyLayout();
	}

	private List<ScrollViewer> BodyViewports()
	{
		var viewports = new List<ScrollViewer>();
		Collect(View);
		return viewports;

		void Collect(Element element)
		{
			if (element is ScrollViewer viewport)
			{
				if (viewport.IsVisible && viewport.Bounds.Height > 0) viewports.Add(viewport);
				return;
			}
			if (element is not IVisualTreeHost host) return;
			var children = new List<Element>();
			host.VisitChildren(child => { children.Add(child); return true; });
			foreach (var child in children) Collect(child);
		}
	}
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
			// 从缓存恢复展开状态不是用户本次的阅读动作：先保持原位置，等读者滚到底
			// 或显式展开再重新锁底，避免重开组体时被拽到末尾。
			foreach (var viewport in BodyViewports())
			{
				if (SmoothScroll.Find(viewport) is { } smooth) smooth.SuppressAutoFollow();
			}
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

	/// <summary>代码恢复折叠状态时不让流式思考重新请求贴底；用户点击/键盘切开的路径不受影响。</summary>
	private void SetExpandedWithoutFollowing(Expander expander, bool expanded)
	{
		var previous = _focus?.SuppressGrowthFollow ?? false;
		if (_focus is not null) _focus.SuppressGrowthFollow = true;
		try { expander.IsExpanded = expanded; }
		finally { if (_focus is not null) _focus.SuppressGrowthFollow = previous; }
	}

	private void Track(string key, Expander expander, bool initial, bool group = false)
	{
		_controls[key] = expander;
		if (key == "run" || group) _focus?.TrackFold(expander);
		SetExpandedWithoutFollowing(expander, _states.GetValueOrDefault(key, initial));
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
		Nodes(_entry.ProcessEntries);
		var nodes = _nodes;
		var nodeCount = _nodeCount;
		// 尾部已经是中间回复/错误时，前一个过程组已经结束，不能仍标「正在…」。
		var latest = nodeCount > 0 && nodes[nodeCount - 1].Group ? nodes[nodeCount - 1].Key : null;
		var common = Math.Min(nodeCount, _slots.Count);
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
		for (var i = 0; i < nodeCount; i++)
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
				if (!_states.GetValueOrDefault($"manual/{latest}") && _controls.TryGetValue(latest, out var current))
				{
					_autoExpandingGroup = true;
					try { current.IsExpanded = true; }
					finally { _autoExpandingGroup = false; }
				}
			}
			finally { _automatic = false; }
		}
		_latestGroup = latest;
	}

	private Slot Group(string key, IReadOnlyList<ChatEntry> entries, bool running)
	{
		// 小组用内容驱动的栈：展开一行，组体就跟着长高（最高 300）。
		// 步骤多到一屏放不下时改走虚拟化列表，此时内容必然超过 300，组体固定限高才不会因为
		// ItemsControl 的估算行高停在折叠高度上。
		const int stackLimit = 24;
		const double bodyMaxHeight = 300;
		var members = entries;
		var runningGroup = running;
		StepRows? rows = null;
		// 折叠时把组体换成它：Expander.Content 不接受 null，用一个零高度占位保持「已展开才建树」。
		var collapsedBody = new Border { Height = 0 };
		Border? body = null;
		ItemsControl? list = null;
		StackPanel? stack = null;
		var stackEntries = new List<ChatEntry>();
		var stackHosts = new List<Grid>();
		// 组体的滚动视口：栈布局是外层 ScrollViewer，虚拟化时是 ItemsControl 内层视口。
		ScrollViewer? viewport = null;
		// 每个过程组各自持有追帧状态：一个视图里可能有多组（中间回复会把过程切成多段），
		// 共用一份标志会让后一组的重夹/露出被前一组吞掉。
		var reclampQueued = false;
		var switchQueued = false;
		// 自动展开的「正在运行」组跟住最新步骤；手动展开的组从头看。
		var followOnOpen = false;
		// 展开/收起步骤后把它的预览行固定在原屏幕位置，而不是滚向内容末尾。
		Grid? anchorRow = null;
		double anchorOffset = 0;
		// 流式思考/正文只原位更新已实现的那一行，不向虚拟化器发 Replace：
		// Replace 会把该行已测高度作废成估算值，组体 extent 先缩后涨，看起来就是进度跳变。
		var inPlaceRows = new List<int>();
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
			if (expanded)
			{
				followOnOpen = _autoExpandingGroup && runningGroup;
				BuildBody();
			}
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
				SyncRows();
			}
			else if (members.Count > stackLimit) BuildVirtualBody();
			else BuildStackBody();

			// 自动通道（流式最新组）展开后先跟住末尾，后续增长由 SmoothScroll 的跟随增长接管；
			// 手动点开的组保持顶部，不把读者拽到最后。
			KickBodyLayout();
			if (!followOnOpen) return;
			followOnOpen = false;
			FollowGroup();
		}

		void FollowGroup()
		{
			if (viewport is not null) SmoothScroll.Find(viewport)?.FollowGrowth();
		}

		void StopGroupFollow()
		{
			// 工具步骤展开是阅读行为：进度停在预览行，不跟着展开内容滚到末尾。
			if (viewport is not null) SmoothScroll.Find(viewport)?.StopFollowingGrowth();
		}

		void BuildStackBody()
		{
			stack = new StackPanel().Spacing(4);
			stackEntries.Clear();
			stackHosts.Clear();
			AddStackRows();
			// 栈布局把全部行都实现出来（上限 24 行），高度由内容决定，展开/收起自然带动组体。
			viewport = new ScrollViewer().Content(stack).MaxHeight(bodyMaxHeight)
				.SmoothScrolling().NoHorizontalScroll();
			CompleteBody(viewport);
		}

		void BuildVirtualBody()
		{
			rows = new StepRows();
			viewport = null;
			list = new ItemsControl().VariableHeightPresenter().ItemsSource(rows)
				.ItemHeight(28).ItemPadding(Thickness.Zero).BorderThickness(0)
				.Background(AppTheme.Transparent).Padding(Thickness.Zero)
				// 能走到虚拟化说明行数已经超过一屏；固定组体高度，滚轮与追帧都只作用在内层。
				.Height(bodyMaxHeight);
			list.ItemTemplate = new DelegateTemplate<ChatEntry>(
				_ => StepRowHost(),
				(host, entry, index, _) => BindStepRow((Grid)host, entry, index),
				(host, _, _, _) => UnbindStep((Grid)host, key));
			((IVisualTreeHost)list).VisitChildren(child =>
			{
				viewport ??= child as ScrollViewer;
				return viewport is null;
			});
			// 组体与整段过程一样是「可滚动的小窗」：内层能滚就滚内层，内层到边界（或内容装得下）
			// 才把滚轮交还外层会话——由 SmoothScroll 在视口边界上交接（ScrollCascade）。
			viewport?.MaxHeight(bodyMaxHeight).SmoothScrolling().NoHorizontalScroll();
			CompleteBody(list);
			rows.Set(members);
		}

		void CompleteBody(FrameworkElement content)
		{
			// 组体是“最新步骤”的窗口：滚动条置底时持续跟随新步骤；用户上滚后暂停，
			// 回到底部（滚轮/键盘/拖动滚动条）自动重新锁底。
			// 组体就是“最新步骤”的窗口：置底即跟随。这里不能做任何抑制——栈/虚拟化切换、
			// 步骤行回收后的重建都只是布局，不是读者的阅读意图；恢复态在 Restore 里单独处理。
			if (viewport is not null) SmoothScroll.For(viewport).AutoFollowAtEnd = true;
			// 与 PiDeck 一样，连续竖线独占左列；步骤（图标 + 摘要）在右列，不让线从图标背后穿过。
			var rail = new Border().Width(1).VerticalAlignment(VerticalAlignment.Stretch)
				.WithTheme((theme, border) => border.Background = AppTheme.For(theme).Border);
			var bodyHost = new Grid().Columns("1,*").Children(
				rail.Column(0), content.Column(1).Margin(new Thickness(12, 0, 0, 0)));
			body = new Border().Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, 0))
				.HorizontalAlignment(HorizontalAlignment.Stretch).Child(bodyHost);
			body.MouseWheel += RouteBodyWheel;
			fold.Content(body);
		}

		/// <summary>滚轮落在组体空白处时也要先归组体；装得下或到边界才留给外层会话。</summary>
		void RouteBodyWheel(MouseWheelEventArgs args)
		{
			if (args.Handled || viewport is null) return;
			// 布局刚变过时滚动条范围可能还落后一帧，先按最新 extent 刷新一次再判断归属。
			viewport.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset);
			if (SmoothScroll.Find(viewport) is not { } smooth) return;
			var delta = smooth.WheelDelta(args.Delta.X, args.Delta.Y);
			if (delta.X == 0 && delta.Y == 0) return;
			HoverHints.Cancel(viewport);
			smooth.ScrollBy(delta.X, delta.Y);
			args.Handled = true;
		}

		void AddStackRows()
		{
			if (stack is null) return;
			for (var index = 0; index < members.Count; index++)
			{
				var row = StepRowHost();
				BindStepRow(row, members[index], index);
				stack.Children(row);
				stackEntries.Add(members[index]);
				stackHosts.Add(row);
			}
		}

		/// <summary>步骤行的绑定入口：展开/收起只重测这一行并让它的预览行停在原地。</summary>
		void BindStepRow(Grid row, ChatEntry entry, int index) =>
			BindStep(row, entry, index, key, expanded =>
			{
				if (expanded && entry.Role == ChatRole.Tool) StopGroupFollow();
				InvalidateRow(row, expanded);
			});

		/// <summary>同步栈里的步骤行；同一位置的步骤只更新内容，不重建行。</summary>
		void SyncStackRows()
		{
			if (stack is null) return;
			if (members.Count > stackLimit)
			{
				// 流式更新可能恰好发生在会话列表的测量/排布里；此时换 presenter 会重建整棵组体，
				// 排到 Render 再做，避免在布局过程中重新种树。测试等无消息循环的场景直接切换。
				if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
				{
					SwitchToVirtualBody();
					return;
				}
				if (!switchQueued)
				{
					switchQueued = true;
					dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
					{
						switchQueued = false;
						if (body is not null && stack is not null && members.Count > stackLimit) SwitchToVirtualBody();
					});
				}
				return;
			}
			if (!StackMatches())
			{
				RebuildStack();
				return;
			}
			for (var index = 0; index < members.Count; index++)
			{
				// BindStep 遇到相同内容会直接返回；流式更新则保留展开状态原地更新。
				// 注意：不能把循环变量 index 带进回调；展开回调在循环结束后才触发，会拿到越界索引。
				BindStepRow(stackHosts[index], members[index], index);
				stackEntries[index] = members[index];
			}
		}

		bool StackMatches()
		{
			if (stackHosts.Count != members.Count) return false;
			for (var index = 0; index < members.Count; index++)
			{
				var left = stackEntries[index];
				var right = members[index];
				if (!ReferenceEquals(left, right) &&
					(left.Role != right.Role || left.ToolCallId != right.ToolCallId || left.EntryId != right.EntryId))
					return false;
			}
			return true;
		}

		void RebuildStack()
		{
			if (stack is null) return;
			stack.Clear();
			foreach (var row in stackHosts)
			{
				UnbindStep(row, key);
				UiLifetime.Release(row);
			}
			stackEntries.Clear();
			stackHosts.Clear();
			AddStackRows();
		}

		void SwitchToVirtualBody()
		{
			var offset = viewport?.VerticalOffset ?? 0;
			var atEnd = viewport is not null && SmoothScroll.Find(viewport) is { } smooth &&
				offset >= smooth.MaxVerticalOffset - 0.5;
			DestroyBody();
			BuildVirtualBody();
			if (viewport is { } target) RestoreOffset(target, offset, atEnd);
		}

		/// <summary>换 presenter 后把原来的阅读位置放回去；等新列表真正量出 extent 再落位。</summary>
		void RestoreOffset(ScrollViewer target, double offset, bool atEnd)
		{
			if (offset <= 0) return;
			var applied = false;
			void Apply()
			{
				if (applied) return;
				if (body is null || !ReferenceEquals(viewport, target))
				{
					target.SizeChanged -= OnSize;
					return;
				}
				var max = SmoothScroll.Find(target)?.MaxVerticalOffset ?? 0;
				// 新列表还没有可滚余量，说明它还没量完（越过 24 步时内容必然超高），等下一次尺寸通知。
				if (max <= 0.5) return;
				applied = true;
				target.SizeChanged -= OnSize;
				target.SetScrollOffsets(target.HorizontalOffset, atEnd ? max : Math.Min(offset, max));
			}
			void OnSize(SizeChangedEventArgs _) => Apply();
			target.SizeChanged += OnSize;
			if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
			// 兜底：尺寸没变（例如切换前后高度相同）时也按当前 extent 落位。
			dispatcher.BeginInvoke(DispatcherPriority.Render, Apply);
		}

		void SyncRows()
		{
			if (list is not null)
			{
				inPlaceRows.Clear();
				rows?.Set(members, inPlaceRows);
				// 原位更新的行不经过 ItemsChange，因此虚拟化器不会重新绑定它们；
				// 这里直接更新已实现的宿主，保留各自的已测高度。
				for (var i = 0; i < inPlaceRows.Count; i++)
				{
					var index = inPlaceRows[i];
					if (index >= members.Count || RealizedRow(index) is not { } row) continue;

					ConversationTimeline.InvalidateMeasureTree(row);
					BindStepRow(row, members[index], index);
				}
			}
			else SyncStackRows();
			// 流式思考/正文在组体里增长时同样要走 arrange 重测，否则行高停在展开时的那一版。
			if (body is not null) KickBodyLayout();
		}

		/// <summary>当前虚拟化器已实现的宿主；行滚出视口会被回收，因此只用于原位更新时查找。</summary>
		Grid? RealizedRow(int index)
		{
			if (list is null) return null;
			Grid? found = null;
			Visit(list);
			return found;

			void Visit(Element element)
			{
				if (found is not null) return;
				// 只认容器层：不要为了找一个索引把每一行的正文/Markdown 子树都走一遍。
				if (element is ItemContainer container)
				{
					if (container.Index == index) found = container.Content as Grid;
					return;
				}
				if (element is not IVisualTreeHost host) return;
				var children = new List<Element>();
				host.VisitChildren(child => { children.Add(child); return true; });
				foreach (var child in children)
				{
					Visit(child);
					if (found is not null) return;
				}
			}
		}

		/// <summary>步骤行展开/收起改变了行高：重测这一行，并让它的预览行停在原地。</summary>
		void InvalidateRow(Grid row, bool expanded)
		{
			if (body is null) return;
			ConversationTimeline.InvalidateMeasureTree(row);
			list?.InvalidateMeasure();
			// 栈布局靠内容测量更新组体高度，虚拟化列表靠 arrange 重测行高并重算 extent。
			KickBodyLayout();
			// 自动跟底（流式思考/最新组）优先：此时读者看的就是内容末尾，不应该把视口钉回标题。
			var following = viewport is not null && SmoothScroll.Find(viewport)?.IsFollowingGrowth == true;
			anchorRow = following ? null : row;
			anchorOffset = anchorRow is not null && viewport is not null ? row.Bounds.Y - viewport.Bounds.Y : 0;
			QueueClamp();
		}

		void QueueClamp()
		{
			if (anchorRow is null || viewport is null) return;
			// 行高只有测完才准。优先在 ScrollViewer 完成本轮排布时立即还原预览行：
			// ScrollChanged 在 arrange 末尾触发，早于 Render 回调，也早于会话锚点恢复。
			// 若这一轮没有偏移变化（例如只是行变高、extent 未变），ScrollChanged 不会触发，
			// 此时由同一帧的 Render 回调（ExecutionProcessView.SyncBodyMetrics）收尾，
			// 绝不留到下一次无关更新才应用——那是迟到的跳动。
			if (!reclampQueued)
			{
				reclampQueued = true;
				void OnScrollChanged()
				{
					viewport.ScrollChanged -= OnScrollChanged;
					reclampQueued = false;
					_pendingClamps.Remove(ApplyAnchorClamp);
					ApplyAnchorClamp();
				}
				viewport.ScrollChanged += OnScrollChanged;
			}
			QueuePendingClamp(ApplyAnchorClamp);
		}

		void ApplyAnchorClamp()
		{
			var anchor = anchorRow;
			var targetOffset = anchorOffset;
			anchorRow = null;
			if (body is null || viewport is null) return;
			viewport.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset);
			if (anchor is null || anchor.Bounds.Height <= 0 || !anchor.IsDescendantOf(viewport)) return;
			var delta = anchor.Bounds.Y - viewport.Bounds.Y - targetOffset;
			if (Math.Abs(delta) > 0.5)
				viewport.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset + delta);
		}

		void DestroyBody()
		{
			if (body is not { } content) return;
			body = null;
			list = null;
			stack = null;
			stackEntries.Clear();
			stackHosts.Clear();
			viewport = null;
			rows?.Reset();
			rows = null;
			fold.Content(collapsedBody);
			UiLifetime.Release(content);
		}

		void Update(IReadOnlyList<ChatEntry> next, bool isRunning)
		{
			members = next;
			runningGroup = isRunning;
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
			if (body is not null) SyncRows();
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
				SetExpandedWithoutFollowing(expander, true);
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

	/// <summary>
	/// 把过程条目切成「过程组 / 中间回复」。节点对象与成员缓冲按索引复用：
	/// 流式期每帧都会走这里，反复 new 列表与数组纯属给 GC 添活。
	/// </summary>
	private List<Node> Nodes(IReadOnlyList<ChatEntry> entries)
	{
		var nodes = _nodes;
		var used = 0;
		var members = _memberScratch;
		members.Clear();
		for (var i = 0; i < entries.Count; i++)
		{
			var entry = entries[i];
			if (entry.Role == ChatRole.Tool || entry.Thinking.Length > 0 || entry.IsThinking) members.Add(entry);
			else
			{
				FlushGroup();
				var node = Take($"interim/{Id(entry, i)}", group: false);
				node.Entries = [entry];
				node.Version++;
				used++;
			}
		}

		FlushGroup();
		// 多余的旧节点留着复用（下次组数变多时直接取），但不再参与本轮。
		for (var i = used; i < nodes.Count; i++) nodes[i].Version++;
		_nodeCount = used;
		return nodes;
		void FlushGroup()
		{
			if (members.Count == 0) return;
			var node = Take($"group/{Id(members[0], used)}", group: true);
			node.Buffer.Clear();
			node.Buffer.AddRange(members);
			// 成员数量与上一次相同就继续用同一份数组：稳定后这一步不再分配。
			if (node.Entries is not ChatEntry[] previous || previous.Length != node.Buffer.Count)
			{
				node.Entries = node.Buffer.ToArray();
			}
			else
			{
				for (var index = 0; index < previous.Length; index++) previous[index] = node.Buffer[index];
			}

			node.Version++;
			members.Clear();
			used++;
		}

		Node Take(string key, bool group)
		{
			if (used < nodes.Count)
			{
				var existing = nodes[used];
				existing.Key = key;
				existing.Group = group;
				return existing;
			}

			var created = new Node { Key = key, Group = group };
			nodes.Add(created);
			return created;
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
		private readonly List<ChatEntry> _items = [];

		public int Count => _items.Count;
		public Func<object?, object?>? KeySelector { get; } =
			static item => item is ChatEntry entry ? (object?)entry.EntryId ?? entry : null;
		public event Action<ItemsChange>? Changed;

		public object GetItem(int index) => _items[index];
		public string GetText(int index) => string.Empty;
		public void Invalidate() => Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, Count));

		/// <summary>丢弃内容但不通知：调用方正在销毁整个列表。</summary>
		public void Reset() => _items.Clear();

		/// <summary>
		/// 更新步骤列表。<paramref name="inPlace"/> 收集只原位更新、不向虚拟化器发通知的索引；
		/// 调用方随后直接重绑这些已实现行的内容。
		/// </summary>
		public void Set(IReadOnlyList<ChatEntry> items, List<int>? inPlace = null)
		{
			inPlace?.Clear();
			var previousCount = _items.Count;
			if (previousCount == 0)
			{
				_items.AddRange(items);
				Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, items.Count));
				return;
			}
			if (items.Count == previousCount)
			{
				// 只重绑真正变化的连续行段：把“第一处差异一直到末尾”整段作废会让
				// 后面未变化行的高度缓存回落成估算值，组体的滚动范围随之抖动。
				var runStart = -1;
				for (var index = 0; index < items.Count; index++)
				{
					var previous = _items[index];
					var next = items[index];
					if (ConversationTimeline.SameEntry(previous, next))
					{
						if (runStart >= 0)
						{
							Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, index - runStart));
							runStart = -1;
						}
						continue;
					}

					_items[index] = next;
					if (CanUpdateInPlace(previous, next))
					{
						inPlace?.Add(index);
						if (runStart >= 0)
						{
							Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, index - runStart));
							runStart = -1;
						}
					}
					else if (runStart < 0)
					{
						runStart = index;
					}
				}
				if (runStart >= 0) Changed?.Invoke(new ItemsChange(ItemsChangeKind.Replace, runStart, items.Count - runStart));
				return;
			}
			if (items.Count > previousCount)
			{
				// 追加新步骤时，前缀里也可能同时有流式行在增长。先把前缀变化拆成
				// 原位更新 / 局部 Replace，最后再发 Add：虚拟化器的 Add 会先扩高
				// 度缓存并重映射已实现行，若先发 Replace，它会因计数未同步而整表
				// ResetHeights，把前面所有已测行高打回估算值。
				var replacements = new List<ItemsChange>();
				var runStart = -1;
				for (var index = 0; index < previousCount; index++)
				{
					var previous = _items[index];
					var next = items[index];
					if (ConversationTimeline.SameEntry(previous, next)) { Flush(index); continue; }

					_items[index] = next;
					if (CanUpdateInPlace(previous, next))
					{
						inPlace?.Add(index);
						Flush(index);
					}
					else if (runStart < 0)
					{
						runStart = index;
					}
				}
				Flush(previousCount);

				for (var index = previousCount; index < items.Count; index++) _items.Add(items[index]);
				Changed?.Invoke(new ItemsChange(ItemsChangeKind.Add, previousCount, items.Count - previousCount));
				foreach (var replacement in replacements) Changed?.Invoke(replacement);
				return;

				void Flush(int end)
				{
					if (runStart < 0) return;
					replacements.Add(new ItemsChange(ItemsChangeKind.Replace, runStart, end - runStart));
					runStart = -1;
				}
			}
			_items.Clear();
			_items.AddRange(items);
			Changed?.Invoke(new ItemsChange(ItemsChangeKind.Reset, 0, items.Count));
		}

		/// <summary>
		/// 流式助手步骤有持久视图，能原地吸收新的思考/正文，包括“流式行 → 落盘后的最终行”
		/// （两者的 EntryId 会从临时的 live-assistant 变成会话条目 id，但时间戳与图片不变）。
		/// 其余步骤改变内容时仍要重绑：工具行的摘要与展开体在创建时固定，不能靠 Update 追加。
		/// </summary>
		private static bool CanUpdateInPlace(ChatEntry current, ChatEntry next) =>
			current.Role == ChatRole.Assistant && current.IsStreaming && next.Role == ChatRole.Assistant &&
			current.Timestamp == next.Timestamp && current.Images.SequenceEqual(next.Images);
	}

	/// <summary>
	/// 过程组/中间回复的节点。做成可变类是为了让 <see cref="Nodes"/> 在流式期原地复用：
	/// 每帧新建 Node + 成员数组 + 插值键，在长会话里就是持续的 GC 压力。
	/// </summary>
	private sealed class Node
	{
		public string Key = string.Empty;
		public IReadOnlyList<ChatEntry> Entries = [];
		public bool Group;
		// 复用时的成员缓冲；只在内容真的变化时才换新数组，稳定后就一直用同一份。
		internal readonly List<ChatEntry> Buffer = [];
		internal int Version;
	}
	private sealed record Slot(string Key, Border Host, Action<IReadOnlyList<ChatEntry>, bool> Update);
}
