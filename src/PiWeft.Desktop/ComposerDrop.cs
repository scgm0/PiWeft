using Aprillz.MewUI.Platform;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// <summary>Snapshots native drop data before the drag ends; file reads are bounded and asynchronous.</summary>
internal static class ComposerDrop
{
	public const int MaxImageBytes = 10 * 1024 * 1024;
	public const int MaxTotalBytes = 20 * 1024 * 1024;
	public const int MaxImages = 10;

	/// <summary>Native drag-over advertises formats before the payload can be read.</summary>
	public static bool CanAccept(IDataObject data) => data.Contains(StandardDataFormats.StorageItems) ||
		data.Contains(StandardDataFormats.Uris) || data.Contains(StandardDataFormats.Text) ||
		new[] { "image/png", "image/jpeg", "image/webp", "image/gif" }.Any(data.Contains);

	public static IReadOnlyList<string> Paths(IDataObject data, string cwd)
	{
		IEnumerable<string> sources = [];
		if (data.TryGetData(DataFormats.StorageItems, out IReadOnlyList<string>? items) && items.Count > 0) sources = items;
		else if (data.TryGetData(DataFormats.Uris, out IReadOnlyList<string>? uris) && uris.Count > 0) sources = uris;
		else if (data.TryGetData(DataFormats.Text, out string? text)) sources = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
		var paths = new List<string>();
		foreach (var source in sources.Take(100))
		{
			var value = source.Trim().Trim('"', '\'');
			if (value.Length == 0 || value.StartsWith('#')) continue;
			try
			{
				if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) value = new Uri(value).LocalPath;
				else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !Path.IsPathFullyQualified(value)) continue;
				var path = Path.GetFullPath(value, cwd);
				if (File.Exists(path) || Directory.Exists(path)) paths.Add(path);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UriFormatException) { }
		}
		return paths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
	}

	public static (string Path, ImageContent Content)? RawImage(IDataObject data)
	{
		foreach (var (format, extension) in new[] { ("image/png", ".png"), ("image/jpeg", ".jpg"), ("image/webp", ".webp"), ("image/gif", ".gif") })
		{
			if (data.TryGetData(format, out byte[]? bytes))
			{
				if (bytes.Length == 0 || bytes.Length > MaxImageBytes) throw new IOException(Strings.AttachmentLimit);
				return ("dropped-" + Guid.NewGuid().ToString("N")[..8] + extension,
					new ImageContent { Data = Convert.ToBase64String(bytes), MimeType = format });
			}
		}
		return null;
	}

	public static string Reference(string path) => "@\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
	public static bool IsImage(string path) => MimeType(path) is not null;
	public static string? MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
	{
		".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
		".webp" => "image/webp", ".bmp" => "image/bmp", _ => null,
	};

	public static async Task<ImageContent> ReadImageAsync(string path)
	{
		await using var stream = File.OpenRead(path);
		if (stream.Length > MaxImageBytes) throw new IOException(Strings.AttachmentLimit);
		using var buffer = new MemoryStream();
		var chunk = new byte[8192];
		int count;
		while ((count = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + count > MaxImageBytes) throw new IOException(Strings.AttachmentLimit);
			buffer.Write(chunk, 0, count);
		}
		return new ImageContent { Data = Convert.ToBase64String(buffer.ToArray()), MimeType = MimeType(path)! };
	}
}
