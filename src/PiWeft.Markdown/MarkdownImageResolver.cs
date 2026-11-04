using Aprillz.MewUI;

namespace PiWeft.Markdown;

public sealed class MarkdownImageRequestedEventArgs(string url, Uri? resolvedUri, string? title, ImageSource? image) : EventArgs
{
	public string Url { get; } = url;
	public Uri? ResolvedUri { get; } = resolvedUri;
	public string? Title { get; } = title;

	public ImageSource? Image { get; } = image;
}

public sealed class MarkdownImageRequest(string url, Uri? resolvedUri, double maxWidth)
{
	public string Url { get; } = url;

	public Uri? ResolvedUri { get; } = resolvedUri;

	public double MaxWidth { get; } = maxWidth;
}

/// Host-controlled image loading. Without a resolver the renderer never touches the file system
/// or the network and images show their alt text. The resolver owns decoding, policy (schemes,
/// size limits) and caching; the view caches results per paragraph for the document's lifetime
/// and never disposes a returned source.
public interface IMarkdownImageResolver
{
	/// Returns the decoded image, or null when the request is unsupported or failed (the result
	/// is remembered, so a failing URL is not retried on every streaming frame).
	ValueTask<ImageSource?> ResolveAsync(MarkdownImageRequest request, CancellationToken cancellation);
}
