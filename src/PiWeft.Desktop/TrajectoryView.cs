using System.Globalization;
using System.Runtime.InteropServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// <summary>
/// 会话轨迹页（右侧面板的「轨迹」分段，对齐 PiDeck 的会话复盘）：
/// 顶部是轮数/记录数与被折叠记录入口，中间是四条泳道的概览，下面是按轮分组的账本，
/// 底部检查器显示选中记录的入参、输出与耗时。
/// </summary>
/// <remarks>
/// 数据只来自会话分支（<see cref="SessionEntry"/>）与聊天投影里还没落盘的流式尾行，
/// 不另开读取通道、不复制历史正文：摘要按前缀生成，全文只在检查器选中时提取。
/// 面板打开期间由 <see cref="RightPanel"/> 每秒调用一次 <see cref="Refresh"/>；入口签名没变时
/// 只推进「进行中」的秒表，不重建账本。
/// </remarks>
internal sealed class TrajectoryView : IDisposable
{
	private static readonly TrajectoryLane[] LaneOrder =
	[TrajectoryLane.Input, TrajectoryLane.Model, TrajectoryLane.Tools, TrajectoryLane.Process];

	private const double RowHeight = 28;

	private const int InspectorTextLimit = 20_000;

	private readonly Func<TrajectorySource> _source;
	private readonly ItemsControl _ledger;
	private readonly ScrollViewer _ledgerScroll;
	private readonly TextBlock _empty = new();
	private readonly StackPanel _inspectorBody = new();
	private readonly ScrollViewer _inspectorScroll;
	private readonly TrajectoryOverview _overview = new();
	private readonly TextBlock _turns = new();
	private readonly TextBlock _recordCount = new();
	private readonly Button _foldAll;
	private readonly Button _loadOlder;
	private readonly Dictionary<string, TrajectoryRecord> _byId = new(StringComparer.Ordinal);
	private readonly TrajectoryProjector _projector = new();
	private readonly List<LedgerRow> _ledgerRows = [];
	private readonly Dictionary<string, int> _rowIndexById = new(StringComparer.Ordinal);
	private readonly HashSet<int> _folded = [];
	private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
	/// <summary>当前实现的「进行中」轮头时长标签：每秒只改文本，不重建账本。</summary>
	private readonly Dictionary<int, (TextBlock Label, long StartedAt)> _liveDurations = [];

	private TrajectoryModel _model = TrajectoryModel.Empty;
	private SourceSignature? _signature;
	private InspectorSignature? _inspectorSignature;
	private string? _inspectedId;
	private string? _selectedId;
	private int _turnWindow = TrajectoryLedger.TurnWindowStep;
	private bool _foldInitialized;
	private bool _hasRendered;
	private bool _disposed;

