using System.Runtime.CompilerServices;
using System.Text;
using Aprillz.MewUI;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Emoji;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PiWeft.Markdown;

/// <summary>Maps a Markdig document to the small immutable model the view renders incrementally.</summary>
public static class MarkdownParser
{
	/// <summary>Layout-only outer spacing inserted around inline-code runs.</summary>
	internal const char InlineCodeMargin = '\u2009';

	// Options carrying a delegate are rare; weak keys keep the cache from growing on every assignment.
	private static readonly ConditionalWeakTable<MarkdownOptions, MarkdownPipeline> Pipelines = [];
	private static readonly MarkdownOptions DefaultOptions = new();

	/// <summary>
	/// Parses <paramref name="source"/>, reusing the tail of <paramref name="previous"/> when the new
	/// text only appended to it. Only the last top-level block is re-parsed, so a streamed frame
	/// costs time proportional to the appended text instead of the whole message. While
	/// <paramref name="streaming"/> is set, the unfinished tail receives provisional closers so
	/// unclosed syntax renders like the finished document.
	/// </summary>
	public static MarkdownDocument Parse(string source, MarkdownOptions? options = null, MarkdownDocument? previous = null, bool streaming = false)
	{
		ArgumentNullException.ThrowIfNull(source);
		options ??= DefaultOptions;

		if (TryParseTail(source, builder: null, options, previous, streaming, out MarkdownDocument? incremental))
		{
			return incremental;
		}

		return ParseFull(source, options, streaming);
	}

	/// <summary>
	/// Parses a source that only grows by appends. Reference equality with the previous
	/// document's builder proves the append-only property, so the incremental path needs neither
	/// a full-source prefix comparison nor a full-string materialization.
	/// </summary>
	public static MarkdownDocument Parse(StringBuilder source, MarkdownOptions? options = null, MarkdownDocument? previous = null, bool streaming = false)
	{
		ArgumentNullException.ThrowIfNull(source);
		options ??= DefaultOptions;

		if (TryParseTail(null, source, options, previous, streaming, out MarkdownDocument? incremental))
		{
			return incremental;
		}

		// Full parse: blocks come from the materialized text, but the document stays
		// builder-backed so later appends keep the tail fast path.
		return ParseFull(source.ToString(), options, streaming, source);
	}

	private static MarkdownDocument ParseFull(string source, MarkdownOptions options, bool streaming, StringBuilder? builder = null)
	{
		MarkdownPipeline pipeline = Pipelines.GetValue(options, static value => BuildPipeline(value));
		string parseSource = source;
		string suffix = streaming ? MarkdownStreamRepair.ComputeSuffix(source) : string.Empty;
		if (suffix.Length > 0)
		{
			parseSource = source + suffix;
		}
		Markdig.Syntax.MarkdownDocument parsed = Markdig.Markdown.Parse(parseSource, pipeline);
		HashSet<string> anchors = new(StringComparer.Ordinal);
		List<MarkdownBlock> blocks = MapChildren(parsed, parseSource, options, anchors);
		MarkdownHashBuilder.Assign(blocks);
		return new MarkdownDocument(source, builder, blocks, options)
		{
			HasLinkReferenceDefinitions = ContainsLinkReferenceDefinition(source.AsSpan()),
			TailRepaired = suffix.Length > 0
		};
	}

