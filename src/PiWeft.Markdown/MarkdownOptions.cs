using Markdig;

namespace PiWeft.Markdown;

/// <summary>Immutable parsing options; changing them reparses the document.</summary>
public sealed record MarkdownOptions
{
	public bool UsePipeTables { get; init; } = true;
	public bool UseTaskLists { get; init; } = true;
	public bool UseAutoLinks { get; init; } = true;
	public bool UseStrikethrough { get; init; } = true;
	public bool UseInserted { get; init; } = true;
	public bool UseMarked { get; init; } = true;
	public bool SoftBreakAsNewLine { get; init; }

	/// <summary>
	/// Renders a strict inline HTML whitelist as native text formatting instead of literal tags:
	/// br (hard break), b/strong, i/em, u, s/strike/del, mark, ins, code/tt, sub/sup, and the
	/// transparent span/font. Whole inline comments are hidden. Attributes are never interpreted
	/// and structural HTML stays literal, so nothing can introduce links, images or scripts.
	/// </summary>
	public bool UseHtmlFormatting { get; init; } = true;

	/// <summary>
	/// Extra Markdig extensions (mathematics, footnotes, ...), applied after the built-in
	/// switches. Blocks without a built-in mapping become <see cref="MarkdownBlockKind.Custom"/>
	/// blocks keyed by the Markdig node type name, which host <see cref="MarkdownRenderers"/> can
	/// present. Configure the pipeline once per options instance: instances compare by value for
	/// the diff, so a delegate makes comparisons reference-based.
	/// </summary>
	public Action<MarkdownPipelineBuilder>? ConfigurePipeline { get; init; }

	/// <summary>
	/// Link scheme allowlist ("http", "https", ...). Null keeps every link. When set, absolute
	/// links with another scheme render as plain text and image runs with such URLs are never
	/// resolved; anchors and relative URLs always pass — the host still decides what to open.
	/// </summary>
	public IReadOnlySet<string>? LinkSchemes { get; init; }
}
