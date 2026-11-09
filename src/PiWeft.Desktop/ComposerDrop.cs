using Aprillz.MewUI.Platform;
using PiWeft.Ai;

namespace PiWeft.Desktop;

internal static class ComposerDrop
{
	public const int MaxImageBytes = 10 * 1024 * 1024;
	public const int MaxTotalBytes = 20 * 1024 * 1024;
	public const int MaxImages = 10;

	private static readonly (string Format, string Extension)[] ImageFormats =
	[
		("image/png", ".png"),
		("image/jpeg", ".jpg"),
		("image/webp", ".webp"),
		("image/gif", ".gif"),
	];

	private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;

	// DragOver 每秒触发几十次：静态格式表避免每次分配数组与委托。
	public static bool CanAccept(IDataObject data)
	{
		if (data.Contains(StandardDataFormats.StorageItems) ||
			data.Contains(StandardDataFormats.Uris) ||
			data.Contains(StandardDataFormats.Text))
		{
			return true;
		}

		foreach (var (format, _) in ImageFormats)
		{
			if (data.Contains(format))
			{
				return true;
			}
		}

		return false;
	}

	public static IReadOnlyList<string> Paths(IDataObject data, string cwd)
	{
		var paths = new List<string>();
		var seen = new HashSet<string>(PathComparer);
		if (data.TryGetData(DataFormats.StorageItems, out IReadOnlyList<string>? items) && items.Count > 0)
		{
			AddPaths(items, cwd, paths, seen);
		}
		else if (data.TryGetData(DataFormats.Uris, out IReadOnlyList<string>? uris) && uris.Count > 0)
		{
			AddPaths(uris, cwd, paths, seen);
		}
		else if (data.TryGetData(DataFormats.Text, out string? text))
		{
			// 只按需切前 100 行：大文本一次性 Split 会先分配整段行数组。
			var count = 0;
			foreach (var range in text.AsSpan().SplitAny(['\r', '\n']))
			{
				var (offset, length) = range.GetOffsetAndLength(text.Length);
				if (length == 0)
				{
					continue;
				}

				if (count++ >= 100)
				{
					break;
				}

				AddPath(text.AsSpan(offset, length), cwd, paths, seen);
			}
		}

		return paths;
	}

	public static (string Path, ImageContent Content)? RawImage(IDataObject data)
	{
		foreach (var (format, extension) in ImageFormats)
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
		// 按已知长度预分配：大图不再按 8 KiB 分段反复扩容并整段拷贝。
		using var buffer = new MemoryStream((int)stream.Length);
		var chunk = new byte[8192];
		int count;
		while ((count = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + count > MaxImageBytes) throw new IOException(Strings.AttachmentLimit);
			buffer.Write(chunk, 0, count);
		}
		return new ImageContent { Data = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length), MimeType = MimeType(path)! };
	}

	private static void AddPaths(IReadOnlyList<string> sources, string cwd, List<string> paths, HashSet<string> seen)
	{
		var count = Math.Min(sources.Count, 100);
		for (var index = 0; index < count; index++)
		{
			AddPath(sources[index], cwd, paths, seen);
		}
	}

	private static void AddPath(ReadOnlySpan<char> source, string cwd, List<string> paths, HashSet<string> seen)
	{
		var value = source.Trim().Trim("\"'");
		if (value.IsEmpty || value[0] == '#')
		{
			return;
		}

		var text = value.ToString();
		try
		{
			if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
			{
				text = new Uri(text).LocalPath;
			}
			else if (Uri.TryCreate(text, UriKind.Absolute, out _) && !Path.IsPathFullyQualified(text))
			{
				return;
			}

			var path = Path.GetFullPath(text, cwd);
			if ((File.Exists(path) || Directory.Exists(path)) && seen.Add(path))
			{
				paths.Add(path);
			}
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UriFormatException) { }
	}
}