	private static bool TryParseTail(
		string? source,
		StringBuilder? builder,
		MarkdownOptions options,
		MarkdownDocument? previous,
		bool streaming,
		out MarkdownDocument document)
	{
		document = null!;
		if (previous is null ||
			previous.Blocks.Count == 0 ||
			previous.Options != options ||
			previous.HasLinkReferenceDefinitions)
		{
			return false;
		}

		int previousLength = previous.SourceLength;
		if (builder is not null)
		{
			if (!ReferenceEquals(previous.SourceBuilder, builder) || builder.Length < previousLength)
			{
				return false;
			}
		}
		else if (source!.Length < previousLength ||
			!source.StartsWith(previous.Source, StringComparison.Ordinal))
		{
			return false;
		}

		int currentLength = builder?.Length ?? source!.Length;
		if (currentLength == previousLength)
		{
			if (!previous.TailRepaired || streaming)
			{
				// Nothing new to parse; the previous document is still the best model.
				document = previous;
				return true;
			}
			// Finalize after a repaired tail: re-parse the tail once without repair so the
			// final model is exact even when the provisional closers guessed differently.
		}

		int tailStart = previous.Blocks[^1].SourceStart;
		if (tailStart <= 0 || tailStart > previousLength)
		{
			return false;
		}
		// A link reference definition anywhere in the tail can resolve links in earlier blocks,
		// so those frames take the full path.
		string tail = builder is not null
			? builder.ToString(tailStart, currentLength - tailStart)
			: source![tailStart..];
		if (ContainsLinkReferenceDefinition(tail))
		{
			return false;
		}

		// While streaming, close whatever construct is still open at the end of the tail so the
		// unfinished last block renders like the finished document. The suffix only appends
		// invisible markup characters; the finalized parse replaces it wholesale.
		string suffix = streaming ? MarkdownStreamRepair.ComputeSuffix(tail) : string.Empty;
		if (suffix.Length > 0)
		{
			tail += suffix;
		}

		// Parse only the tail. The last block starts at a real top-level boundary, so parsing the
		// substring is equivalent to parsing the whole source from that boundary, and the cost is
		// proportional to the tail rather than the message. Markdig spans are relative to the
		// substring; shift them back to original source offsets after mapping.
		MarkdownPipeline pipeline = Pipelines.GetValue(options, static value => BuildPipeline(value));
		Markdig.Syntax.MarkdownDocument parsed = Markdig.Markdown.Parse(tail, pipeline);

		// Seed uniqueness from the blocks that are not re-parsed: the last block is replaced by the
		// freshly parsed tail, so its old anchors must not reserve their own slugs.
		HashSet<string> anchors = new(StringComparer.Ordinal);
		for (int index = 0; index < previous.Blocks.Count - 1; index++)
		{
			CollectAnchors(previous.Blocks[index], anchors);
		}

		List<MarkdownBlock> tailBlocks = MapChildren(parsed, tail, options, anchors);
		foreach (MarkdownBlock block in tailBlocks)
		{
			ShiftSourceOffsets(block, tailStart);
		}
		MarkdownHashBuilder.Assign(tailBlocks);

		List<MarkdownBlock> blocks = new(previous.Blocks.Count + tailBlocks.Count);
		for (int index = 0; index < previous.Blocks.Count - 1; index++)
		{
			blocks.Add(previous.Blocks[index]);
		}
		blocks.AddRange(tailBlocks);

		document = new MarkdownDocument(source, builder, blocks, options)
		{
			TailRepaired = suffix.Length > 0
		};
		return true;
	}

	private static void ShiftSourceOffsets(MarkdownBlock block, int offset)
	{
		if (block.SourceStart >= 0)
		{
			block.SourceStart += offset;
		}
		if (block.Runs.Count > 0)
		{
			MarkdownRun[] runs = new MarkdownRun[block.Runs.Count];
			for (int index = 0; index < runs.Length; index++)
			{
				MarkdownRun run = block.Runs[index];
				runs[index] = run.SourceStart >= 0 ? run with { SourceStart = run.SourceStart + offset } : run;
			}
			block.Runs = runs;
		}
		foreach (MarkdownBlock child in block.Children)
		{
			ShiftSourceOffsets(child, offset);
		}
	}

	private static void CollectAnchors(MarkdownBlock block, HashSet<string> anchors)
	{
		if (!string.IsNullOrEmpty(block.Anchor))
		{
			anchors.Add(block.Anchor);
		}
		foreach (string alias in block.Aliases)
		{
			anchors.Add(alias);
		}
		foreach (MarkdownBlock child in block.Children)
		{
			CollectAnchors(child, anchors);
		}
	}

