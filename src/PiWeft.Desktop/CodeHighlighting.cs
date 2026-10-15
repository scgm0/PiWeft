using System.Collections.Frozen;
using Aprillz.MewUI;
using Aprillz.MewUI.Text;

namespace PiWeft.Desktop;

/// <summary>
/// 代码块的轻量语法着色：按语言识别关键字、字符串、注释与数字。
/// 只做逐行扫描（不跟踪跨行块注释），对聊天里展示的片段足够，且完全无依赖、AOT 友好。
/// </summary>
public sealed class KeywordClassifier(string? language) : ITextClassifier
{
	private static readonly FrozenDictionary<string, FrozenSet<string>> KeywordSets =
		new[] { "csharp", "typescript", "javascript", "python", "bash", "json", "sql", "rust", "go" }
			.ToFrozenDictionary(static name => name, static name => KeywordsFor(name).ToFrozenSet(
				name == "sql" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

	private readonly string _language = Normalize(language);
	private readonly FrozenSet<string> _keywords = KeywordSets.GetValueOrDefault(Normalize(language), FrozenSet<string>.Empty);

	/// <summary>当前语言的显示名。</summary>
	public string Language => _language.Length == 0 ? "text" : _language;

	public void Classify(in TextClassificationContext context, IList<TextPaintSpan> output)
	{
		var text = context.Text.Span;
		var keywords = _keywords.GetAlternateLookup<ReadOnlySpan<char>>();
		var index = 0;

		while (index < text.Length)
		{
			var c = text[index];

			// 行注释
			if (IsLineCommentStart(text, index))
			{
				Add(output, index, text.Length - index, SyntaxPalette.Comment);
				return;
			}

			// 字符串（含转义）
			if (c is '"' or '\'' or '`')
			{
				var end = ScanString(text, index);
				Add(output, index, end - index, SyntaxPalette.String);
				index = end;
				continue;
			}

			// 数字
			if (char.IsAsciiDigit(c) && (index == 0 || !IsIdentifierChar(text[index - 1])))
			{
				var end = index;
				while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '.' or '_' or 'x' or 'X'))
				{
					end++;
				}

				Add(output, index, end - index, SyntaxPalette.Number);
				index = end;
				continue;
			}

			// 标识符：命中的关键字/类型着色，其余保持默认
			if (IsIdentifierStart(c))
			{
				var end = index;
				while (end < text.Length && IsIdentifierChar(text[end]))
				{
					end++;
				}

				var word = text[index..end];
				if (keywords.Contains(word))
				{
					Add(output, index, end - index, SyntaxPalette.Keyword);
				}

				index = end;
				continue;
			}

			index++;
		}
	}

	private bool IsLineCommentStart(ReadOnlySpan<char> text, int index)
	{
		if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/')
		{
			return true;
		}

		if (index + 1 < text.Length && text[index] == '-' && text[index + 1] == '-' && _language is "sql" or "lua" or "haskell")
		{
			return true;
		}

		// # 只在按 shell 家族的语言里当注释，避免把 C# 的 #region 之类误判。
		return text[index] == '#' && _language is "python" or "py" or "bash" or "sh" or "shell" or "yaml" or "yml" or "ruby" or "toml" or "ini" or "dockerfile" or "makefile";
	}

	private static int ScanString(ReadOnlySpan<char> text, int start)
	{
		var quote = text[start];
		var index = start + 1;

		while (index < text.Length)
		{
			if (text[index] == '\\')
			{
				index += 2;
				continue;
			}

			if (text[index] == quote)
			{
				return index + 1;
			}

			index++;
		}

		return text.Length;
	}

	private static void Add(IList<TextPaintSpan> output, int offset, int length, Color color)
	{
		if (length > 0)
		{
			output.Add(new TextPaintSpan(new TextRange(offset, length), color));
		}
	}

	private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_' || c == '$';

	private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '$';

	/// <summary>把 Markdown 里的语言标记折叠成我们认识的名字。</summary>
	private static string Normalize(string? language)
	{
		if (string.IsNullOrWhiteSpace(language))
		{
			return string.Empty;
		}

		var value = language.Trim().TrimStart('{', '.').ToLowerInvariant();

		// 去掉 Markdown 里常见的修饰，例如 "csharp title=..." 或 "{.js}".
		var cut = value.IndexOfAny([' ', '{', '}', ',', ';']);
		if (cut > 0)
		{
			value = value[..cut];
		}

		return value.Trim('.', '!') switch
		{
			"cs" or "c#" => "csharp",
			"ts" or "tsx" => "typescript",
			"js" or "jsx" or "mjs" or "cjs" => "javascript",
			"py" => "python",
			"sh" or "shell" or "zsh" => "bash",
			"rs" => "rust",
			"golang" => "go",
			_ => value.Trim('.', '!'),
		};
	}

