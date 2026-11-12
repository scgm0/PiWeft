using System.Runtime.InteropServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed class ExecutionProcessView
{
	private readonly Func<ChatEntry, UIElement> _create;
	private readonly TextFocusScrollGuard? _focus;
	private bool _metricsQueued;
	// 展开/收起步骤后「把预览行钉在原位」的收尾：在本帧布局之后立刻执行，
	// 不能等下一次无关的 ScrollChanged，那时行已移位，会看到一次迟到的跳动。
	private readonly List<Action> _pendingClamps = [];
	private readonly TextBlock _header = new();
	private readonly WrapPanel _stats = new();
	private readonly StackPanel _toolStat;
	private readonly StackPanel _reportStat;
	private readonly StackPanel _thinkingStat;
	private readonly StackPanel _interimStat;
	private readonly StackPanel _errorStat;
	private readonly TextBlock _toolCount = new();
	private readonly TextBlock _reportCount = new();
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
	private int _nodeCount;
	private ChatEntry _entry;
	private ExecutionProcess.Stats _statsSnapshot;
	private bool _statsValid;
	private double? _totalDuration;
	private bool _totalValid;
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
		_reportStat = Stat("git-branch", AppStyles.RoleInfo, _reportCount);
		_thinkingStat = Stat("sparkles", AppStyles.RoleThinking, _thinkingCount);
		_interimStat = Stat("message-square", AppStyles.RoleInfo, _interimCount);
		_errorStat = Stat("x", AppStyles.RoleError, _errorCount);
		_stats.Children(_toolStat, _reportStat, _thinkingStat, _interimStat, _errorStat);
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
			if (!expanded) return;
			Build();
			// 收起期间内容不参与视觉树，动画与秒表会停；展开后整棵内容重新测量，
			// 各控件的 MeasureContent 负责把它们重新挂回帧泵/计时器。
			ConversationTimeline.InvalidateMeasureTree(View);
		};
		Track("run", _fold, entry.IsStreaming);
		Update(entry);
	}

	public Border View { get; }

	/// <summary>组体内容长高时同一帧补跟底的次数，供测试确认这条路径真的走到了。</summary>
	internal int ContentSizeFollows { get; private set; }

	/// The step list only re-measures rows while arranging, and an ancestor that was already
	/// measure-dirty leaves the arrange clean — a row that grows (expanded step, streaming thinking)
	/// would then keep its old height and the body would neither grow nor scroll to it. Invalidate the
	/// arrange of every open body and republish the virtualized extent once layout has settled.
	private void KickBodyLayout()
	{
		foreach (var viewport in BodyViewports())
		{
			if (!viewport.IsVisible || viewport.Bounds.Height <= 0) continue;
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

	internal void SyncBodyMetrics()
	{
		foreach (var viewport in BodyViewports())
		{
			if (!viewport.IsVisible || viewport.Bounds.Height <= 0) continue;
			// Capture intent before SetScrollOffsets: a virtualized presenter can still expose
			// the previous extent for one pass, and the resulting clamp/ScrollChanged would
			// otherwise clear the tail-follow flag before we get a chance to follow the new end.
			viewport.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset);
			if (SmoothScroll.Find(viewport) is { IsFollowingGrowth: true } smooth) smooth.FollowGrowth();
		}
		RunPendingClamps();
	}

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

	/// 组体视口清单。每个组在 <see cref="CompleteBody" /> 时就知道自己的视口，不必每帧遍历整棵树：
	/// 流式期 <see cref="KickBodyLayout" /> / <see cref="SyncBodyMetrics" /> 每次都要跑，
	/// 遍历可视化树加逐节点 List 分配是纯开销。
	private readonly List<ScrollViewer> _bodyViewports = [];

	private List<ScrollViewer> BodyViewports() => _bodyViewports;
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
		// This runs for every streaming frame of every visible process group. The counts are
		// unchanged on most frames, so only format strings and touch the stat rows when a
		// category really changed.
		if (!_statsValid || stats != _statsSnapshot)
		{
			_statsValid = true;
			_statsSnapshot = stats;
			_toolCount.Text(stats.Tools.ToString());
			_reportCount.Text(stats.Reports.ToString());
			_thinkingCount.Text(stats.Thinking.ToString());
			_interimCount.Text(stats.Interim.ToString());
			_errorCount.Text(stats.Errors.ToString()).ErrorText();
			_toolStat.IsVisible(stats.Tools > 0);
			_reportStat.IsVisible(stats.Reports > 0);
			_thinkingStat.IsVisible(stats.Thinking > 0);
			_interimStat.IsVisible(stats.Interim > 0);
			_errorStat.IsVisible(stats.Errors > 0);
		}
		// 错误已在计数里显示，不再追加第二个红叉；运行中也不能预先打成功勾。
		_ok.IsVisible(!entry.IsStreaming && stats.Errors == 0 && stats.Stopped == 0);
		_running.IsVisible(entry.IsStreaming);
		// 收起时组头和步骤都看不见：不建树、不跟流。展开时 ExpandedChanged 会补一次 Build，
		// 放下期间累积的内容也在那一帧对齐。
		if (_fold.IsExpanded) Build();
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
		if (!_totalValid || duration != _totalDuration)
		{
			_totalValid = true;
			_totalDuration = duration;
			_total.Text(duration is { } value ? Strings.ExecutionTotal(value) : string.Empty);
		}
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
			// 先拆组体再释放行：组体的视口登记在 _bodyViewports 里，不拆会一直拖着
			// 已释放的行树（也会被每帧的 KickBodyLayout / SyncBodyMetrics 扫到）。
			slot.Retire?.Invoke();
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
		// 流式期推进到新组时自动换到最新组；收起期间没跑 Build，展开这一帧补上（_latestGroup 非空说明
		// 之前跟踪过运行态，不是首次展开历史过程）。
		if (latest != _latestGroup && latest is not null && (_entry.IsStreaming || _latestGroup is not null))
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
		// 小组用内容驱动的栈：展开一行，组体长高（上限 300）。
		// 步骤多到一屏放不下时改用虚拟化面板；内容必然超过 300，组体固定限高，
		// 免得估算行高停在折叠高度。
		const int stackLimit = 24;
		const double bodyMaxHeight = 300;
		var members = entries;
		var runningGroup = running;
		StepRows? rows = null;
		// 折叠时把组体换成它：Expander.Content 不接受 null，用一个零高度占位保持「已展开才建树」。
		var collapsedBody = new Border { Height = 0 };
		Border? body = null;
		VirtualRowsPanel<ChatEntry, Grid>? panel = null;
		StackPanel? stack = null;
		var stackEntries = new List<ChatEntry>();
		var stackHosts = new List<Grid>();
		// 组体的滚动视口：栈布局与虚拟化面板都直接持有一个 ScrollViewer。
		ScrollViewer? viewport = null;
		// 每个过程组各自持有追帧状态：一个视图里可能有多组（中间回复会把过程切成多段），
		// 共用一份标志会让后一组的重夹/露出被前一组吞掉。
		var reclampQueued = false;
		var switchQueued = false;
		// 自动展开的「正在运行」组跟住最新步骤；手动展开的组从头看。
		var followOnOpen = false;
		// 展开/收起步骤后把预览行固定在原屏幕位置，不滚向内容末尾。
		// 虚拟化行可能在重测过程中被换掉，因此同时记住步骤索引，布局后再解析当前宿主。
		Grid? anchorRow = null;
		double anchorOffset = 0;
		// The offset this clamp wrote last; anything else moved it (reader intent) → give up.
		double anchorApplied = double.NaN;
		var anchorIndex = -1;
		var anchorAttempts = 0;
		// A virtualized presenter may correct the viewport offset while a streaming row is
		// remeasured. Reassert the pre-layout follow intent in the Render-stage settle.
		var followAfterLayout = false;
		// 流式思考/正文只原位更新已实现的那一行：重建会把实测高度作废成估算值，
		// 组体 extent 先缩后涨，看起来就是进度跳变。
		var inPlaceRows = new List<int>();
		// 订阅组体内容的尺寸变化：流式思考/工具输出在测量里长高时，extent 已经发布，
		// 这里同一帧补跟底，新露出的一行才不会在视口底边被切掉一截。
		FrameworkElement? followedContent = null;
		// 内容未变（或可原位更新）的行：高度缓存按身份沿用，重排时由面板补偿坐标系平移。
		var keptRows = new List<int>();
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
		// 组头摘要/图标只依赖这一组成员。流式帧里只有正在运行的最新组会变；前面的
		// 历史组每次 Build 仍会走到这里，若不判重就会每帧重建 HashSet、拼接摘要并重设
		// 图标源。记录上一次的成员列表与末尾条目，稳定组直接跳过组头更新。
		// Node.Entries 会原地复用同一数组来避免每帧分配，因此不能保存「成员列表引用」做判重：
		// 列表内容变了引用仍然相同。记录数量与末尾条目的引用，才能识别原地重写的组。
		int headerCount = -1;
		ChatEntry? headerLast = null;
		bool headerRunning = false;
		// 运行组的实时详情只取决于最新工具的名字与参数。流式帧每帧都会重建成员对象，
		// 但参数字符串引用通常不变；缓存一次解析结果，避免每 16ms 对同一段 JSON 再 Parse。
		string? detailTool = null;
		string? detailArguments = null;
		int detailLength = -1;
		string? detailText = null;

		Track(key, fold, false, group: true);
		var host = new Border().Padding(new Thickness(2, 0, 0, 0)).Child(fold);
		var slot = new Slot(key, host, Update, DestroyBody);
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
			// 同时取消可能尚未执行的「布局后跟底」动作，否则展开工具后的下一帧
			// 仍可能被上一次流式思考的跟随意图拉回末尾。
			followAfterLayout = false;
			_pendingClamps.Remove(FollowAfterLayout);
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
			rows.Set(members, inPlaceRows, keptRows, out _);
			// 行数据归 StepRows 所有（它知道哪些行只是内容变化）；面板只负责按高度虚拟化。
			// 捕获局部副本：DestroyBody 会把 rows 置空，面板的 count/itemAt 委托不能跟着失效。
			var source = rows;
			panel = new VirtualRowsPanel<ChatEntry, Grid>(
				() => source.Count,
				source.ItemAt,
				StepRowHost,
				(host, entry, index) => BindStepRow(host, entry, index),
				host => UnbindStep(host))
			{
				Key = static entry => (object?)entry.EntryId ?? entry,
				Same = ConversationTimeline.SameEntry,
				// 折叠的步骤行基本是固定高度（图标 + 一行摘要），展开的那几行由实测校正。
				DefaultEstimate = 32,
				// 跟底时（组体停在末尾、看的是最新步骤）不能让「锚点上方长高」把偏移拉走：
				// 旧 presenter 也是 follow-end 优先于锚点校正。浏览态才补偿坐标系平移。
				PreserveReadingAnchor = () => viewport is null || SmoothScroll.Find(viewport) is not { IsFollowingGrowth: true },
			};
			// 组体与整段过程一样是「可滚动的小窗」：内层能滚就滚内层，内层到边界（或内容装得下）
			// 才把滚轮交还外层会话，由 SmoothScroll 在视口边界交接（ScrollCascade）。
			// 能走到虚拟化说明行数已经超过一屏；固定组体高度，滚轮与追帧都只作用在内层。
			viewport = new ScrollViewer().Content(panel).MaxHeight(bodyMaxHeight)
				.SmoothScrolling().NoHorizontalScroll();
			panel.Scroll = viewport;
			SmoothScroll.For(viewport).CompensateLayoutCorrections = true;
			CompleteBody(viewport);
		}

		void OnBodyContentSized(SizeChangedEventArgs _)
		{
			if (viewport is not { } target) return;
			if (followedContent is { } sized)
			{
				// 组体这一帧既移动又长高时，前面已经稳定的行只被场景当作「跟着帧平移」，
				// 一旦平移量算错，它们就停在旧坐标上，直到有东西让它重录（读者选一下、点一下）。
				// 这里让内容整棵重录一次：流式行本来每帧都重录，静态行靠它回到正确坐标。
				sized.InvalidateVisual();
			}
			if (SmoothScroll.Find(target) is not { IsFollowingGrowth: true } smooth) return;
			// 已经贴着末尾（内容没长高）就不用写偏移，免得在排布里多排一轮。
			if (smooth.MaxVerticalOffset - target.VerticalOffset <= 0.5) return;
			ContentSizeFollows++;
			smooth.FollowGrowth();
		}

		void CompleteBody(FrameworkElement content)
		{
			if (viewport is { } registered && !_bodyViewports.Contains(registered)) _bodyViewports.Add(registered);
			// 订阅的是视口里的内容（栈/虚拟化面板），不是视口自己：视口高度被 MaxHeight 钉住，
			// 内容长高时它不会变。
			var sized = (content as ScrollViewer)?.Content as FrameworkElement ?? content;
			if (!ReferenceEquals(followedContent, sized))
			{
				if (followedContent is { } previous) previous.SizeChanged -= OnBodyContentSized;
				followedContent = sized;
				if (Environment.GetEnvironmentVariable("PIWEFT_NO_BODY_FOLLOW") != "1") sized.SizeChanged += OnBodyContentSized;
			}
			// 组体是“最新步骤”的窗口：滚动条置底时持续跟随新步骤；用户上滚后暂停，
			// 回到底部（滚轮/键盘/拖动滚动条）自动重新锁底。
			// 组体是「最新步骤」的窗口：置底即跟随。这里不做抑制：栈/虚拟化切换、
			// 步骤行回收后的重建都只是布局，不是读者的阅读意图；恢复态在 Restore 里单独处理。
			if (viewport is not null) SmoothScroll.For(viewport).AutoFollowAtEnd = true;
			// 展开项标题在组体里吸顶：栈布局的行在建视口之前就已经绑好，这里补挂一次。
			if (viewport is { } bodyViewport)
				for (var index = 0; index < stackHosts.Count; index++) AttachSticky(stackHosts[index], stackHosts[index], bodyViewport);
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

		void RouteBodyWheel(MouseWheelEventArgs args)
		{
			if (args.Handled || viewport is null) return;
			// A wheel gesture is reader intent; don't let a still-pending expansion clamp
			// pull the viewport back to the old preview row afterwards.
			followAfterLayout = false;
			ClearAnchor();
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
			for (var index = 0; index < members.Count; index++) AppendStackRow(members[index], index);
		}

		void AppendStackRow(ChatEntry entry, int index)
		{
			if (stack is null) return;
			var row = StepRowHost();
			BindStepRow(row, entry, index);
			// 直接 Add：Fluent 的 Children(row) 每次都要给 params 数组分配一个一元数组。
			stack.Add(row);
			stackEntries.Add(entry);
			stackHosts.Add(row);
		}

		bool BindStepRow(Grid row, ChatEntry entry, int index) =>
			BindStep(row, entry, index, key, viewport, (expanded, expander) =>
			{
				// A tool detail is a reading action: keep its preview row pinned. A thinking
				// block, on the other hand, is streamed content and should keep following the
				// group tail; pinning it here would fight SmoothScroll on every growth frame.
				var preservePreview = entry.Role == ChatRole.Tool;
				if (expanded && preservePreview) StopGroupFollow();
				// 从吸顶标题收起：读者读的是这一项的开头，收起后标题应当落在视口顶部，
				// 不把已经滚走的行顶留在原处，否则标题会直接消失。
				var collapseFromPinned = !expanded && expander is StickyExpander { HeaderDisplaced: true };
				InvalidateRow(row, index, preservePreview, collapseFromPinned);
			});

		/// 返回这一帧的步骤内容是否变化：没变就不必作废组体排列。
		bool SyncStackRows()
		{
			if (stack is null) return false;
			if (members.Count > stackLimit)
			{
				// 流式更新可能恰好发生在会话列表的测量/排布里；此时换 presenter 会重建整棵组体，
				// 排到 Render 再做，避免在布局过程中重新种树。测试等无消息循环的场景直接切换。
				if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
				{
					SwitchToVirtualBody();
					return true;
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
				return false;
			}
			// 结构不一致时只搬差异那一段：流式期每追加一步就重建整块组体会把展开步骤里的
			// 焦点与选区一起丢掉，也把刚量好的行高打回估算值。
			var prefix = Math.Min(stackHosts.Count, members.Count);
			for (var index = 0; index < prefix; index++)
			{
				var left = stackEntries[index];
				var right = members[index];
				if (!ReferenceEquals(left, right) &&
					(left.Role != right.Role || left.ToolCallId != right.ToolCallId || left.EntryId != right.EntryId))
				{
					RebuildStack();
					return true;
				}
			}
			var changed = false;
			for (var index = 0; index < prefix; index++)
			{
				// BindStep 遇到相同内容会直接返回；流式更新则保留展开状态原地更新。
				// 注意：不能把循环变量 index 带进回调；展开回调在循环结束后才触发，会拿到越界索引。
				changed |= BindStepRow(stackHosts[index], members[index], index);
				stackEntries[index] = members[index];
			}
			for (var index = prefix; index < members.Count; index++)
			{
				AppendStackRow(members[index], index);
				changed = true;
			}
			// 尾部收缩：组内步骤一般在末尾增减，只释放多出来的行。
			for (var index = stackHosts.Count - 1; index >= members.Count; index--)
			{
				var row = stackHosts[index];
				stack?.Remove(row);
				UnbindStep(row);
				UiLifetime.Release(row);
				stackHosts.RemoveAt(index);
				stackEntries.RemoveAt(index);
				changed = true;
			}
			return changed;
		}

		void RebuildStack()
		{
			if (stack is null) return;
			stack.Clear();
			foreach (var row in stackHosts)
			{
				UnbindStep(row);
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

		void FollowAfterLayout()
		{
			if (!followAfterLayout) return;
			followAfterLayout = false;
			if (viewport is not null && SmoothScroll.Find(viewport) is { } smooth && !smooth.ReaderEscaped)
			{
				smooth.FollowGrowth();
			}
		}

		void SyncRows()
		{
			// A streaming row can emit a transient ScrollChanged while it is being remeasured,
			// which would otherwise clear the group's tail-follow intent. Remember it across the
			// update and reassert it after layout; a real wheel/scrollbar gesture still wins later
			// in the same frame.
			var following = viewport is not null && SmoothScroll.Find(viewport)?.IsFollowingGrowth == true;
			if (!following)
			{
				// The reader scrolled away between updates: do not let an old layout-follow
				// action pull the body back to the tail.
				followAfterLayout = false;
				_pendingClamps.Remove(FollowAfterLayout);
			}
			var changed = false;
			if (panel is not null && rows is not null)
			{
				inPlaceRows.Clear();
				keptRows.Clear();
				var anchor = panel.CaptureShiftAnchor();
				var previous = rows.Set(members, inPlaceRows, keptRows, out changed);
				// 内容与行数都没变时跳过：ApplyRows 会作废测量，流式期每帧都做等于白跑一遍布局。
				if (changed) panel.ApplyRows(previous, rows.Count, keptRows, anchor);
				// 原位更新的行不重建，但要让它们的子树重新测量；面板 Arrange 会重绑整段实现范围，
				// 这里补视口外的焦点固定行。
				for (var i = 0; i < inPlaceRows.Count; i++)
				{
					var index = inPlaceRows[i];
					if (index >= members.Count || RealizedRow(index) is not { } row) continue;

					ConversationTimeline.InvalidateMeasureTree(row);
					if (viewport is null || !row.IsDescendantOf(viewport)) BindStepRow(row, members[index], index);
				}
			}
			else changed = SyncStackRows();
			// 内容没变时组体的排列仍然有效：每帧作废一次会让整块行表重新 arrange，
			// 而流式期前面的历史组本来就不动（一顿 40 ms 一帧的推流里就是白烧）。
			if (!changed) return;
			if (following)
			{
				followAfterLayout = true;
				QueuePendingClamp(FollowAfterLayout);
			}
			// 流式思考/正文在组体里增长时同样要走 arrange 重测，否则行高停在展开时的那一版。
			if (body is not null) KickBodyLayout();
			if (following && viewport is not null && SmoothScroll.Find(viewport) is { } smooth)
			{
				smooth.FollowGrowth();
			}
		}

		// 面板持有 index→行宿主的映射，原位更新时直接取，不用遍历可视化树。
		Grid? RealizedRow(int index) => panel?.HostAt(index);

		void InvalidateRow(Grid row, int index, bool preservePreview, bool collapseFromPinned = false)
		{
			if (body is null) return;
			ConversationTimeline.InvalidateMeasureTree(row);
			panel?.InvalidateMeasure();
			// 栈布局靠内容测量更新组体高度，虚拟化列表靠 arrange 重测行高并重算 extent。
			KickBodyLayout();
			if (!preservePreview && !collapseFromPinned)
			{
				ClearAnchor();
				return;
			}
			// 工具正文的开合是阅读行为：即使滚动条正好停在末尾（几何上属于跟随态），也要把
			// 预览行钉在原屏幕位置，否则展开的那一步会被「贴住内容末尾」吞掉。
			// 流式思考/最新组的自动通道不钉锚：读者看的就是末尾，增长要贴住它（见上分支）。
			anchorRow = row;
			anchorIndex = index;
			anchorAttempts = 0;
			anchorApplied = double.NaN;
			anchorOffset = collapseFromPinned ? 0 : viewport is not null ? row.Bounds.Y - viewport.Bounds.Y : 0;
			QueueClamp();
		}

		void QueueClamp()
		{
			if (viewport is null || (anchorRow is null && anchorIndex < 0)) return;
			// 行高只有测完才准。ScrollChanged 在 arrange 末尾触发时先做一次；
			// Render 阶段的 SyncBodyMetrics 再做最终收尾，避免虚拟化行在后续 layout pass
			// 又被重测/替换后锚点已经提前清掉。
			if (!reclampQueued)
			{
				reclampQueued = true;
				void OnScrollChanged()
				{
					viewport.ScrollChanged -= OnScrollChanged;
					reclampQueued = false;
					ApplyAnchorClamp(settle: false);
				}
				viewport.ScrollChanged += OnScrollChanged;
			}
			QueuePendingClamp(ApplyAnchorClampFinal);
		}

		void ApplyAnchorClampFinal() => ApplyAnchorClamp(settle: true);

		void ApplyAnchorClamp(bool settle)
		{
			if (body is null || viewport is null)
			{
				ClearAnchor();
				return;
			}
			Grid? anchor = anchorRow;
			if (anchor is null && anchorIndex >= 0)
			{
				anchor = RealizedRow(anchorIndex);
				anchorRow = anchor;
			}
			if (anchor is null)
			{
				if (settle && anchorAttempts++ < 2)
				{
					QueuePendingClamp(ApplyAnchorClampFinal);
					KickBodyLayout();
				}
				else
				{
					ClearAnchor();
				}
				return;
			}

			// The offset moved without this clamp writing it: the reader (or another owner) took
			// over the viewport, so a late anchor correction would fight them.
			if (!double.IsNaN(anchorApplied) && Math.Abs(viewport.VerticalOffset - anchorApplied) > 1)
			{
				ClearAnchor();
				return;
			}
			viewport.SetScrollOffsets(viewport.HorizontalOffset, viewport.VerticalOffset);
			if (anchor.Bounds.Height <= 0 || !anchor.IsDescendantOf(viewport))
			{
				if (settle)
				{
					ClearAnchor();
				}
				return;
			}
			var delta = anchor.Bounds.Y - viewport.Bounds.Y - anchorOffset;
			if (Math.Abs(delta) > 0.5)
			{
				var target = viewport.VerticalOffset + delta;
				viewport.SetScrollOffsets(viewport.HorizontalOffset, target);
				anchorApplied = viewport.VerticalOffset;
			}
			else
			{
				anchorApplied = viewport.VerticalOffset;
			}
			if (!settle)
			{
				return;
			}

			// One frame may need a second pass when a virtualized row is measured/recreated
			// after the first clamp. Keep the anchor for one retry instead of dropping it.
			if (Math.Abs(delta) > 0.5 && anchorAttempts++ < 2)
			{
				QueuePendingClamp(ApplyAnchorClampFinal);
				KickBodyLayout();
				return;
			}
			ClearAnchor();
		}

		void ClearAnchor()
		{
			anchorRow = null;
			anchorIndex = -1;
			anchorAttempts = 0;
			anchorApplied = double.NaN;
			_pendingClamps.Remove(ApplyAnchorClampFinal);
		}

		void DestroyBody()
		{
			if (body is not { } content) return;
			if (followedContent is { } followed)
			{
				followed.SizeChanged -= OnBodyContentSized;
				followedContent = null;
			}
			if (viewport is { } retired) _bodyViewports.Remove(retired);
			body = null;
			panel?.DisposePanel();
			panel = null;
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
			var lastMember = next.Count > 0 ? next[^1] : null;
			var headerChanged = headerCount != next.Count || !ReferenceEquals(headerLast, lastMember) ||
				headerRunning != isRunning;
			headerCount = next.Count;
			headerLast = lastMember;
			headerRunning = isRunning;
			if (headerChanged)
			{
				if (isRunning)
				{
					icon.Source(AppIcons.Get(ExecutionProcess.GroupIcon(next), AppStyles.ColorForRole(AppStyles.RoleAccent)));
					label.Text(ExecutionProcess.RunningLabel(next));
					var latest = next.Count > 0 ? next[^1] : null;
					// 表头摘要只依赖参数；只有参数缺失时才退回正文。流式 bash 每来一段输出都换一次
					// TextLength，若也当作缓存键，会把同一段 command 每拍重新解析一遍 JSON。
					var textChanged = latest is { ToolArguments: not { Length: > 0 } } && latest.TextLength != detailLength;
					if (latest is null || latest.ToolName != detailTool ||
						!ReferenceEquals(latest.ToolArguments, detailArguments) || textChanged)
					{
						detailTool = latest?.ToolName;
						detailArguments = latest?.ToolArguments;
						detailLength = latest?.TextLength ?? -1;
						detailText = ExecutionProcess.RunningDetail(next);
					}
					separator.IsVisible(detailText is not null);
					detail.IsVisible(detailText is not null);
					if (detailText is { } text) detail.Text(text);
				}
				else
				{
					icon.Source(AppIcons.Get(ExecutionProcess.GroupIcon(next), AppStyles.ColorForRole(AppStyles.RoleSubtle)));
					var summary = ExecutionProcess.GroupSummary(next);
					label.Text(summary.Length > 0 ? summary : Strings.ProcessGroupAnalyzed);
					separator.IsVisible(false);
					detail.IsVisible(false);
				}
			}
			if (body is not null) SyncRows();
		}
	}

	/// 组内的步骤行：只为视口建摘要，展开时才建具体内容；折叠/滚出视口即释放。
	private Grid StepRowHost() => new Grid().Columns("22,*");

	/// <returns>这一步是否真的重绑了内容（决定组体要不要重排）。</returns>
	private bool BindStep(Grid host, ChatEntry entry, int index, string groupKey, ScrollViewer? bodyViewport,
		Action<bool, Expander> layoutChanged)
	{
		// 行宿主带着当前下标：面板按需回收/复用，宿主本身不记录身份；测试与诊断靠它定位行。
		host.Tag = index;
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

		if (content.Tag is ChatEntry previous && ConversationTimeline.SameEntry(previous, entry)) return false;
		if (content.Tag is ChatEntry { IsStreaming: true } streaming && entry.Role == ChatRole.Assistant &&
			streaming.Timestamp == entry.Timestamp && ConversationTimeline.SameItems(streaming.Images, entry.Images) &&
			content.Child is Border { Tag: AssistantMessageView assistant })
		{
			// 流式思考只更新既有视图，不重建行。
			assistant.Update(entry);
			content.Tag = entry;
			return true;
		}
		if (content.Tag is ChatEntry { IsStreaming: true } && entry.Role == ChatRole.Tool &&
			content.Child is Border { Tag: ToolMessageView tool } && tool.Update(entry))
		{
			// 工具步骤仍在输出：表头/秒表都就地更新，不重建行（图标只依赖工具名，不会变）。
			content.Tag = entry;
			return true;
		}
		// The key is only needed when this row's content is actually rebuilt; formatting it before
		// the equal/streaming fast paths allocated one string per visible step per streaming frame.
		var stepKey = $"{groupKey}/{Id(entry, index)}";
		if (content.Child is { } old) UiLifetime.Release(old);
		var view = _create(entry);
		content.Child(view);
		content.Tag = entry;
		// 虚拟化列表的行是延后创建的：那时组体视口已经就绪，可以在这一层直接挂上吸顶宿主。
		if (bodyViewport is { } body) AttachSticky(view, host, body);
		TrackStepStates(stepKey, view, layoutChanged);
		return true;
	}

	private static void AttachSticky(Element root, FrameworkElement rowHost, ScrollViewer body)
	{
		foreach (var expander in Expanders(root))
			if (expander is StickyExpander sticky) sticky.AttachBody(body, rowHost);
	}

	private static void UnbindStep(Grid host)
	{
		if (host.Children.Count < 2 || host.Children[1] is not Border content) return;
		if (content.Child is { } old) UiLifetime.Release(old);
		content.Child = null;
		content.Tag = null;
	}

	private void TrackStepStates(string stepKey, Element view, Action<bool, Expander>? layoutChanged = null)
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
				layoutChanged?.Invoke(expanded, expander);
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
			// 工具步骤仍在输出：表头/秒表都有就地更新路径，不重建行（图标只依赖工具名，不会变）。
			else if (previous is { IsStreaming: true } && value.Role == ChatRole.Tool &&
				content.Child is Border { Tag: ToolMessageView tool } && tool.Update(value)) { }
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

	/// 把过程条目切成「过程组 / 中间回复」。节点对象按索引复用、成员数组按数量复用：
	/// 流式期每帧都会走这里，反复 new 列表与数组纯属给 GC 添活。
	private List<Node> Nodes(IReadOnlyList<ChatEntry> entries)
	{
		var nodes = _nodes;
		var used = 0;
		var memberStart = 0;
		var memberCount = 0;
		// 成员只记区间，不再往一块成员缓冲里逐个搬：分组最后直接写进节点的数组。
		for (var i = 0; i < entries.Count; i++)
		{
			var entry = entries[i];
			// 回报也是过程组的一步：它撑满时间线会把正文挤走，收进组里默认只占一行。
			if (entry.Role is ChatRole.Tool or ChatRole.Report || entry.ThinkingLength > 0 || entry.IsThinking)
			{
				if (memberCount == 0) memberStart = i;
				memberCount++;
				continue;
			}

			FlushGroup();
			var node = Take(entry, i, group: false);
			// 中间回复行只有一个成员：复用同一份单元素数组，避免每帧新分配。
			if (node.Entries is ChatEntry[] { Length: 1 } single) single[0] = entry;
			else node.Entries = [entry];
			used++;
		}

		FlushGroup();
		_nodeCount = used;
		return nodes;
		void FlushGroup()
		{
			if (memberCount == 0) return;
			var node = Take(entries[memberStart], used, group: true);
			// 成员数量与上一次相同就继续用同一份数组：稳定后这一步不再分配。
			if (node.Entries is not ChatEntry[] members || members.Length != memberCount)
			{
				members = new ChatEntry[memberCount];
				node.Entries = members;
			}
			for (var offset = 0; offset < memberCount; offset++) members[offset] = entries[memberStart + offset];
			memberCount = 0;
			used++;
		}

		Node Take(ChatEntry anchor, int index, bool group)
		{
			Node node;
			if (used < nodes.Count)
			{
				node = nodes[used];
			}
			else
			{
				node = new Node();
				nodes.Add(node);
			}

			// 键只用于识别槽位。锚点的 EntryId 每帧都是重新格式化的新字符串，按值比较后
			// 沿用旧键，稳定的组/中间回复就不会每帧再分配一次插值字符串。
			if (node.Key.Length > 0 && node.Group == group && SameAnchor(node, anchor, index)) return node;
			node.Group = group;
			node.AnchorId = anchor.EntryId;
			node.AnchorRole = anchor.Role;
			node.AnchorTimestamp = anchor.Timestamp;
			node.AnchorIndex = index;
			node.Key = $"{(group ? "group" : "interim")}/{Id(anchor, index)}";
			return node;
		}

		static bool SameAnchor(Node node, ChatEntry anchor, int index) =>
			anchor.EntryId is { } id
				? string.Equals(node.AnchorId, id, StringComparison.Ordinal)
				: node.AnchorId is null && node.AnchorRole == anchor.Role &&
				  node.AnchorTimestamp == anchor.Timestamp && node.AnchorIndex == index;
	}

	private static StackPanel Stat(string icon, string role, TextBlock count)
	{
		count.FontSize(AppTheme.FontSizeMicro).SubtleText().VerticalAlignment(VerticalAlignment.Center);
		return new StackPanel().Orientation(Orientation.Horizontal).Spacing(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(AppIcons.Icon(icon, role, 11), count);
	}

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
			ChatRole.Assistant when entry.ThinkingLength > 0 || entry.IsThinking => "sparkles",
			ChatRole.Assistant => "message-square",
			ChatRole.Report => "git-branch",
			ChatRole.Error => "x",
			_ => "ellipsis",
		};

		internal static string RoleFor(ChatEntry entry) => entry.Role switch
		{
			ChatRole.Assistant when entry.ThinkingLength > 0 || entry.IsThinking => AppStyles.RoleThinking,
			ChatRole.Assistant => AppStyles.RoleInfo,
			ChatRole.Report => AppStyles.RoleInfo,
			ChatRole.Error => AppStyles.RoleSubtle,
			_ => AppStyles.RoleSubtle,
		};
	}

	private sealed class StepRows
	{
		private readonly List<ChatEntry> _items = [];
		private ChatEntry[] _snapshot = [];

		public int Count => _items.Count;

		public ChatEntry? ItemAt(int index) => (uint)index < (uint)_items.Count ? _items[index] : null;

		public void Reset() => _items.Clear();

		/// <summary>
		/// 更新步骤列表。<paramref name="inPlace"/> 收集只原位更新、不重建视图的索引；
		/// <paramref name="kept"/> 收集高度可以沿用的索引（内容未变或原位更新）。
		/// 返回改动前的快照，供面板按行身份迁移高度；<paramref name="changed"/> 为 false 时
		/// 内容与行数都没变，调用方可以整帧跳过面板更新。
		/// </summary>
		public RowsSnapshot<ChatEntry> Set(IReadOnlyList<ChatEntry> items, List<int> inPlace, List<int> kept, out bool changed)
		{
			inPlace.Clear();
			kept.Clear();
			var previous = Snapshot();
			var previousCount = _items.Count;
			changed = false;
			if (previousCount == items.Count)
			{
				for (var index = 0; index < items.Count; index++)
				{
					var current = _items[index];
					var next = items[index];
					if (ConversationTimeline.SameEntry(current, next))
					{
						kept.Add(index);
						continue;
					}
					changed = true;
					_items[index] = next;
					// 工具/流式助手步骤有持久视图，能原地吸收新的输出；其余改变内容时要重建。
					var inPlaceable = CanUpdateInPlace(current, next);
					if (inPlaceable) inPlace.Add(index);
					if (inPlaceable || ConversationTimeline.SameMetadataOnly(current, next)) kept.Add(index);
				}
				return previous;
			}

			changed = true;
			var common = Math.Min(previousCount, items.Count);
			for (var index = 0; index < common; index++)
			{
				var current = _items[index];
				var next = items[index];
				_items[index] = next;
				if (ConversationTimeline.SameEntry(current, next))
				{
					kept.Add(index);
					continue;
				}
				// 同一条判定可接两次调用：类型/流式状态的连续 && 链，重复一遍纯属白算。
				var inPlaceable = CanUpdateInPlace(current, next);
				if (inPlaceable || ConversationTimeline.SameMetadataOnly(current, next))
				{
					if (inPlaceable) inPlace.Add(index);
					kept.Add(index);
				}
			}
			if (items.Count > previousCount)
			{
				for (var index = previousCount; index < items.Count; index++) _items.Add(items[index]);
			}
			else if (items.Count < previousCount)
			{
				_items.RemoveRange(items.Count, previousCount - items.Count);
			}
			return previous;
		}

		/// <summary>复用同一块快照缓冲：组体行数不多，但流式期每帧都会走到这里。</summary>
		private RowsSnapshot<ChatEntry> Snapshot()
		{
			if (_snapshot.Length < _items.Count) _snapshot = new ChatEntry[Math.Max(8, _items.Count)];
			CollectionsMarshal.AsSpan(_items).CopyTo(_snapshot);
			return new RowsSnapshot<ChatEntry>(_snapshot, _items.Count);
		}

		/// 流式助手步骤有持久视图，能原地吸收新的思考/正文，包括“流式行 → 落盘后的最终行”
		/// （两者的 EntryId 会从临时的 live-assistant 变成会话条目 id，但时间戳与图片不变）。
		/// 工具步骤的流式输出同理：同一次调用每来一段就换一条 ChatEntry，只有正文在长；
		/// 表头只依赖参数（见 ToolMessageView.Update），原地重绑就能保住行高与秒表。
		private static bool CanUpdateInPlace(ChatEntry current, ChatEntry next) =>
			(current.Role == ChatRole.Tool && current.IsStreaming && next.Role == ChatRole.Tool &&
				next.IsStreaming && current.ToolCallId == next.ToolCallId) ||
			(current.Role == ChatRole.Assistant && current.IsStreaming && next.Role == ChatRole.Assistant &&
				current.Timestamp == next.Timestamp && ConversationTimeline.SameItems(current.Images, next.Images));
	}

	/// 过程组/中间回复的节点。做成可变类是为了让 <see cref="Nodes"/> 在流式期原地复用：
	/// 每帧新建 Node + 成员数组 + 插值键，在长会话里就是持续的 GC 压力。
	private sealed class Node
	{
		public string Key = string.Empty;
		public IReadOnlyList<ChatEntry> Entries = [];
		public bool Group;
		// 键的锚点（组的首个成员 / 中间回复本身）：用于按值判断键能否沿用。
		internal string? AnchorId;
		internal ChatRole AnchorRole;
		internal long AnchorTimestamp;
		internal int AnchorIndex;
	}
	private sealed record Slot(string Key, Border Host, Action<IReadOnlyList<ChatEntry>, bool> Update, Action? Retire = null);
}
