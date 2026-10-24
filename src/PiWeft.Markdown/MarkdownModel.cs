using System.Text;
using Aprillz.MewUI;

namespace PiWeft.Markdown;

/// <summary>Top-level and nested block kinds the renderer understands.</summary>
public enum MarkdownBlockKind
{
	Paragraph,
	Heading,
	Code,
	Rule,
	Quote,
	List,
	ListItem,
	Table,
	TableRow,
	TableCell,
	Group,

	/// <summary>A block without a built-in mapping (e.g. a math block); <see cref="MarkdownBlock.CustomKind"/> carries the Markdig node type name.</summary>
	Custom
}

/// <summary>Inline style flags carried by a <see cref="MarkdownRun"/>.</summary>
[Flags]
public enum MarkdownRunStyle
{
	None = 0,
	Bold = 1 << 0,
	Italic = 1 << 1,
	Strike = 1 << 2,
	Inserted = 1 << 3,
	Marked = 1 << 4,
	Code = 1 << 5,
	Underline = 1 << 6,

	/// <summary>Subscript/superscript text from the inline HTML whitelist: smaller font, same baseline.</summary>
	Script = 1 << 7
}

/// <summary>
/// One inline run. Text is materialized by the mapper; source offsets let selection, links and
/// diagnostics address the original document.
/// </summary>
public readonly record struct MarkdownRun(
	string Text,
	MarkdownRunStyle Style = MarkdownRunStyle.None,
	string? Url = null,
	string? Title = null,
	bool Image = false,
	bool? TaskChecked = null,
	int SourceStart = -1,
	int SourceLength = 0);

/// <summary>An immutable mapped block; <see cref="Hash"/> identifies equal presentation content.</summary>
public sealed class MarkdownBlock
{
	public MarkdownBlockKind Kind { get; init; }
	public IReadOnlyList<MarkdownRun> Runs { get; internal set; } = [];
	public IReadOnlyList<MarkdownBlock> Children { get; init; } = [];
	public int Level { get; init; }
	public string? Info { get; init; }
	public string? Marker { get; init; }
	public bool Header { get; init; }
	public bool Loose { get; init; }
	public TextAlignment Alignment { get; init; }
	public string? Anchor { get; init; }
	public IReadOnlyList<string> Aliases { get; init; } = [];
	public int SourceStart { get; internal set; }
	public int SourceLength { get; init; }

	/// <summary>Plain display text for paragraph, heading and code blocks; empty for containers.</summary>
	public string Text { get; init; } = string.Empty;

	/// <summary>Normalized fence language for code blocks.</summary>
	public string? CodeLanguage { get; init; }

	/// <summary>Markdig node type name for <see cref="MarkdownBlockKind.Custom"/> blocks; null otherwise.</summary>
	public string? CustomKind { get; init; }

	/// <summary>
	/// GitHub alert kind ("NOTE"/"TIP"/"IMPORTANT"/"WARNING"/"CAUTION") for quote blocks whose
	/// first line carries the marker; the marker itself is stripped from the content.
	/// </summary>
	public string? Alert { get; init; }

	/// <summary>Stable 64-bit hash of every input that affects presentation.</summary>
	public ulong Hash { get; internal set; }
}

/// <summary>A selectable leaf block and the separator copied before it.</summary>
public sealed record MarkdownTextUnit(
	MarkdownBlock Block,
	string Text,
	string Separator,
	int TopIndex,
	bool HasLinks,
	string ListMarkerPrefix = "");

/// <summary>One parsed revision: blocks, source and the bookkeeping selection and navigation need.</summary>
public sealed class MarkdownDocument
{
	private readonly Dictionary<MarkdownBlock, int> _unitIndex = new(ReferenceEqualityComparer.Instance);
	private readonly string? _sourceText;
	private readonly StringBuilder? _sourceBuilder;

	/// <summary>
	/// String-backed documents carry the full text; builder-backed documents hold the growing
	/// <see cref="StringBuilder"/> instead, so streaming frames never materialize the whole
	/// source. A builder-backed document may still carry a materialized snapshot from its
	/// latest full parse.
	/// </summary>
	internal MarkdownDocument(
		string? sourceText,
		StringBuilder? sourceBuilder,
		IReadOnlyList<MarkdownBlock> blocks,
		MarkdownOptions options)
	{
		_sourceText = sourceText;
		_sourceBuilder = sourceBuilder;
		SourceLength = sourceText?.Length ?? sourceBuilder?.Length ?? 0;
		Options = options;
		Blocks = blocks;

		Dictionary<string, int> anchorBlocks = new(StringComparer.OrdinalIgnoreCase);
		List<MarkdownTextUnit> units = [];
		for (int index = 0; index < blocks.Count; index++)
		{
			CollectAnchors(blocks[index], index, anchorBlocks);
			CollectTextUnits(blocks[index], "\n", index, units);
		}

		AnchorBlocks = anchorBlocks;
		TextUnits = units;
	}

	/// <summary>Length of the source this document was parsed from, without materializing it.</summary>
	internal int SourceLength { get; }

	/// <summary>The builder this document was parsed from, when it is append-only backed.</summary>
	internal StringBuilder? SourceBuilder => _sourceBuilder;

	/// <summary>True when the last block was parsed with a provisional streaming repair suffix.</summary>
	internal bool TailRepaired { get; set; }

	public string Source => _sourceText ?? _sourceBuilder?.ToString() ?? string.Empty;

