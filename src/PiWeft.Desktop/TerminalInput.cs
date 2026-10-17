using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;

namespace PiWeft.Desktop;

/// <summary>把键盘事件翻译成终端字节序列；返回 <c>null</c> 表示这不是终端输入。</summary>
internal static class TerminalKeys
{
	/// <summary>
	/// 处理有确定字节序列的特殊键。普通字符走 IME/文本输入。
	/// 应用光标键模式（DECCKM）下方向键与 Home/End 用 SS3 前缀，与 xterm 一致。
	/// </summary>
	public static string? Translate(KeyEventArgs key, bool applicationCursorKeys)
	{
		if (key.ControlKey && !key.AltKey && Control(key.Key) is { } control)
		{
			return control;
		}

		var sequence = Special(key, applicationCursorKeys);
		if (sequence is null)
		{
			return null;
		}

		// Alt 修饰的普通键在终端里是 ESC 前缀；方向键这类本身带 ESC 的序列不再叠加。
		return key.AltKey && !sequence.StartsWith('\u001b') ? "\u001b" + sequence : sequence;
	}

	private static string? Special(KeyEventArgs key, bool application)
	{
		if (key.ShiftKey && key.Key == Key.Tab)
		{
			return "\u001b[Z";
		}

		return key.Key switch
		{
			Key.Enter => "\r",
			Key.Backspace => "\u007f",
			Key.Tab => "\t",
			Key.Escape => "\u001b",
			Key.Up => application ? "\u001bOA" : "\u001b[A",
			Key.Down => application ? "\u001bOB" : "\u001b[B",
			Key.Right => application ? "\u001bOC" : "\u001b[C",
			Key.Left => application ? "\u001bOD" : "\u001b[D",
			Key.Home => application ? "\u001bOH" : "\u001b[H",
			Key.End => application ? "\u001bOF" : "\u001b[F",
			Key.Insert => "\u001b[2~",
			Key.Delete => "\u001b[3~",
			Key.PageUp => "\u001b[5~",
			Key.PageDown => "\u001b[6~",
			Key.F1 => "\u001bOP",
			Key.F2 => "\u001bOQ",
			Key.F3 => "\u001bOR",
			Key.F4 => "\u001bOS",
			Key.F5 => "\u001b[15~",
			Key.F6 => "\u001b[17~",
			Key.F7 => "\u001b[18~",
			Key.F8 => "\u001b[19~",
			Key.F9 => "\u001b[20~",
			Key.F10 => "\u001b[21~",
			Key.F11 => "\u001b[23~",
			Key.F12 => "\u001b[24~",
			_ => null,
		};
	}

	/// <summary>Ctrl+字母 = 0x01–0x1A；Ctrl+空格 = NUL。</summary>
	private static string? Control(Key key) => key switch
	{
		>= Key.A and <= Key.Z => ((char)(1 + ((int)key - (int)Key.A))).ToString(),
		Key.Space => "\0",
		_ => null,
	};
}

internal interface ITerminalInputSink
{
	void SendKey(KeyEventArgs key);

	void SendText(string text);

	void PointerDown(MouseEventArgs e);

	void PointerMove(MouseEventArgs e);

	void PointerUp(MouseEventArgs e);

	void Wheel(MouseWheelEventArgs e);

	/// <summary>输入法预编辑是否进行中。</summary>
	bool IsComposing { get; }

	/// <summary>IME 候选窗定位用的光标矩形（窗口坐标）。</summary>
	Rect CursorRect { get; }

	void CompositionStarted(string text);

	void CompositionUpdated(string text);

	void CompositionEnded();
}

/// <summary>捕获键盘、IME 与鼠标，转成终端输入或本地选择；点击即聚焦。</summary>
internal sealed class TerminalInputSurface : Control, ITextInputClient, ITextCompositionClient
{
	private readonly ITerminalInputSink _sink;

	public TerminalInputSurface(ITerminalInputSink sink)
	{
		_sink = sink;
		Focusable = true;
		Cursor = CursorType.IBeam;
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		Focus();
		_sink.PointerDown(e);
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		_sink.PointerMove(e);
	}

	protected override void OnMouseUp(MouseEventArgs e)
	{
		base.OnMouseUp(e);
		_sink.PointerUp(e);
	}

	protected override void OnMouseWheel(MouseWheelEventArgs e)
	{
		base.OnMouseWheel(e);
		_sink.Wheel(e);
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		_sink.SendKey(e);
	}

	void ITextInputClient.HandleTextInput(TextInputEventArgs e)
	{
		if (e.Text.Length == 0 || e.Handled)
		{
			return;
		}

		e.Handled = true;
		// IME 提交的文本与普通按键一样走 SendText；先把预编辑浮层收掉。
		if (_sink.IsComposing)
		{
			_sink.CompositionEnded();
		}

		_sink.SendText(e.Text);
	}

	// ------------------------------------------------------------ IME 预编辑

	bool ITextCompositionClient.IsComposing => _sink.IsComposing;

	/// <summary>终端没有文本缓冲区，候选窗位置直接用当前光标格。</summary>
	int ITextCompositionClient.CompositionStartIndex => 0;

	Rect ITextCompositionClient.GetCharRectInWindow(int charIndex) => _sink.CursorRect;

	void ITextCompositionClient.HandleTextCompositionStart(TextCompositionEventArgs e) => _sink.CompositionStarted(e.Text);

	void ITextCompositionClient.HandleTextCompositionUpdate(TextCompositionEventArgs e) => _sink.CompositionUpdated(e.Text);

	void ITextCompositionClient.HandleTextCompositionEnd(TextCompositionEventArgs e) => _sink.CompositionEnded();
}
