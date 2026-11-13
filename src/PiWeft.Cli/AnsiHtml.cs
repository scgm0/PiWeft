using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.Cli;

/// 把终端 ANSI 序列转成 HTML。pi 1.5.2+ 的 <c>export-html/ansi-to-html.ts</c> 同义：
/// SGR（标准色、256 色、真彩、粗体/暗色/斜体/下划线）转成内联样式，
/// OSC 8 超链接转成 <c>&lt;a&gt;</c>（只放行 http/https/mailto），
/// 其余 CSI / OSC / APC 序列与孤立 ESC 一律丢掉——否则导出文件里会露出 <c>[38;2;…m</c> 这样的乱码。
public static partial class AnsiHtml
{
	private static readonly string[] AnsiColors =
	[
		"#000000", "#800000", "#008000", "#808000", "#000080", "#800080", "#008080", "#c0c0c0",
		"#808080", "#ff0000", "#00ff00", "#ffff00", "#0000ff", "#ff00ff", "#00ffff", "#ffffff",
	];

	private sealed class TextStyle
	{
		public string? Foreground;

		public string? Background;

		public bool Bold;

		public bool Dim;

		public bool Italic;

		public bool Underline;

		public bool Any => Foreground is not null || Background is not null || Bold || Dim || Italic || Underline;

		public string ToCss()
		{
			var parts = new List<string>(6);
			if (Foreground is not null) parts.Add($"color:{Foreground}");
			if (Background is not null) parts.Add($"background-color:{Background}");
			if (Bold) parts.Add("font-weight:bold");
			if (Dim) parts.Add("opacity:0.6");
			if (Italic) parts.Add("font-style:italic");
			if (Underline) parts.Add("text-decoration:underline");
			return string.Join(';', parts);
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
	}

	/// 去掉所有转义序列与孤立 ESC，保留纯文本。
	public static string StripEscapes(string text) =>
		text.Length == 0 ? text : EscapePattern().Replace(text, string.Empty).Replace("\u001b", string.Empty);

	/// 转成 HTML。文本按行保留空白（由外层 <c>&lt;pre&gt;</c> 负责换行）。
	public static string ToHtml(string text)
	{
		if (text.Length == 0)
		{
			return text;
		}

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
			if (style.Any)
			{
				result.Append("<span style=\"").Append(style.ToCss()).Append("\">");
				inSpan = true;
			}
		}

		foreach (Match match in EscapePattern().Matches(text))
		{
			if (match.Index > cursor)
			{
				result.Append(EscapeHtml(text[cursor..match.Index].Replace("\u001b", string.Empty)));
			}

			cursor = match.Index + match.Length;
			var sgr = match.Groups[1];
			var osc = match.Groups[2];

			if (sgr.Success)
			{
				CloseSpan();
				ApplySgr(sgr.Value, style);
				OpenSpan();
				continue;
			}

			if (osc.Success)
			{
				var hyperlink = Osc8().Match(osc.Value);
				if (!hyperlink.Success)
				{
					continue;
				}

				// 浏览器会剥掉 URL 里的 C0 控制符：先清掉再判协议。
				var url = ControlCharacters().Replace(hyperlink.Groups[1].Value.Trim(), string.Empty);
				CloseSpan();
				if (inLink)
				{
					result.Append("</a>");
					inLink = false;
				}

				if (url.Length > 0 && SafeLink().IsMatch(url))
				{
					result.Append("<a href=\"").Append(EscapeHtml(url)).Append("\">");
					inLink = true;
				}

				OpenSpan();
			}

			// 其它 CSI / APC 序列直接丢弃。
		}

		if (cursor < text.Length)
		{
			result.Append(EscapeHtml(text[cursor..].Replace("\u001b", string.Empty)));
		}

		CloseSpan();
		if (inLink)
		{
			result.Append("</a>");
		}

		return result.ToString();
	}

	private static void ApplySgr(string parameters, TextStyle style)
	{
		var codes = parameters.Length == 0
			? [0]
			: parameters.Split(';').Select(static part => int.TryParse(part, out var value) ? value : 0).ToArray();

		for (var index = 0; index < codes.Length; index++)
		{
			var code = codes[index];
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
					index += ApplyExtended(codes, index, value => style.Foreground = value);
					break;
				case 39:
					style.Foreground = null;
					break;
				case >= 40 and <= 47:
					style.Background = AnsiColors[code - 40];
					break;
				case 48:
					index += ApplyExtended(codes, index, value => style.Background = value);
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
		}
	}

	/// 扩展色：<c>38;5;N</c> 与 <c>38;2;R;G;B</c>。返回额外消费的参数个数。
	private static int ApplyExtended(int[] codes, int index, Action<string> apply)
	{
		if (index + 1 >= codes.Length)
		{
			return 0;
		}

		if (codes[index + 1] == 5 && index + 2 < codes.Length)
		{
			apply(Color256(codes[index + 2]));
			return 2;
		}

		if (codes[index + 1] == 2 && index + 4 < codes.Length)
		{
			apply($"rgb({codes[index + 2]},{codes[index + 3]},{codes[index + 4]})");
			return 4;
		}

		return 0;
	}

	private static string Color256(int index)
	{
		if (index < 0)
		{
			index = 0;
		}

		if (index < 16)
		{
			return AnsiColors[index];
		}

		if (index < 232)
		{
			var cube = index - 16;
			var red = cube / 36;
			var green = cube % 36 / 6;
			var blue = cube % 6;
			return $"#{Component(red):x2}{Component(green):x2}{Component(blue):x2}";

			static int Component(int value) => value == 0 ? 0 : 55 + value * 40;
		}

		var gray = Math.Min(255, 8 + (index - 232) * 10);
		return $"#{gray:x2}{gray:x2}{gray:x2}";
	}

	private static string EscapeHtml(string text) =>
		text.Replace("&", "&amp;")
			.Replace("<", "&lt;")
			.Replace(">", "&gt;")
			.Replace("\"", "&quot;")
			.Replace("'", "&#039;");

	[GeneratedRegex("\u001b\\[([\\d;]*)m|\u001b\\[[0-?]*[ -/]*[@-~]|\u001b\\]([^\u0007\u001b]*)(?:\u0007|\u001b\\\\)|\u001b_[^\u0007\u001b]*(?:\u0007|\u001b\\\\)")]
	private static partial Regex EscapePattern();

	[GeneratedRegex("^8;[^;]*;(.*)$", RegexOptions.Singleline)]
	private static partial Regex Osc8();

	[GeneratedRegex("^(?:https?:|mailto:)", RegexOptions.IgnoreCase)]
	private static partial Regex SafeLink();

	[GeneratedRegex("[\\x00-\\x1f\\x7f]")]
	private static partial Regex ControlCharacters();
}