	public TrajectoryView(Func<TrajectorySource> source)
	{
		_source = source;

		_turns.FontSize(AppTheme.FontSizeMicro).MutedText().VerticalAlignment(VerticalAlignment.Center);
		_recordCount.FontSize(AppTheme.FontSizeMicro).MutedText().VerticalAlignment(VerticalAlignment.Center);
		var dot = new TextBlock().Text("·").FontSize(AppTheme.FontSizeMicro).FaintText()
			.VerticalAlignment(VerticalAlignment.Center);

		_foldAll = LinkButton(Strings.TrajectoryFoldAll, ToggleAllTurns);
		_loadOlder = LinkButton(Strings.TrajectoryLoadOlder, LoadOlder);
		_loadOlder.IsVisible(false);

		var info = new StackPanel().Orientation(Orientation.Horizontal).Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				AppIcons.Icon("activity", AppStyles.RoleSubtle, 14),
				_turns,
				dot,
				_recordCount,
				_foldAll);
		var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2)
			.HorizontalAlignment(HorizontalAlignment.Right).VerticalAlignment(VerticalAlignment.Center)
			.Children(_loadOlder);
		var header = new Border()
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.BorderThickness(new Thickness(0, 0, 0, 1))
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).BorderSubtle)
			.Child(new Grid().Columns("*,Auto")
				.Children(info.Column(0), actions.Column(1)));

		_overview.HorizontalAlignment(HorizontalAlignment.Stretch);
		_overview.RecordSelected += id => Select(id, scroll: true);
		var laneLabels = new StackPanel().Spacing(0);
		foreach (var lane in LaneOrder)
		{
			laneLabels.Children(new TextBlock()
				.Text(LaneLabel(lane))
				.FontSize(AppTheme.FontSizeMicro)
				.SubtleText()
				.Height(TrajectoryOverview.LaneHeight)
				.VerticalAlignment(VerticalAlignment.Center));
		}

		var overview = new Border()
			.BorderThickness(new Thickness(0, 0, 0, 1))
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).BorderSubtle)
			.Child(new Grid()
				.Columns(GridLength.Pixels(40), GridLength.Stars(1))
				.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
				.Children(laneLabels.Column(0), _overview.Column(1)));

		// 账本只实现视口附近的行：一条会话可能有几百上千条记录，全部建控件会让打开轨迹变成
		// 一次几百毫秒的布局。固定行高（轮头/记录/折叠组同为 28）是这里唯一的排版约定。
		_ledger = new ItemsControl()
			.FixedHeightPresenter(RowHeight)
			.ItemPadding(Thickness.Zero)
			.BorderThickness(0)
			.Background(AppTheme.Transparent)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, AppTheme.Space2));
		_ledger.ItemTemplate = new DelegateTemplate<LedgerRow>(
			_ => CreateRowView(),
			(view, row, _, _) => BindRow((Button)view, row));
		ScrollViewer? ledgerScroll = null;
		((IVisualTreeHost)_ledger).VisitChildren(child =>
		{
			ledgerScroll = child as ScrollViewer;
			return false;
		});
		_ledgerScroll = ledgerScroll ?? throw new InvalidOperationException("The trajectory ledger requires a scroll viewport.");
		_ledgerScroll.SmoothScrolling().NoHorizontalScroll();
		_ledgerScroll.AutoHideScrollBars = true;

		_empty.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap)
			.HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Top)
			.Margin(new Thickness(AppTheme.Space3, AppTheme.Space6, AppTheme.Space3, 0))
			.IsVisible(false);

		_inspectorScroll = new ScrollViewer()
			.SmoothScrolling()
			.Content(_inspectorBody)
			.AutoVerticalScroll()
			.NoHorizontalScroll();
		_inspectorScroll.AutoHideScrollBars = true;

		var inspector = new Border()
			.Height(222)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.BorderThickness(new Thickness(0, 1, 0, 0))
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).BorderSubtle)
			.Child(_inspectorScroll);

		View = new Border().Child(new Grid().Rows("Auto,Auto,*,Auto")
			.Children(
				header.Row(0),
				overview.Row(1),
				new Grid().Children(_ledger, _empty).Row(2),
				inspector.Row(3)));

		ShowInspector(null);
		// 面板可能在构造后才被绑定数据源；这里先取一帧，避免首次切到轨迹分段时
		// 要等下一次秒表才看见内容。
		Refresh();
	}

	public UIElement View { get; }

	/// <summary>重新读取数据源；入口没变时只推进进行中的秒表。</summary>
	public void Refresh()
	{
		if (_disposed)
		{
			return;
		}

		TrajectorySource source;
		try
		{
			source = _source();
		}
		catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
		{
			// 会话正在关闭（切标签/关窗）时数据源可能已经释放：保持上一帧，不把异常抛给秒表。
			return;
		}

		var signature = SignatureOf(source);
		var previousSignature = _signature;
		var timingOnlyChange = previousSignature is { } previous &&
			previous.ThinkingTimingsRevision != signature.ThinkingTimingsRevision &&
			(previous with { ThinkingTimingsRevision = signature.ThinkingTimingsRevision }) == signature;
		if (_signature == signature && (_foldInitialized || _model.Turns.Count == 0))
		{
			// 入口没变：只推进进行中的轮头秒表与概览时间域，不重建虚拟列表。
			// 空会话的 _foldInitialized 还没置位，但内容确实没变，不必每秒空建一次。
			TickLiveDurations();
			return;
		}

		var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		if (timingOnlyChange)
		{
			// 条目先落盘、思考起止后到：最后一轮可能已被复用，尾重建补不到，强制全量一次。
			_projector.Invalidate();
		}

		_model = _projector.Project(source.Entries, source.LiveRows, now, source.Busy, source.ThinkingTimings);
		_byId.Clear();
		if (_model.Records is List<TrajectoryRecord> recordList)
		{
			foreach (var record in CollectionsMarshal.AsSpan(recordList))
			{
				_byId[record.Id] = record;
			}
		}
		else
		{
			foreach (var record in _model.Records)
			{
				_byId[record.Id] = record;
			}
		}

		// 构建成功后才记签名：构建抛错时下一次秒表会重试，而不是永久跳过。
		_signature = signature;

		if (!_foldInitialized)
		{
			// 空会话的首帧不初始化：等第一批轮次真正到来时再默认「只展开最后一轮」，
			// 否则新会话的所有轮都会以展开态出现。
			if (_model.Turns.Count > 0)
			{
				_foldInitialized = true;
				foreach (var index in TrajectoryLedger.DefaultFoldedTurns(_model.Turns))
				{
					_folded.Add(index);
				}
			}
		}
		else
		{
			_folded.RemoveWhere(index => index >= _model.Turns.Count);
		}

		// 窗口至少保底一步；选中的记录若落在窗口外，先把窗口扩到它所在的轮。
		var turnCount = Math.Max(1, _model.Turns.Count);
		_turnWindow = Math.Min(Math.Max(TrajectoryLedger.TurnWindowStep, _turnWindow), turnCount);
		if (_selectedId is { } selectedId)
		{
			if (_byId.TryGetValue(selectedId, out var selected))
			{
				// 只保证选中的轮在渲染窗口里；用户手动折叠它时不要在每次刷新时又展开。
				EnsureTurnInWindow(selected.TurnIndex);
			}
			else
			{
				_selectedId = null;
			}
		}

		var offset = _ledgerScroll.VerticalOffset;
		// 首次布局前 extent 还是 0，IsAtBottom 会把「刚打开」误判成「停在底部」；
		// 第一帧固定从轮头开始，之后才沿用读者的位置。
		var follow = _hasRendered && IsAtBottom();
		Render(keepScroll: true, offset, follow);
		_hasRendered = true;
	}

	/// <summary>视觉验收：打印轮数/记录数与当前渲染窗口，脚本据此断言轨迹真的建起来了。</summary>
	public void ReportForSmoke()
	{
		var folded = _model.Turns.Count(turn => _folded.Contains(turn.Index));
		var window = TrajectoryLedger.Window(_model.Turns, _turnWindow);
		var max = SmoothScroll.For(_ledgerScroll).MaxVerticalOffset;
		Console.WriteLine(
			$"piweft-desktop: trajectory turns={_model.Turns.Count} records={_model.Records.Count} " +
			$"folded={folded} window={window.Count} rows={_ledgerRows.Count} selected={_selectedId ?? "-"} " +
			$"ledger={_ledger.Bounds.Width:0}x{_ledger.Bounds.Height:0} offset={_ledgerScroll.VerticalOffset:0}/{max:0} " +
			$"turn0={(_model.Turns.Count > 0 ? $"{TrajectoryFormat.Time(_model.Turns[0].StartedAt)}..{TrajectoryFormat.Time(_model.Turns[0].EndedAt ?? 0)} dur={_model.Turns[0].DurationMs:0} live={_model.Turns[0].InFlight}" : "-")}");
	}

	/// <summary>视觉验收：选中第一条已完成的工具记录，检查器才有内容可看。</summary>
	public void SelectForSmoke()
	{
		// 选最后一轮里的工具记录：窗口仍是默认的尾部三轮，「加载更早记录」不会被选中动作撑开。
		var lastTurn = _model.Turns.Count > 0 ? _model.Turns[^1] : null;
		var target = lastTurn?.Records.FirstOrDefault(static record => record.Kind == TrajectoryKind.Tool && !record.InFlight)
			?? _model.Records.FirstOrDefault(static record => record.Kind == TrajectoryKind.Tool && !record.InFlight)
			?? _model.Records.FirstOrDefault();
		if (target is not null)
		{
			Select(target.Id, scroll: true);
		}
	}

	/// <summary>主题切换后重上色：角色文本走统一刷新，泳道/圆点由各自的 WithTheme 跟随。</summary>
	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors(View);
		_overview.InvalidateVisual();
	}

	public void Dispose()
	{
		_disposed = true;
		UiLifetime.Release(View);
	}

	private void Render(bool keepScroll, double offset = 0, bool follow = false)
	{
		_turns.Text(Strings.TrajectoryTurns(_model.Turns.Count));
		_recordCount.Text(Strings.TrajectoryRecords(_model.Records.Count));
		var allFolded = _model.Turns.Count > 0 && _model.Turns.All(turn => _folded.Contains(turn.Index));
		_foldAll.Content(new TextBlock()
			.Text(allFolded ? Strings.TrajectoryUnfoldAll : Strings.TrajectoryFoldAll)
			.FontSize(AppTheme.FontSizeMicro)
			.AccentText());
		_loadOlder.IsVisible(_model.Turns.Count > _turnWindow);
		_overview.SetData(_model.Records, _model.DomainStart, _model.DomainEnd, _selectedId);

		BuildLedgerRows();
		_empty.IsVisible(_ledgerRows.Count == 0);
		_ledger.IsVisible(_ledgerRows.Count > 0);
		// 重建会换掉所有行视图：先丢掉旧的进行中标签，等新行绑定时再登记。
		_liveDurations.Clear();
		_ledger.ItemsSource = ItemsView.Create(
			_ledgerRows,
			static row => row.Text,
			static row => row.Key);

		var selected = _selectedId is { } id && _byId.TryGetValue(id, out var found) ? found : null;
		// 进行中的一轮每秒会重渲染账本；检查器只有内容真的变了才重建，
		// 否则读者正在看的入参/结果会被每秒弹回顶部。
		var inspectorSignature = InspectorSignatureOf(selected);
		if (_inspectorSignature != inspectorSignature)
		{
			// 同一条记录（通常是在流式更新）保留检查器滚动位置；换选中项才回到顶部。
			var sameRecord = string.Equals(_inspectedId, selected?.Id, StringComparison.Ordinal);
			var inspectorOffset = sameRecord ? _inspectorScroll.VerticalOffset : 0;
			_inspectorSignature = inspectorSignature;
			_inspectedId = selected?.Id;
			ShowInspector(selected);
			ScheduleAfterLayout(() =>
			{
				var max = SmoothScroll.For(_inspectorScroll).MaxVerticalOffset;
				_inspectorScroll.SetScrollOffsets(0, Math.Clamp(inspectorOffset, 0, max));
			});
		}

		if (keepScroll)
		{
			RestoreScroll(offset, follow);
		}
	}

	private void BuildLedgerRows()
	{
		_ledgerRows.Clear();
		_rowIndexById.Clear();
		foreach (var turn in TrajectoryLedger.Window(_model.Turns, _turnWindow))
		{
			_ledgerRows.Add(new LedgerRow.Header(turn));
			if (_folded.Contains(turn.Index))
			{
				if (TrajectoryLedger.Peek(turn.Records) is { } peek)
				{
					AddRow(new LedgerRow.Entry(peek), peek.Id);
				}

				continue;
			}

			foreach (var item in TrajectoryLedger.Fold(turn.Records))
			{
				switch (item)
				{
					case TrajectoryLedgerItem.Record single:
						AddRow(new LedgerRow.Entry(single.Value), single.Value.Id);
						break;

					case TrajectoryLedgerItem.Group group:
						if (_expandedGroups.Contains(group.Key))
						{
							foreach (var record in group.Records)
							{
								AddRow(new LedgerRow.Entry(record), record.Id);
							}
						}
						else
						{
							_ledgerRows.Add(new LedgerRow.ToolGroup(group));
						}

						break;
				}
			}
		}
	}

	private void AddRow(LedgerRow row, string recordId)
	{
		_rowIndexById[recordId] = _ledgerRows.Count;
		_ledgerRows.Add(row);
	}

	private void TickLiveDurations()
	{
		if (_model.Turns.Count > 0)
		{
			var anyInFlight = false;
			foreach (var turn in _model.Turns)
			{
				if (turn.InFlight)
				{
					anyInFlight = true;
					break;
				}
			}

			if (anyInFlight)
			{
				var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
				if (now > _model.DomainEnd)
				{
					// 进行中的会话右端跟着 now 生长：概览的开区间保持可见。
					_model = _model with { DomainEnd = now };
					_overview.SetData(_model.Records, _model.DomainStart, _model.DomainEnd, _selectedId);
				}
			}
		}

		if (_liveDurations.Count == 0)
		{
			return;
		}

		var current = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		foreach (var (label, startedAt) in _liveDurations.Values)
		{
			label.Text(TrajectoryFormat.Duration(Math.Max(0, current - startedAt)));
		}
	}

	private void RestoreScroll(double offset, bool follow)
	{
		ScheduleAfterLayout(() =>
		{
			var max = SmoothScroll.For(_ledgerScroll).MaxVerticalOffset;
			_ledgerScroll.SetScrollOffsets(0, follow ? max : Math.Clamp(offset, 0, max));
		});
	}

	private bool IsAtBottom()
	{
		var max = SmoothScroll.For(_ledgerScroll).MaxVerticalOffset;
		return max <= 0 || _ledgerScroll.VerticalOffset >= max - 4;
	}

	private void ToggleAllTurns()
	{
		var allFolded = _model.Turns.Count > 0 && _model.Turns.All(turn => _folded.Contains(turn.Index));
		_folded.Clear();
		if (!allFolded)
		{
			foreach (var turn in _model.Turns)
			{
				_folded.Add(turn.Index);
			}
		}

		// 全局折叠/展开是一张总览动作：回到账本顶部（窗口第一轮），不要停在旧偏移的
		// 中段——内容缩短后旧偏移会被夹到底部，看起来像跳到了另一轮。
		Render(keepScroll: false);
		ScheduleAfterLayout(() => _ledgerScroll.SetScrollOffsets(0, 0));
	}

	private void ToggleTurn(int index)
	{
		if (!_folded.Add(index))
		{
			_folded.Remove(index);
		}

		Render(keepScroll: true, _ledgerScroll.VerticalOffset, IsAtBottom());
	}

	private void ToggleGroup(string key)
	{
		if (!_expandedGroups.Add(key))
		{
			_expandedGroups.Remove(key);
		}

		Render(keepScroll: true, _ledgerScroll.VerticalOffset, IsAtBottom());
	}

	private void LoadOlder()
	{
		if (_model.Turns.Count <= _turnWindow)
		{
			return;
		}

		var offset = _ledgerScroll.VerticalOffset;
		var before = _ledgerRows.Count;
		_turnWindow = Math.Min(_model.Turns.Count, _turnWindow + TrajectoryLedger.TurnWindowStep);
		Render(keepScroll: false);
		var added = _ledgerRows.Count - before;
		// 固定行高的列表里，新轮只会在顶部插入：按新增行数平移阅读位置。
		ScheduleAfterLayout(() =>
		{
			var max = SmoothScroll.For(_ledgerScroll).MaxVerticalOffset;
			_ledgerScroll.SetScrollOffsets(0, Math.Clamp(offset + added * RowHeight, 0, max));
		});
	}

	private void Select(string id, bool scroll)
	{
		if (!_byId.TryGetValue(id, out var record))
		{
			return;
		}

		_selectedId = id;
		// 行点击时这条记录已经在眼前（所在轮和工具组都展开着）；只有概览跳转才需要
		// 扩窗口 / 展开轮与组，避免每次点行都重扫整轮找分组。
		if (scroll)
		{
			RevealRecord(record);
		}

		var offset = _ledgerScroll.VerticalOffset;
		Render(keepScroll: true, offset, IsAtBottom());
		if (scroll)
		{
			ScheduleAfterLayout(() =>
			{
				if (_rowIndexById.TryGetValue(id, out var index))
				{
					_ledger.ScrollIntoView(index);
				}
			});
		}
	}

	/// <summary>只保证某一轮落在渲染窗口里；不改变用户对折叠状态的选择。</summary>
	private void EnsureTurnInWindow(int turnIndex)
	{
		if (turnIndex < 0 || turnIndex >= _model.Turns.Count)
		{
			return;
		}

		var first = Math.Max(0, _model.Turns.Count - _turnWindow);
		if (turnIndex < first)
		{
			_turnWindow = Math.Min(_model.Turns.Count, _model.Turns.Count - turnIndex);
		}
	}

	/// <summary>用户明确选中一条记录时：扩窗口、展开轮与所在工具组，让行真的出现在视口里。</summary>
	private void RevealRecord(TrajectoryRecord record)
	{
		EnsureTurnInWindow(record.TurnIndex);
		_folded.Remove(record.TurnIndex);

		// 选中的记录可能藏在一段被折叠的连续工具里：那一段必须先展开。
		foreach (var item in TrajectoryLedger.Fold(_model.Turns[record.TurnIndex].Records))
		{
			if (item is TrajectoryLedgerItem.Group group && group.Records.Any(candidate => candidate.Id == record.Id))
			{
				_expandedGroups.Add(group.Key);
			}
		}
	}

	private Button CreateRowView()
	{
		Button? view = null;
		view = new Button()
			.Height(RowHeight)
			.MinHeight(RowHeight)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.CornerRadius(0)
			.BorderThickness(0)
			.Padding(Thickness.Zero)
			.OnClick(() => Activate(view!.Tag as LedgerRow));
		return view;
	}

	private void BindRow(Button view, LedgerRow row)
	{
		if (view.Tag is LedgerRow.Header previous)
		{
			_liveDurations.Remove(previous.Turn.Index);
		}

		view.Tag = row;
		view.Content = new Border()
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space3, 0))
			.BorderThickness(row is LedgerRow.Header ? new Thickness(0) : new Thickness(0, 0, 0, 1))
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).BorderSubtle)
			.Child(BuildRowContent(row));
		view.StyleName(row is LedgerRow.Header
			? AppStyles.CardMutedButton
			: row is LedgerRow.Entry entry && entry.Record.Id == _selectedId
				? AppStyles.SelectedButton
				: AppStyles.GhostButton);
	}

	private void Activate(LedgerRow? row)
	{
		switch (row)
		{
			case LedgerRow.Header header:
				ToggleTurn(header.Turn.Index);
				break;

			case LedgerRow.Entry entry:
				Select(entry.Record.Id, scroll: false);
				break;

			case LedgerRow.ToolGroup group:
				ToggleGroup(group.Group.Key);
				break;
		}
	}

	private UIElement BuildRowContent(LedgerRow row) => row switch
	{
		LedgerRow.Header header => BuildTurnContent(header.Turn),
		LedgerRow.Entry entry => BuildRecordContent(entry.Record),
		LedgerRow.ToolGroup group => BuildGroupContent(group.Group),
		_ => new Grid(),
	};

	private UIElement BuildTurnContent(TrajectoryTurn turn)
	{
		var summary = TrajectoryLedger.Summarize(turn);
		var folded = _folded.Contains(turn.Index);
		var chevron = new GlyphElement()
			.Kind(folded ? GlyphKind.ChevronRight : GlyphKind.ChevronDown)
			.GlyphSize(2.5)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, glyph) => glyph.Foreground = AppTheme.For(theme).TextFaint);
		var title = new TextBlock()
			.Text(Strings.TrajectoryTurn(turn.Index + 1))
			.FontSize(AppTheme.FontSizeMicro)
			.FontWeight(FontWeight.SemiBold)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center);
		var left = new StackPanel().Orientation(Orientation.Horizontal).Spacing(5)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(chevron, title);

		var stats = new TextBlock()
			.Text(Strings.TrajectoryTurnStats(summary.Steps, summary.ToolCalls))
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Right);
		var hasTokens = summary.InputTokens + summary.OutputTokens > 0;
		var tokens = new TextBlock()
			.Text(hasTokens ? Strings.TrajectoryTokenPair(summary.InputTokens, summary.OutputTokens) : string.Empty)
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.IsVisible(hasTokens);
		var middle = new StackPanel().Orientation(Orientation.Horizontal).Spacing(8)
			.HorizontalAlignment(HorizontalAlignment.Right).VerticalAlignment(VerticalAlignment.Center)
			.Children(stats, tokens);

		var duration = new TextBlock()
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Margin(new Thickness(AppTheme.Space2, 0, 0, 0));
		if (turn.InFlight)
		{
			duration.Text(TrajectoryFormat.Duration(Math.Max(0, DateTimeOffset.Now.ToUnixTimeMilliseconds() - turn.StartedAt)));
			_liveDurations[turn.Index] = (duration, turn.StartedAt);
		}
		else
		{
			duration.Text(turn.DurationMs is { } measured ? TrajectoryFormat.Duration(measured) : Strings.TrajectoryUnknownDuration);
		}

		var grid = new Grid().Columns("Auto,*,Auto")
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(left.Column(0), middle.Column(1), duration.Column(2));

		return grid;
	}

	private UIElement BuildGroupContent(TrajectoryLedgerItem.Group group)
	{
		var tools = group.Records.Count;
		var running = group.Records.Any(static record => record.Status == "running");
		var failed = group.Records.Any(static record => record.IsError);
		var text = new TextBlock()
			.Text("… " + Strings.TrajectoryGroup(tools, tools))
			.FontSize(AppTheme.FontSizeMicro)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.TextWrapping(TextWrapping.NoWrap)
			.VerticalAlignment(VerticalAlignment.Center);
		if (failed)
		{
			text.ErrorText();
		}

		var marker = new Border()
			.Width(6)
			.Height(6)
			.CornerRadius(3)
			.Margin(new Thickness(0, 0, 8, 0))
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = failed
				? AppTheme.For(theme).Error
				: running ? AppTheme.For(theme).Info : AppTheme.For(theme).TextMuted);

		return new Grid().Columns("Auto,*")
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(marker.Column(0), text.Column(1));
	}

	private UIElement BuildRecordContent(TrajectoryRecord record)
	{
		var marker = BuildMarker(record);
		var label = new TextBlock()
			.Text(KindLabel(record))
			.FontSize(AppTheme.FontSizeMicro)
			.FontWeight(FontWeight.Medium)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.TextWrapping(TextWrapping.NoWrap)
			.VerticalAlignment(VerticalAlignment.Center)
			.Width(64)
			.Role(KindRole(record));
		var summary = new TextBlock()
			.Text(RowSummary(record))
			.FontSize(AppTheme.FontSizeMicro)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.TextWrapping(TextWrapping.NoWrap)
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0));
		if (record.Kind == TrajectoryKind.Tool)
		{
			summary.FontFamily(AppTheme.MonoFont);
		}

		var hasTokens = record.Usage is not null;
		var tokens = new TextBlock()
			.Text(record.Usage is { } usage
				? Strings.TrajectoryTokenPair(usage.Input, usage.Output)
				: string.Empty)
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0))
			.IsVisible(hasTokens);
		var time = new TextBlock()
			.Text(TimeLabel(record))
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Right);

		return new Grid().Columns("Auto,Auto,*,Auto,Auto")
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				marker.Column(0),
				label.Column(1),
				summary.Column(2),
				tokens.Column(3),
				time.Column(4));
	}

	private static UIElement BuildMarker(TrajectoryRecord record)
	{
		if (record.IsError || record.Kind == TrajectoryKind.Error)
		{
			// 错误行沿用设计契约的「2px 纵向错误色短线」。
			return new Border()
				.Width(2)
				.Height(16)
				.CornerRadius(1)
				.Margin(new Thickness(3, 0, 7, 0))
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, border) => border.Background = AppTheme.For(theme).Error);
		}

		return new Border()
			.Width(6)
			.Height(6)
			.CornerRadius(3)
			.Margin(new Thickness(1, 0, 9, 0))
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = LaneColor(AppTheme.For(theme), record.Lane));
	}

	/// <summary>
	/// 虚拟列表里的一行：轮头、单条记录或一段被折叠的连续工具。
	/// <see cref="Key"/> 给固定行高 presenter 当复用键，滚动与重建时少换容器。
	/// </summary>
	private abstract record LedgerRow(string Key, string Text)
	{
		public sealed record Header(TrajectoryTurn Turn)
			: LedgerRow($"turn:{Turn.Index}", Strings.TrajectoryTurn(Turn.Index + 1));

		public sealed record Entry(TrajectoryRecord Record)
			: LedgerRow(Record.Id, Record.Summary);

		public sealed record ToolGroup(TrajectoryLedgerItem.Group Group)
			: LedgerRow(Group.Key, Strings.TrajectoryGroup(Group.Records.Count, Group.Records.Count));
	}

	private static InspectorSignature InspectorSignatureOf(TrajectoryRecord? record) => record is null
		? default
		: new InspectorSignature(
			record.Id,
			record.Status,
			record.EndedAt,
			record.DurationMs,
			record.Summary,
			record.IsError,
			record.Live?.TextLength ?? 0,
			record.Live?.ThinkingLength ?? 0);

	/// <summary>检查器内容签名（值类型，不为每次重绘拼字符串）。</summary>
	private readonly record struct InspectorSignature(
		string? Id,
		string? Status,
		long? EndedAt,
		double? DurationMs,
		string? Summary,
		bool IsError,
		int LiveTextLength,
		int LiveThinkingLength);

	private void ShowInspector(TrajectoryRecord? record)
	{
		_inspectorBody.Clear();
		if (record is null)
		{
			_inspectorBody.Children(new TextBlock()
				.Text(Strings.TrajectoryInspectHint)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText()
				.TextWrapping(TextWrapping.Wrap)
				.Margin(new Thickness(0, AppTheme.Space2, 0, 0)));
			return;
		}

		var title = new StackPanel().Orientation(Orientation.Horizontal).Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				AppIcons.Icon(record.Kind == TrajectoryKind.Tool ? "wrench" : "activity", KindRole(record), 14),
				new TextBlock()
					.Text(record.Kind == TrajectoryKind.Tool && record.ToolName is { Length: > 0 } name ? name : KindLabel(record))
					.FontSize(AppTheme.FontSizeControl)
					.FontWeight(FontWeight.SemiBold)
					.PrimaryText());
		_inspectorBody.Children(title);

		var fields = new StackPanel().Spacing(1).Margin(new Thickness(0, AppTheme.Space2, 0, 0));
		var details = new List<(string Label, string Value, bool Mono)>();
		if (record.StartedAt > 0)
		{
			details.Add((Strings.TrajectoryFieldTime, TrajectoryFormat.Time(record.StartedAt), true));
		}

		details.Add((Strings.TrajectoryFieldDuration, record.InFlight
			? Strings.TrajectoryInFlight
			: record.DurationMs is { } measured ? TrajectoryFormat.Duration(measured) : Strings.TrajectoryUnknownDuration, true));
		if (record.Status is { Length: > 0 } status)
		{
			details.Add((Strings.TrajectoryFieldStatus, StatusLabel(status), false));
		}

		if (record.Kind == TrajectoryKind.Tool && record.ToolName is { Length: > 0 } toolName)
		{
			details.Add((Strings.TrajectoryFieldTool, toolName, true));
		}

		if (record.ToolCallId is { Length: > 0 } callId)
		{
			details.Add((Strings.TrajectoryFieldCallId, callId, true));
		}

		if (record.Provider is { Length: > 0 } || record.ModelId is { Length: > 0 })
		{
			details.Add((Strings.TrajectoryFieldModel, $"{record.Provider}/{record.ModelId}".Trim('/'), false));
		}

		if (record.ThinkingLevel is { Length: > 0 } level)
		{
			details.Add((Strings.TrajectoryFieldThinkingLevel, level, false));
		}

		if (record.CustomType is { Length: > 0 } customType)
		{
			details.Add((Strings.TrajectoryFieldCustomType, customType, true));
		}

		if (record.Usage is { } usage)
		{
			details.Add((Strings.TrajectoryFieldInputTokens, usage.Input.ToString(CultureInfo.InvariantCulture), true));
			details.Add((Strings.TrajectoryFieldOutputTokens, usage.Output.ToString(CultureInfo.InvariantCulture), true));
			if (usage.CacheRead > 0)
			{
				details.Add((Strings.TrajectoryFieldCacheRead, usage.CacheRead.ToString(CultureInfo.InvariantCulture), true));
			}

			if (usage.CacheWrite > 0)
			{
				details.Add((Strings.TrajectoryFieldCacheWrite, usage.CacheWrite.ToString(CultureInfo.InvariantCulture), true));
			}

			if (usage.Reasoning is > 0)
			{
				details.Add((Strings.TrajectoryFieldReasoningTokens, usage.Reasoning.Value.ToString(CultureInfo.InvariantCulture), true));
			}
		}

		if (record.TokensBefore is { } tokensBefore)
		{
			details.Add((Strings.TrajectoryFieldTokensBefore, tokensBefore.ToString(CultureInfo.InvariantCulture), true));
		}

		foreach (var (label, value, mono) in details)
		{
			fields.Children(Field(label, value, mono));
		}

		_inspectorBody.Children(fields);

		if (record.Kind == TrajectoryKind.Error)
		{
			// 错误原因比同一条助手消息可能残留的正文更重要：单独一块，复制拿到的是原始报错。
			var message = record.Detail is { Length: > 0 } detail ? detail : record.Summary;
			if (message.Length > 0)
			{
				_inspectorBody.Children(TextSection(Strings.TrajectoryKindError, message));
			}

			return;
		}

		var input = TrajectoryText.InputOf(record);
		var hasBody = false;
		if (input.Length > 0)
		{
			_inspectorBody.Children(TextSection(Strings.TrajectoryFieldInput, input));
			hasBody = true;
		}

		var liveOutputAdded = false;
		if (record.Live is { } live && record.Kind is TrajectoryKind.Assistant or TrajectoryKind.Thinking)
		{
			// 流式正文不整篇物化：显示只切前缀，复制时才取当前快照的全文。
			var source = record.Kind == TrajectoryKind.Thinking ? live.ThinkingSource : live.BodyText;
			var length = record.Kind == TrajectoryKind.Thinking ? live.ThinkingLength : live.TextLength;
			if (length > 0)
			{
				var preview = source.Slice(0, Math.Min(length, InspectorTextLimit));
				var label = record.Kind == TrajectoryKind.Thinking
					? Strings.TrajectoryFieldThinking
					: Strings.TrajectoryFieldOutput;
				_inspectorBody.Children(TextSection(label, preview, () => source.Slice(0, length), length > InspectorTextLimit));
				hasBody = true;
				liveOutputAdded = true;
			}
		}

		var output = !liveOutputAdded ? TrajectoryText.OutputOf(record) : string.Empty;
		if (output.Length > 0)
		{
			var label = record.Kind switch
			{
				TrajectoryKind.Thinking => Strings.TrajectoryFieldThinking,
				TrajectoryKind.SystemPrompt => Strings.TrajectoryFieldPrompt,
				TrajectoryKind.Tool => Strings.TrajectoryFieldResult,
				_ => Strings.TrajectoryFieldOutput,
			};
			_inspectorBody.Children(TextSection(label, output));
			hasBody = true;
		}
		else if (record.Detail is { Length: > 0 } detail &&
			record.Detail is not ("initial" or "update"))
		{
			_inspectorBody.Children(TextSection(Strings.TrajectoryFieldDetail, detail));
			hasBody = true;
		}

		if (!hasBody && record.Summary.Length > 0)
		{
			// 模型切换 / 思考档位 / 会话信息这类过程记录只有一行摘要：检查器也要能复制到它。
			_inspectorBody.Children(TextSection(Strings.TrajectoryFieldDetail, record.Summary));
		}
	}

	private static UIElement Field(string label, string value, bool mono)
	{
		var text = new TextBlock()
			.Text(value)
			.FontSize(AppTheme.FontSizeMicro)
			.PrimaryText()
			.TextWrapping(TextWrapping.Wrap);
		if (mono)
		{
			text.FontFamily(AppTheme.MonoFont);
		}

		return new Grid()
			.Columns(GridLength.Pixels(84), GridLength.Stars(1))
			.Margin(new Thickness(0, 1, 0, 1))
			.Children(
				new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).SubtleText().Column(0),
				text.Column(1));
	}

	private UIElement TextSection(string label, string text, Func<string>? fullText = null, bool truncated = false)
	{
		// 模型输出/系统提示可能是几百 KB：检查器只渲染前缀，复制按钮仍拿全文。
		// 不截断的话一次 TextBlock 测量就会把面板拖住。
		var display = !truncated && text.Length <= InspectorTextLimit
			? text
			: string.Concat(text.AsSpan(0, Math.Min(text.Length, InspectorTextLimit)), "\n", Strings.TrajectoryTextTruncated);
		var copy = AppIcons.Button("copy", Strings.TrajectoryCopy, () => Copy(fullText?.Invoke() ?? text), size: 12);
		copy.Width(24).Height(24);
		var header = new DockPanel().LastChildFill()
			.Children(copy.DockRight(),
				new TextBlock()
					.Text(label)
					.FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.SemiBold)
					.SubtleText()
					.VerticalAlignment(VerticalAlignment.Center));
		var body = new TextBlock()
			.Text(display)
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.MutedText()
			.TextWrapping(TextWrapping.Wrap);
		var scroll = new ScrollViewer()
			.Content(body)
			.AutoVerticalScroll()
			.NoHorizontalScroll()
			.MaxHeight(200);
		scroll.AutoHideScrollBars = true;
		var box = new Border()
			.StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2))
			.Margin(new Thickness(0, 0, 0, AppTheme.Space1))
			.Child(scroll);
		return new StackPanel()
			.Spacing(2)
			.Margin(new Thickness(0, AppTheme.Space3, 0, 0))
			.Children(header, box);
	}

	private void Copy(string text)
	{
		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		Toasts.Show(View, clipboard?.TrySetText(text) == true ? Strings.Copied : Strings.ClipboardUnavailable);
	}

	private static void ScheduleAfterLayout(Action action)
	{
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			if (Application.IsRunning)
			{
				action();
			}

			return;
		}

		dispatcher.BeginInvoke(DispatcherPriority.Render, action);
	}

	private static Button LinkButton(string text, Action action)
	{
		var label = new TextBlock()
			.Text(text)
			.FontSize(AppTheme.FontSizeMicro)
			.AccentText()
			.VerticalAlignment(VerticalAlignment.Center);
		return new Button()
			.StyleName(AppStyles.GhostButton)
			.Height(22)
			.MinHeight(22)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.VerticalAlignment(VerticalAlignment.Center)
			.Content(label)
			.OnClick(action);
	}

	private static SourceSignature SignatureOf(TrajectorySource source)
	{
		var last = source.Entries.Count > 0 ? source.Entries[^1] : null;
		var live = source.LiveRows.Count > 0 ? source.LiveRows[^1] : null;
		// 流式摘要通常在 2048 个原始字符内就能凑满 96 个可见字符；超过后不再因正文增长重建：
		// 一条很长的单轮会话从「每秒重建整轮」降成「只在落盘/工具结果变化时重建」。
		return new SourceSignature(
			source.Entries.Count,
			last?.Id,
			last?.Timestamp,
			source.LiveRows.Count,
			live?.Timestamp,
			live is null ? 0 : Math.Min(live.TextLength, 2048) / 64,
			live is null ? 0 : Math.Min(live.ThinkingLength, 2048) / 64,
			live?.ThinkingStartedAt,
			live?.ThinkingEndedAt,
			live?.IsThinking == true,
			source.Busy,
			source.ThinkingTimingsRevision);
	}

	/// <summary>数据源签名（值类型，不每秒分配字符串）：比较它决定是否重建账本。</summary>
	private readonly record struct SourceSignature(
		int EntryCount,
		string? LastEntryId,
		string? LastEntryTimestamp,
		int LiveRowCount,
		long? LiveTimestamp,
		int LiveTextBuckets,
		int LiveThinkingBuckets,
		long? LiveThinkingStartedAt,
		long? LiveThinkingEndedAt,
		bool LiveIsThinking,
		bool Busy,
		long ThinkingTimingsRevision);

	private static string LaneLabel(TrajectoryLane lane) => lane switch
	{
		TrajectoryLane.Input => Strings.TrajectoryLaneInput,
		TrajectoryLane.Tools => Strings.TrajectoryLaneTools,
		TrajectoryLane.Process => Strings.TrajectoryLaneProcess,
		_ => Strings.TrajectoryLaneModel,
	};
	internal static Color LaneColor(AppPalette palette, TrajectoryLane lane) => lane switch
	{
		// 输入是「纬线」本身：跟随主强调色；模型用信息色，工具是中性灰，过程与思考同属紫色语义。
		TrajectoryLane.Input => palette.Accent,
		TrajectoryLane.Model => palette.Info,
		TrajectoryLane.Tools => palette.TextMuted,
		_ => palette.Thinking,
	};

	private static string KindLabel(TrajectoryRecord record) => record.Kind switch
	{
		TrajectoryKind.User => record.IsInitialPrompt ? Strings.TrajectoryKindInitialPrompt : Strings.TrajectoryKindUser,
		TrajectoryKind.Assistant => Strings.TrajectoryKindAssistant,
		TrajectoryKind.Thinking => Strings.TrajectoryKindThinking,
		TrajectoryKind.Tool => Strings.TrajectoryKindTool,
		TrajectoryKind.Error => Strings.TrajectoryKindError,
		TrajectoryKind.SystemPrompt => record.Detail == "initial"
			? Strings.TrajectoryKindSystemPromptInitial
			: record.Detail == "update" ? Strings.TrajectoryKindSystemPromptUpdate : Strings.TrajectoryKindSystemPrompt,
		TrajectoryKind.ModelChange => Strings.TrajectoryKindModelChange,
		TrajectoryKind.ThinkingChange => Strings.TrajectoryKindThinkingChange,
		TrajectoryKind.Compaction => Strings.TrajectoryKindCompaction,
		TrajectoryKind.BranchSummary => Strings.TrajectoryKindBranchSummary,
		TrajectoryKind.Usage => Strings.TrajectoryKindUsage,
		TrajectoryKind.Custom => Strings.TrajectoryKindCustom,
		TrajectoryKind.SessionInfo => Strings.TrajectoryKindSessionInfo,
		TrajectoryKind.ContextEdit => Strings.TrajectoryKindContextEdit,
		TrajectoryKind.InternalPrompt => Strings.TrajectoryKindInternalPrompt,
		TrajectoryKind.Label => Strings.TrajectoryKindLabel,
		_ => Strings.TrajectoryKindUnknown,
	};

	private static string KindRole(TrajectoryRecord record) => record.Kind switch
	{
		TrajectoryKind.Thinking => AppStyles.RoleThinking,
		TrajectoryKind.Error => AppStyles.RoleError,
		TrajectoryKind.Tool => AppStyles.RoleMuted,
		TrajectoryKind.SystemPrompt => AppStyles.RoleInfo,
		TrajectoryKind.Assistant or TrajectoryKind.User => AppStyles.RolePrimary,
		_ => AppStyles.RoleSubtle,
	};

	private static string RowSummary(TrajectoryRecord record)
	{
		if (record.Summary.Length == 0)
		{
			return Strings.TrajectoryUnknownDuration;
		}

		return record.Kind == TrajectoryKind.Tool && record.ToolName is { Length: > 0 } name
			? $"{name} · {record.Summary}"
			: record.Summary;
	}

	private static string TimeLabel(TrajectoryRecord record)
	{
		if (record.InFlight)
		{
			return Strings.TrajectoryInFlight;
		}

		return record.DurationMs is { } measured
			? TrajectoryFormat.Duration(measured)
			: TrajectoryFormat.Clock(record.StartedAt);
	}

	private static string StatusLabel(string status) => status switch
	{
		"running" => Strings.TrajectoryStatusRunning,
		"done" or "stop" => Strings.TrajectoryStatusDone,
		"error" => Strings.TrajectoryStatusError,
		"pending" => Strings.TrajectoryStatusPending,
		"length" => Strings.TrajectoryStatusLength,
		"toolUse" => Strings.TrajectoryStatusToolUse,
		"aborted" => Strings.TrajectoryStatusAborted,
		"deferred" => Strings.TrajectoryStatusDeferred,
		"stopped" => Strings.TrajectoryStatusStopped,
		_ => status,
	};
}