	public IReadOnlyList<MarkdownBlock> Blocks { get; }

	/// <summary>True when the source contains a link reference definition; incremental parsing is then unsafe.</summary>
	public bool HasLinkReferenceDefinitions { get; init; }
	public MarkdownOptions Options { get; }
	public IReadOnlyDictionary<string, int> AnchorBlocks { get; }
	public IReadOnlyList<MarkdownTextUnit> TextUnits { get; }

	/// <summary>Returns the text unit index of a leaf block, or -1 when it has no selectable text.</summary>
	public int GetTextUnit(MarkdownBlock block) => _unitIndex.TryGetValue(block, out int index) ? index : -1;

	/// <summary>Display text of one run, matching what the paragraph lays out.</summary>
	public static string VisualText(MarkdownRun run) => run.Image && run.Text.Length == 0 ? "\uFFFC" : run.Text;

	/// <summary>Marker a list item displays: nothing for a task item, a bullet when the item has no marker.</summary>
	public static string GetListMarkerText(MarkdownBlock item)
		=> item.Children.FirstOrDefault()?.Runs.Any(static run => run.TaskChecked.HasValue) == true
			? string.Empty
			: string.IsNullOrWhiteSpace(item.Marker) ? "•" : item.Marker;

	private void CollectTextUnits(MarkdownBlock block, string separator, int topIndex, List<MarkdownTextUnit> units)
	{
		switch (block.Kind)
		{
			case MarkdownBlockKind.Paragraph:
			case MarkdownBlockKind.Heading:
			case MarkdownBlockKind.Code:
				_unitIndex[block] = units.Count;
				bool hasLinks = block.Runs.Any(static run => VisualText(run).Length > 0 && run.Url != null);
				units.Add(new MarkdownTextUnit(block, block.Text, separator, topIndex, hasLinks));
				break;

			case MarkdownBlockKind.Table:
				for (int rowIndex = 0; rowIndex < block.Children.Count; rowIndex++)
				{
					MarkdownBlock row = block.Children[rowIndex];
					for (int cellIndex = 0; cellIndex < row.Children.Count; cellIndex++)
					{
						string cellSeparator = cellIndex > 0 ? "\t" : rowIndex > 0 ? "\n" : separator;
						foreach (MarkdownBlock child in row.Children[cellIndex].Children)
						{
							CollectTextUnits(child, cellSeparator, topIndex, units);
							cellSeparator = "\n";
						}
					}
				}
				break;

			case MarkdownBlockKind.ListItem:
				int firstUnit = units.Count;
				foreach (MarkdownBlock child in block.Children)
				{
					CollectTextUnits(child, separator, topIndex, units);
				}
				string marker = GetListMarkerText(block);
				if (marker.Length > 0 && firstUnit < units.Count)
				{
					units[firstUnit] = units[firstUnit] with
					{
						ListMarkerPrefix = marker + "\t" + units[firstUnit].ListMarkerPrefix
					};
				}
				break;

			default:
				foreach (MarkdownBlock child in block.Children)
				{
					CollectTextUnits(child, separator, topIndex, units);
				}
				break;
		}
	}

	private static void CollectAnchors(MarkdownBlock block, int topIndex, Dictionary<string, int> anchors)
	{
		if (!string.IsNullOrEmpty(block.Anchor))
		{
			anchors.TryAdd(block.Anchor, topIndex);
		}
		foreach (string alias in block.Aliases)
		{
			anchors.TryAdd(alias, topIndex);
		}
		foreach (MarkdownBlock child in block.Children)
		{
			CollectAnchors(child, topIndex, anchors);
		}
	}

	/// <summary>Plain text of the whole document, using the same separators as selection copy.</summary>
	public string ToPlainText()
	{
		StringBuilder builder = new();
		for (int index = 0; index < TextUnits.Count; index++)
		{
			MarkdownTextUnit unit = TextUnits[index];
			if (index > 0)
			{
				builder.Append(unit.Separator);
			}
			builder.Append(unit.ListMarkerPrefix).Append(unit.Text);
		}
		return builder.ToString();
	}
}

/// <summary>FNV-1a hashing for stable block identity within a process.</summary>
internal static class MarkdownHash
{
	private const ulong Offset = 14695981039346656037UL;
	private const ulong Prime = 1099511628211UL;

	public static ulong Begin() => Offset;

	public static ulong Add(ulong hash, byte value) => (hash ^ value) * Prime;

	public static ulong Add(ulong hash, int value)
	{
		hash = Add(hash, (byte)value);
		hash = Add(hash, (byte)(value >> 8));
		hash = Add(hash, (byte)(value >> 16));
		hash = Add(hash, (byte)(value >> 24));
		return hash;
	}

	public static ulong Add(ulong hash, ulong value)
	{
		for (int shift = 0; shift < 64; shift += 8)
		{
			hash = Add(hash, (byte)(value >> shift));
		}
		return hash;
	}

	public static ulong Add(ulong hash, bool value) => Add(hash, (byte)(value ? 1 : 0));

	public static ulong Add(ulong hash, string? value)
	{
		if (value is null)
		{
			return Add(hash, 0xFF);
		}
		hash = Add(hash, value.Length);
		foreach (char character in value)
		{
			hash = Add(hash, (byte)character);
			hash = Add(hash, (byte)(character >> 8));
		}
		return hash;
	}
}
