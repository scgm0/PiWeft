using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// <summary>
/// 把 shell 输出整理成可以安全放进模型上下文、会话 JSONL 和 UI 的文本。
/// 对应 pi 的 <c>stripAnsi</c> + <c>sanitizeBinaryOutput</c>：
/// 命令可能打印二进制文件、PNG、进度条或彩色日志，NUL/ESC 进入上下文后，
/// 轻则污染渲染，重则被 provider 以 400 拒绝，整段会话再也发不出去。
/// <c>\r</c> 有意保留（pi 的 bash 工具同样保留原始输出，显示层再处理）。
/// </summary>
internal static partial class ShellTextSanitizer
{
	// 与 pi 的 utils/ansi.ts 保持同一套模式（ansi-regex 的移植）。
	private const string St = @"(?:\u0007|\u001B\u005C|\u009C)";
	private const string OscStart = @"\u001B\]";
	private const string CsiStart = @"(?:(?:\u001B\[|\u009B)[<=>]|[\u001B\u009B][[\]()#;?]*)(?:\d{1,4}(?:[;:]\d{0,4})*)?";
	private const string CsiFinal = @"[\dA-PR-TZcf-nq-uy=><~]";

	/// <summary>流式输出里最多扣住这么长的未完成转义序列，避免无界等待。</summary>
	private const int MaxPendingAnsiLength = 256;

	[GeneratedRegex("(?:" + OscStart + @"[\s\S]*?" + St + ")|" + CsiStart + CsiFinal, RegexOptions.CultureInvariant)]
	private static partial Regex AnsiRegex();

	[GeneratedRegex(
		"(?:" + OscStart + @"(?:[^\u0007\u009C\u001B]|\u001B(?!\\))*|" + CsiStart + ")$",
		RegexOptions.CultureInvariant)]
	private static partial Regex UnfinishedAnsiSuffixRegex();

	/// <summary>
	/// 把流式文本拆成「现在可以安全清洗的前缀」和「末尾尚未收完的 ANSI 序列」。
	/// 序列被拆在两个 chunk 里时（<c>ESC[</c> + <c>31m</c>），直接逐块 strip 会漏掉前半段。
	/// </summary>
	public static (string Complete, string Pending) SplitIncompleteAnsiSuffix(string value)
	{
		if (!value.Contains('\u001B') && !value.Contains('\u009B'))
		{
			return (value, string.Empty);
		}

		var windowStart = Math.Max(0, value.Length - MaxPendingAnsiLength);
		var window = windowStart == 0 ? value : value[windowStart..];
		var match = UnfinishedAnsiSuffixRegex().Match(window);
		if (!match.Success)
		{
			return (value, string.Empty);
		}

		var splitAt = windowStart + match.Index;
		return (value[..splitAt], value[splitAt..]);
	}

	public static string StripAnsi(string value)
	{
		if (!value.Contains('\u001B') && !value.Contains('\u009B'))
		{
			return value;
		}

		return AnsiRegex().Replace(value, string.Empty);
	}

	/// <summary>
	/// 去掉 ANSI 转义与 NUL/控制字符。保留 tab、CR、LF 与所有可见 Unicode。
	/// </summary>
	public static string Sanitize(string value)
	{
		if (value.Length == 0)
		{
			return value;
		}

		var stripped = StripAnsi(value);
		var needsWork = false;
		foreach (var ch in stripped)
		{
			if (IsRemovedControl(ch))
			{
				needsWork = true;
				break;
			}
		}

		if (!needsWork)
		{
			return stripped;
		}

		var builder = new StringBuilder(stripped.Length);
		foreach (var ch in stripped)
		{
			if (IsRemovedControl(ch))
			{
				continue;
			}

			builder.Append(ch);
		}

		return builder.ToString();
	}

	/// <summary>与 pi 的 sanitizeBinaryOutput 相同：保留 tab、LF、CR。</summary>
	private static bool IsRemovedControl(char ch) =>
		ch <= '\u0008' ||
		ch == '\u000B' ||
		ch == '\u000C' ||
		(ch >= '\u000E' && ch <= '\u001F') ||
		(ch >= '\uFFF9' && ch <= '\uFFFB');
}
