using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Markdown;

/// <summary>Document metrics and optional colors; null colors follow the current UI theme.</summary>
public sealed record MarkdownTheme
{
	/// <summary>Gap between top-level blocks in DIPs.</summary>
	public double BlockSpacing { get; init; } = 8;

	/// <summary>
	/// 正文（段落 / 列表 / 引用 / 表格单元）的行高，按字号倍率给出，与 PiDeck
	/// <c>--line-height-chat</c> 同源（1.6）。倍率随字号缩放，改会话字号时行距同步走，
	/// 不会出现「字号变大、行挤在一起」。小于字体自然行高时按自然行高排版（不压字）。
	/// 置 0 或负值＝关闭，退回字体自然行高。
	/// </summary>
	public double LineHeightScale { get; init; } = 1.6;

	/// <summary>
	/// 标题行高倍率。标题字号大，需要比正文更紧的行距才不至于把一行拉成一片空白，
	/// 取 PiDeck <c>.markdown-body h1..h6</c> 的 1.3。
	/// </summary>
	public double HeadingLineHeightScale { get; init; } = 1.3;

	/// <summary>Hanging indent reserved for each list nesting level in DIPs.</summary>
	public double ListIndent { get; init; } = 32;

	/// <summary>Font family for inline and block code.</summary>
	public string CodeFontFamily { get; init; } = "monospace";

	/// <summary>Optional link foreground.</summary>
	public Color? LinkForeground { get; init; }

	/// <summary>Optional code background.</summary>
	public Color? CodeBackground { get; init; }

	/// <summary>Optional theme-aware code background; takes precedence over <see cref="CodeBackground"/>.</summary>
	public Func<Theme, Color>? CodeBackgroundSelector { get; init; }

	/// <summary>Optional inline-code chip background.</summary>
	public Color? InlineCodeBackground { get; init; }

	/// <summary>Optional theme-aware inline-code chip background; takes precedence over <see cref="InlineCodeBackground"/>.</summary>
	public Func<Theme, Color>? InlineCodeBackgroundSelector { get; init; }

	/// <summary>Optional inline-code chip border.</summary>
	public Color? InlineCodeBorder { get; init; }

	/// <summary>Optional theme-aware inline-code chip border; takes precedence over <see cref="InlineCodeBorder"/>.</summary>
	public Func<Theme, Color>? InlineCodeBorderSelector { get; init; }

	/// <summary>Horizontal padding around an inline-code run in DIPs.</summary>
	public double InlineCodePadding { get; init; } = 4;

	/// <summary>Vertical padding around an inline-code run in DIPs.</summary>
	public double InlineCodeVerticalPadding { get; init; } = 1;

	/// <summary>Corner radius of an inline-code chip in DIPs.</summary>
	public double InlineCodeCornerRadius { get; init; } = 5;

	/// <summary>Font scale applied to inline code relative to its surrounding text.</summary>
	public double InlineCodeFontScale { get; init; } = 0.92;

	/// <summary>Corner radius of fenced-code and table cards in DIPs.</summary>
	public double CardCornerRadius { get; init; } = 10;

	/// <summary>Whether fenced-code blocks show a line-number gutter.</summary>
	public bool ShowCodeLineNumbers { get; init; } = true;

	/// <summary>Scale applied to table cell text relative to the document font size.</summary>
	public double TableFontSizeScale { get; init; } = 0.9;

	/// <summary>Vertical gap between the text line and a resolved image, and between images, in DIPs.</summary>
	public double ImageSpacing { get; init; } = 6;

	/// <summary>Fixed alert color for GitHub alerts (kind names "NOTE"/"TIP"/"IMPORTANT"/"WARNING"/"CAUTION").</summary>
	public Func<string, Color?>? AlertColor { get; init; }

	/// <summary>Theme-aware alert color; takes precedence over <see cref="AlertColor"/>.</summary>
	public Func<Theme, string, Color?>? AlertColorSelector { get; init; }

	/// <summary>Localized alert label; defaults to the kind name itself ("NOTE" renders as "Note").</summary>
	public Func<string, string?>? AlertLabel { get; init; }

	/// <summary>Optional marked-text background.</summary>
	public Color? MarkedBackground { get; init; }

	/// <summary>Scale applied to the control font size for fenced code.</summary>
	public double CodeFontSizeScale { get; init; } = 0.92;

	/// <summary>Exact fenced-code font size; overrides <see cref="CodeFontSizeScale"/> when set.</summary>
	public double? CodeFontSize { get; init; }

	/// <summary>Label on the default code copy button.</summary>
	public string CodeCopyLabel { get; init; } = "Copy";

	/// <summary>Tooltip on the default code copy button.</summary>
	public string CodeCopyTooltip { get; init; } = "Copy code";

	/// <summary>
	/// 复制按钮的图标由宿主提供（桌面端是 Lucide 内嵌 SVG）：控件库不自己画图标，
	/// 也不引 SVG 依赖。给了图标就用图标，没给就退回 <see cref="CodeCopyLabel"/> 文本。
	/// </summary>
	public Func<double, FrameworkElement?>? CodeCopyIcon { get; init; }

	/// <summary>复制成功后的确认图标；缺省时不切换内容，只保留提示。</summary>
	public Func<double, FrameworkElement?>? CodeCopiedIcon { get; init; }

	/// <summary>图标按钮里的图标边长（DIP）。</summary>
	public double CodeCopyIconSize { get; init; } = 14;

	/// <summary>
	/// 宿主的提示挂接（例如桌面端的延迟悬浮层）。为空时退回内建 <c>ToolTip</c>——
	/// 它在悬停瞬间出现，与宿主统一的显示延迟不一致，因此桌面宿主应始终提供。
	/// </summary>
	public Action<FrameworkElement, string>? AttachToolTip { get; init; }
}
