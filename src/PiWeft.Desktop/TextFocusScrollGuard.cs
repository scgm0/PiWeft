using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>会话阅读控件获得焦点不驱动外层滚动；文本选择、键盘操作与显式滚动仍可用。</summary>
internal sealed class TextFocusScrollGuard : IDisposable
{
	private readonly ScrollViewer _scroll;
	private readonly Action<UIElement>? _preserveAnchor;
	private readonly ConditionalWeakTable<UIElement, object> _trackedFocus = new();
	private readonly ConditionalWeakTable<UIElement, object> _trackedWheel = new();
	// Focus-into-view walks every ancestor ScrollViewer, including the execution group body and
	// the conversation itself. Preserve all of them for the focus transaction: a MarkdownView is
	// often taller than the viewport, so letting the framework "show" the whole focused view
	// scrolls the group body to its top/bottom and looks like a progress jump.
	private readonly List<PreservedViewport> _preserved = [];
	private bool _preserving;
	private bool _restoring;
	private int _preservationVersion;

	public TextFocusScrollGuard(ScrollViewer scroll, Action<UIElement>? preserveAnchor = null)
	{
		_scroll = scroll;
		_preserveAnchor = preserveAnchor;
	}

	public void Track(UIElement text)
	{
		ProtectFocus(text);
		if (!_trackedWheel.TryGetValue(text, out _))
		{
			_trackedWheel.Add(text, new object());
			text.MouseWheel += args => RouteWheel(text, args);
		}
	}

	public void TrackTree(Element root)
	{
		if (root is UIElement control) ProtectFocus(control);
		if (root is IVisualTreeHost tree) tree.VisitChildren(child => { TrackTree(child); return true; });
	}

	private void ProtectFocus(UIElement control)
	{
		if (_trackedFocus.TryGetValue(control, out _)) return;
		_trackedFocus.Add(control, new object());
		control.GotFocus += () => Preserve(control);
	}

	/// <summary>折叠开关改变了行高：宿主据此重排会话视图（与手动滚动同样的收敛）。</summary>
	public Action<FrameworkElement>? LayoutInvalidated { get; set; }

	/// <summary>展开正在流式输出的内容（思考）时请求会话重新进入跟底。</summary>
	public Action? FollowConversation { get; set; }

	/// <summary>组内步骤的折叠是阅读行为：暂停外层跟底，但不移动外层滚动位置。</summary>
	public Action? PauseFollowing { get; set; }

	/// <summary>恢复折叠状态时抑制「跟随内容增长」，避免滚动中的虚拟化行重建后把读者拽回末尾。</summary>
	public bool SuppressGrowthFollow { get; set; }

	/// <summary>
	/// 展开流式内容后让最近的滚动视口跟住内容末尾：会话本身交给 <see cref="FollowConversation"/>，
	/// 过程组的组体这类嵌套视口由自己的 <see cref="SmoothScroll"/> 持续贴底。
	/// </summary>
	/// <remarks>
	/// 只有在读者本就看着末尾时才继续贴底：正在向上浏览时展开思考，不应该把读者拽到末尾。
	/// 这也与组体的「置底才跟随」一致——上滚之后展开/折叠不能抢走阅读位置。
	/// </remarks>
	public void FollowGrowth(Element origin)
	{
		if (NearestViewport(origin) is not { } viewport) return;
		if (ReferenceEquals(viewport, _scroll))
		{
			FollowConversation?.Invoke();
			return;
		}
		// 组体小窗先跟住思考末尾；组体在会话里时，会话本身也要跟到最新，
		// 否则组体内部在滚、外层仍停在思考标题上。
		if (SmoothScroll.Find(viewport) is { } smooth &&
			(smooth.IsFollowingGrowth || smooth.MaxVerticalOffset - viewport.VerticalOffset <= AtTailTolerance))
		{
			smooth.FollowGrowth();
		}
		if (origin.IsDescendantOf(_scroll)) FollowConversation?.Invoke();
	}

	/// <summary>判定视口是否停在末尾的容差（DIP）。</summary>
	internal const double AtTailTolerance = 4;

	/// <summary>折叠流式内容时停止跟随增长，视口停在读者看到的位置。</summary>
	public void StopGrowth(Element origin)
	{
		if (NearestViewport(origin) is not { } viewport || ReferenceEquals(viewport, _scroll)) return;
		SmoothScroll.Find(viewport)?.StopFollowingGrowth();
	}

	private static ScrollViewer? NearestViewport(Element origin)
	{
		for (var parent = origin.Parent; parent is not null; parent = parent.Parent)
			if (parent is ScrollViewer { IsVisible: true } viewport) return viewport;
		return null;
	}