	/// <summary>常用语言的关键字表。表里没有的语言只做字符串/注释/数字着色。</summary>
	private static HashSet<string> KeywordsFor(string language) => language switch
	{
		"csharp" or "cs" or "c#" => new HashSet<string>(StringComparer.Ordinal)
		{
			"abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
			"class", "const", "continue", "decimal", "default", "delegate", "do", "double", "dynamic", "else", "enum",
			"event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "get", "global",
			"goto", "if", "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "namespace",
			"new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
			"record", "ref", "return", "sbyte", "sealed", "set", "short", "sizeof", "stackalloc", "static", "string",
			"struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
			"using", "value", "var", "virtual", "void", "volatile", "when", "where", "while", "with", "yield",
			"field", "extension", "required", "scoped",
			"Task", "List", "Dictionary", "IEnumerable", "IReadOnlyList", "Span", "ReadOnlySpan", "String", "Int32",
		},

		"typescript" or "ts" or "tsx" => new HashSet<string>(StringComparer.Ordinal)
		{
			"abstract", "any", "as", "async", "await", "boolean", "break", "case", "catch", "class", "const", "continue",
			"declare", "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for", "from",
			"function", "get", "if", "implements", "import", "in", "instanceof", "interface", "is", "keyof", "let",
			"namespace", "never", "new", "null", "number", "object", "of", "private", "protected", "public", "readonly",
			"return", "satisfies", "set", "static", "string", "super", "switch", "this", "throw", "true", "try", "type",
			"typeof", "undefined", "unknown", "var", "void", "while", "yield", "Promise", "Array", "Record",
		},

		"javascript" or "js" or "jsx" or "mjs" or "cjs" => new HashSet<string>(StringComparer.Ordinal)
		{
			"async", "await", "break", "case", "catch", "class", "const", "continue", "default", "delete", "do", "else",
			"export", "extends", "false", "finally", "for", "from", "function", "if", "import", "in", "instanceof", "let",
			"new", "null", "of", "return", "super", "switch", "this", "throw", "true", "try", "typeof", "undefined",
			"var", "void", "while", "yield", "Promise", "Array", "Object",
		},

		"python" or "py" => new HashSet<string>(StringComparer.Ordinal)
		{
			"and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except",
			"False", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not",
			"or", "pass", "raise", "return", "True", "try", "while", "with", "yield", "self", "print", "len", "range",
		},

		"bash" or "sh" or "shell" or "zsh" => new HashSet<string>(StringComparer.Ordinal)
		{
			"if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "function", "return",
			"local", "export", "echo", "cd", "set", "unset", "read", "exit", "source", "sudo", "grep", "sed", "awk",
		},

		"json" => new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null" },

		"sql" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"select", "from", "where", "insert", "into", "values", "update", "set", "delete", "create", "table", "alter",
			"drop", "index", "join", "left", "right", "inner", "outer", "on", "group", "by", "order", "having", "limit",
			"offset", "as", "and", "or", "not", "null", "distinct", "union", "all", "primary", "key", "foreign", "references",
		},

		"rust" or "rs" => new HashSet<string>(StringComparer.Ordinal)
		{
			"as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern", "false", "fn",
			"for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self",
			"static", "struct", "super", "trait", "true", "type", "unsafe", "use", "where", "while",
		},

		"go" or "golang" => new HashSet<string>(StringComparer.Ordinal)
		{
			"break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go",
			"goto", "if", "import", "interface", "map", "nil", "package", "range", "return", "select", "struct", "switch",
			"type", "var",
		},

		"yaml" or "yml" or "toml" or "ini" or "dockerfile" or "makefile" or "xml" or "html" or "css" or "scss" or "md" or "markdown" => [],

		_ => new HashSet<string>(StringComparer.Ordinal),
	};
}

/// <summary>
/// 语法着色用的颜色，取自 PiDeck 的 <c>--code-*</c> 令牌（GitHub 浅色/暗色两套），
/// 因此代码块与 PiDeck 里的高亮观感一致。
/// </summary>
public static class SyntaxPalette
{
	private static bool IsLight => AppTheme.IsLightTheme;

	public static Color Keyword => IsLight ? new Color(0xcf, 0x22, 0x2e) : new Color(0xff, 0x7b, 0x72);

	public static Color String => IsLight ? new Color(0x0a, 0x30, 0x69) : new Color(0xa5, 0xd6, 0xff);

	public static Color Comment => IsLight ? new Color(0x6e, 0x77, 0x81) : new Color(0x8b, 0x94, 0x9e);

	public static Color Number => IsLight ? new Color(0x05, 0x50, 0xae) : new Color(0x79, 0xc0, 0xff);
}
