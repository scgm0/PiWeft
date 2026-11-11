using System.Text.RegularExpressions;

namespace PiWeft.Desktop;

internal sealed partial record MessageLinkTarget(string Target, bool IsWeb, int? Line = null, int? Column = null)
{
	// 这两个形状在每次渲染链接时都要判一次：源生成正则不走全局缓存查表，也能让裁剪器
	// 保住这条实现（静态 Regex.IsMatch/Match 在 AOT 下要靠解释式编译）。
	[GeneratedRegex(@"^[^:]+\.[^:/]+:\d+(?::\d+)?$", RegexOptions.CultureInvariant)]
	private static partial Regex LocatedFile();

	[GeneratedRegex(@"(?::(\d+)(?::(\d+))?|#L(\d+)(?:C(\d+))?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex LineSuffix();

	public static MessageLinkTarget? Resolve(string url, string cwd)
	{
		try
		{
			var value = url.Trim();
			if (value.Length == 0 || value.StartsWith('#') || HasControl(value)) return null;
			var locatedFile = LocatedFile().IsMatch(value);
			if (!locatedFile && Uri.TryCreate(value, UriKind.Absolute, out var uri))
			{
				if (uri.Scheme is "http" or "https") return new(uri.AbsoluteUri, true);
				if (!uri.IsFile) return null;
				// Reject remote file shares. They must not silently send local credentials.
				if (uri.Host.Length > 0 && uri.Host != "localhost") return null;
				value = uri.LocalPath + uri.Fragment;
			}
			else value = Uri.UnescapeDataString(value);

			if (value.StartsWith("~/", StringComparison.Ordinal))
				value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
			var direct = Path.GetFullPath(value, cwd);
			if (File.Exists(direct) || Directory.Exists(direct)) return new(direct, false);

			int? line = null, column = null;
			var suffix = LineSuffix().Match(value);
			if (suffix.Success)
			{
				var lineText = suffix.Groups[1].Success ? suffix.Groups[1].Value : suffix.Groups[3].Value;
				var columnText = suffix.Groups[2].Success ? suffix.Groups[2].Value : suffix.Groups[4].Value;
				if (int.TryParse(lineText, out var parsedLine) && parsedLine > 0) line = parsedLine;
				if (int.TryParse(columnText, out var parsedColumn) && parsedColumn > 0) column = parsedColumn;
				value = value[..suffix.Index];
			}
			return new(Path.GetFullPath(value, cwd), false, line, column);
		}
		catch (Exception error) when (error is ArgumentException or NotSupportedException or UriFormatException or PathTooLongException)
		{
			return null;
		}
	}

	/// 控制字符不能进路径。按区间扫比 `value.Any(char.IsControl)` 省掉一个 CharEnumerator
	/// 与委托分配：`char.IsControl` 认的两段正好是 C0（含 DEL 之前的 0x00–0x1F）与 C1（0x7F–0x9F）。
	private static bool HasControl(string value) =>
		value.AsSpan().IndexOfAnyInRange('\0', '\u001F') >= 0 ||
		value.AsSpan().IndexOfAnyInRange('\u007F', '\u009F') >= 0;
}
