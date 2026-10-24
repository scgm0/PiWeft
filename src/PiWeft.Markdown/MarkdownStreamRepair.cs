namespace PiWeft.Markdown;

/// <summary>
/// Computes provisional closers for an unfinished streaming tail, so unclosed emphasis, code
/// spans and link URLs render like the finished document while tokens are still arriving. The
/// repair only appends invisible markup characters at the very end of the tail: it cannot create
/// blocks or change text before the last one, and the finalized parse replaces the repaired
/// model wholesale. Fences, indented code and HTML markup render acceptably while unclosed, so
/// they are skipped rather than repaired.
/// </summary>
internal static class MarkdownStreamRepair
{
	private const int MaxOpenConstructs = 64;
	private const int MaxSuffixLength = 128;

	private enum ConstructKind
	{
		Emphasis,
		CodeSpan,
		Bracket,
		LinkUrl,
		Paren
	}

	private readonly record struct OpenConstruct(ConstructKind Kind, char Delimiter, int Count);

	/// <summary>Returns the closing markers to append to <paramref name="text"/>, or empty.</summary>
	public static string ComputeSuffix(ReadOnlySpan<char> text)
	{
		List<OpenConstruct> open = new(8);
		bool inFence = false;
		char fenceChar = default;
		int fenceLength = 0;
		bool inIndentedCode = false;
		bool previousBlank = true;

		int position = 0;
	nextLine:
		while (position < text.Length)
		{
			int newline = text[position..].IndexOf('\n');
			int lineEnd = newline < 0 ? text.Length : position + newline;
			ReadOnlySpan<char> line = text[position..lineEnd];

			if (inFence)
			{
				if (IsClosingFence(line, fenceChar, fenceLength))
				{
					inFence = false;
				}
				position = NextLine(lineEnd, text.Length);
				previousBlank = false;
				continue;
			}
			if (inIndentedCode)
			{
				if (IsBlank(line))
				{
					position = NextLine(lineEnd, text.Length);
					previousBlank = true;
					continue;
				}
				if (Indentation(line) >= 4)
				{
					position = NextLine(lineEnd, text.Length);
					previousBlank = false;
					continue;
				}
				inIndentedCode = false;
			}

			if (IsBlank(line))
			{
				// A blank line ends the paragraph: emphasis and links opened before it can never
				// close. HTML comments may span blank lines, so those stay open.
				open.Clear();
				previousBlank = true;
				position = NextLine(lineEnd, text.Length);
				continue;
			}

			int indent = Indentation(line);
			bool followsBlank = previousBlank;
			if (indent >= 4 && previousBlank)
			{
				// Indented code: markup inside is literal, and a repair suffix would land in
				// the code text where it becomes visible.
				inIndentedCode = true;
				open.Clear();
				previousBlank = false;
				position = NextLine(lineEnd, text.Length);
				continue;
			}
			previousBlank = false;

			int scanStart = position + Math.Min(indent, line.Length);
			if (indent <= 3)
			{
				ReadOnlySpan<char> content = line[Math.Min(indent, line.Length)..];
				if (TryFenceOpen(content, out fenceChar, out fenceLength))
				{
					inFence = true;
					open.Clear();
					position = NextLine(lineEnd, text.Length);
					continue;
				}
				if (IsThematicBreakOrSetext(content))
				{
					// Rules and setext underlines are pure marker lines: nothing to repair, and
					// their characters must not be mistaken for inline delimiters.
					open.Clear();
					position = NextLine(lineEnd, text.Length);
					continue;
				}
				if (IsHeading(content) || IsListMarker(content) ||
					(followsBlank && content[0] == '<' && content.Length > 1 && IsTagNameStart(content[1])))
				{
					// Blocks that interrupt a paragraph: open inline constructs can no longer
					// close, so repairing them would draw markup across a block boundary.
					open.Clear();
				}
				if (content[0] == '>')
				{
					// Block quote markers are transparent: the paragraph continues.
					int offset = indent;
					while (offset < lineEnd && text[offset] == '>')
					{
						offset++;
						if (offset < lineEnd && text[offset] == ' ')
						{
							offset++;
						}
					}
					scanStart = offset;
				}
			}

			int index = scanStart;
			while (index < lineEnd)
			{
				char c = text[index];

				if (c == '\\' && index + 1 < lineEnd)
				{
					index += 2;
					continue;
				}

				if (c == '`')
				{
					int run = CountRun(text, index, lineEnd, '`');
					int close = FindCodeSpanClose(text, index + run, lineEnd, run);
					if (close >= 0)
					{
						index = close;
						continue;
					}
					if (lineEnd == text.Length)
					{
						// The run is unmatched at the end of the tail: the rest is provisional
						// code span content, and nothing after it can change the repair.
						if (open.Count < MaxOpenConstructs)
						{
							open.Add(new OpenConstruct(ConstructKind.CodeSpan, '`', run));
						}
						position = text.Length;
						break nextLine;
					}
					index += run;
					continue;
				}

				if (c is '*' or '_')
				{
					int run = CountRun(text, index, lineEnd, c);
					index = ScanEmphasisRun(text, open, index, run, c);
					continue;
				}

				if (c is '~' or '=' or '+')
				{
					int run = CountRun(text, index, lineEnd, c);
					if (run >= 2)
					{
						TogglePair(open, c);
					}
					index += run;
					continue;
				}

				if (c == '[')
				{
					if (open.Count < MaxOpenConstructs)
					{
						open.Add(new OpenConstruct(ConstructKind.Bracket, '[', 0));
					}
					index++;
					continue;
				}

				if (c == ']' && open.Count > 0)
				{
					int bracket = LastOfKind(open, ConstructKind.Bracket);
					if (bracket >= 0)
					{
						open.RemoveAt(bracket);
						if (index + 1 < text.Length && text[index + 1] == '(')
						{
							if (open.Count < MaxOpenConstructs)
							{
								open.Add(new OpenConstruct(ConstructKind.LinkUrl, '(', 0));
							}
							index += 2;
							continue;
						}
					}
					index++;
					continue;
				}

				if (c == '(')
				{
					// Parens inside a link destination must balance before the URL can close.
					if (LastOfKind(open, ConstructKind.LinkUrl) >= 0 && open.Count < MaxOpenConstructs)
					{
						open.Add(new OpenConstruct(ConstructKind.Paren, '(', 0));
					}
					index++;
					continue;
				}

				if (c == ')')
				{
					int paren = LastOfKind(open, ConstructKind.Paren);
					if (paren >= 0)
					{
						open.RemoveAt(paren);
					}
					else
					{
						int url = LastOfKind(open, ConstructKind.LinkUrl);
						if (url >= 0)
						{
							open.RemoveAt(url);
						}
					}
					index++;
					continue;
				}

				if (c == '<')
				{
					// Inline markup (tags and comments) is literal output in this renderer: skip
					// it without tracking. An unclosed "<!--" is therefore scanned as ordinary
					// text on later lines, which keeps the tail-local repair consistent with a
					// full parse — Markdig does not let an inline comment interrupt blocks either.
					int tagClose = text[index..lineEnd].IndexOf('>');
					index = tagClose >= 0 ? index + tagClose + 1 : lineEnd;
					continue;
				}

				if (c is ' ' or '\t' && LastOfKind(open, ConstructKind.LinkUrl) >= 0)
				{
					// Whitespace ends a bare link destination; the URL can no longer close.
					RemoveLastOfKind(open, ConstructKind.LinkUrl);
					index++;
					continue;
				}

				index++;
			}

			// A link destination cannot contain a newline either — but an unterminated one at
			// the end of the tail is exactly what the repair closes.
			if (lineEnd < text.Length)
			{
				RemoveLastOfKind(open, ConstructKind.LinkUrl);
			}
			position = NextLine(lineEnd, text.Length);
		}

		if (inFence || inIndentedCode || open.Count == 0)
		{
			return string.Empty;
		}

		var builder = new System.Text.StringBuilder();
		for (int index = open.Count - 1; index >= 0 && builder.Length < MaxSuffixLength; index--)
		{
			OpenConstruct construct = open[index];
			switch (construct.Kind)
			{
				case ConstructKind.Emphasis:
					builder.Append(construct.Delimiter, construct.Count);
					break;
				case ConstructKind.CodeSpan:
					builder.Append('`', construct.Count);
					break;
				case ConstructKind.LinkUrl or ConstructKind.Paren:
					builder.Append(')');
					break;
			}
		}
		return builder.ToString();
	}