/// <summary>
/// 概览泳道：四条 lane 上按时间布刻度，点击刻度选中对应记录。
/// 与 PiDeck 的轨迹概览同义；只画数据，不建控件（几百条记录也不会变成几百个可命中的按钮）。
/// </summary>
internal sealed class TrajectoryOverview : FrameworkElement
{
	public const double LaneHeight = 15;

	private static readonly TrajectoryLane[] LaneOrder =
	[TrajectoryLane.Input, TrajectoryLane.Model, TrajectoryLane.Tools, TrajectoryLane.Process];

	private readonly List<Bucket> _buckets = [];
	private readonly List<TrajectoryRecord>[] _laneRecords = [[], [], [], []];
	private IReadOnlyList<TrajectoryRecord> _records = [];
	private TrajectoryRecord? _selected;
	private long _domainStart;
	private long _domainEnd;
	private string? _selectedId;

	public TrajectoryOverview()
	{
		Cursor = CursorType.Hand;
		Height = LaneHeight * LaneOrder.Length;
	}

	/// <summary>点了某个刻度：参数是代表记录的 id。</summary>
	public event Action<string>? RecordSelected;

	public void SetData(IReadOnlyList<TrajectoryRecord> records, long domainStart, long domainEnd, string? selectedId)
	{
		// 记录列表在模型重建时才换引用：选中/折叠导致的重复 SetData 不重新分 lane、不重排。
		var dataChanged = !ReferenceEquals(_records, records);
		if (dataChanged)
		{
			_records = records;
			foreach (var lane in _laneRecords)
			{
				lane.Clear();
			}

			if (records is List<TrajectoryRecord> recordList)
			{
				foreach (var record in CollectionsMarshal.AsSpan(recordList))
				{
					_laneRecords[(int)record.Lane].Add(record);
				}
			}
			else
			{
				foreach (var record in records)
				{
					_laneRecords[(int)record.Lane].Add(record);
				}
			}

			foreach (var lane in _laneRecords)
			{
				lane.Sort(static (left, right) => left.StartedAt.CompareTo(right.StartedAt));
			}

			_buckets.Clear();
		}

		if (dataChanged || !string.Equals(_selectedId, selectedId, StringComparison.Ordinal))
		{
			_selectedId = selectedId;
			_selected = null;
			if (selectedId is not null)
			{
				foreach (var record in records)
				{
					if (string.Equals(record.Id, selectedId, StringComparison.Ordinal))
					{
						_selected = record;
						break;
					}
				}
			}
		}

		_domainStart = domainStart;
		_domainEnd = domainEnd;
		InvalidateVisual();
	}

