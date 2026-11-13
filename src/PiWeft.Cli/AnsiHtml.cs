using System.Buffers;
using System.Text;

namespace PiWeft.Cli;

/// 把终端 ANSI 序列转成 HTML。pi 1.5.2+ 的 <c>export-html/ansi-to-html.ts</c> 同义：
/// SGR（标准色、256 色、真彩、粗体/暗色/斜体/下划线）转成内联样式，
/// OSC 8 超链接转成 <c>&lt;a&gt;</c>（只放行 http/https/mailto），
/// 其余 CSI / OSC / APC 序列与孤立 ESC 一律丢掉——否则导出文件里会露出 <c>[38;2;…m</c> 这样的乱码。
/// <para>
/// 用状态机而不是正则：工具结果动辄上万行，逐段 <c>Match</c> 会为每段分配子串，
/// 进度条这类每条行都带颜色的输出尤其贵。没有 ESC 的文本走一次 <see cref="string.IndexOf(char)" /> 直接返回。
/// </para>
public static class AnsiHtml
{
	private const char Escape = '\u001b';

	private const char Bell = '\u0007';

	private static readonly string[] AnsiColors =
	[
		"#000000", "#800000", "#008000", "#808000", "#000080", "#800080", "#008080", "#c0c0c0",
		"#808080", "#ff0000", "#00ff00", "#ffff00", "#0000ff", "#ff00ff", "#00ffff", "#ffffff",
	];

	/// 256 色表的 232..255 灰阶与 16..231 立方体是纯函数，先算好：每条 SGR 都会查一次。
	private static readonly string[] ExtendedColors = BuildExtendedColors();

	private static readonly SearchValues<char> HtmlEscapes = SearchValues.Create("&<>\"'");

	private static readonly SearchValues<char> ControlCharacters = SearchValues.Create(
		[.. Enumerable.Range(0, 0x20).Select(static code => (char)code), '\u007f']);

	private sealed class TextStyle
	{
		public string? Foreground;

		public string? Background;

		public bool Bold;

		public bool Dim;

		public bool Italic;

		public bool Underline;

		public bool Any => Foreground is not null || Background is not null || Bold || Dim || Italic || Underline;

		/// 直接写进结果缓冲：每条 SGR 都会重写一次样式，拼临时字符串不值当。
		public void AppendCss(StringBuilder target)
		{
			var first = true;
			if (Foreground is { } foreground)
			{
				Separate(target, "color:", foreground, ref first);
			}

			if (Background is { } background)
			{
				Separate(target, "background-color:", background, ref first);
			}

			if (Bold)
			{
				Separate(target, "font-weight:", "bold", ref first);
			}

			if (Dim)
			{
				Separate(target, "opacity:", "0.6", ref first);
			}

			if (Italic)
			{
				Separate(target, "font-style:", "italic", ref first);
			}

			if (Underline)
			{
				Separate(target, "text-decoration:", "underline", ref first);
			}
		}

		public void Reset()
		{
			Foreground = null;
			Background = null;
			Bold = false;
			Dim = false;
			Italic = false;
			Underline = false;
		}

		private static void Separate(StringBuilder target, string property, string value, ref bool first)
		{
			if (!first)
			{
				target.Append(';');
			}

			first = false;
			target.Append(property).Append(value);
		}
	}

	/// 去掉所有转义序列与孤立 ESC，保留纯文本。
	public static string StripEscapes(string text) =>
		text.Length == 0 || text.IndexOf(Escape) < 0 ? text : Render(text, html: false);

	/// 转成 HTML。文本按行保留空白（由外层 <c>&lt;pre&gt;</c> 负责换行）。
	public static string ToHtml(string text)
	{
		if (text.Length == 0)
		{
			return text;
		}

		return text.IndexOf(Escape) < 0 ? EscapeHtml(text) : Render(text, html: true);
	}

