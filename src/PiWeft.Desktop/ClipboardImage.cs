using PiWeft.Ai;

namespace PiWeft.Desktop;

/// <summary>从剪贴板读到的图片附件：显示名（含扩展名）与已编码内容块。</summary>
internal readonly record struct ClipboardAttachment(string Name, ImageContent Content);

/// <summary>
/// 系统剪贴板的图片读写端口。MewUI 的 <c>IClipboardService</c> 只覆盖文本，图片必须走各平台原生实现；
/// 与 PRIMARY 选区一样通过可替换的服务定位器接进界面，测试注入假实现即可覆盖交互。
/// </summary>
internal interface IClipboardImages : IDisposable
{
	/// <summary>剪贴板当前是否含图片；读取前先问一次，避免纯文本粘贴付出解码代价。</summary>
	bool HasImage();

	/// <summary>读出剪贴板中的图片；不可用或没有图片时返回空列表。</summary>
	Task<IReadOnlyList<ClipboardAttachment>> ReadAsync();

	/// <summary>把已编码的图片写入剪贴板；返回是否成功。</summary>
	Task<bool> TrySetImageAsync(string mimeType, byte[] data);
}

/// <summary>图片剪贴板的选择器与共用规则：支持的 MIME、扩展名、轻量签名校验。</summary>
internal static class ClipboardImages
{
	/// <summary>与拖放附件共用同一条体积上限。</summary>
	internal const int MaxBytes = ComposerDrop.MaxImageBytes;

	/// <summary>按优先级排列的可接收 MIME；provider 也只接受这几种（BMP 除外，见 <see cref="ComposerDrop.MimeType"/>）。</summary>
	internal static readonly string[] Mimes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

	private static readonly string[] Extensions = [".png", ".jpg", ".webp", ".gif"];

	/// <summary>界面使用的图片剪贴板服务；未装配（平台没有可用实现）时所有操作都退化为「不可用」。</summary>
	internal static IClipboardImages? Service { get; set; }

	/// <summary>
	/// 平台原生实现的构造点，按**构建期**目标平台选择，与 <c>Program.RegisterPlatform</c> 的约定一致。
	/// 返回 null 表示该平台没有图片剪贴板实现，界面按「不可用」处理并保留原有文本粘贴。
	/// </summary>
	internal static IClipboardImages? Create()
	{
#if MEWUI_WIN32
		return Win32ClipboardImages.Create();
#elif MEWUI_MACOS
		return MacClipboardImages.Create();
#elif MEWUI_X11
		return X11ClipboardImages.Create();
#else
		return null;
#endif
	}

	internal static string Extension(string mimeType)
	{
		var index = Array.FindIndex(Mimes, mime => string.Equals(mime, mimeType, StringComparison.OrdinalIgnoreCase));
		return index < 0 ? ".png" : Extensions[index];
	}

	/// <summary>粘贴得到的附件名：与拖放一致地用随机后缀，避免多次粘贴互相覆盖。</summary>
	internal static string PastedName(string mimeType) => "pasted-" + Guid.NewGuid().ToString("N")[..8] + Extension(mimeType);

	/// <summary>
	/// 声明的 MIME 与其字节内容是否相符。剪贴板 owner 常常只是「声明」了某个类型，
	/// 读回来才发现是空数据或别的东西，签名校验让这些情况退回文本粘贴而不是粘上坏图。
	/// </summary>
	internal static bool LooksLike(string mimeType, ReadOnlySpan<byte> data)
	{
		if (data.Length < 4) return false;
		if (string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase))
			return PngCodec.IsPng(data);
		if (string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
			return data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
		if (string.Equals(mimeType, "image/gif", StringComparison.OrdinalIgnoreCase))
			return data[..3].SequenceEqual("GIF"u8);
		if (string.Equals(mimeType, "image/webp", StringComparison.OrdinalIgnoreCase))
			return data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);
		return false;
	}
}