	protected override Size MeasureContent(Size availableSize) =>
		new(double.IsFinite(availableSize.Width) ? availableSize.Width : 320, LaneHeight * LaneOrder.Length);

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnMouseDown(MouseEventArgs args)
	{
		if (_records.Count == 0)
		{
			return;
		}

		var position = args.GetPosition(this);
		var index = Math.Clamp((int)(position.Y / LaneHeight), 0, LaneOrder.Length - 1);
		var lane = LaneOrder[index];
		var buckets = BuildBuckets(lane);
		Bucket? nearest = null;
		var distance = double.MaxValue;
		foreach (var bucket in buckets)
		{
			var delta = Math.Abs(XOf(bucket.X, Bounds.Width) - position.X);
			if (delta >= distance)
			{
				continue;
			}

			distance = delta;
			nearest = bucket;
		}

		if (nearest is { } hit && distance <= 8)
		{
			args.Handled = true;
			RecordSelected?.Invoke(hit.RecordId);
		}
	}

	protected override void OnRender(IGraphicsContext context)
	{
		var box = Bounds;
		if (box.Width < 16 || box.Height < LaneHeight)
		{
			return;
		}

		var palette = AppTheme.For(Theme);
		var scale = context.DpiScale > 0 ? context.DpiScale : 1;
		var tickWidth = Math.Max(1 / scale, Snap(3, scale));
		var tickHeight = Math.Max(1 / scale, Snap(6, scale));
		var ringPadding = Math.Max(1 / scale, Snap(1, scale));
		context.FillRoundedRectangle(box, AppTheme.RadiusXs, AppTheme.RadiusXs, palette.CardBackgroundAlt.WithAlpha(0x66));
		for (var index = 1; index < LaneOrder.Length; index++)
		{
			var y = Snap(box.Y + index * LaneHeight, scale);
			context.DrawLine(new Point(box.X + 2, y), new Point(box.Right - 2, y), palette.BorderSubtle.WithAlpha(0x66), 1, pixelSnap: true);
		}

		if (SelectedRecord() is { } selected)
		{
			var x = Snap(box.X + XOf(RatioOf(selected.StartedAt), box.Width), scale);
			context.DrawLine(new Point(x, box.Y + 2), new Point(x, box.Bottom - 2), palette.Accent.WithAlpha(0x40), 1, pixelSnap: true);
		}

		if (_records.Count == 0)
		{
			return;
		}

		for (var index = 0; index < LaneOrder.Length; index++)
		{
			var lane = LaneOrder[index];
			var center = box.Y + index * LaneHeight + LaneHeight / 2;
			var color = TrajectoryView.LaneColor(palette, lane);
			var buckets = BuildBuckets(lane);
			// 先画普通刻度，再画选中框：选中框不能被同 lane 后画的刻度压掉右边框。
			foreach (var bucket in buckets)
			{
				if (bucket.Selected)
				{
					continue;
				}

				// 小刻度按设备像素对齐并画方角：3×6 DIP 的圆角矩形在 1x/2x 屏上都会被
				// 抗锯齿糊成一团，轨迹概览看起来就“发虚”。
				var left = Snap(box.X + XOf(bucket.X, box.Width), scale);
				var top = Snap(center - tickHeight / 2, scale);
				context.FillRectangle(new Rect(left, top, tickWidth, tickHeight), color);
			}

			foreach (var bucket in buckets)
			{
				if (!bucket.Selected)
				{
					continue;
				}

				// 选中框由「白色外框 + 1 DIP 底色间隔 + 刻度本体」组成：
				// 输入泳道本身是近白色，只有外框没有间隔时右边框会和刻度糊在一起。
				var left = Snap(box.X + XOf(bucket.X, box.Width), scale);
				var top = Snap(center - tickHeight / 2, scale);
				context.FillRectangle(
					new Rect(left - ringPadding * 2, top - ringPadding * 2,
						tickWidth + ringPadding * 4, tickHeight + ringPadding * 4),
					palette.TextPrimary);
				context.FillRectangle(
					new Rect(left - ringPadding, top - ringPadding, tickWidth + ringPadding * 2, tickHeight + ringPadding * 2),
					palette.CardBackgroundAlt);
				context.FillRectangle(new Rect(left, top, tickWidth, tickHeight), color);
			}
		}
	}

