#if MEWUI_WIN32
using System.Runtime.InteropServices;
using Aprillz.MewUI.Resources;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// <summary>
/// Windows 的图片剪贴板：直接读写原生剪贴板。注册的 <c>"PNG"</c> 格式优先（浏览器、终端等现代程序都放它），
/// 只有 <c>CF_DIB</c> / <c>CF_DIBV5</c> 时先解出真彩像素再编成 PNG（读），或反过来写出像素格式（写）；
/// 复制文件（<c>CF_HDROP</c>）里的图片也会被附加。解码用 MewUI 的 <see cref="ImageDecoders"/>，
/// 编码只有 PNG 这一侧需要自己实现。
/// </summary>
internal sealed partial class Win32ClipboardImages : IClipboardImages
{
	private const uint CfDib = 8;
	private const uint CfHDrop = 15;
	private const uint CfDibV5 = 17;
	private const uint GmemMoveable = 0x0002;
	private const uint GmemZeroInit = 0x0040;
	private const uint MaxPath = 260;
	private const uint MaxFiles = 64;

	private readonly uint _png;
	private readonly uint _jfif;

	private Win32ClipboardImages(uint png, uint jfif)
	{
		_png = png;
		_jfif = jfif;
	}

	public static Win32ClipboardImages? Create()
	{
		if (!OperatingSystem.IsWindows()) return null;
		return new Win32ClipboardImages(RegisterClipboardFormat("PNG"), RegisterClipboardFormat("JFIF"));
	}

	public bool HasImage()
	{
		if (IsClipboardFormatAvailable(_png) || IsClipboardFormatAvailable(CfDibV5) || IsClipboardFormatAvailable(CfDib)) return true;
		// 复制的文件里也可能有图片，但这要打开剪贴板才知道是不是图片扩展名。
		return IsClipboardFormatAvailable(CfHDrop) && CopiedFiles().Any(IsImagePath);
	}

	public async Task<IReadOnlyList<ClipboardAttachment>> ReadAsync()
	{
		ClipboardPayload payload = ReadPayload();
		if (payload.MimeType is { } mimeType && payload.Data is { } data &&
			data.Length > 0 && data.Length <= ClipboardImages.MaxBytes && ClipboardImages.LooksLike(mimeType, data))
			return [new ClipboardAttachment(ClipboardImages.PastedName(mimeType), new ImageContent { Data = Convert.ToBase64String(data), MimeType = mimeType })];

		if (payload.Paths.Count == 0) return [];
		List<ClipboardAttachment> attachments = [];
		foreach (var path in payload.Paths)
		{
			try
			{
				var content = await Task.Run(() => ComposerDrop.ReadImageAsync(path)).ConfigureAwait(false);
				attachments.Add(new ClipboardAttachment(Path.GetFileName(path), content));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
				// 单个文件读不到不影响其余附件。
			}
		}

		return attachments;
	}

	public Task<bool> TrySetImageAsync(string mimeType, byte[] data)
	{
		if (_disposed || data.Length == 0 || data.Length > ClipboardImages.MaxBytes) return Task.FromResult(false);
		var format = FormatOf(mimeType);
		if (format == 0) return Task.FromResult(false);

		// 能解出像素就同时给出两种像素格式：CF_DIBV5 带 alpha，CF_DIB 是画图/Word 这类老程序唯一认的版本。
		PixelImage? pixels = null;
		if (string.Equals(mimeType, ClipboardImages.Mimes[0], StringComparison.OrdinalIgnoreCase) &&
			ImageDecoders.TryDecode(data, out var bitmap) && bitmap.WidthPx > 0 && bitmap.HeightPx > 0)
			pixels = new PixelImage(bitmap.WidthPx, bitmap.HeightPx, bitmap.Data);

		if (!OpenClipboard(0)) return Task.FromResult(false);
		try
		{
			EmptyClipboard();
			var written = WriteFormat(format, data);
			if (pixels is not null)
			{
				written |= WriteFormat(CfDib, DibCodec.WriteRgb(pixels));
				written |= WriteFormat(CfDibV5, DibCodec.Write(pixels));
			}

			return Task.FromResult(written);
		}
		finally { CloseClipboard(); }
	}

