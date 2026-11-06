using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Markdown;

public sealed record MarkdownTheme
{
	public double BlockSpacing { get; init; } = 8;

	/// 正文（段落 / 列表 / 引用 / 表格单元）的行高，按字号倍率给出，与 PiDeck
	/// <c>--line-height-chat</c> 同源（1.6）。倍率随字号缩放，改会话字号时行距同步走，
	/// 不会出现「字号变大、行挤在一起」。小于字体自然行高时按自然行高排版（不压字）。
	/// 置 0 或负值＝关闭，退回字体自然行高。
	public double LineHeightScale { get; init; } = 1.6;

	/// 标题行高倍率。标题字号大，需要比正文更紧的行距才不至于把一行拉成一片空白，
	/// 取 PiDeck <c>.markdown-body h1..h6</c> 的 1.3。
	public double HeadingLineHeightScale { get; init; } = 1.3;

	public double ListIndent { get; init; } = 32;

	public string CodeFontFamily { get; init; } = "monospace";

	public Color? LinkForeground { get; init; }

	public Color? CodeBackground { get; init; }

	public Func<Theme, Color>? CodeBackgroundSelector { get; init; }

	public Color? InlineCodeBackground { get; init; }

	public Func<Theme, Color>? InlineCodeBackgroundSelector { get; init; }

	public Color? InlineCodeBorder { get; init; }

	public Func<Theme, Color>? InlineCodeBorderSelector { get; init; }

	public double InlineCodePadding { get; init; } = 4;

	public double InlineCodeVerticalPadding { get; init; } = 1;

	public double InlineCodeCornerRadius { get; init; } = 5;

	public double InlineCodeFontScale { get; init; } = 0.92;

	public double CardCornerRadius { get; init; } = 10;

	public bool ShowCodeLineNumbers { get; init; } = true;

	public double TableFontSizeScale { get; init; } = 0.9;

	public double ImageSpacing { get; init; } = 6;

	public Func<string, Color?>? AlertColor { get; init; }

	public Func<Theme, string, Color?>? AlertColorSelector { get; init; }

	public Func<string, string?>? AlertLabel { get; init; }

	public Color? MarkedBackground { get; init; }

	public double CodeFontSizeScale { get; init; } = 0.92;

	public double? CodeFontSize { get; init; }

	public string CodeCopyLabel { get; init; } = "Copy";

	public string CodeCopyTooltip { get; init; } = "Copy code";

	/// 复制按钮的图标由宿主提供（桌面端是 Lucide 内嵌 SVG）：控件库不自己画图标，
	/// 也不引 SVG 依赖。给了图标就用图标，没给就退回 <see cref="CodeCopyLabel"/> 文本。
	public Func<double, FrameworkElement?>? CodeCopyIcon { get; init; }

	public Func<double, FrameworkElement?>? CodeCopiedIcon { get; init; }

	public double CodeCopyIconSize { get; init; } = 14;

	/// 宿主的提示挂接（例如桌面端的延迟悬浮层）。为空时退回内建 <c>ToolTip</c>；
	/// 它在悬停瞬间出现，与宿主统一的显示延迟不一致，因此桌面宿主应始终提供。
	public Action<FrameworkElement, string>? AttachToolTip { get; init; }
}
