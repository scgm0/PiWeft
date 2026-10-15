#if MEWUI_MACOS
using System.Runtime.InteropServices;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// <summary>
/// macOS 的图片剪贴板：直接走 NSPasteboard。读取按 public.png、public.jpeg、public.tiff 的顺序尝试，
/// TIFF（系统截图默认放的就是它）用 NSBitmapImageRep 转成 PNG 再返回；写入用与 MIME 对应的 UTI。
/// Objective-C 互操作与 MewUI 的 macOS 平台层同构（libobjc + objc_msgSend），每次调用自带 autorelease pool。
/// </summary>
internal sealed partial class MacClipboardImages : IClipboardImages
{
	/// <summary>NSBitmapImageFileTypePNG。</summary>
	private const ulong BitmapFileTypePng = 4;

	private static readonly string[] Readable = ["public.png", "public.jpeg", "public.tiff"];

	private static readonly string[] Writable = ["public.png", "public.jpeg"];

	private bool _disposed;

	public static MacClipboardImages? Create()
	{
		if (!OperatingSystem.IsMacOS()) return null;
		// AppKit 的类在托管进程里必须先显式加载框架，objc_getClass 才找得到它们。
		NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _);
		NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _);
		return ObjC.GetClass("NSPasteboard") == 0 ? null : new MacClipboardImages();
	}

	public bool HasImage()
	{
		if (_disposed) return false;
		using var pool = new ObjC.Pool();
		var pasteboard = Pasteboard();
		if (pasteboard == 0) return false;
		return Readable.Any(type => Data(pasteboard, type) != 0);
	}

	public Task<IReadOnlyList<ClipboardAttachment>> ReadAsync()
	{
		if (_disposed) return Task.FromResult<IReadOnlyList<ClipboardAttachment>>([]);
		using var pool = new ObjC.Pool();
		var pasteboard = Pasteboard();
		if (pasteboard == 0) return Task.FromResult<IReadOnlyList<ClipboardAttachment>>([]);

		foreach (var type in Readable)
		{
			var data = Data(pasteboard, type);
			if (data == 0) continue;
			var (mimeType, bytes) = type == "public.tiff" ? ToPng(data) : (MimeOf(type), Bytes(data));
			if (mimeType is null || bytes is null || bytes.Length == 0 ||
				bytes.Length > ClipboardImages.MaxBytes || !ClipboardImages.LooksLike(mimeType, bytes)) continue;
			IReadOnlyList<ClipboardAttachment> attachments =
			[
				new ClipboardAttachment(ClipboardImages.PastedName(mimeType),
					new ImageContent { Data = Convert.ToBase64String(bytes), MimeType = mimeType }),
			];
			return Task.FromResult(attachments);
		}

		return Task.FromResult<IReadOnlyList<ClipboardAttachment>>([]);
	}

	public Task<bool> TrySetImageAsync(string mimeType, byte[] data)
	{
		if (_disposed || data.Length == 0 || data.Length > ClipboardImages.MaxBytes) return Task.FromResult(false);
		var type = Writable.FirstOrDefault(candidate => string.Equals(MimeOf(candidate), mimeType, StringComparison.OrdinalIgnoreCase));
		if (type is null) return Task.FromResult(false);

		using var pool = new ObjC.Pool();
		var pasteboard = Pasteboard();
		if (pasteboard == 0) return Task.FromResult(false);
		ObjC.MsgSend_void(pasteboard, ObjC.Sel("clearContents"));
		var nsData = ObjC.CreateData(data);
		if (nsData == 0) return Task.FromResult(false);
		var nsType = ObjC.CreateNSString(type);
		return Task.FromResult(nsType != 0 && ObjC.MsgSend_bool_nint_nint(pasteboard, ObjC.Sel("setData:forType:"), nsData, nsType));
	}

	/// <summary>系统截图把 TIFF 放上剪贴板，交给 NSBitmapImageRep 转一次 PNG。</summary>
	private static (string? MimeType, byte[]? Data) ToPng(nint tiff)
	{
		var bitmapClass = ObjC.GetClass("NSBitmapImageRep");
		if (bitmapClass == 0) return (null, null);
		// alloc/init 的对象归我们所有，必须显式释放；其余走 autorelease pool。
		var rep = ObjC.MsgSend_nint_nint(ObjC.MsgSend_nint(bitmapClass, ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), tiff);
		if (rep == 0) return (null, null);
		try
		{
			var properties = ObjC.MsgSend_nint(ObjC.GetClass("NSDictionary"), ObjC.Sel("dictionary"));
			var png = ObjC.MsgSend_nint_nint_ulong(rep, ObjC.Sel("representationUsingType:properties:"), BitmapFileTypePng, properties);
			return png == 0 ? (null, null) : ("image/png", Bytes(png));
		}
		finally
		{
			ObjC.MsgSend_void(rep, ObjC.Sel("release"));
		}
	}

	private static nint Pasteboard() =>
		ObjC.MsgSend_nint(ObjC.GetClass("NSPasteboard"), ObjC.Sel("generalPasteboard"));

	private static nint Data(nint pasteboard, string type)
	{
		var name = ObjC.CreateNSString(type);
		return name == 0 ? 0 : ObjC.MsgSend_nint_nint(pasteboard, ObjC.Sel("dataForType:"), name);
	}

	private static byte[]? Bytes(nint data)
	{
		var length = ObjC.MsgSend_ulong(data, ObjC.Sel("length"));
		if (length == 0 || length > ClipboardImages.MaxBytes) return null;
		var pointer = ObjC.MsgSend_nint(data, ObjC.Sel("bytes"));
		if (pointer == 0) return null;
		var buffer = new byte[length];
		Marshal.Copy(pointer, buffer, 0, buffer.Length);
		return buffer;
	}

	private static string? MimeOf(string type) => type switch
	{
		"public.png" => "image/png",
		"public.jpeg" => "image/jpeg",
		_ => null,
	};

	public void Dispose() => _disposed = true;

	/// <summary>只声明用得到的 objc 入口；放在这里以免与平台层的内部互操作耦合。</summary>
	private static unsafe partial class ObjC
	{
		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
		private static partial nint objc_getClass(byte* name);

		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
		private static partial nint sel_registerName(byte* name);

		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
		private static partial nint objc_msgSend_nint(nint receiver, nint selector);

		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
		private static partial nint objc_msgSend_nint_nint(nint receiver, nint selector, nint argument);

		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
		private static partial nint objc_msgSend_nint_nint_ulong(nint receiver, nint selector, ulong first, nint second);

		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
		private static partial ulong objc_msgSend_ulong(nint receiver, nint selector);

		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
		private static partial byte objc_msgSend_byte_nint_nint(nint receiver, nint selector, nint first, nint second);

		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
		private static partial void objc_msgSend_void(nint receiver, nint selector);

		[LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
		private static partial nint objc_msgSend_nint_bytePtr_ulong(nint receiver, nint selector, byte* bytes, nuint length);

		public static nint GetClass(string name)
		{
			var utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
			fixed (byte* pointer = utf8) return objc_getClass(pointer);
		}

		public static nint Sel(string name)
		{
			var utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
			fixed (byte* pointer = utf8) return sel_registerName(pointer);
		}

		public static nint CreateNSString(string text)
		{
			var utf8 = System.Text.Encoding.UTF8.GetBytes(text + "\0");
			fixed (byte* pointer = utf8)
				return objc_msgSend_nint_nint(GetClass("NSString"), Sel("stringWithUTF8String:"), (nint)pointer);
		}

		public static nint CreateData(byte[] content)
		{
			fixed (byte* pointer = content)
				return objc_msgSend_nint_bytePtr_ulong(GetClass("NSData"), Sel("dataWithBytes:length:"), pointer, (nuint)content.Length);
		}

		public static nint MsgSend_nint(nint receiver, nint selector) => objc_msgSend_nint(receiver, selector);

		public static nint MsgSend_nint_nint(nint receiver, nint selector, nint argument) =>
			objc_msgSend_nint_nint(receiver, selector, argument);

		public static nint MsgSend_nint_nint_ulong(nint receiver, nint selector, ulong first, nint second) =>
			objc_msgSend_nint_nint_ulong(receiver, selector, first, second);

		public static ulong MsgSend_ulong(nint receiver, nint selector) => objc_msgSend_ulong(receiver, selector);

		public static bool MsgSend_bool_nint_nint(nint receiver, nint selector, nint first, nint second) =>
			objc_msgSend_byte_nint_nint(receiver, selector, first, second) != 0;

		public static void MsgSend_void(nint receiver, nint selector) => objc_msgSend_void(receiver, selector);

		/// <summary>临时对象的作用域；没有池时 Cocoa 会打印「autoreleased with no pool」并在进程生命周期内泄漏。</summary>
		public readonly struct Pool : IDisposable
		{
			private readonly nint _pool;

			public Pool() => _pool = objc_msgSend_nint(objc_msgSend_nint(GetClass("NSAutoreleasePool"), Sel("alloc")), Sel("init"));

			public void Dispose()
			{
				if (_pool != 0) objc_msgSend_void(_pool, Sel("drain"));
			}
		}
	}
}
#endif
