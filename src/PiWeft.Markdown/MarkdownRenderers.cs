using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Markdown;

/// <summary>
/// A block element produced by a host renderer can update itself in place across revisions;
/// without it the element is rebuilt whenever the block's model changes (which is what a
/// streamed custom block does anyway).
/// </summary>
public interface IMarkdownCustomView
{
	/// <summary>Applies the next model revision; returns false to fall back to a full rebuild.</summary>
	bool TryUpdate(MarkdownBlock block);
}

/// <summary>
/// Host-provided presentation for <see cref="MarkdownBlockKind.Custom"/> blocks, keyed by the
/// Markdig node type name (e.g. "MathBlock" when <see cref="MarkdownOptions.ConfigurePipeline"/>
/// enables mathematics). Unregistered kinds render as literal text.
/// </summary>
public sealed class MarkdownRenderers
{
	private readonly Dictionary<string, Func<MarkdownBlock, MarkdownRenderContext, FrameworkElement?>> _blocks =
		new(StringComparer.Ordinal);

	/// <summary>Registers the presenter for one Markdig node type; replaces an existing one.</summary>
	public MarkdownRenderers RegisterBlock(string markdigTypeName, Func<MarkdownBlock, MarkdownRenderContext, FrameworkElement?> factory)
	{
		ArgumentNullException.ThrowIfNull(markdigTypeName);
		ArgumentNullException.ThrowIfNull(factory);
		_blocks[markdigTypeName] = factory;
		return this;
	}

	public bool TryGetBlock(string kind, out Func<MarkdownBlock, MarkdownRenderContext, FrameworkElement?> factory)
		=> _blocks.TryGetValue(kind, out factory!);
}

/// <summary>Everything a host block renderer may touch for one block.</summary>
public sealed class MarkdownRenderContext(MarkdownView view, MarkdownTheme theme, MarkdownBlock block)
{
	public MarkdownView View { get; } = view;
	public MarkdownTheme Theme { get; } = theme;
	public MarkdownBlock Block { get; } = block;

	/// <summary>Renders the block's children with the built-in pipeline (custom containers).</summary>
	public FrameworkElement? RenderChildren() => View.RenderChildrenInternal(Block);
}
