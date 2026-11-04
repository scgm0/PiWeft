using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;

namespace PiWeft.Desktop;

/// <summary>Public property bindings observe selections without polling; password controls are never attached.</summary>
internal static class PrimarySelectionText
{
	internal static IPrimarySelection? Service { get; set; }
	private static readonly ConditionalWeakTable<TextBase, SelectionObserver> Observers = new();
	private static readonly ConditionalWeakTable<Window, WindowObserver> Windows = new();

	/// <summary>
	/// Window 级兜底：动态创建的代码块、表单输入等不一定经过显式的 PrimarySelection()，
	/// 第一次点击/键盘选中时按命中元素惰性挂上 observer，后续选区变化即发布 PRIMARY。
	/// </summary>
	internal static void Attach(Window window) =>
		Windows.GetValue(window, static owner => new WindowObserver(owner));

	/// <param name="smoothScroll">
	/// 是否给多行文本框装帧驱动的平滑滚动。嵌在另一个平滑视口里、又自带高度上限的文本框要传
	/// <see langword="false"/>：平滑控制器接管滚轮后要经滚动条回写文本偏移，嵌套场景里
	/// 算不出正确余量，表现是吞掉滚轮但不移动；内建滚轮处理会直接写偏移。
	/// </param>
	public static T PrimarySelection<T>(this T control, bool smoothScroll = true) where T : TextBase
	{
		if (smoothScroll && control is MultiLineTextBox multi) multi.SmoothScrolling();
		if (Service is null || control is not TextBox and not MultiLineTextBox) return control;
		Observers.GetValue(control, static text => new SelectionObserver(text));
		return control;
	}

	internal static int CaretAt(TextBase control, Point point)
	{
		var length = TextOf(control).Length;
		if (control is MultiLineTextBox multi)
		{
			var viewport = multi.TextViewportBounds;
			var x = point.X - viewport.X + multi.HorizontalOffset;
			var y = Math.Max(0, point.Y - viewport.Y + multi.VerticalOffset);
			var line = multi.Document.GetLineByNumber(multi.FindLineByY(y));
			if (multi.GetLineLayout(line.Offset) is { } layout)
			{
				var hit = layout.HitTest(new Point(x - layout.DocumentX, y - layout.DocumentY));
				return Math.Clamp(layout.LogicalLine.Offset + layout.MapProjectedOffsetToSource(hit.InsertionIndex), 0, length);
			}
		}
		// Single-line boxes expose character rectangles; binary search avoids scanning a long field.
		var low = 0;
		var high = length;
		while (low < high)
		{
			var mid = (low + high) / 2;
			var rect = control.GetCharRectInWindow(mid);
			if (rect.Bottom <= point.Y) low = mid + 1; else high = mid;
		}
		var row = control.GetCharRectInWindow(low);
		var start = low;
		high = length;
		while (low < high)
		{
			var mid = (low + high + 1) / 2;
			var rect = control.GetCharRectInWindow(mid);
			if (rect.Y <= row.Y) low = mid; else high = mid - 1;
		}
		var end = low;
		low = start;
		high = end;
		while (low < high)
		{
			var mid = (low + high) / 2;
			if (control.GetCharRectInWindow(mid).X < point.X) low = mid + 1; else high = mid;
		}
		if (low > start)
		{
			var previous = control.GetCharRectInWindow(low - 1);
			var current = control.GetCharRectInWindow(low);
			if (Math.Abs(previous.X - point.X) < Math.Abs(current.X - point.X)) low--;
		}
		return Math.Clamp(low, 0, length);
	}

	internal static async Task PasteAsync(TextBase control)
	{
		if (Service is not { } service || control.IsReadOnly || !control.IsEffectivelyEnabled) return;
		var caret = control.CaretPosition;
		var text = TextOf(control);
		var root = control.FindVisualRoot();
		var inserted = await service.ReadAsync().ConfigureAwait(false);
		void Apply()
		{
			// Async IO must not overwrite typing, a new selection, a switched tab or a closed window.
			if (inserted is null || control.IsReadOnly || !control.IsEffectivelyEnabled || !control.IsVisible ||
				control.CaretPosition != caret || control.SelectionLength != 0 || TextOf(control) != text ||
				!ReferenceEquals(control.FindVisualRoot(), root)) return;
			for (Element? parent = control; parent is not null; parent = parent.Parent)
				if (parent is UIElement { IsVisible: false }) return;
			control.ReplaceSelection(control is TextBox ? inserted.Replace('\r', ' ').Replace('\n', ' ') : inserted);
		}
		if (Application.IsRunning) Application.Current.Dispatcher?.BeginInvoke(Apply);
		else Apply();
	}

