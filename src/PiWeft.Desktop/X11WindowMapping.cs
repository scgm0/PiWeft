#if MEWUI_X11
using System.Runtime.InteropServices;

namespace PiWeft.Desktop;

/// <summary>X11 下把已隐藏的窗口重新映射到屏幕。</summary>
internal static partial class X11WindowMapping
{
	private const string LibX11 = "libX11.so.6";

	/// <summary>
	/// MewUI 的 X11 后端在 Hide 后仍记住窗口已呈现，再 Show 时 PresentSurface 早退，窗口保持 unmapped。
	/// Win32 后端会重发 SW_SHOW，macOS 的 Activate 会 orderFront，只有 X11 需要自己映射。
	/// 用独立的显示连接映射同一个窗口，保留窗口的状态、位置与尺寸；上游修好这条路径后可删。
	/// </summary>
	public static void Map(nint handle)
	{
		if (handle == 0)
		{
			return;
		}

		var display = XOpenDisplay(0);
		if (display == 0)
		{
			return;
		}

		try
		{
			_ = XMapWindow(display, handle);
			// XSync 保证服务器已处理 map：紧接的 Activate 会查 map_state 才设焦点。
			_ = XSync(display, 0);
		}
		finally
		{
			_ = XCloseDisplay(display);
		}
	}

	[LibraryImport(LibX11, EntryPoint = "XOpenDisplay")]
	private static partial nint XOpenDisplay(nint name);

	[LibraryImport(LibX11, EntryPoint = "XMapWindow")]
	private static partial int XMapWindow(nint display, nint window);

	[LibraryImport(LibX11, EntryPoint = "XSync")]
	private static partial int XSync(nint display, int discard);

	[LibraryImport(LibX11, EntryPoint = "XCloseDisplay")]
	private static partial int XCloseDisplay(nint display);
}
#else
namespace PiWeft.Desktop;

/// <summary>非 X11 平台不需要补偿：Show 或 Activate 会重新显示窗口。</summary>
internal static class X11WindowMapping
{
	public static void Map(nint handle)
	{
	}
}
#endif