	private static string Render(string text, bool html)
	{
		var result = new StringBuilder(text.Length + 32);
		var style = new TextStyle();
		var inSpan = false;
		var inLink = false;
		var cursor = 0;

		void CloseSpan()
		{
			if (inSpan)
			{
				result.Append("</span>");
				inSpan = false;
			}
		}

		void OpenSpan()
		{
			if (html && style.Any)
			{
				result.Append("<span style=\"");
				style.AppendCss(result);
				result.Append("\">");
				inSpan = true;
			}
		}

		while (cursor < text.Length)
		{
			var start = text.IndexOf(Escape, cursor);
			if (start < 0)
			{
				AppendText(result, text.AsSpan(cursor), html);
				break;
			}

			if (start > cursor)
			{
				AppendText(result, text.AsSpan(cursor, start - cursor), html);
			}

			// 孤立 ESC：只丢这一个字符，后面的文本照常保留。
			if (start + 1 >= text.Length)
			{
				break;
			}

			cursor = start + 1;
			switch (text[cursor])
			{
				case '[':
					// CSI：参数字节 0x30-0x3F、中间字节 0x20-0x2F、终止字节 0x40-0x7E。
					var index = cursor + 1;
					while (index < text.Length && text[index] is >= '\u0030' and <= '\u003f')
					{
						index++;
					}

					while (index < text.Length && text[index] is >= '\u0020' and <= '\u002f')
					{
						index++;
					}

					if (index >= text.Length || text[index] is < '\u0040' or > '\u007e')
					{
						// 不是完整序列：丢掉 ESC，`[` 与后面按正文保留。
						continue;
					}

					var final = text[index];
					var parameters = text.AsSpan(cursor + 1, index - cursor - 1);
					cursor = index + 1;
					if (final != 'm')
					{
						continue;
					}

					CloseSpan();
					ApplySgr(parameters, style);
					OpenSpan();
					continue;

				case ']':
				case '_':
					// OSC / APC：BEL 或 ST（ESC \）收尾。OSC 8 是超链接，APC 一律丢弃。
					var isOsc = text[cursor] == ']';
					var terminated = TryScanControlString(text, start, cursor + 1, out var payload, out var next);
					cursor = next;
					if (!html || !terminated || !isOsc || !TryParseLink(payload, out var url))
					{
						continue;
					}

					CloseSpan();
					if (inLink)
					{
						result.Append("</a>");
						inLink = false;
					}

					if (url.Length > 0)
					{
						result.Append("<a href=\"").Append(EscapeHtml(url)).Append("\">");
						inLink = true;
					}

					OpenSpan();
					continue;

				default:
					// 其它 ESC 序列（如 `ESC ( B`）：只丢 ESC。
					continue;
			}
		}

		CloseSpan();
		if (inLink)
		{
			result.Append("</a>");
		}

		return result.ToString();
	}

	/// 扫到 BEL 或 ST 为止。未收尾时按「只丢 ESC」处理，游标停在 ESC 之后。
	private static bool TryScanControlString(
		string text,
		int escapeIndex,
		int start,
		out ReadOnlySpan<char> payload,
		out int next)
	{
		for (var index = start; index < text.Length; index++)
		{
			if (text[index] == Bell)
			{
				payload = text.AsSpan(start, index - start);
				next = index + 1;
				return true;
			}

			if (text[index] == Escape && index + 1 < text.Length && text[index + 1] == '\\')
			{
				payload = text.AsSpan(start, index - start);
				next = index + 2;
				return true;
			}
		}

		payload = default;
		next = escapeIndex + 1;
		return false;
	}

	/// OSC 8 的形状是 `8;参数;URL`；其它 OSC（改标题、改剪贴板）没有链接。
	private static bool TryParseLink(ReadOnlySpan<char> payload, out string url)
	{
		url = string.Empty;
		if (!payload.StartsWith("8;", StringComparison.Ordinal))
		{
			return false;
		}

		var separator = payload[2..].IndexOf(';');
		if (separator < 0)
		{
			return false;
		}

		// 浏览器会剥掉 URL 里的 C0 控制符：先清掉再判协议，否则 `java\tscript:` 能混过去。
		var raw = payload[(separator + 3)..].Trim();
		if (raw.IndexOfAny(ControlCharacters) >= 0)
		{
			var cleaned = new StringBuilder(raw.Length);
			foreach (var character in raw)
			{
				if (character is >= '\u0020' and not '\u007f')
				{
					cleaned.Append(character);
				}
			}

			raw = cleaned.ToString().AsSpan().Trim();
		}

		var text = raw.ToString();
		if (text.Length == 0 || !IsSafeLink(text))
		{
			return true;
		}

		url = text;
		return true;
	}

