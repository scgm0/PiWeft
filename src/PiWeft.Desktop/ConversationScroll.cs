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
		// Layout can clamp the offset when a block collapses. It is not an instruction to resume follow.
		if (_readingAnchor is null && !_restoringAnchor && !_smooth.IsApplyingOffset && offset != _lastOffset && maximum == _lastMaximum)
		{
			Following = maximum - offset <= EndThreshold;
		}
		_lastOffset = offset;
		_lastMaximum = maximum;
		_showLatest(maximum - offset > EndThreshold);
		QueueAnchorRestore();
		// Arrange publishes the new extent after a message grows. Tracking it here avoids scrolling
		// against stale metrics in Render(), and keeps a browsing reader at the same offset.
		if (Following && !_smooth.IsAnimating && !_smooth.IsApplyingOffset && _smooth.MaxVerticalOffset - offset > 0.5)
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
			if (!_disposed && Following && !_smooth.IsAnimating) _smooth.JumpToEnd();
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
		if (Following && !_smooth.IsAnimating) _smooth.JumpToEnd();
	}

	public void ScrollToLatest()
	{
		_readingAnchor = null;
		Following = true;
		_smooth.ScrollToEnd();
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
