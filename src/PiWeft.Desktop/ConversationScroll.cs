using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Owns conversation follow state; layout growth is separate from the reader's scroll intent.</summary>
internal sealed class ConversationScroll : IDisposable
{
	private const double EndThreshold = 40;
	private readonly ScrollViewer _view;
	private readonly SmoothScroll _smooth;
	private readonly Action<bool> _showLatest;
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

	public ConversationScroll(ScrollViewer view, Action<bool> showLatest)
	{
		_view = view;
		_smooth = SmoothScroll.For(view);
		_showLatest = showLatest;
		_lastOffset = view.VerticalOffset;
		_lastMaximum = _smooth.MaxVerticalOffset;
		view.ScrollChanged += OnScrollChanged;
		_smooth.VerticalInput += OnVerticalInput;
	}

	public bool Following { get; private set; } = true;

	private void OnVerticalInput(double notches)
	{
		_readingAnchor = null;
		// Disable follow on the first upward wheel event, before an animation frame or stream update.
		Following = notches > 0 && _smooth.MaxVerticalOffset - _smooth.TargetVerticalOffset <= EndThreshold;
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
		if (!ownMove && _readingAnchor is null && !_restoringAnchor && !_smooth.IsApplyingOffset)
		{
			// A shrinking extent clamps the offset down to the new end. That is layout, not a
			// reader instruction: keep the previous intent instead of resuming follow.
			var clampedByShrink = maximumDelta < -0.5 && offset >= maximum - 0.5;
			if (!clampedByShrink)
			{
				if (maximum - offset <= 0.5 && offsetDelta >= -0.5)
				{
					// Reaching the end is always an instruction to follow, even when a virtualized
					// extent grew during the drag that brought the reader here.
					Following = true;
				}
				else if (offsetDelta < -0.5)
				{
					// Any upward movement is reading intent, whatever the extent did at the same time.
					Following = false;
				}
				else if (Math.Abs(maximumDelta) <= 0.5 && Math.Abs(offsetDelta) > 0.5)
				{
					// A manual move while the extent is stable: follow only if it settled at the end.
					Following = maximum - offset <= EndThreshold;
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

	/// <summary>布局落定后把偏移对齐到最新末端；虚拟化时间线的末端只有测量后才成立。</summary>
	internal void FollowAfterLayout()
	{
		if (!Following) return;
		// 滑行还在进行时先让它跑完；落位后再补一次到真正的末端。
		if (_smooth.IsAnimating) return;
		JumpToEndKeepingIntent();
	}

	/// <summary>跳到最新，并把这次位移标记成自己的动作，避免被当成读者滚动而取消跟随。</summary>
	private void JumpToEndKeepingIntent()
	{
		_followingJump = true;
		_smooth.JumpToEnd();
	}

	/// <summary>A manual fold is reading intent, not a request to follow a growing reply.</summary>
	public void PreserveAnchor(UIElement anchor)
	{
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
		if (!Following) return;
		// Jump once now for the common case, then ask for one more pass after layout. A virtualized
		// row can only publish its grown height during arrange, so the first jump may still land on
		// the previous extent.
		if (!_smooth.IsAnimating) JumpToEndKeepingIntent();
		QueueFollow();
	}

	public void ScrollToLatest()
	{
		_readingAnchor = null;
		Following = true;
		// 远端行还没被测量时，末端只是估算；先重排再滚，落位后由 QueueFollow 收敛到真正的末尾。
		_view.InvalidateArrange();
		_smooth.ScrollToEnd();
		// 有调度器时再排一次落位后的对齐；无窗口（测试）时交给动画本身。
		if (Application.IsRunning) QueueFollow();
	}

	/// <summary>显式跳转到历史消息：取消在途跟底和折叠锚点，后续流式增长不把用户拉回末尾。</summary>
	public void Browse()
	{
		_readingAnchor = null;
		Following = false;
		_smooth.Stop();
	}

	public void Stop() => _smooth.Stop();

	public void Dispose()
	{
		_disposed = true;
		_readingAnchor = null;
		_view.ScrollChanged -= OnScrollChanged;
		_smooth.VerticalInput -= OnVerticalInput;
		_smooth.Dispose();
	}
}