	/// <summary>
	/// True when the text may contain a Markdown link reference definition. The scan is
	/// deliberately conservative: a false positive only makes that frame take the full parse
	/// path, while a false negative could leave earlier link URLs stale.
	/// </summary>
	internal static bool ContainsLinkReferenceDefinition(ReadOnlySpan<char> text)
	{
		int lineStart = 0;
		while (lineStart < text.Length)
		{
			int lineEnd = text[lineStart..].IndexOf('\n');
			ReadOnlySpan<char> line = lineEnd < 0 ? text[lineStart..] : text.Slice(lineStart, lineEnd);
			line = line.TrimEnd('\r');
			int marker = line.IndexOf("]:", StringComparison.Ordinal);
			if (marker >= 0 && line[..marker].LastIndexOf('[') >= 0)
			{
				return true;
			}
			if (lineEnd < 0)
			{
				break;
			}
			lineStart += lineEnd + 1;
		}
		return false;
	}
	private static MarkdownPipeline BuildPipeline(MarkdownOptions options)
	{
		MarkdownPipelineBuilder builder = new();
		builder.UsePreciseSourceLocation();
		if (options.UsePipeTables)
		{
			builder.UsePipeTables();
		}
		if (options.UseTaskLists)
		{
			builder.UseTaskLists();
		}
		if (options.UseAutoLinks)
		{
			builder.UseAutoLinks();
		}

		EmphasisExtraOptions emphasis = 0;
		if (options.UseStrikethrough)
		{
			emphasis |= EmphasisExtraOptions.Strikethrough;
		}
		if (options.UseInserted)
		{
			emphasis |= EmphasisExtraOptions.Inserted;
		}
		if (options.UseMarked)
		{
			emphasis |= EmphasisExtraOptions.Marked;
		}
		if (emphasis != 0)
		{
			builder.UseEmphasisExtras(emphasis);
		}

		options.ConfigurePipeline?.Invoke(builder);
		return builder.Build();
	}

	private static List<MarkdownBlock> MapChildren(
		ContainerBlock container,
		string source,
		MarkdownOptions options,
		HashSet<string> anchors,
		TextAlignment alignment = TextAlignment.Left)
	{
		List<MarkdownBlock> children = [];
		foreach (Block child in container)
		{
			MarkdownBlock? mapped = MapBlock(child, source, options, anchors, alignment: alignment);
			if (mapped is not null)
			{
				children.Add(mapped);
			}
		}
		return children;
	}

	private static MarkdownBlock? MapBlock(
		Block block,
		string source,
		MarkdownOptions options,
		HashSet<string> anchors,
		string? marker = null,
		TextAlignment alignment = TextAlignment.Left)
	{
		switch (block)
		{
			// Front matter is document metadata, not content (reachable when the host enables the
			// YAML extension via ConfigurePipeline). It derives from CodeBlock and must be
			// intercepted before the generic code case.
			case YamlFrontMatterBlock:
				return null;

			case HeadingBlock heading:
			{
				List<MarkdownRun> runs = MapInlines(heading.Inline, options, source);
				string text = Concat(runs);
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Heading,
					Level = heading.Level,
					Header = true,
					Alignment = alignment,
					Runs = runs,
					Text = text,
					Anchor = CreateAnchor(text, anchors),
					SourceStart = GetStart(heading),
					SourceLength = GetLength(heading)
				};
			}

			case QuoteBlock quote:
			{
				List<MarkdownBlock> children = MapChildren(quote, source, options, anchors, alignment);
				string? alert = DetectAlert(children);
				if (alert is not null)
				{
					children = StripAlertMarker(children, alert);
				}
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Quote,
					Alert = alert,
					Children = children,
					SourceStart = GetStart(quote),
					SourceLength = GetLength(quote)
				};
			}