	private static int NextLine(int lineEnd, int length) => Math.Min(lineEnd + 1, length);

	private static bool IsBlank(ReadOnlySpan<char> line) => line.IsWhiteSpace();

	private static int Indentation(ReadOnlySpan<char> line)
	{
		int indent = 0;
		foreach (char c in line)
		{
			if (c == ' ')
			{
				indent++;
			}
			else if (c == '\t')
			{
				indent += 4;
			}
			else
			{
				break;
			}
		}
		return indent;
	}

	private static int CountRun(ReadOnlySpan<char> text, int start, int end, char c)
	{
		int run = 0;
		while (start + run < end && text[start + run] == c)
		{
			run++;
		}
		return run;
	}

	private static int FindCodeSpanClose(ReadOnlySpan<char> text, int start, int end, int length)
	{
		int index = start;
		while (index < end)
		{
			if (text[index] == '`')
			{
				int run = CountRun(text, index, end, '`');
				if (run == length)
				{
					return index + run;
				}
				index += run;
				continue;
			}
			index++;
		}
		return -1;
	}

	private static bool TryFenceOpen(ReadOnlySpan<char> content, out char fenceChar, out int fenceLength)
	{
		fenceChar = default;
		fenceLength = 0;
		if (content.IsEmpty || content[0] is not ('`' or '~'))
		{
			return false;
		}
		char c = content[0];
		int run = CountRun(content, 0, content.Length, c);
		if (run < 3 || (c == '`' && content[run..].Contains('`')))
		{
			return false;
		}
		fenceChar = c;
		fenceLength = run;
		return true;
	}