	public void TrackFold(Expander fold, bool preserveAnchor = true)
	{
		// Focusing the whole expanded subtree must not scroll its bottom into view.
		ProtectFocus(fold);
		fold.ExpandedChanged += expanded =>
		{
			if (expanded && fold.Content is { } content) TrackTree(content);
			LayoutInvalidated?.Invoke(fold);
		};
		void ReadingIntent()
		{
			if (preserveAnchor) _preserveAnchor?.Invoke(fold);
			else PauseFollowing?.Invoke();
		}
		fold.MouseDown += args =>
		{
			if (fold.FindVisualRoot() is not Window { Handle: not 0 }) return;
			var inner = fold.Bounds.Deflate(fold.Padding);
			var header = new Rect(inner.X, inner.Y, inner.Width, Math.Max(20, fold.Header?.DesiredSize.Height ?? 20));
			var local = args.GetPosition(fold);
			var point = new Point(fold.Bounds.X + local.X, fold.Bounds.Y + local.Y);
			if (args.Button == MouseButton.Left && header.Contains(point)) ReadingIntent();
		};
		fold.KeyUp += args =>
		{
			if (!args.Handled && args.Key is Key.Space or Key.Enter) ReadingIntent();
		};
	}

	internal void RouteWheel(UIElement text, MouseWheelEventArgs args)
	{
		if (args.Handled || args.Delta.Y == 0) return;
		// 滚轮落在文本/代码上时被这里接管，不会继续冒泡到窗口根：延迟取样点必须放在这里。
		Diagnostics.NoteInput();
		if (args.ShiftKey)
		{
			// Shift+滚轮只用于代码横向浏览，普通纵向滚轮始终由会话列表处理。
			for (var parent = text.Parent; parent is not null && !ReferenceEquals(parent, _scroll); parent = parent.Parent)
			{
				if (parent is ScrollViewer { HorizontalScroll: not ScrollMode.Disabled } horizontal)
				{
					SmoothScroll.For(horizontal).ScrollBy(-args.Delta.Y, 0);
					args.Handled = true;
					return;
				}
			}
		}
		// 文字落在可滚动的嵌套小窗里（执行过程的组体）时，纵向滚轮先归那一层：
		// 它到边界后才会继续沿祖先链往外，最后才轮到会话。直接滚会话会让组体永远滚不动。
		if (ScrollCascade.Route(text, 0, args.Delta.Y))
		{
			args.Handled = true;
			return;
		}
		SmoothScroll.For(_scroll).ScrollBy(0, -args.Delta.Y);
		args.Handled = true;
	}

	private void Preserve(UIElement focused)
	{
		EndPreservation();
		int version = ++_preservationVersion;
		_preserving = true;
		// Capture the nearest viewport first; restoring inner scrolls before the outer ones keeps
		// the focused element's window position stable when both hosts react to the same focus.
		for (Element? current = focused; current is not null; current = current.Parent)
		{
			if (current is not ScrollViewer viewport)
			{
				continue;
			}
			AddPreserved(viewport);
		}
		AddPreserved(_scroll);

		if (_preserved.Count == 0 || !Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			return;
		}
		dispatcher.BeginInvoke(() =>
		{
			// A later focus in the same frame supersedes this preservation; its own callback clears.
			if (version == _preservationVersion)
			{
				EndPreservation();
			}
		});
	}

	private void AddPreserved(ScrollViewer viewport)
	{
		foreach (var preserved in _preserved)
		{
			if (ReferenceEquals(preserved.Viewport, viewport))
			{
				return;
			}
		}
		_preserved.Add(new PreservedViewport(viewport, viewport.HorizontalOffset, viewport.VerticalOffset));
		viewport.ScrollChanged += Restore;
	}

	private void EndPreservation()
	{
		_preservationVersion++;
		_preserving = false;
		foreach (var preserved in _preserved)
		{
			preserved.Viewport.ScrollChanged -= Restore;
		}
		_preserved.Clear();
	}

	public void Dispose() => EndPreservation();

	private void Restore()
	{
		// MewUI 在 GotFocus 之后同步通知祖先滚动容器。只撤回这次焦点事务引起的位移。
		if (!_preserving || _restoring)
		{
			return;
		}

		_restoring = true;
		try
		{
			foreach (var preserved in _preserved)
			{
				ScrollViewer viewport = preserved.Viewport;
				if (viewport.HorizontalOffset != preserved.Horizontal ||
					viewport.VerticalOffset != preserved.Vertical)
				{
					viewport.SetScrollOffsets(preserved.Horizontal, preserved.Vertical);
				}
			}
		}
		finally
		{
			_restoring = false;
		}
	}

	private sealed record PreservedViewport(ScrollViewer Viewport, double Horizontal, double Vertical);
}
