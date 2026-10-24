using Aprillz.MewUI;

namespace PiWeft.Markdown;

/// <summary>Raised when the user activates a rendered image; the host decides what to show.</summary>
public sealed class MarkdownImageRequestedEventArgs(string url, Uri? resolvedUri, string? title, ImageSource? image) : EventArgs
{
	public string Url { get; } = url;
	public Uri? ResolvedUri { get; } = resolvedUri;
	public string? Title { get; } = title;

	/// <summary>The resolved source at click time; null when the image never resolved.</summary>
	public ImageSource? Image { get; } = image;
}

/// <summary>One image load request from the renderer.</summary>
public sealed class MarkdownImageRequest(string url, Uri? resolvedUri, double maxWidth)
{
	/// <summary>Raw image URL from the source.</summary>
	public string Url { get; } = url;

	/// <summary>URL resolved against the view's BaseUri, when it resolves.</summary>
	public Uri? ResolvedUri { get; } = resolvedUri;

	/// <summary>Available content width in DIPs; NaN before the first layout.</summary>
	public double MaxWidth { get; } = maxWidth;
}

/// <summary>
/// Host-controlled image loading. Without a resolver the renderer never touches the file system
/// or the network and images show their alt text. The resolver owns decoding, policy (schemes,
/// size limits) and caching; the view caches results per paragraph for the document's lifetime
/// and never disposes a returned source.
/// </summary>
public interface IMarkdownImageResolver
{
	/// <summary>
	/// Returns the decoded image, or null when the request is unsupported or failed (the result
	/// is remembered, so a failing URL is not retried on every streaming frame).
	/// </summary>
	ValueTask<ImageSource?> ResolveAsync(MarkdownImageRequest request, CancellationToken cancellation);
}
