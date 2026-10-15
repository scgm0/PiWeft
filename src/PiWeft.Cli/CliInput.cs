using System.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Cli;

/// <summary>
/// CLI 输入装配：<c>@file</c> 附件、管道 stdin、系统提示词的文件/文本解析。
/// 对应 pi 的 <c>cli/file-processor.ts</c>、<c>cli/initial-message.ts</c> 与
/// <c>resolvePromptInput</c> 的最小实现，不引入图片缩放等额外依赖。
/// </summary>
internal static class CliInput
{
	/// <summary>与文件工具一致的图片扩展名到 MIME 的映射。</summary>
	private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		[".png"] = "image/png",
		[".jpg"] = "image/jpeg",
		[".jpeg"] = "image/jpeg",
		[".gif"] = "image/gif",
		[".webp"] = "image/webp",
		[".bmp"] = "image/bmp",
	};

	/// <summary>
	/// 解析 <c>--system-prompt</c> / <c>--append-system-prompt</c> 的值：
	/// 若是存在的文件路径则读取内容，否则按字面文本处理（与 pi 的 <c>resolvePromptInput</c> 一致）。
	/// </summary>
	public static string ResolvePromptInput(string value, string cwd)
	{
		if (value.Length == 0)
		{
			return value;
		}

		var path = TryResolveExistingFile(value, cwd);
		if (path is null)
		{
			return value;
		}

		try
		{
			return File.ReadAllText(path).TrimStart('\uFEFF');
		}
		catch (IOException)
		{
			return value;
		}
		catch (UnauthorizedAccessException)
		{
			return value;
		}
	}

	/// <summary>
	/// 把 <c>@file</c> 参数展开成初始提示词文本与图片附件。
	/// 文本文件包在 <c>&lt;file name="..."&gt;</c> 里，图片以内容块附带并留下同名占位引用。
	/// </summary>
	/// <exception cref="FileNotFoundException">参数指向的文件不存在。</exception>
	public static (string Text, List<ImageContent> Images) ProcessFiles(IReadOnlyList<string> fileArgs, string cwd)
	{
		if (fileArgs.Count == 0)
		{
			return (string.Empty, []);
		}

		var text = new StringBuilder();
		var images = new List<ImageContent>();

		foreach (var fileArg in fileArgs)
		{
			if (fileArg.Length == 0)
			{
				continue;
			}

			var path = ResolvePath(fileArg, cwd);
			if (!File.Exists(path))
			{
				throw new FileNotFoundException($"File not found: {path}", path);
			}

			if (new FileInfo(path).Length == 0)
			{
				continue; // pi 跳过空文件
			}

			if (ImageMimeTypes.TryGetValue(Path.GetExtension(path), out var mimeType))
			{
				var bytes = File.ReadAllBytes(path);
				images.Add(new ImageContent { Data = Convert.ToBase64String(bytes), MimeType = mimeType });
				text.Append("<file name=\"").Append(path).Append("\"></file>\n");
				continue;
			}

			var content = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
			text.Append("<file name=\"").Append(path).Append("\">\n").Append(content).Append("\n</file>\n");
		}

		return (text.ToString(), images);
	}

	/// <summary>展开 <c>~</c> 并转成相对 <paramref name="cwd"/> 的绝对路径。</summary>
	public static string ResolvePath(string value, string cwd) =>
		Path.GetFullPath(UserPath.ExpandTilde(value), cwd);

	private static string? TryResolveExistingFile(string value, string cwd)
	{
		try
		{
			// 字面文本里可能含有换行或空字符，这些都不是合法路径，直接跳过。
			if (value.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0)
			{
				return null;
			}

			var resolved = ResolvePath(value, cwd);
			return File.Exists(resolved) ? resolved : null;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}
}
