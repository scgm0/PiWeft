using Aprillz.MewUI;
using PiWeft.Markdown;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// The desktop markdown image loader: data URI parsing (base64 and URL-encoded), scheme
/// allowlisting, file loading with size caps, and per-instance result caching.
/// </summary>
public sealed class MarkdownImageLoaderTests
{
	// 1x1 transparent PNG.
	private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

	[Fact]
	public void DataUriParsingExtractsMimeAndBase64()
	{
		byte[] bytes = MarkdownImageLoader.DataUriBytes("data:image/png;base64," + Png1x1, out string mime);
		Assert.Equal("image/png", mime);
		Assert.Equal(Png1x1, Convert.ToBase64String(bytes));

		byte[] text = MarkdownImageLoader.DataUriBytes("data:image/svg+xml;utf8,%3Csvg/%3E", out string svgMime);
		Assert.Equal("image/svg+xml", svgMime);
		Assert.Equal("<svg/>", System.Text.Encoding.UTF8.GetString(text));

		Assert.Empty(MarkdownImageLoader.DataUriBytes("data:image/png;base64,!!!not-base64!!!", out _));
		Assert.Empty(MarkdownImageLoader.DataUriBytes("data:image/png", out _));
	}

	[Fact]
	public void DataUrisResolveAndCachePerInstance()
	{
		string png = Convert.ToBase64String(PngCodec.Encode(PixelImage.Create(1, 1)));
		var loader = new MarkdownImageLoader();
		var request = new MarkdownImageRequest("data:image/png;base64," + png, null, 520);
		ImageSource? first = loader.ResolveAsync(request, CancellationToken.None).AsTask().Result;
		Assert.NotNull(first);
		Assert.Equal(1, first.PixelWidth);

		ImageSource? second = loader.ResolveAsync(request, CancellationToken.None).AsTask().Result;
		Assert.Same(first, second);
	}

	[Fact]
	public void FileUrisLoadExistingFilesWithinTheSizeCap()
	{
		string path = Path.Combine(Path.GetTempPath(), $"piweft-md-img-{Guid.NewGuid():N}.png");
		File.WriteAllBytes(path, Convert.FromBase64String(Png1x1));
		try
		{
			var loader = new MarkdownImageLoader();
			var uri = new Uri(path);
			ImageSource? source = loader.ResolveAsync(new MarkdownImageRequest(uri.ToString(), uri, 520), CancellationToken.None)
				.AsTask().Result;
			Assert.NotNull(source);

			var missing = new Uri(path + ".missing.png");
			Assert.Null(loader.ResolveAsync(new MarkdownImageRequest(missing.ToString(), missing, 520), CancellationToken.None).AsTask().Result);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void UnsupportedSchemesResolveToNull()
	{
		var loader = new MarkdownImageLoader();
		var uri = new Uri("ftp://example.com/a.png");
		Assert.Null(loader.ResolveAsync(new MarkdownImageRequest(uri.ToString(), uri, 520), CancellationToken.None).AsTask().Result);
	}

	[Fact]
	public void UnresolvableRequestsResolveToNull()
	{
		var loader = new MarkdownImageLoader();
		Assert.Null(loader.ResolveAsync(new MarkdownImageRequest("::bad uri::", null, 520), CancellationToken.None).AsTask().Result);
	}
}
