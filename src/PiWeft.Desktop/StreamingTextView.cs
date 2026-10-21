using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;
using Aprillz.MewUI.Text.Editing;

namespace PiWeft.Desktop;

/// <summary>
/// 只读、可流式追加的文本视图。
/// </summary>
/// <remarks>
/// 直接给 <see cref="MultiLineTextBox.Text"/> 赋值会调用 <c>EditableTextDocument.SetText</c>：
/// 重建整篇文档与行索引、清空撤销历史、整篇重新测量。流式内容每 40 ms 增长一次，这个成本随长度
/// 线性上升（实测 32 K 约 42 ms、64 K 约 73 ms），远超一帧预算。
/// <para>
/// 纯追加只需要 <see cref="MultiLineTextBox.ReplaceRange"/>：文档按区间增量更新行索引，
/// 文本视图只重排受影响的行。撤销历史对只读展示没有意义，直接关掉，避免每帧生成一条编辑记录。
/// 实测 32 K–64 K 的追加稳定在 1 ms 左右。
/// </para>
/// 文本回退（重发、编辑、模型改写前缀）无法走追加路径，退回整篇替换；这种情况在流式里很少见。
/// </remarks>
internal sealed class StreamingTextView
{
	private readonly MultiLineTextBox _view;
	private string _text = string.Empty;

	public StreamingTextView(bool muted = false, bool mono = false, double? fontSize = null)
	{
		_view = new MultiLineTextBox(new EditableTextDocument())
		{
			IsReadOnly = true,
			// 只读正文不需要输入法：避免在内容上误触发组合输入。
			ImeMode = ImeMode.Disabled,
			SizeToDocument = true,
			Wrap = true,
			Background = AppTheme.Transparent,
			BorderThickness = 0,
			Padding = Thickness.Zero,
			MinHeight = 0,
			CornerRadius = 0,
			FontFamily = mono ? AppTheme.MonoFont : AppFonts.DefaultUi,
			FontSize = fontSize ?? AppTheme.FontSizeChat,
			Foreground = muted ? AppTheme.TextMuted : AppTheme.TextPrimary,
			Tag = muted ? AppStyles.RoleMuted : AppStyles.RolePrimary,
		}.PrimarySelection();
		// 只读展示不保留撤销历史：每帧追加只产生一个文档增量，不留编辑记录。
		_view.Document.UndoSizeLimit = 0;
	}

	public MultiLineTextBox View => _view;

	/// <summary>当前已写入的文本（与文档内容一致，可直接用于前缀比较）。</summary>
	public string Text => _text;

	/// <summary>把内容更新到最新值：追加走增量编辑，其余情况整篇替换。</summary>
	public void Update(string text)
	{
		if (string.Equals(_text, text, StringComparison.Ordinal)) return;
		// 与 EditableTextDocument 的换行归一化保持一致，否则前缀比较会与文档内容错位。
		var normalized = Normalize(text);
		if (normalized.Length > _text.Length && normalized.StartsWith(_text, StringComparison.Ordinal))
		{
			// 纯追加：只编辑文档尾部，前面的行、选区与已测行高都保留。
			_view.ReplaceRange(_text.Length, 0, normalized[_text.Length..]);
			_text = normalized;
			return;
		}

		// 回退或整段改写：无法推断增量，整篇替换。
		_view.ReplaceRange(0, _text.Length, normalized);
		_text = normalized;
	}

	private static string Normalize(string text) =>
		text.Contains('\r', StringComparison.Ordinal)
			? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
			: text;
}
