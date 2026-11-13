using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent;

/// 会话标题输入的构造与清洗。PiDeck 的 `pi-deck-session-title.ts` 是独立 .ts 扩展，
/// 本端口把它做成会话原生行为；这里只放纯函数，便于逐条对照扩展的判定。
public static partial class AutoTitle
{
	/// 标题请求的 system prompt，与扩展逐字一致（32 字上限由提示约束，不硬截断）。
	public const string SystemPrompt =
		"You generate a concise title for a coding assistant conversation.\n" +
		"Return only one plain-text title on one line, with no explanation, quotes, Markdown, emoji, or \"Title:\" prefix.\n" +
		"Use the same language as the user's request when practical. Keep technical names that help identify the task.\n" +
		"Prefer a specific task summary over a generic title. Keep it within 32 characters.\n" +
		"Never repeat credentials, tokens, passwords, private keys, or other sensitive values.";

	public const int MaxUserInputChars = 1600;

	public const int MaxAssistantInputChars = 600;

	public const int MaxAttempts = 2;

	private static readonly HashSet<string> GenericTitles = new(StringComparer.OrdinalIgnoreCase)
	{
		"title",
		"session title",
		"conversation title",
		"new session",
		"untitled",
		"会话标题",
		"新会话",
		"未命名",
	};

	/// 标题请求的输入：<see cref="DirectTitle"/> 有值表示不必花一次模型请求。
	public sealed record TitleInput(string? DirectTitle, string? UserText, string? AssistantText);

	/// 从首条用户消息构造输入。消息只有引用块时直接返回块标签当标题。
	public static TitleInput? Build(string? firstUserText, string? firstAssistantText)
	{
		if (string.IsNullOrWhiteSpace(firstUserText) || IsCommandInput(firstUserText))
		{
			return null;
		}

		// 粘贴几 MB 的日志时别让四条块正则与五条脱敏正则扫全文：真正发出去的只有前
		// MaxUserInputChars 个码点，切在它 16 倍处不可能把要发送的片段切坏。
		var (text, blockOnly) = TitleTextFromUserInput(CapInput(firstUserText, MaxUserInputChars * 16));
		if (blockOnly)
		{
			return new TitleInput(text, null, null);
		}

		return new TitleInput(
			null,
			PrepareInputText(text, MaxUserInputChars),
			string.IsNullOrWhiteSpace(firstAssistantText)
				? null
				: PrepareInputText(CapInput(firstAssistantText, MaxAssistantInputChars * 16), MaxAssistantInputChars));
	}

	/// 斜杠命令开头的输入不参与命名：那是命令，不是任务描述。
	public static bool IsCommandInput(string text) => CommandInput().IsMatch(text);

	/// 剥掉自包含引用块，只留用户自己写的文字；块外没有文字时用块标签。
	/// 标签形式与扩展一致：<c>❝引用名</c> / <c>&amp;会话名</c> / <c>/skill:名</c> / <c>/模板名</c>。
	public static (string Text, bool BlockOnly) TitleTextFromUserInput(string text)
	{
		if (!text.Contains('<'))
		{
			return (text, false);
		}

		var blocks = new List<(int Start, int End, string Label)>();
		foreach (var (pattern, label) in BlockPatterns)
		{
			foreach (Match match in pattern.Matches(text))
			{
				blocks.Add((match.Index, match.Index + match.Length, label(match.Groups[1].Value)));
			}
		}

		if (blocks.Count == 0)
		{
			return (text, false);
		}

		// 外层块覆盖内层块：引用会话的正文里可能再嵌引用块。
		blocks.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : right.End.CompareTo(left.End));
		var outside = new StringBuilder();
		var labels = new List<string>();
		var cursor = 0;
		foreach (var block in blocks)
		{
			if (block.Start < cursor)
			{
				continue;
			}

			if (block.Start > cursor)
			{
				outside.Append(text, cursor, block.Start - cursor).Append(' ');
			}

			labels.Add(block.Label);
			cursor = block.End;
		}

		if (cursor < text.Length)
		{
			outside.Append(text, cursor, text.Length - cursor);
		}