	/// <summary>把 DIP 坐标对齐到设备像素，避免 1px 线/小刻度被抗锯齿抹成两像素灰边。</summary>
	private static double Snap(double value, double scale) =>
		Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;

	private TrajectoryRecord? SelectedRecord() => _selected;

	private double RatioOf(long at)
	{
		var span = Math.Max(1, _domainEnd - _domainStart);
		return Math.Clamp((at - _domainStart) / (double)span, 0, 1);
	}

	/// <summary>比例 → 轨道内横坐标（左右各留 8 DIP，给选中框的外框留出空间）。</summary>
	private static double XOf(double ratio, double width) => 8 + ratio * Math.Max(1, width - 16);

	/// <summary>按 lane + 像素桶聚合：概览窄的时候不因为记录多而把点击目标压到 1 DIP 以下。</summary>
	private List<Bucket> BuildBuckets(TrajectoryLane lane)
	{
		_buckets.Clear();
		var width = Math.Max(1, Bounds.Width - 8);
		// 刻度间距至少 5 DIP：方角 3 DIP 的刻度之间留出 2 DIP，1x 屏也不会粘成一片。
		var perLane = Math.Max(1, Math.Min(160, (int)(width / 5)));
		var laneRecords = _laneRecords[(int)lane];
		if (laneRecords.Count == 0)
		{
			return _buckets;
		}

		var count = Math.Min(perLane, laneRecords.Count);
		for (var index = 0; index < count; index++)
		{
			var from = (int)(index * (long)laneRecords.Count / count);
			var to = Math.Max(from + 1, (int)((index + 1) * (long)laneRecords.Count / count));
			to = Math.Min(to, laneRecords.Count);
			var representative = laneRecords[to - 1];
			_buckets.Add(new Bucket(representative.Id, representative.StartedAt, RatioOf(representative.StartedAt),
				to - from, representative.Id == _selectedId));
		}

		// 选中项没有落在任何桶的代表上时补一格：点击概览永远能找到当前记录。
		if (_selected is { } selected && selected.Lane == lane &&
			!_buckets.Exists(bucket => bucket.RecordId == selected.Id))
		{
			_buckets.Add(new Bucket(selected.Id, selected.StartedAt, RatioOf(selected.StartedAt), 1, true));
		}

		return _buckets;
	}

	private readonly record struct Bucket(string RecordId, long StartedAt, double X, int Count, bool Selected);
}