	private static bool IsClosingFence(ReadOnlySpan<char> line, char fenceChar, int fenceLength)
	{
		int indent = Indentation(line);
		if (indent > 3)
		{
			return false;
		}
		ReadOnlySpan<char> content = line[indent..];
		int run = CountRun(content, 0, content.Length, fenceChar);
		return run >= fenceLength && content[run..].IsWhiteSpace();
	}

	private static bool IsThematicBreakOrSetext(ReadOnlySpan<char> content)
	{
		int markers = 0;
		char firstMarker = default;
		foreach (char c in content)
		{
			if (c == ' ')
			{
				continue;
			}
			if (c is not ('-' or '*' or '_' or '='))
			{
				return false;
			}
			if (markers == 0)
			{
				firstMarker = c;
			}
			markers++;
		}
		if (markers == 0)
		{
			return false;
		}
		// A run of '=' is a setext underline at any length; the other markers need three or
		// more for a thematic break. Both interrupt the paragraph, which is all the scanner
		// cares about.
		return firstMarker == '=' || markers >= 3;
	}

	private static bool IsHeading(ReadOnlySpan<char> content)
		=> content.Length > 0 && content[0] == '#' &&
			(content.Length == 1 || content[1] is ' ' or '\t');

	private static bool IsListMarker(ReadOnlySpan<char> content)
	{
		if (content.IsEmpty)
		{
			return false;
		}
		if (content[0] is '-' or '+' or '*')
		{
			return content.Length == 1 || content[1] is ' ' or '\t';
		}
		int digits = 0;
		while (digits < content.Length && char.IsAsciiDigit(content[digits]))
		{
			digits++;
		}
		return digits > 0 && digits < content.Length &&
			content[digits] is '.' or ')' &&
			(digits + 1 == content.Length || content[digits + 1] is ' ' or '\t');
	}

	private static bool IsTagNameStart(char c) => char.IsAsciiLetter(c) || c == '/' || c == '!';

	private static int ScanEmphasisRun(ReadOnlySpan<char> text, List<OpenConstruct> open, int index, int run, char delimiter)
	{
		// '\0' marks the start/end of the text, which flanks like whitespace.
		char previous = index > 0 ? text[index - 1] : '\0';
		char next = index + run < text.Length ? text[index + run] : '\0';
		bool leftFlanking = !IsWhitespace(next) &&
			(!IsPunctuation(next) || IsWhitespace(previous) || IsPunctuation(previous));
		bool rightFlanking = !IsWhitespace(previous) &&
			(!IsPunctuation(previous) || IsWhitespace(next) || IsPunctuation(next));
		bool canOpen = delimiter == '*' ? leftFlanking
			: leftFlanking && (!rightFlanking || IsPunctuation(previous) || IsWhitespace(previous));
		bool canClose = delimiter == '*' ? rightFlanking
			: rightFlanking && (!leftFlanking || IsPunctuation(next) || IsWhitespace(next));

		int remaining = run;
		if (canClose)
		{
			while (remaining > 0)
			{
				int match = LastEmphasis(open, delimiter);
				if (match < 0)
				{
					break;
				}
				OpenConstruct entry = open[match];
				int take = Math.Min(remaining, entry.Count);
				remaining -= take;
				if (take >= entry.Count)
				{
					open.RemoveAt(match);
				}
				else
				{
					open[match] = entry with { Count = entry.Count - take };
				}
			}
		}
		if (remaining > 0 && canOpen && open.Count < MaxOpenConstructs)
		{
			open.Add(new OpenConstruct(ConstructKind.Emphasis, delimiter, remaining));
		}
		return index + run;
	}

	private static void TogglePair(List<OpenConstruct> open, char delimiter)
	{
		int match = LastEmphasis(open, delimiter);
		if (match >= 0)
		{
			open.RemoveAt(match);
		}
		else if (open.Count < MaxOpenConstructs)
		{
			open.Add(new OpenConstruct(ConstructKind.Emphasis, delimiter, 2));
		}
	}

	private static int LastEmphasis(List<OpenConstruct> open, char delimiter)
	{
		for (int index = open.Count - 1; index >= 0; index--)
		{
			OpenConstruct construct = open[index];
			if (construct.Kind == ConstructKind.Emphasis && construct.Delimiter == delimiter)
			{
				return index;
			}
		}
		return -1;
	}

	private static int LastOfKind(List<OpenConstruct> open, ConstructKind kind)
	{
		for (int index = open.Count - 1; index >= 0; index--)
		{
			if (open[index].Kind == kind)
			{
				return index;
			}
		}
		return -1;
	}

	private static void RemoveLastOfKind(List<OpenConstruct> open, ConstructKind kind)
	{
		int match = LastOfKind(open, kind);
		if (match >= 0)
		{
			open.RemoveAt(match);
		}
	}

	private static bool IsWhitespace(char c)
		=> c is '\0' or ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

	private static bool IsPunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c);
}
