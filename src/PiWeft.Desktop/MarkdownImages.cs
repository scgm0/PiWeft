using System.Text;
using Aprillz.MewUI;
using PiWeft.Markdown;

namespace PiWeft.Desktop;

/// <summary>
/// Loads images referenced by markdown: data URIs, local files and http(s), each with a size cap.
/// Results — including failures — are cached by URL, so a streaming frame never re-fetches and a
/// broken URL costs one request. The cache is bounded; evicted entries re-resolve on demand.
/// </summary>
internal sealed class MarkdownImageLoader : IMarkdownImageResolver
{
	private const int MaxBytes = 8 << 20;
	private const int MaxCachedEntries = 48;
	private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

	public static readonly MarkdownImageLoader Shared = new();

	private readonly object _lock = new();
	private readonly Dictionary<string, ImageSource?> _cache = [];
	private readonly LinkedList<string> _lru = [];
	private HttpClient? _http;

	public async ValueTask<ImageSource?> ResolveAsync(MarkdownImageRequest request, CancellationToken cancellation)
	{
		string key = request.ResolvedUri?.ToString() ?? request.Url;
		lock (_lock)
		{
			if (_cache.TryGetValue(key, out ImageSource? cached))
			{
				Touch(key);
				return cached;
			}
		}

		ImageSource? source;
		try
		{
			source = await LoadAsync(request, cancellation);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			source = null;
		}

		lock (_lock)
		{
			_cache[key] = source;
			_lru.AddLast(key);
			while (_lru.Count > MaxCachedEntries && _lru.First is { } oldest)
			{
				_cache.Remove(oldest.Value);
				_lru.RemoveFirst();
			}
		}
		return source;
	}

	private async ValueTask<ImageSource?> LoadAsync(MarkdownImageRequest request, CancellationToken cancellation)
	{
		// Data URIs parse from the raw source text: canonicalizing them through Uri can mangle
		// base64 payloads.
		if (request.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
		{
			string dataUri = request.Url;
			byte[] bytes = DataUriBytes(dataUri, out string mimeType);
			if (bytes.Length == 0)
			{
				return null;
			}
			// ChatImages.Source registers the original bytes for the preview's copy action and
			// dedupes identical payloads with attachment images.
			int comma = dataUri.IndexOf(',');
			return ChatImages.Source(new ChatImage(dataUri[(comma + 1)..], mimeType));
		}
		if (request.ResolvedUri is not { } uri)
		{
			return null;
		}
		if (uri.Scheme is "file")
		{
			var info = new FileInfo(uri.LocalPath);
			if (!info.Exists || info.Length is 0 or > MaxBytes)
			{
				return null;
			}
			return ImageSource.FromFile(info.FullName);
		}
		if (uri.Scheme is "http" or "https")
		{
			HttpClient client = _http ??= new() { Timeout = HttpTimeout };
			using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
			if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxBytes)
			{
				return null;
			}
			// Content-Length 无上限声明（分块传输）时逐块读取并在上限处截停，
			// 不能先整包读入再检查。
			if (response.Content.Headers.ContentLength is null)
			{
				await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
				var bounded = new MemoryStream();
				byte[] buffer = new byte[64 * 1024];
				int read;
				while ((read = await stream.ReadAsync(buffer, cancellation)) > 0)
				{
					if (bounded.Length + read > MaxBytes)
					{
						return null;
					}
					bounded.Write(buffer, 0, read);
				}
				return bounded.Length == 0 ? null : ImageSource.FromBytes(bounded.ToArray());
			}
			byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
			return bytes.Length is 0 or > MaxBytes ? null : ImageSource.FromBytes(bytes);
		}
		return null;
	}

	internal static byte[] DataUriBytes(string dataUri, out string mimeType)
	{
		// data:image/png;base64,<payload>
		mimeType = string.Empty;
		int header = dataUri.IndexOf(':');
		int comma = dataUri.IndexOf(',');
		if (header < 0 || comma < header)
		{
			return [];
		}
		string meta = dataUri[(header + 1)..comma];
		int semicolon = meta.IndexOf(';');
		mimeType = semicolon > 0 ? meta[..semicolon] : meta;
		if (!meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
		{
			// URL-encoded payload (e.g. an SVG fragment): no base64 involved.
			return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(dataUri[(comma + 1)..]));
		}
		string payload = dataUri[(comma + 1)..];
		try
		{
			return payload.Length is 0 or > (MaxBytes * 4 / 3 + 4)
				? []
				: Convert.FromBase64String(payload);
		}
		catch (FormatException)
		{
			return [];
		}
	}

	private void Touch(string key)
	{
		_lru.Remove(key);
		_lru.AddLast(key);
	}
}