			case ListBlock list:
			{
				List<MarkdownBlock> children = [];
				int orderedStart = int.TryParse(list.OrderedStart, out int parsedStart) ? parsedStart : 1;
				for (int index = 0; index < list.Count; index++)
				{
					Block child = list[index];
					// Unordered items always display the canonical bullet, regardless of whether the
					// source used '-', '*' or '+'. SourceBullet is empty/unstable and must not drive
					// the UI or copied text.
					string childMarker = list.IsOrdered
						? $"{orderedStart + index}{list.OrderedDelimiter}"
						: "•";
					MarkdownBlock? mapped = MapBlock(child, source, options, anchors, childMarker, alignment);
					if (mapped is not null)
					{
						children.Add(mapped);
					}
				}
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.List,
					Level = list.IsOrdered ? orderedStart : 0,
					Marker = list.IsOrdered ? list.OrderedDelimiter.ToString() : "•",
					Loose = list.IsLoose,
					Children = children,
					SourceStart = GetStart(list),
					SourceLength = GetLength(list)
				};
			}

			case ListItemBlock item:
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.ListItem,
					Marker = marker,
					Children = MapChildren(item, source, options, anchors, alignment),
					SourceStart = GetStart(item),
					SourceLength = GetLength(item)
				};

			case MathBlock math:
			{
				// MathBlock derives from CodeBlock and would otherwise be claimed by the generic
				// code case; host renderers key on the node type name.
				string content = math.Lines.ToString();
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Custom,
					CustomKind = nameof(MathBlock),
					Text = content,
					Runs = [new MarkdownRun(content, SourceStart: GetStart(math), SourceLength: GetLength(math))],
					SourceStart = GetStart(math),
					SourceLength = GetLength(math)
				};
			}

			case FencedCodeBlock fenced:
			{
				string code = fenced.Lines.ToString();
				string? info = fenced.Info;
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Code,
					Info = info,
					CodeLanguage = NormalizeLanguage(info),
					Text = code,
					SourceStart = GetStart(fenced),
					SourceLength = GetLength(fenced)
				};
			}

			case CodeBlock code:
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Code,
					Text = code.Lines.ToString(),
					SourceStart = GetStart(code),
					SourceLength = GetLength(code)
				};

			case ThematicBreakBlock rule:
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Rule,
					SourceStart = GetStart(rule),
					SourceLength = GetLength(rule)
				};

			case Table table:
				return MapTable(table, source, options, anchors);

			case TableRow row:
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.TableRow,
					Header = row.IsHeader,
					Children = MapChildren(row, source, options, anchors),
					SourceStart = GetStart(row),
					SourceLength = GetLength(row)
				};

			case TableCell cell:
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.TableCell,
					Children = MapChildren(cell, source, options, anchors),
					SourceStart = GetStart(cell),
					SourceLength = GetLength(cell)
				};

			case HtmlBlock html:
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Paragraph,
					Alignment = alignment,
					Runs = [new MarkdownRun(GetSourceText(html, source), SourceStart: GetStart(html), SourceLength: GetLength(html))],
					Text = GetSourceText(html, source),
					SourceStart = GetStart(html),
					SourceLength = GetLength(html)
				};

			// Footnote definitions are stored twice by Markdig (here and in the footnote group);
			// this copy is parsing metadata, not presentation.
			case LinkReferenceDefinitionGroup:
				return null;

			case LeafBlock leaf when leaf.Inline is not null:
			{
				List<MarkdownRun> runs = MapInlines(leaf.Inline, options, source);
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Paragraph,
					Alignment = alignment,
					Runs = runs,
					Text = Concat(runs),
					SourceStart = GetStart(leaf),
					SourceLength = GetLength(leaf)
				};
			}

			case ContainerBlock container:
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Group,
					Children = MapChildren(container, source, options, anchors, alignment),
					SourceStart = GetStart(container),
					SourceLength = GetLength(container)
				};

			default:
			{
				// A leaf block without a built-in mapping (a math block, a host extension, ...):
				// keep the raw source as payload and expose the node type so host renderers can
				// present it. Without a registration the view falls back to literal text.
				string text = GetSourceText(block, source);
				return new MarkdownBlock
				{
					Kind = MarkdownBlockKind.Custom,
					CustomKind = block.GetType().Name,
					Alignment = alignment,
					Runs = [new MarkdownRun(text, SourceStart: GetStart(block), SourceLength: GetLength(block))],
					Text = text,
					SourceStart = GetStart(block),
					SourceLength = GetLength(block)
				};
			}
		}
	}

	private static MarkdownBlock MapTable(
		Table table,
		string source,
		MarkdownOptions options,
		HashSet<string> anchors)
	{
		List<MarkdownBlock> rows = [];
		foreach (Block child in table)
		{
			if (child is not TableRow row)
			{
				MarkdownBlock? mapped = MapBlock(child, source, options, anchors);
				if (mapped is not null)
				{
					rows.Add(mapped);
				}
				continue;
			}

			List<MarkdownBlock> cells = [];
			int cellIndex = 0;
			foreach (Block rowChild in row)
			{
				if (rowChild is not TableCell cell)
				{
					continue;
				}

				TextAlignment alignment = GetTableAlignment(table, cellIndex);
				List<MarkdownBlock> mappedChildren = [];
				foreach (Block cellChild in cell)
				{
					MarkdownBlock? mapped = MapBlock(cellChild, source, options, anchors, alignment: alignment);
					if (mapped is not null)
					{
						mappedChildren.Add(mapped);
					}
				}
				cells.Add(new MarkdownBlock
				{
					Kind = MarkdownBlockKind.TableCell,
					Alignment = alignment,
					Children = mappedChildren,
					SourceStart = GetStart(cell),
					SourceLength = GetLength(cell)
				});
				cellIndex++;
			}

			rows.Add(new MarkdownBlock
			{
				Kind = MarkdownBlockKind.TableRow,
				Header = row.IsHeader,
				Children = cells,
				SourceStart = GetStart(row),
				SourceLength = GetLength(row)
			});
		}

		return new MarkdownBlock
		{
			Kind = MarkdownBlockKind.Table,
			Children = rows,
			SourceStart = GetStart(table),
			SourceLength = GetLength(table)
		};
	}

	private static TextAlignment GetTableAlignment(Table table, int columnIndex)
	{
		if (columnIndex < 0 || columnIndex >= table.ColumnDefinitions.Count)
		{
			return TextAlignment.Left;
		}
		return table.ColumnDefinitions[columnIndex].Alignment switch
		{
			TableColumnAlign.Center => TextAlignment.Center,
			TableColumnAlign.Right => TextAlignment.Right,
			_ => TextAlignment.Left
		};
	}

	private static readonly string[] AlertMarkers =
	[
		"[!NOTE]", "[!TIP]", "[!IMPORTANT]", "[!WARNING]", "[!CAUTION]",
	];

	/// <summary>
	/// GitHub alerts arrive as a plain QuoteBlock in Markdig 1.x: the first paragraph starts with
	/// the marker. Detection reads the mapped model text, which is robust against the inline
	/// parser splitting the marker across literal runs.
	/// </summary>
	private static string? DetectAlert(List<MarkdownBlock> children)
	{
		if (children.Count == 0 || children[0].Kind is not (MarkdownBlockKind.Paragraph or MarkdownBlockKind.Heading))
		{
			return null;
		}
		string text = children[0].Text;
		foreach (string marker in AlertMarkers)
		{
			if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
			{
				return marker[2..^1];
			}
		}
		return null;
	}

	private static List<MarkdownBlock> StripAlertMarker(List<MarkdownBlock> children, string alert)
	{
		int markerLength = alert.Length + 3;
		MarkdownBlock first = children[0];
		List<MarkdownRun> runs = new(first.Runs.Count);
		int position = 0;
		bool eatGap = true;
		foreach (MarkdownRun run in first.Runs)
		{
			string text = run.Text;
			int start = 0;
			if (position < markerLength)
			{
				start = Math.Min(markerLength - position, text.Length);
				if (start < text.Length)
				{
					// The marker ends inside this run: eat the gap right here.
					while (start < text.Length && (text[start] == ' ' || text[start] == '\t'))
					{
						start++;
					}
					eatGap = false;
				}
				// start == text.Length: the run was fully consumed and the marker may continue in
				// the next run, which then eats the gap via the branch below.
			}
			else if (eatGap)
			{
				// The gap after the marker can straddle run boundaries: keep eating across
				// whitespace-only runs until real content appears.
				int probe = start;
				while (probe < text.Length && (text[probe] == ' ' || text[probe] == '\t'))
				{
					probe++;
				}
				if (probe < text.Length)
				{
					start = probe;
					eatGap = false;
				}
				else
				{
					// The whole run is gap: drop it.
					position += text.Length;
					continue;
				}
			}
			position += text.Length;
			if (start >= text.Length)
			{
				continue;
			}
			runs.Add(run with
			{
				Text = text[start..],
				SourceStart = run.SourceStart >= 0 ? run.SourceStart + start : run.SourceStart,
				SourceLength = Math.Max(0, run.SourceLength - start),
			});
		}

		if (runs.Count == 0)
		{
			// The marker was the whole first line; the content lives in the following paragraphs.
			return children.Skip(1).ToList();
		}
		var builder = new StringBuilder();
		foreach (MarkdownRun run in runs)
		{
			builder.Append(MarkdownDocument.VisualText(run));
		}
		List<MarkdownBlock> result = [CloneWithRuns(first, runs, builder.ToString())];
		result.AddRange(children.Skip(1));
		return result;
	}

	private static MarkdownBlock CloneWithRuns(MarkdownBlock block, List<MarkdownRun> runs, string text)
		=> new()
		{
			Kind = block.Kind,
			Runs = runs,
			Children = block.Children,
			Level = block.Level,
			Info = block.Info,
			Marker = block.Marker,
			Header = block.Header,
			Loose = block.Loose,
			Alignment = block.Alignment,
			Anchor = block.Anchor,
			Aliases = block.Aliases,
			SourceStart = block.SourceStart,
			SourceLength = block.SourceLength,
			Text = text,
			CodeLanguage = block.CodeLanguage,
			CustomKind = block.CustomKind,
			Alert = block.Alert,
		};

	/// <summary>
	/// Scheme gate for <see cref="MarkdownOptions.LinkSchemes"/>: absolute URLs with a scheme
	/// outside the allowlist render as plain text (links and images alike); anchors, relative
	/// URLs and unset options always pass. The host still decides what to open on activation.
	/// </summary>
	private static bool IsLinkAllowed(MarkdownOptions options, string? url)
	{
		if (options.LinkSchemes is not { } schemes || string.IsNullOrEmpty(url) || url.StartsWith('#'))
		{
			return true;
		}
		int colon = url.IndexOf(':');
		if (colon <= 0)
		{
			return true;
		}
		int slash = url.IndexOf('/');
		if (slash >= 0 && slash < colon)
		{
			return true;
		}
		return schemes.Contains(url[..colon].ToLowerInvariant());
	}

	private static List<MarkdownRun> MapInlines(ContainerInline? inline, MarkdownOptions options, string source)
	{
		List<MarkdownRun> runs = [];
		VisitInlines(inline?.FirstChild, default, options, source, runs);
		return runs;
	}

	private static void VisitInlines(
		Inline? inline,
		MarkdownInlineState state,
		MarkdownOptions options,
		string source,
		List<MarkdownRun> runs)
	{
		for (Inline? current = inline; current is not null; current = current.NextSibling)
		{
			switch (current)
			{
				// Unlike other extension inlines, ToString() carries the rendered character.
				// EmojiInline derives from CodeInline, so it must be matched before the code case.
				case Markdig.Extensions.Emoji.EmojiInline emoji:
					AddRun(emoji.ToString(), state, emoji, runs);
					break;

				case LiteralInline literal:
					AddRun(literal.Content.ToString(), state, literal, runs);
					break;

				case CodeInline code:
				{
					string codeText = code.Content.ToString();
					if (codeText.Length > 0)
					{
						// CSS inline-code padding is part of layout, not just a painted background.
						// Insert thin spaces outside the code run so the chip gets a real left/right
						// gap from neighbouring glyphs (copy still sees ordinary whitespace).
						AddInlineCodeMargin(state, runs);
						AddRun(codeText, state with { Style = state.Style | MarkdownRunStyle.Code }, code, runs);
						AddInlineCodeMargin(state, runs);
					}
					break;
				}

				case LineBreakInline lineBreak:
					AddRun(options.SoftBreakAsNewLine || lineBreak.IsHard ? "\n" : " ", state, lineBreak, runs);
					break;

				case LinkInline link:
					VisitLink(link, state, options, source, runs);
					break;

				case EmphasisInline emphasis:
					VisitEmphasis(emphasis, state, options, source, runs);
					break;

				case AutolinkInline autolink:
				{
					string url = autolink.Url?.ToString() ?? string.Empty;
					if (autolink.IsEmail && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
					{
						url = "mailto:" + url;
					}
					AddRun(url, IsLinkAllowed(options, url) ? state with { Url = url } : state, autolink, runs);
					break;
				}

				case HtmlEntityInline entity:
					AddRun(entity.Transcoded.ToString(), state, entity, runs);
					break;

				case TaskList task:
					runs.Add(new MarkdownRun(
						string.Empty,
						state.Style,
						TaskChecked: task.Checked,
						SourceStart: GetStart(task),
						SourceLength: GetLength(task)));
					break;

				case HtmlInline html:
					if (options.UseHtmlFormatting && ApplyHtmlTag(html, ref state, runs))
					{
						break;
					}
					AddRun(html.Tag, state, html, runs);
					break;

				case ContainerInline container:
					VisitInlines(container.FirstChild, state, options, source, runs);
					break;

				default:
					// Unknown inline nodes (math, footnotes, host extensions) have unreliable
					// ToString() output; their span is the honest source text.
					AddRun(GetSourceText(current, source), state, current, runs);
					break;
			}
		}
	}

	/// <summary>
	/// Maps one inline HTML tag of the <see cref="MarkdownOptions.UseHtmlFormatting"/> whitelist to
	/// run styling. Returns false for anything outside the whitelist so it stays literal. Opening
	/// and closing tags adjust <paramref name="state"/>; a whole comment node is simply dropped.
	/// </summary>
	private static bool ApplyHtmlTag(HtmlInline html, ref MarkdownInlineState state, List<MarkdownRun> runs)
	{
		string tag = html.Tag ?? string.Empty;
		if (tag.Length == 0)
		{
			return false;
		}
		if (tag.StartsWith("<!--", StringComparison.Ordinal))
		{
			// Markdig delivers a complete inline comment as one node.
			return true;
		}

		bool closing = tag.StartsWith("</", StringComparison.Ordinal);
		int nameStart = closing ? 2 : 1;
		int nameEnd = nameStart;
		while (nameEnd < tag.Length && char.IsAsciiLetter(tag[nameEnd]))
		{
			nameEnd++;
		}
		if (nameEnd == nameStart)
		{
			return false;
		}
		string name = tag.Substring(nameStart, nameEnd - nameStart).ToLowerInvariant();

		if (name == "br")
		{
			if (!closing)
			{
				AddRun("\n", state, html, runs);
			}
			return true;
		}

		MarkdownRunStyle bits = name switch
		{
			"b" or "strong" => MarkdownRunStyle.Bold,
			"i" or "em" => MarkdownRunStyle.Italic,
			"u" => MarkdownRunStyle.Underline,
			"s" or "strike" or "del" => MarkdownRunStyle.Strike,
			"mark" => MarkdownRunStyle.Marked,
			"ins" => MarkdownRunStyle.Inserted,
			"code" or "tt" => MarkdownRunStyle.Code,
			"sub" or "sup" => MarkdownRunStyle.Script,
			_ => 0,
		};
		if (bits == 0 && name is not ("span" or "font"))
		{
			return false;
		}
		if (bits != 0)
		{
			state = closing
				? state with { Style = state.Style & ~bits }
				: state with { Style = state.Style | bits };
		}
		return true;
	}

	private static void VisitLink(
		LinkInline link,
		MarkdownInlineState state,
		MarkdownOptions options,
		string source,
		List<MarkdownRun> runs)
	{
		string? url = link.Url?.ToString();
		string? title = link.Title?.ToString();
		if (!IsLinkAllowed(options, url))
		{
			url = null;
			title = null;
		}
		int start = runs.Count;
		VisitInlines(link.FirstChild, state with { Url = url, Title = title, Image = link.IsImage }, options, source, runs);

		if (link.IsImage)
		{
			string text = string.Concat(runs.Skip(start).Select(static run => run.Text));
			runs.RemoveRange(start, runs.Count - start);
			runs.Add(new MarkdownRun(
				text,
				state.Style,
				url,
				title,
				Image: true,
				SourceStart: GetStart(link),
				SourceLength: GetLength(link)));
			return;
		}

		for (int index = start; index < runs.Count; index++)
		{
			runs[index] = runs[index] with
			{
				Url = url,
				Title = title,
				SourceStart = GetStart(link),
				SourceLength = GetLength(link)
			};
		}
	}

	private static void VisitEmphasis(
		EmphasisInline emphasis,
		MarkdownInlineState state,
		MarkdownOptions options,
		string source,
		List<MarkdownRun> runs)
	{
		bool asterisk = emphasis.DelimiterChar is '*' or '_';
		MarkdownRunStyle style = state.Style;
		if (asterisk && emphasis.DelimiterCount >= 2)
		{
			style |= MarkdownRunStyle.Bold;
		}
		if (asterisk && (emphasis.DelimiterCount == 1 || emphasis.DelimiterCount >= 3))
		{
			style |= MarkdownRunStyle.Italic;
		}
		if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
		{
			style |= MarkdownRunStyle.Strike;
		}
		if (emphasis.DelimiterChar == '+' && emphasis.DelimiterCount >= 2)
		{
			style |= MarkdownRunStyle.Inserted;
		}
		if (emphasis.DelimiterChar == '=' && emphasis.DelimiterCount >= 2)
		{
			style |= MarkdownRunStyle.Marked;
		}

		VisitInlines(emphasis.FirstChild, state with { Style = style }, options, source, runs);
	}

	private static void AddInlineCodeMargin(MarkdownInlineState state, List<MarkdownRun> runs)
	{
		// Thin space: enough of an outer gap to match the chip's side padding without turning
		// into a full word space. Synthetic runs have no source location.
		runs.Add(new MarkdownRun(InlineCodeMargin.ToString(), state.Style, SourceStart: -1, SourceLength: 0));
	}

	private static void AddRun(string text, MarkdownInlineState state, MarkdownObject source, List<MarkdownRun> runs)
	{
		if (text.Length == 0)
		{
			return;
		}
		runs.Add(new MarkdownRun(
			text,
			state.Style,
			state.Url,
			state.Title,
			state.Image,
			SourceStart: GetStart(source),
			SourceLength: GetLength(source)));
	}

	private static string Concat(List<MarkdownRun> runs)
	{
		if (runs.Count == 0)
		{
			return string.Empty;
		}
		if (runs.Count == 1)
		{
			return VisualText(runs[0]);
		}
		StringBuilder builder = new();
		foreach (MarkdownRun run in runs)
		{
			builder.Append(VisualText(run));
		}
		return builder.ToString();
	}

	private static string VisualText(MarkdownRun run)
		=> run.Image && run.Text.Length == 0 ? "\uFFFC" : run.Text;

	private static string? NormalizeLanguage(string? info)
	{
		if (string.IsNullOrWhiteSpace(info))
		{
			return null;
		}
		string value = info.Trim().TrimStart('{', '.').ToLowerInvariant();
		int cut = value.IndexOfAny([' ', '{', '}', ',', ';']);
		if (cut > 0)
		{
			value = value[..cut];
		}
		return value.Length == 0 ? null : value;
	}

	private static string CreateAnchor(string text, HashSet<string> anchors)
	{
		StringBuilder builder = new();
		bool pendingSeparator = false;
		foreach (char character in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
		{
			if (char.IsLetterOrDigit(character))
			{
				if (pendingSeparator && builder.Length > 0)
				{
					builder.Append('-');
				}
				builder.Append(character);
				pendingSeparator = false;
			}
			else
			{
				pendingSeparator = true;
			}
		}

		string baseAnchor = builder.Length == 0 ? "section" : builder.ToString();
		string anchor = baseAnchor;
		int suffix = 1;
		while (!anchors.Add(anchor))
		{
			suffix++;
			anchor = $"{baseAnchor}-{suffix}";
		}
		return anchor;
	}

	private static string GetSourceText(MarkdownObject node, string source)
	{
		int start = GetStart(node);
		int length = GetLength(node);
		return start >= 0 && length > 0 && start + length <= source.Length
			? source.Substring(start, length)
			: node.ToString() ?? string.Empty;
	}

	internal static int GetStart(MarkdownObject node) => node.Span.Start;

	internal static int GetLength(MarkdownObject node)
		=> node.Span.End >= node.Span.Start ? node.Span.End - node.Span.Start + 1 : 0;

	private readonly record struct MarkdownInlineState(
		MarkdownRunStyle Style,
		string? Url,
		string? Title,
		bool Image);
}

/// <summary>Assigns stable hashes bottom-up after mapping.</summary>
internal static class MarkdownHashBuilder
{
	public static void Assign(IReadOnlyList<MarkdownBlock> blocks)
	{
		foreach (MarkdownBlock block in blocks)
		{
			Assign(block);
		}
	}

	private static ulong Assign(MarkdownBlock block)
	{
		foreach (MarkdownBlock child in block.Children)
		{
			Assign(child);
		}

		ulong hash = MarkdownHash.Begin();
		hash = MarkdownHash.Add(hash, (int)block.Kind);
		hash = MarkdownHash.Add(hash, block.Level);
		hash = MarkdownHash.Add(hash, block.Info);
		hash = MarkdownHash.Add(hash, block.Marker);
		hash = MarkdownHash.Add(hash, block.Header);
		hash = MarkdownHash.Add(hash, block.Loose);
		hash = MarkdownHash.Add(hash, (int)block.Alignment);
		hash = MarkdownHash.Add(hash, block.Anchor);
		hash = MarkdownHash.Add(hash, block.CodeLanguage);
		hash = MarkdownHash.Add(hash, block.CustomKind);
		hash = MarkdownHash.Add(hash, block.Alert);
		hash = MarkdownHash.Add(hash, block.Text);
		foreach (string alias in block.Aliases)
		{
			hash = MarkdownHash.Add(hash, alias);
		}
		foreach (MarkdownRun run in block.Runs)
		{
			hash = MarkdownHash.Add(hash, run.Text);
			hash = MarkdownHash.Add(hash, (int)run.Style);
			hash = MarkdownHash.Add(hash, run.Url);
			hash = MarkdownHash.Add(hash, run.Title);
			hash = MarkdownHash.Add(hash, run.Image);
			hash = MarkdownHash.Add(hash, run.TaskChecked.HasValue ? (run.TaskChecked.Value ? 1 : 0) : -1);
		}
		foreach (MarkdownBlock child in block.Children)
		{
			hash = MarkdownHash.Add(hash, child.Hash);
		}
		block.Hash = hash;
		return hash;
	}
}