		var outsideText = CollapseWhitespace(outside.ToString());
		return outsideText.Length > 0 ? (outsideText, false) : (string.Join(' ', labels), true);
	}

	/// 送去标题请求前的准备：压平空白、遮蔽凭据、按码点截断。
	public static string PrepareInputText(string text, int maxChars)
	{
		var normalized = CollapseWhitespace(text.Replace("\u0000", " ").Replace("\r\n", "\n").Replace('\r', '\n'));
		var redacted = RedactSensitiveText(normalized);
		var truncated = Truncate(redacted, maxChars);
		return truncated.Length > 0 ? truncated : "[redacted]";
	}

	/// 遮蔽常见凭据形态，避免标题请求重复携带秘密。
	public static string RedactSensitiveText(string text)
	{
		var redacted = PrivateKey().Replace(text, "[REDACTED SECRET]");
		redacted = Bearer().Replace(redacted, "Bearer [REDACTED]");
		redacted = UrlSecret().Replace(redacted, "$1[REDACTED]");
		redacted = SecretAssignment().Replace(redacted, "${prefix}[REDACTED]");
		return RawToken().Replace(redacted, "[REDACTED TOKEN]");
	}

	/// 清洗模型输出：单行、去掉引号/强调/前缀/emoji；不可用时返回 <see langword="null" />。
	public static string? CleanTitle(string raw)
	{
		var firstLine = raw
			.Replace("\r\n", "\n")
			.Replace('\r', '\n')
			.Split('\n')
			.Select(static line => line.Trim())
			.FirstOrDefault(static line => line.Length > 0 && !FenceLine().IsMatch(line));
		if (firstLine is null)
		{
			return null;
		}

		// 先剥强调标记：`**标题**` 的星号也是 LeadingDecoration 会吃的字符，
		// 顺序反了会只剩尾部星号，最后一步再剥已经配不成对。
		var title = StripMarkdownEmphasis(firstLine);
		title = FencePrefix().Replace(title, string.Empty);
		title = title.Replace("```", string.Empty);
		title = LeadingDecoration().Replace(title, string.Empty);
		title = TitlePrefix().Replace(title, string.Empty);
		title = title.Replace("`", string.Empty);
		title = StripPictographs(title);
		title = StripMarkdownEmphasis(StripWrappingQuotes(title));
		title = CollapseWhitespace(title);
		title = title.Trim(' ', ':', '：', '|');
		title = TrailingPunctuation().Replace(title, string.Empty).Trim();

		if (title.Length == 0)
		{
			return null;
		}

		// 模型输出也不可信：命中凭据模式就丢弃，不把脱敏占位符写进会话。
		if (!string.Equals(RedactSensitiveText(title), title, StringComparison.Ordinal))
		{
			return null;
		}

		// 按码点算长度：一个 emoji 或 CJK 扩展字不能算两个字符。
		if (title.EnumerateRunes().Count() < 2)
		{
			return null;
		}

		if (GenericTitles.Contains(title))
		{
			return null;
		}

		if (GenericPattern().IsMatch(title))
		{
			return null;
		}

		// 上下文代理接管的旁路响应是一段工具日志而不是标题：收下会永久污染会话名。
		if (LooksLikeToolMarkerTitle(title) || SourceMarkerHead().IsMatch(title))
		{
			return null;
		}

		return title;
	}

	/// 工具/上下文代理的确认标记行（`[ACP] acp_status result: …`）不是标题。
	public static bool LooksLikeToolMarkerTitle(string title)
	{
		var trimmed = StripLeadingPictographs(CollapseWhitespace(title));
		return MarkerTagTitle().IsMatch(trimmed);
	}

	/// 按码点截断，不在多字节字符中间切开。先算 UTF-16 下标再切一次子串：
	/// 逐码点 `rune.ToString()` 会为每个字符分配一个字符串。
	public static string Truncate(string text, int maxChars)
	{
		var index = 0;
		for (var count = 0; count < maxChars && index < text.Length; count++)
		{
			Rune.DecodeFromUtf16(text.AsSpan(index), out _, out var consumed);
			index += consumed;
		}

		return index >= text.Length ? text.TrimEnd() : text[..index].TrimEnd();
	}

	/// 粗切输入：只保证不切断代理对，正则与脱敏都在这个窗口内跑。
	private static string CapInput(string text, int maxChars) =>
		text.Length <= maxChars
			? text
			: text[..(char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars)];

	private static string CollapseWhitespace(string text) => Whitespace().Replace(text, " ").Trim();

	/// 去掉 emoji 与变体选择符。.NET 正则没有 Extended_Pictographic 属性，
	/// 用 Unicode 类别 OtherSymbol 覆盖同一批字符（含区域指示符与常见 emoji），
	/// 再单独清掉 FE0E/FE0F 变体选择符与 ZWJ。
	private static string StripPictographs(string text) => string.Concat(
		text.EnumerateRunes().Where(static rune => !IsPictograph(rune)).Select(static rune => rune.ToString()));

	private static string StripLeadingPictographs(string text)
	{
		var index = 0;
		var runes = text.EnumerateRunes().ToArray();
		while (index < runes.Length && (IsPictograph(runes[index]) || runes[index].Value is ' ' or '\t'))
		{
			index++;
		}

		return string.Concat(runes[index..].Select(static rune => rune.ToString()));
	}

	private static bool IsPictograph(Rune rune) =>
		rune.Value is 0xFE0E or 0xFE0F or 0x200D ||
		Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.OtherSymbol;

	private static string StripWrappingQuotes(string text)
	{
		var result = text.Trim();
		for (var pass = 0; pass < 2; pass++)
		{
			var pair = QuotePairs.FirstOrDefault(candidate =>
				result.StartsWith(candidate.Left, StringComparison.Ordinal) &&
				result.EndsWith(candidate.Right, StringComparison.Ordinal) &&
				result.Length > candidate.Left.Length + candidate.Right.Length);
			if (pair.Left is null)
			{
				break;
			}

			result = result[pair.Left.Length..^pair.Right.Length].Trim();
		}

		return result;
	}

	private static string StripMarkdownEmphasis(string text)
	{
		var result = text.Trim();
		foreach (var (left, right) in EmphasisWrappers)
		{
			if (result.StartsWith(left, StringComparison.Ordinal) &&
				result.EndsWith(right, StringComparison.Ordinal) &&
				result.Length > left.Length + right.Length)
			{
				return result[left.Length..^right.Length].Trim();
			}
		}

		return result;
	}

	private static readonly (string Left, string Right)[] QuotePairs =
	[
		("\"", "\""),
		("'", "'"),
		("`", "`"),
		("“", "”"),
		("「", "」"),
		("『", "』"),
	];

	private static readonly (string Left, string Right)[] EmphasisWrappers =
	[
		("**", "**"),
		("__", "__"),
		("~~", "~~"),
		("*", "*"),
		("_", "_"),
	];

	/// 与扩展的 TITLE_BLOCK_PATTERNS 一一对应：属性名与换行要求必须一致。
	private static readonly (Regex Pattern, Func<string, string> Label)[] BlockPatterns =
	[
		(QuotedContextBlock(), static name => "❝" + name),
		(ReferencedSessionBlock(), static name => "&" + name),
		(SkillBlock(), static name => "/skill:" + name),
		(PromptTemplateBlock(), static name => "/" + name),
	];

	[GeneratedRegex(@"^\s*\/[\w:-]+(?:\s|$)")]
	private static partial Regex CommandInput();

	[GeneratedRegex(@"<quoted_context\s+label=""([^""]*)""\s+message_id=""[^""]*"">\r?\n[\s\S]*?\r?\n</quoted_context>")]
	private static partial Regex QuotedContextBlock();

	[GeneratedRegex(@"<referenced_session\s+name=""([^""]*)"">\r?\n[\s\S]*?\r?\n</referenced_session>")]
	private static partial Regex ReferencedSessionBlock();

	[GeneratedRegex(@"<skill\b[^>]*\bname=""([^""]*)""[^>]*>[\s\S]*?</skill>", RegexOptions.IgnoreCase)]
	private static partial Regex SkillBlock();

	[GeneratedRegex(@"<prompt_template\b[^>]*\bname=""([^""]*)""[^>]*>[\s\S]*?</prompt_template>", RegexOptions.IgnoreCase)]
	private static partial Regex PromptTemplateBlock();

	[GeneratedRegex(@"-----BEGIN [^-]+ PRIVATE KEY-----[\s\S]*?-----END [^-]+ PRIVATE KEY-----", RegexOptions.IgnoreCase)]
	private static partial Regex PrivateKey();

	[GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
	private static partial Regex Bearer();

	[GeneratedRegex(@"([?&](?:api[\s_-]*key|access[\s_-]*token|refresh[\s_-]*token|token|secret|password)=)[^&#\s]+", RegexOptions.IgnoreCase)]
	private static partial Regex UrlSecret();

	[GeneratedRegex(@"(?<prefix>\b(?:api[\s_-]*key|access[\s_-]*token|refresh[\s_-]*token|authorization|password|passwd|secret|token|cookie)\b\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|`[^`\r\n]*`|[^\s""'`,;}\]]{4,})", RegexOptions.IgnoreCase)]
	private static partial Regex SecretAssignment();

	[GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9_-]{16,}|github_pat_[A-Za-z0-9_]{16,}|xox[baprs]-[A-Za-z0-9-]{16,}|AKIA[0-9A-Z]{16})\b")]
	private static partial Regex RawToken();

	[GeneratedRegex(@"^```(?:text|markdown)?$", RegexOptions.IgnoreCase)]
	private static partial Regex FenceLine();

	[GeneratedRegex(@"^```(?:text|markdown)?\s*", RegexOptions.IgnoreCase)]
	private static partial Regex FencePrefix();

	[GeneratedRegex(@"^[#>*+\-\s]+")]
	private static partial Regex LeadingDecoration();

	[GeneratedRegex(@"^(?:title|session title|conversation title|标题|会话标题)\s*[:：-]\s*", RegexOptions.IgnoreCase)]
	private static partial Regex TitlePrefix();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();

	[GeneratedRegex(@"[.!?。！？；;，,]+$")]
	private static partial Regex TrailingPunctuation();

	[GeneratedRegex(@"^(?:title|session|conversation|untitled|new)\s*(?:title|session)?$", RegexOptions.IgnoreCase)]
	private static partial Regex GenericPattern();

	[GeneratedRegex(@"^\[[A-Z][A-Z0-9_-]{1,11}\][ \t]+[\x20-\x7E]*[:_]")]
	private static partial Regex MarkerTagTitle();

	[GeneratedRegex(@"^\[[A-Z][A-Z0-9_-]{1,11}\][ \t]+[\x20-\x7E]+$")]
	private static partial Regex SourceMarkerHead();
}
