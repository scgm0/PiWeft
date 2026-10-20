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
		if (_readingAnchor is null && !_restoringAnchor && !_smooth.IsApplyingOffset)
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
		if (Following && !_smooth.IsApplyingOffset && _smooth.MaxVerticalOffset - offset > 0.5)
		{
			QueueFollow();
		}
	}

	private void QueueFollow()
	{
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			_smooth.JumpToEnd();
			return;
		}
		if (_followQueued) return;
		_followQueued = true;
		// ScrollChanged runs during Arrange. Moving a virtualized viewport there leaves its
		// already-arranged rows at the old offset; defer until the layout transaction ends.
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_followQueued = false;
			// A wheel animation started before the reply grew would settle short of the new end.
			// Steer it to the end instead of abandoning follow because an animation is running.
			if (!_disposed && Following) _smooth.FollowEnd();
		});
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
		if (!_smooth.IsAnimating) _smooth.JumpToEnd();
		QueueFollow();
	}

	public void ScrollToLatest()
	{
		_readingAnchor = null;
		Following = true;
		_smooth.ScrollToEnd();
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