	private uint FormatOf(string mimeType)
	{
		if (string.Equals(mimeType, ClipboardImages.Mimes[0], StringComparison.OrdinalIgnoreCase)) return _png;
		if (string.Equals(mimeType, ClipboardImages.Mimes[1], StringComparison.OrdinalIgnoreCase)) return _jfif;
		// WebP / GIF 在 Windows 上没有通用注册格式，写进去也没有程序认。
		return 0;
	}

	/// <summary>剪贴板句柄只在持有期间有效，所以这里把字节与路径都复制出来再关闭。</summary>
	private ClipboardPayload ReadPayload()
	{
		if (!OpenClipboard(0)) return new(null, null, []);
		try
		{
			if (ReadBytes(_png) is { Length: > 0 } png) return new("image/png", png, []);
			foreach (var format in new[] { CfDibV5, CfDib })
			{
				if (ReadBytes(format) is not { Length: > 0 } dib) continue;
				if (!DibCodec.TryRead(dib, out var pixels) || pixels is null) continue;
				return new("image/png", PngCodec.Encode(pixels), []);
			}

			var drop = GetClipboardData(CfHDrop);
			return drop == 0 ? new(null, null, []) : new(null, null, CopiedFiles(drop).Where(IsImagePath).ToArray());
		}
		finally { CloseClipboard(); }
	}

	private byte[]? ReadBytes(uint format)
	{
		var handle = GetClipboardData(format);
		if (handle == 0) return null;
		var size = (long)(nuint)GlobalSize(handle);
		if (size <= 0 || size > ClipboardImages.MaxBytes) return null;
		var pointer = GlobalLock(handle);
		if (pointer == 0) return null;
		try
		{
			var data = new byte[size];
			Marshal.Copy(pointer, data, 0, data.Length);
			return data;
		}
		finally { GlobalUnlock(handle); }
	}

	private string[] CopiedFiles()
	{
		if (!OpenClipboard(0)) return [];
		try
		{
			var drop = GetClipboardData(CfHDrop);
			return drop == 0 ? [] : CopiedFiles(drop);
		}
		finally { CloseClipboard(); }
	}

	private static string[] CopiedFiles(nint drop)
	{
		var count = Math.Min(DragQueryFile(drop, uint.MaxValue, 0, 0), MaxFiles);
		List<string> paths = [];
		var buffer = Marshal.AllocHGlobal((int)MaxPath * sizeof(char));
		try
		{
			for (uint index = 0; index < count; index++)
			{
				if (DragQueryFile(drop, index, buffer, MaxPath) == 0) continue;
				if (Marshal.PtrToStringUni(buffer) is { Length: > 0 } path) paths.Add(path);
			}
		}
		finally { Marshal.FreeHGlobal(buffer); }

		return [.. paths];
	}

	private static bool IsImagePath(string path) => ComposerDrop.IsImage(path);

	private static bool WriteFormat(uint format, byte[] data)
	{
		var handle = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)data.Length);
		if (handle == 0) return false;
		var pointer = GlobalLock(handle);
		if (pointer == 0)
		{
			GlobalFree(handle);
			return false;
		}

		try { Marshal.Copy(data, 0, pointer, data.Length); }
		finally { GlobalUnlock(handle); }

		// 成功时句柄归系统所有，失败时仍由我们负责释放。
		if (SetClipboardData(format, handle) == 0)
		{
			GlobalFree(handle);
			return false;
		}

		return true;
	}

	public void Dispose() => _disposed = true;

	private bool _disposed;

	private sealed record ClipboardPayload(string? MimeType, byte[]? Data, IReadOnlyList<string> Paths);

	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool OpenClipboard(nint owner);

	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseClipboard();

	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EmptyClipboard();

	[LibraryImport("user32.dll", SetLastError = true)]
	private static partial nint GetClipboardData(uint format);

	[LibraryImport("user32.dll", SetLastError = true)]
	private static partial nint SetClipboardData(uint format, nint data);

	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsClipboardFormatAvailable(uint format);

	[LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial uint RegisterClipboardFormat(string format);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial nint GlobalAlloc(uint flags, nuint bytes);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial nint GlobalLock(nint memory);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GlobalUnlock(nint memory);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial nuint GlobalSize(nint memory);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial nint GlobalFree(nint memory);

	[LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
	private static partial uint DragQueryFile(nint drop, uint index, nint file, uint length);
}
#endif
