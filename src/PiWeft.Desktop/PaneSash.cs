using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// <summary>
/// 分隔两节的可拖动细线（对齐 PiDeck 的 sash）：静态是一条发丝线，悬停/拖动时提亮，
/// 光标换成上下箭头。按下时记住「指针在窗口里的位置」和「这一节当时的高度」，
/// 之后每次移动都换算成这一节应有的高度报给宿主。
/// </summary>
/// <remarks>
/// 为什么不是增量：分隔条跟着它分隔的那一节一起移动，用「相对上一次事件的位移」累加会自我抵消 ——
/// 节长高把线顶开，指针与新线的距离就变小，实测拖 70 DIP 只长 40 DIP。锚定窗口坐标 + 绝对高度没有这个问题。
/// <para>
/// 也没有复用 MewUI 的 <c>SplitPanel</c>：它一次只能分两栏、分隔条固定 8 DIP，且没有「拖完了」的回调 ——
/// 这里要拖完那一刻才写 desktop.json，移动过程中只改本地高度（每移一像素写一次文件是不能接受的）。
/// </para>
/// </remarks>
internal sealed class PaneSash : FrameworkElement
{
	/// <summary>视觉只有一条线，但命中得有 6 DIP 才按得住。</summary>
	public const double HitThickness = 6;

	private readonly Func<double> _startingHeight;
	private readonly Action<double> _onHeight;
	private readonly Action _onCommit;
	private bool _dragging;
	private bool _hot;
	private double _anchorY;
	private double _anchorHeight;

	public PaneSash(Func<double> startingHeight, Action<double> onHeight, Action onCommit)
	{
		_startingHeight = startingHeight;
		_onHeight = onHeight;
		_onCommit = onCommit;
		Height = HitThickness;
		Cursor = CursorType.SizeNS;
	}

	protected override Size MeasureContent(Size availableSize) =>
		new(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, HitThickness);

	protected override void OnMouseDown(MouseEventArgs args)
	{
		base.OnMouseDown(args);
		if (args.Button != MouseButton.Left)
		{
			return;
		}

		BeginDragForTests(WindowY(args));
		_anchorHeight = _startingHeight();
		if (FindVisualRoot() is Window window)
		{
			window.CaptureMouse(this);
		}

		args.Handled = true;
		InvalidateVisual();
	}

	protected override void OnMouseMove(MouseEventArgs args)
	{
		base.OnMouseMove(args);
		if (!_dragging)
		{
			return;
		}

		MoveForTests(WindowY(args));
		args.Handled = true;
	}

	protected override void OnMouseUp(MouseEventArgs args)
	{
		base.OnMouseUp(args);
		if (!_dragging)
		{
			return;
		}

		EndDragForTests();
		if (FindVisualRoot() is Window window)
		{
			window.ReleaseMouseCapture();
		}

		args.Handled = true;
		InvalidateVisual();
	}

	protected override void OnMouseEnter()
	{
		base.OnMouseEnter();
		_hot = true;
		InvalidateVisual();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();
		if (_dragging)
		{
			return;
		}

		_hot = false;
		InvalidateVisual();
	}

	// 捕获被抢走时不会有那次抬起；就此收尾，别把拖动状态挂住（下一次进来还以为在拖）。
	protected override void OnMewPropertyChanged(MewProperty property)
	{
		base.OnMewPropertyChanged(property);
		if (property != IsMouseCapturedProperty || IsMouseCaptured || !_dragging)
		{
			return;
		}

		_dragging = false;
		_onCommit();
		InvalidateVisual();
	}

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnRender(IGraphicsContext context)
	{
		var box = Bounds;
		if (box.Width < 8)
		{
			return;
		}

		var palette = AppTheme.For(Theme);
		var active = _dragging || _hot;
		var scale = context.DpiScale > 0 ? context.DpiScale : 1.0;
		var line = Math.Round((box.Y + box.Height / 2) * scale) / scale;
		context.DrawLine(
			new Point(box.X + AppTheme.Space2, line),
			new Point(box.Right - AppTheme.Space2, line),
			active ? palette.Accent : palette.BorderSubtle,
			active ? 2 : 1);
	}

	private double WindowY(MouseEventArgs args) =>
		FindVisualRoot() is Window window ? args.GetPosition(window).Y : args.ScreenPosition.Y;

	// 无头测试里没有窗口父级，拿不到窗口坐标，所以拖动入口收在一处：下面三个方法即事件路径的全部逻辑。
	internal void BeginDragForTests(double windowY)
	{
		_dragging = true;
		_anchorY = windowY;
		_anchorHeight = _startingHeight();
	}

	internal void MoveForTests(double windowY)
	{
		if (!_dragging)
		{
			return;
		}

		// 向上拖（窗口 Y 变小）给下面那一节长高：宿主自己决定这条线归属哪一节。
		_onHeight(_anchorHeight + (_anchorY - windowY));
	}

	internal void EndDragForTests()
	{
		if (!_dragging)
		{
			return;
		}

		_dragging = false;
		_onCommit();
	}
}
