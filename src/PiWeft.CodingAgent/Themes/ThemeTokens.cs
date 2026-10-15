using System.Collections.Frozen;

namespace PiWeft.CodingAgent.Themes;

/// <summary>
/// pi 主题的颜色 token 名与可选项回退链。命名与 <c>packages/tui/src/theme-tokens.ts</c> 一致，
/// 使同一个主题文件在 pi 与 PiWeft 上认到相同的角色。
/// </summary>
public static class ThemeTokens
{
	// ---------------------------------------------------------------- 前景角色
	public const string Accent = "accent";
	public const string Border = "border";
	public const string BorderAccent = "borderAccent";
	public const string BorderMuted = "borderMuted";
	public const string Success = "success";
	public const string Error = "error";
	public const string Warning = "warning";
	public const string Muted = "muted";
	public const string Dim = "dim";
	public const string Text = "text";
	public const string ThinkingText = "thinkingText";
	public const string UserMessageText = "userMessageText";
	public const string CustomMessageText = "customMessageText";
	public const string CustomMessageLabel = "customMessageLabel";
	public const string ToolTitle = "toolTitle";
	public const string ToolOutput = "toolOutput";
	public const string MdHeading = "mdHeading";
	public const string MdLink = "mdLink";
	public const string MdLinkUrl = "mdLinkUrl";
	public const string MdCode = "mdCode";
	public const string MdCodeBlock = "mdCodeBlock";
	public const string MdCodeBlockBorder = "mdCodeBlockBorder";
	public const string MdQuote = "mdQuote";
	public const string MdQuoteBorder = "mdQuoteBorder";
	public const string MdHr = "mdHr";
	public const string MdListBullet = "mdListBullet";
	public const string ToolDiffAdded = "toolDiffAdded";
	public const string ToolDiffRemoved = "toolDiffRemoved";
	public const string ToolDiffContext = "toolDiffContext";
	public const string SyntaxComment = "syntaxComment";
	public const string SyntaxKeyword = "syntaxKeyword";
	public const string SyntaxFunction = "syntaxFunction";
	public const string SyntaxVariable = "syntaxVariable";
	public const string SyntaxString = "syntaxString";
	public const string SyntaxNumber = "syntaxNumber";
	public const string SyntaxType = "syntaxType";
	public const string SyntaxOperator = "syntaxOperator";
	public const string SyntaxPunctuation = "syntaxPunctuation";
	public const string ThinkingOff = "thinkingOff";
	public const string ThinkingMinimal = "thinkingMinimal";
	public const string ThinkingLow = "thinkingLow";
	public const string ThinkingMedium = "thinkingMedium";
	public const string ThinkingHigh = "thinkingHigh";
	public const string ThinkingXhigh = "thinkingXhigh";
	public const string ThinkingMax = "thinkingMax";
	public const string BashMode = "bashMode";

	// ---------------------------------------------------------------- 背景角色
	public const string SelectedBg = "selectedBg";
	public const string UserMessageBg = "userMessageBg";
	public const string CustomMessageBg = "customMessageBg";
	public const string ToolPendingBg = "toolPendingBg";
	public const string ToolSuccessBg = "toolSuccessBg";
	public const string ToolErrorBg = "toolErrorBg";

	// ---------------------------------------------------------------- 可选项
	public const string ScrollbarTrack = "scrollbarTrack";
	public const string ScrollbarThumb = "scrollbarThumb";
	public const string SearchMatchBg = "searchMatchBg";
	public const string SearchMatchText = "searchMatchText";

	/// <summary>背景类 token，用于缺省外观推断。</summary>
	public static readonly FrozenSet<string> BackgroundTokens = new[]
	{
		SelectedBg,
		UserMessageBg,
		CustomMessageBg,
		ToolPendingBg,
		ToolSuccessBg,
		ToolErrorBg,
	}.ToFrozenSet(StringComparer.Ordinal);

	/// <summary>
	/// 可选 token → 回退 token。与 pi 的五个可选颜色一致：缺省时取回退项，
	/// 让只写必需角色的主题也能正常工作。
	/// </summary>
	public static readonly FrozenDictionary<string, string> OptionalFallbacks =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[ScrollbarTrack] = Muted,
			[ScrollbarThumb] = Text,
			[SearchMatchBg] = SelectedBg,
			[SearchMatchText] = Text,
			[ThinkingMax] = ThinkingXhigh,
		}.ToFrozenDictionary(StringComparer.Ordinal);

	/// <summary>桌面配色映射需要的关键角色；缺失时记一条警告。</summary>
	public static readonly FrozenSet<string> CoreTokens = new[]
	{
		Text,
		Muted,
		Border,
		Accent,
		Error,
		Success,
		UserMessageBg,
		ToolPendingBg,
	}.ToFrozenSet(StringComparer.Ordinal);
}
