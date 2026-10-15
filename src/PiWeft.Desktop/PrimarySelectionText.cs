using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Public property bindings observe selections without polling; password controls are never attached.</summary>
internal static class PrimarySelectionText
{
	internal static IPrimarySelection? Service { get; set; }
	private static readonly ConditionalWeakTable<TextBase, SelectionObserver> Observers = new();

	public static T PrimarySelection<T>(this T control) where T : TextBase
	{
		if (control is MultiLineTextBox multi) multi.SmoothScrolling();
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

		private void Publish()
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

		private void MiddleDown(MouseEventArgs args)
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
}
