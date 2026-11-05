using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed class ConversationScroll : IDisposable
{
	private const double EndThreshold = 40;
	// 明确离开末尾：必须高于近底带，弹簧/布局滞后到不了这里。
	private const double FarFromBottom = EndThreshold * 3.5;
	// 读者自己累计的上滚位移；与 PiDeck 的 AT_BOTTOM_TOLERANCE_PX 一致。
	private const double ReaderEscapePixels = 25;
	// 触控板 1–2 px 的抖动与真实滚轮刻度分开累计：前者只算短窗口，后者跨慢速手势累计。
	private const double ReaderJitterPixels = 2;
	private readonly ScrollViewer _view;
	private readonly SmoothScroll _smooth;
	private readonly Action<bool> _showLatest;
	private readonly ConversationTimeline? _timeline;
	private ConversationTimeline.ReadingPoint _readingPoint;
	private ConversationTimeline.ReadingPoint? _readingRestoreTarget;
	private bool _hasReadingPoint;
	private bool _readingRestoreQueued;
	private bool _restoringReadingPoint;
	private int _readingRestoreAttempts;
	private double _lastOffset;
	private double _lastMaximum;
	private bool _followQueued;
	private bool _disposed;
	private UIElement? _readingAnchor;
	private double _anchorY;
	private bool _anchorQueued;
	private bool _restoringAnchor;
	// 自己发起的「回到最新」跳跃：随后那次 ScrollChanged 不是读者的滚动意图。
	private bool _followingJump;
	private bool _extentSyncQueued;
	private double _upwardPixels;
	private long _lastUpwardInputAt;
	private bool _upwardJitter;
	private bool _followSuspended;
	// 一次布局夹取把读者带到末尾后，后续稳定帧不能把它当成「读者自己回到底部」而重新锁跟。
	private bool _clampBlockedFollow;

	public ConversationScroll(ScrollViewer view, Action<bool> showLatest, ConversationTimeline? timeline = null)
	{
		_view = view;
		_smooth = SmoothScroll.For(view);
		_showLatest = showLatest;
		_timeline = timeline;
		if (timeline is not null) timeline.SetFollowingProvider(() => Following);
		_lastOffset = view.VerticalOffset;
		_lastMaximum = _smooth.MaxVerticalOffset;
		view.ScrollChanged += OnScrollChanged;
		_smooth.VerticalInput += OnVerticalInput;
		_smooth.NestedInput += OnNestedInput;
		if (_smooth.VerticalBar is { } bar) bar.MouseDown += OnScrollBarPointer;
	}

	public bool Following { get; private set; } = true;

	/// Search results are a reading surface of their own: reaching their end must not silently
	/// re-lock tail following. An explicit "move to latest" clears the suspension.
	public bool FollowSuspended
	{
		get => _followSuspended;
		set
		{
			_followSuspended = value;
			if (!value) return;
			Following = false;
			_readingRestoreTarget = null;
			_readingRestoreAttempts = 0;
			_clampBlockedFollow = false;
			_upwardPixels = 0;
			_smooth.Stop();
		}
	}

	private void OnScrollBarPointer(MouseEventArgs _) => Browse();

	private void OnNestedInput(ScrollViewer nested) => NoteNestedWheel(nested);

	/// 嵌套视口（执行过程的组体）消费了一次滚轮：会话自己的输入通道看不见这次输入，
	/// 而跟底的每帧跳变会把读者正在读的组体持续往上顶（组下方的流式回答在增长）——
	/// 表现为「组体内滚轮缓慢/读的行总在往上跑」。读者仍贴着嵌套视口自己的末尾时
	/// （跟着最新步骤）与跟底同向，跟随不动；离开末尾读历史步骤则立即停追，
	/// 滚回末尾或点「回到最新」照常恢复。
	internal void NoteNestedWheel(ScrollViewer nested)
	{
		if (!Following) return;
		var smooth = SmoothScroll.Find(nested);
		if (smooth is null || smooth.MaxVerticalOffset - nested.VerticalOffset <= EndThreshold) return;
		Browse();
	}

	private void OnVerticalInput(double pixels)
	{
		_timeline?.CancelScrollTarget();
		_readingAnchor = null;
		// 新的读者输入就是新的阅读意图：上一次夹取留下的恢复目标到此为止。
		_readingRestoreTarget = null;
		_readingRestoreAttempts = 0;
		// 输入前的顶部行：第一格上滚就把估算收缩推到夹取时，后面这次 ScrollChanged
		// 已经看不到夹取前的阅读位置，只能用这里保存的点。
		RememberReadingPoint(allowFollowing: true);
		if (pixels < 0)
		{
			// 与 PiDeck 相同的两档判定：明确离开末尾时真实滚轮立即逃逸；仍贴着末尾时，
			// 只有读者自己累计的不止 1–2 px 抖动，才取消跟底。流式布局与触控板噪声都不能
			// 把「正在跟底」误判成「正在浏览」。
			var now = Environment.TickCount64;
			var upward = -pixels;
			var window = upward <= ReaderJitterPixels ? 250 : 2000;
			if (now - _lastUpwardInputAt > window) _upwardPixels = 0;
			_lastUpwardInputAt = now;
			_upwardPixels += upward;
			var distance = _smooth.MaxVerticalOffset - _smooth.TargetVerticalOffset;
			if ((upward > ReaderJitterPixels && distance > FarFromBottom) || _upwardPixels >= ReaderEscapePixels)
			{
				Following = false;
				_upwardJitter = false;
			}
			else
			{
				// The offset this input just moved is a layout notification away; keep the
				// geometry path from mistaking the same jitter for reader intent.
				_upwardJitter = true;
			}
		}
		else if (pixels > 0)
		{
			// 读者自己向下滚：之前那次夹取不再阻止「回到底部就重新跟底」。
			_clampBlockedFollow = false;
			_upwardPixels = 0;
			_upwardJitter = false;
			// 向下回到末尾附近才重新锁定跟随；浏览中途的下滚不算跟底指令。
			if (!_followSuspended && _smooth.MaxVerticalOffset - _smooth.TargetVerticalOffset <= EndThreshold)
			{
				Following = true;
				_upwardPixels = 0;
			}
		}
	}

	private void OnScrollChanged()
	{
		var offset = _view.VerticalOffset;
		var maximum = _smooth.MaxVerticalOffset;
		var offsetDelta = offset - _lastOffset;
		var maximumDelta = maximum - _lastMaximum;
		// 自己发起的跳跃与跟底动画都会改变偏移：那不是读者的滚动意图，不能用来推翻 Following。
		var ownMove = _followingJump || _smooth.IsAnimating;
		if (_followingJump) _followingJump = false;
		// A shrinking extent clamps the offset down to the new end. That is layout, not a
		// reader instruction: keep the previous intent instead of resuming follow.
		var clampedByShrink = maximumDelta < -0.5 && offset >= maximum - 0.5;
		if (clampedByShrink && !Following) _clampBlockedFollow = true;
		if (!ownMove && _readingAnchor is null && !_restoringAnchor && !_smooth.IsApplyingOffset)
		{
			if (!clampedByShrink && !_clampBlockedFollow)
			{
				if (maximum - offset <= 0.5 && offsetDelta >= -0.5)
				{
					// Reaching the end is always an instruction to follow, even when a virtualized
					// extent grew during the drag that brought the reader here.
					Following = !_followSuspended;
					_upwardPixels = 0;
				}
				else if (offsetDelta < -0.5 && maximumDelta <= 0.5 &&
					!(_upwardJitter && Environment.TickCount64 - _lastUpwardInputAt <= 250))
				{
					// Upward movement with a stable extent is reading intent. Extent growth in the
					// same frame is a virtualizer anchor correction (rows above re-measured); the
					// reader's own wheel, key and scrollbar gestures set the intent explicitly, so
					// layout must not silently cancel following here. A sub-threshold trackpad
					// jitter is still the same input, not a new reading position.
					Following = false;
				}
				else if (Math.Abs(maximumDelta) <= 0.5 && Math.Abs(offsetDelta) > 0.5)
				{
					// A manual move while the extent is stable: follow only if it settled at the end.
					Following = !_followSuspended && maximum - offset <= EndThreshold;
					if (Following) _upwardPixels = 0;
				}
				// Extent growth with the offset unchanged (or increased by anchor correction) leaves
				// the current intent alone. Content growth must not cancel a reader's follow request.
			}
		}
		_lastOffset = offset;
		_lastMaximum = maximum;
		// The affordance is for a detached reader. While following, a temporary layout lag is not
		// "new messages below" and must not make the button flicker during streaming.
		_showLatest(!Following && maximum - offset > 1);
		QueueAnchorRestore();
		// Arrange publishes the new extent after a message grows. Tracking it here avoids scrolling
		// against stale metrics in Render(), and keeps a browsing reader at the same offset.
		// 动画在跑时它每帧都会重读末端，这里不要再插一次跳转（会打断动画）。
		if (Following && !_smooth.IsApplyingOffset && !_smooth.IsAnimating && maximum - offset > 0.5)
		{
			QueueFollow();
		}
		// 放在最后：窗口无调度器时恢复会同步写偏移并重入 ScrollChanged，
		// 这里不能再把 _lastOffset 覆盖回夹取后的旧值。
		HandleReadingPoint(clampedByShrink);
	}

	private void HandleReadingPoint(bool clampedByShrink)
	{
		if (_timeline is null || _restoringReadingPoint) return;
		if (Following)
		{
			_readingRestoreTarget = null;
			return;
		}
		if (clampedByShrink && _hasReadingPoint && ReadingUpRecently())
		{
			// 夹取可能跨多轮布局：保留目标，后续排布再把它拉回同一行内位置。
			_readingRestoreTarget = _readingPoint;
			_readingRestoreAttempts = 0;
			// 夹回末尾后旧动画目标已落在新范围之外，先停掉再恢复阅读位置。
			_smooth.Stop();
			if (!_readingRestoreQueued) QueueReadingRestore(_readingPoint);
			return;
		}
		if (_readingRestoreTarget is { } target)
		{
			var current = _timeline.CaptureReadingPoint();
			if (string.Equals(current.EntryId, target.EntryId, StringComparison.Ordinal) &&
				Math.Abs(current.WithinRow - target.WithinRow) <= 1)
			{
				_readingRestoreTarget = null;
				_readingRestoreAttempts = 0;
				_readingPoint = current;
				_hasReadingPoint = true;
				return;
			}
			if (ReadingUpRecently() && _readingRestoreAttempts++ < 4)
			{
				RestoreReadingPoint(target);
				return;
			}
			_readingRestoreTarget = null;
			_readingRestoreAttempts = 0;
		}
		if (_readingAnchor is not null || _readingRestoreQueued) return;
		RememberReadingPoint(allowFollowing: false);
	}

	private void RememberReadingPoint(bool allowFollowing)
	{
		if (_timeline is null || _readingAnchor is not null) return;
		if (!allowFollowing && Following) return;
		var point = _timeline.CaptureReadingPoint();
		if (string.IsNullOrEmpty(point.EntryId)) return;
		_readingPoint = point;
		_hasReadingPoint = true;
	}

	private bool ReadingUpRecently() =>
		_upwardPixels > 0 &&
		(_smooth.IsAnimating || Environment.TickCount64 - _lastUpwardInputAt <= 750);

	private void QueueReadingRestore(ConversationTimeline.ReadingPoint point)
	{
		if (_timeline is null) return;
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			RestoreReadingPoint(point);
			return;
		}
		_readingRestoreQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_readingRestoreQueued = false;
			if (!_disposed) RestoreReadingPoint(point);
		});
	}

	private void RestoreReadingPoint(ConversationTimeline.ReadingPoint point)
	{
		if (_disposed || _timeline is null || Following) return;
		_restoringReadingPoint = true;
		try
		{
			_smooth.Stop();
			_timeline.RestoreReadingPoint(point);
		}
		finally
		{
			_restoringReadingPoint = false;
		}
		// 恢复后的实际顶部可能因夹取/回收略有偏差，后续手势从这里续接。
		var restored = _timeline.CaptureReadingPoint();
		if (string.IsNullOrEmpty(restored.EntryId)) return;
		_readingPoint = restored;
		_hasReadingPoint = true;
	}

	private void QueueFollow(int attempt = 0)
	{
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			JumpToEndKeepingIntent();
			return;
		}
		if (_followQueued) return;
		_followQueued = true;
		// ScrollChanged runs during Arrange. Moving a virtualized viewport there leaves its
		// already-arranged rows at the old offset; defer until the layout transaction ends.
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_followQueued = false;
			if (_disposed) return;
			// A wheel animation started before the reply grew would settle short of the new end.
			// Steer it to the end instead of abandoning follow because an animation is running.
			FollowAfterLayout();
			// 虚拟化时间线的末端要到行被测量后才成立：落位后若仍有差距，再排一次（有上限）。
			if (attempt < 3 && _smooth.MaxVerticalOffset - _smooth.TargetVerticalOffset > 0.5)
			{
				_view.InvalidateArrange();
				QueueFollow(attempt + 1);
			}
		});
	}

	/// Republishes the scroll extent once layout has settled. A row that grew during arrange updates
	/// the virtualizer's height cache after the owner published its metrics; without this the bar
	/// maximum stays at the pre-growth value until the reader scrolls.
	internal void SyncExtentAfterLayout() => _smooth.SyncExtent();

	private void QueueExtentSync()
	{
		if (_extentSyncQueued || !Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
		_extentSyncQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_extentSyncQueued = false;
			if (!_disposed) _smooth.SyncExtent();
		});
	}

	internal void FollowAfterLayout()
	{
		if (!Following) return;
		// 滑行还在进行时先让它跑完；落位后再补一次到真正的末端。
		if (_smooth.IsAnimating) return;
		JumpToEndKeepingIntent();
	}

	private void JumpToEndKeepingIntent()
	{
		_followingJump = true;
		_smooth.JumpToEnd();
	}

	public void PreserveAnchor(UIElement anchor)
	{
		_readingRestoreTarget = null;
		_readingRestoreAttempts = 0;
		_smooth.Stop();
		Following = false;
		_readingAnchor = anchor;
		_anchorY = anchor.Bounds.Y;
		QueueAnchorRestore();
	}

	private void QueueAnchorRestore()
	{
		if (_readingAnchor is null || _anchorQueued || !Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher) return;
		_anchorQueued = true;
		// Normal has higher priority than Layout in MewUI; restore only after layout settles.
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_anchorQueued = false;
			if (!_disposed) RestoreAnchor();
		});
	}

	internal void RestoreAnchor()
	{
		var anchor = _readingAnchor;
		_readingAnchor = null;
		if (anchor is null || !anchor.IsDescendantOf(_view)) return;
		var delta = anchor.Bounds.Y - _anchorY;
		if (Math.Abs(delta) < 0.5) return;
		_restoringAnchor = true;
		try { _view.SetScrollOffsets(_view.HorizontalOffset, _view.VerticalOffset + delta); }
		finally { _restoringAnchor = false; }
	}

	public void FollowContent()
	{
		// Ensure a fixed-size viewport republishes new content metrics after the next measure pass.
		_view.InvalidateArrange();
		if (!Following)
		{
			// 浏览时行高增长只改变滚动范围（滚动条长度与可滚到的末尾），阅读位置本身不动：
			// 布局落定后把虚拟化 extent 重新发布一次，滚动条不会停在增长前的旧范围上。
			QueueExtentSync();
			return;
		}
		// Jump once now for the common case, then ask for one more pass after layout. A virtualized
		// row can only publish its grown height during arrange, so the first jump may still land on
		// the previous extent.
		if (!_smooth.IsAnimating) JumpToEndKeepingIntent();
		QueueFollow();
	}

	/// 流式内容的后续帧请求「继续贴底」：读者仍停在末尾时才成立。
	/// 展开的动作本身由 <see cref="ScrollToLatest"/> 承接（重锁跟底）；而此后每帧一次的续跟
	/// 不能重置读者意图，否则展开着的思考只要还在流，会话就被钉在末尾、拖都拖不动。
	/// 真正的偏移收敛仍在 Render 阶段由 <see cref="FollowContent"/> 完成。
	public void ContinueFollowing()
	{
		if (!Following || _readingAnchor is not null) return;
		// 只排队，不在测量/排布中同步写偏移：续跟来自行视图的 Update。
		_view.InvalidateArrange();
		QueueFollow();
	}

	public void ScrollToLatest()
	{
		_timeline?.CancelScrollTarget();
		_readingRestoreTarget = null;
		_readingRestoreAttempts = 0;
		_clampBlockedFollow = false;
		_readingAnchor = null;
		_followSuspended = false;
		Following = true;
		_upwardPixels = 0;
		_upwardJitter = false;
		// 远端行还没被测量时，末端只是估算；先重排再滚，落位后由 QueueFollow 收敛到真正的末尾。
		_view.InvalidateArrange();
		_smooth.ScrollToEnd();
		// 有调度器时再排一次落位后的对齐；无窗口（测试）时交给动画本身。
		if (Application.IsRunning) QueueFollow();
	}

	public void Browse()
	{
		_timeline?.CancelScrollTarget();
		_readingRestoreTarget = null;
		_readingRestoreAttempts = 0;
		_clampBlockedFollow = false;
		_readingAnchor = null;
		Following = false;
		_upwardPixels = 0;
		_upwardJitter = false;
		_smooth.Stop();
	}

	public void Stop() => _smooth.Stop();

	public void Dispose()
	{
		_disposed = true;
		_readingAnchor = null;
		_view.ScrollChanged -= OnScrollChanged;
		_smooth.VerticalInput -= OnVerticalInput;
		_smooth.NestedInput -= OnNestedInput;
		if (_smooth.VerticalBar is { } bar) bar.MouseDown -= OnScrollBarPointer;
		_smooth.Dispose();
	}
}