	private static string TextOf(TextBase control) => control switch
	{
		MultiLineTextBox multi => multi.Text, TextBox single => single.Text, _ => string.Empty,
	};

	private sealed class SelectionObserver : MewObject
	{
		private static readonly MewProperty<int> StartProperty = MewProperty<int>.Register<SelectionObserver>("SelectionStart", 0,
			changed: static (observer, _, _) => observer.Publish());
		private static readonly MewProperty<int> LengthProperty = MewProperty<int>.Register<SelectionObserver>("SelectionLength", 0,
			changed: static (observer, _, _) => observer.Publish());
		private readonly TextBase _control;
		private bool _placingCaret;
		private bool _queued;
		public SelectionObserver(TextBase control)
		{
			_control = control;
			SetBinding(StartProperty, control, TextBase.SelectionStartProperty);
			SetBinding(LengthProperty, control, TextBase.SelectionLengthProperty);
			control.MouseDown += MiddleDown;
		}

		internal void Publish()
		{
			if (_placingCaret || Service is null || _queued) return;
			void Commit()
			{
				_queued = false;
				if (_placingCaret || Service is null) return;
				var text = _control.SelectionLength > LinuxPrimarySelection.MaxCharacters ? string.Empty : _control switch
				{
					MultiLineTextBox multi => multi.SelectedText, TextBox single => single.SelectedText, _ => string.Empty,
				};
				Service.Publish(text);
			}
			if (Application.IsRunning) { _queued = true; Application.Current.Dispatcher?.BeginInvoke(Commit); }
			else Commit();
		}

		internal void MiddleDown(MouseEventArgs args)
		{
			if (args.Button != MouseButton.Middle || args.Handled || _control.IsReadOnly || !_control.IsEffectivelyEnabled || Service is null) return;
			var local = args.GetPosition(_control);
			_placingCaret = true;
			try { _control.MoveCaret(CaretAt(_control, new Point(_control.Bounds.X + local.X, _control.Bounds.Y + local.Y)), false); }
			finally { _placingCaret = false; }
			_control.Focus();
			args.Handled = true;
			_ = PasteAsync(_control);
		}
	}

	private sealed class WindowObserver
	{
		private readonly Window _window;
		private bool _closed;

		public WindowObserver(Window window)
		{
			_window = window;
			window.MouseDown += OnMouseDown;
			window.PreviewKeyDown += OnPreviewKeyDown;
			window.Closed += OnClosed;
		}

		private void OnClosed()
		{
			if (_closed) return;
			_closed = true;
			_window.MouseDown -= OnMouseDown;
			_window.PreviewKeyDown -= OnPreviewKeyDown;
			_window.Closed -= OnClosed;
		}

		private void OnMouseDown(MouseEventArgs args)
		{
			if (_closed || Service is null) return;
			var point = args.GetPosition(_window);
			if (_window.HitTest(point) is not { } leaf || Find(leaf) is not { } text) return;
			var observer = Observers.GetValue(text, static control => new SelectionObserver(control));
			if (args.Button == MouseButton.Middle && !args.Handled)
			{
				observer.MiddleDown(args);
			}
			else
			{
				// 显式调用点通常已经在 MouseDown 前挂好；这里覆盖动态控件，
				// 并补一次发布，避免双击/拖选的第一帧还停留在上一次 PRIMARY。
				observer.Publish();
			}
		}

		private void OnPreviewKeyDown(KeyEventArgs args)
		{
			if (_closed || args.Handled || Service is null) return;
			if (_window.FocusManager.FocusedElement is not { } focused || Find(focused) is not { } text) return;
			Observers.GetValue(text, static control => new SelectionObserver(control)).Publish();
		}

		private static TextBase? Find(Element leaf)
		{
			for (Element? current = leaf; current is not null; current = current.Parent)
			{
				if (current is TextBox or MultiLineTextBox) return (TextBase)current;
			}
			return null;
		}
	}
}