	/// 只放行 http/https/mailto：导出的 HTML 会在浏览器里打开，`javascript:` 与 `data:` 都不能落地。
	private static bool IsSafeLink(ReadOnlySpan<char> url)
	{
		foreach (var scheme in (ReadOnlySpan<string>)["https:", "http:", "mailto:"])
		{
			if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	private static void ApplySgr(ReadOnlySpan<char> parameters, TextStyle style)
	{
		if (parameters.IsEmpty)
		{
			style.Reset();
			return;
		}

		while (true)
		{
			var code = NextParameter(ref parameters);
			switch (code)
			{
				case 0:
					style.Reset();
					break;
				case 1:
					style.Bold = true;
					break;
				case 2:
					style.Dim = true;
					break;
				case 3:
					style.Italic = true;
					break;
				case 4:
					style.Underline = true;
					break;
				case 22:
					style.Bold = false;
					style.Dim = false;
					break;
				case 23:
					style.Italic = false;
					break;
				case 24:
					style.Underline = false;
					break;
				case >= 30 and <= 37:
					style.Foreground = AnsiColors[code - 30];
					break;
				case 38:
					ApplyExtended(ref parameters, ref style.Foreground);
					break;
				case 39:
					style.Foreground = null;
					break;
				case >= 40 and <= 47:
					style.Background = AnsiColors[code - 40];
					break;
				case 48:
					ApplyExtended(ref parameters, ref style.Background);
					break;
				case 49:
					style.Background = null;
					break;
				case >= 90 and <= 97:
					style.Foreground = AnsiColors[code - 90 + 8];
					break;
				case >= 100 and <= 107:
					style.Background = AnsiColors[code - 100 + 8];
					break;
			}

			if (parameters.IsEmpty)
			{
				return;
			}
		}
	}

	/// 扩展色：`38;5;N` 与 `38;2;R;G;B`，消费掉后面的参数。
	private static void ApplyExtended(ref ReadOnlySpan<char> parameters, ref string? slot)
	{
		if (parameters.IsEmpty)
		{
			return;
		}

		var mode = NextParameter(ref parameters);
		if (mode == 5 && !parameters.IsEmpty)
		{
			slot = Color256(NextParameter(ref parameters));
		}
		else if (mode == 2 && !parameters.IsEmpty)
		{
			var red = NextParameter(ref parameters);
			if (parameters.IsEmpty)
			{
				return;
			}

			var green = NextParameter(ref parameters);
			if (parameters.IsEmpty)
			{
				return;
			}

			var blue = NextParameter(ref parameters);
			slot = $"rgb({red},{green},{blue})";
		}
	}

	/// 取下一个 `;` 分隔的参数；空段与非法数字都按 0（与旧实现一致）。
	private static int NextParameter(ref ReadOnlySpan<char> parameters)
	{
		var separator = parameters.IndexOf(';');
		var token = separator < 0 ? parameters : parameters[..separator];
		parameters = separator < 0 ? default : parameters[(separator + 1)..];

		var value = 0;
		foreach (var character in token)
		{
			if (character is < '0' or > '9')
			{
				return 0;
			}

			// 越界的参数只会来自坏输出：夹到 0，别让一次乘法溢出成负色号。
			value = value > 9999 ? value : (value * 10) + (character - '0');
		}

		return value;
	}

	private static string Color256(int index) =>
		index >= 0 && index < ExtendedColors.Length ? ExtendedColors[index] : ExtendedColors[0];

	private static string[] BuildExtendedColors()
	{
		var colors = new string[256];
		Array.Copy(AnsiColors, colors, AnsiColors.Length);
		for (var index = 16; index < 232; index++)
		{
			var cube = index - 16;
			colors[index] = $"#{Component(cube / 36):x2}{Component(cube % 36 / 6):x2}{Component(cube % 6):x2}";
		}

		for (var index = 232; index < 256; index++)
		{
			var gray = Math.Min(255, 8 + ((index - 232) * 10));
			colors[index] = $"#{gray:x2}{gray:x2}{gray:x2}";
		}

		return colors;

		static int Component(int value) => value == 0 ? 0 : 55 + (value * 40);
	}

	private static void AppendText(StringBuilder result, ReadOnlySpan<char> text, bool html)
	{
		if (!html)
		{
			result.Append(text);
			return;
		}

		while (true)
		{
			var index = text.IndexOfAny(HtmlEscapes);
			if (index < 0)
			{
				result.Append(text);
				return;
			}

			result.Append(text[..index]);
			result.Append(text[index] switch
			{
				'&' => "&amp;",
				'<' => "&lt;",
				'>' => "&gt;",
				'"' => "&quot;",
				_ => "&#039;",
			});
			text = text[(index + 1)..];
		}
	}

	private static string EscapeHtml(ReadOnlySpan<char> text)
	{
		var result = new StringBuilder(text.Length);
		AppendText(result, text, html: true);
		return result.ToString();
	}
}
