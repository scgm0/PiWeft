using System.Text;
using Aprillz.MewUI;

namespace PiWeft.Markdown;

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

	Custom
}

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

	Script = 1 << 7
}

/// One inline run. Text is materialized by the mapper; source offsets let selection, links and
/// diagnostics address the original document.
public readonly record struct MarkdownRun(
	string Text,
	MarkdownRunStyle Style = MarkdownRunStyle.None,
	string? Url = null,
	string? Title = null,
	bool Image = false,
	bool? TaskChecked = null,
	int SourceStart = -1,
	int SourceLength = 0);

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

	public string Text { get; init; } = string.Empty;

	public string? CodeLanguage { get; init; }

	public string? CustomKind { get; init; }

	/// GitHub alert kind ("NOTE"/"TIP"/"IMPORTANT"/"WARNING"/"CAUTION") for quote blocks whose
	/// first line carries the marker; the marker itself is stripped from the content.
	public string? Alert { get; init; }

	public ulong Hash { get; internal set; }

	/// <summary>子树里是否有图片 run：高度异步变化，不能进跨实例高度缓存。</summary>
	public bool HasImage { get; internal set; }

	/// <summary>这个块在所属文档文本单元里的下标，由收集阶段写入；没有文本单元的块为 -1。</summary>
	internal int TextUnitIndex { get; set; } = -1;
}

public sealed record MarkdownTextUnit(
	MarkdownBlock Block,
	string Text,
	string Separator,
	int TopIndex,
	bool HasLinks,
	string ListMarkerPrefix = "");

public sealed class MarkdownDocument
{
	private readonly string? _sourceText;
	private readonly StringBuilder? _sourceBuilder;
	private readonly int[] _unitStarts;

	/// String-backed documents carry the full text; builder-backed documents hold the growing
	/// <see cref="StringBuilder"/> instead, so streaming frames never materialize the whole
	/// source. A builder-backed document may still carry a materialized snapshot from its
	/// latest full parse.
	internal MarkdownDocument(
		string? sourceText,
		StringBuilder? sourceBuilder,
		IReadOnlyList<MarkdownBlock> blocks,
		MarkdownOptions options)
		: this(sourceText, sourceBuilder, blocks, options, previous: null, reusedBlockCount: 0, anchorTable: null)
	{
	}

	/// Builds a document whose first <paramref name="reusedBlockCount"/> blocks are the unchanged prefix
	/// of <paramref name="previous"/>. Streaming appends replace only the old tail; rebuilding the
	/// selectable text units for the whole history on every frame is O(document) work even though
	/// the parser already reparsed only the tail. <paramref name="anchorTable"/> lets the caller hand
	/// in a table it already built, and share the previous one when the tail contributed no anchor.
	internal MarkdownDocument(
		string? sourceText,
		StringBuilder? sourceBuilder,
		IReadOnlyList<MarkdownBlock> blocks,
		MarkdownOptions options,
		MarkdownDocument? previous,
		int reusedBlockCount,
		Dictionary<string, int>? anchorTable = null)
	{
		_sourceText = sourceText;
		_sourceBuilder = sourceBuilder;
		SourceLength = sourceText?.Length ?? sourceBuilder?.Length ?? 0;
		Options = options;
		Blocks = blocks;
		reusedBlockCount = Math.Clamp(reusedBlockCount, 0, Math.Min(previous?.Blocks.Count ?? 0, blocks.Count));
		ReusedBlockCount = previous is null ? 0 : reusedBlockCount;

		List<MarkdownTextUnit> units;
		int[] starts;
		if (previous is null || reusedBlockCount == 0)
		{
			anchorTable ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			(units, starts) = BuildAll(blocks, anchorTable);
		}
		else
		{
			anchorTable ??= RebuildAnchors(previous, blocks, reusedBlockCount);
			(units, starts) = BuildIncremental(previous, blocks, reusedBlockCount);
		}

		AnchorTable = anchorTable;
		TextUnits = units;
		_unitStarts = starts;
	}

	/// <summary>锚点表；与上一版文档共享同一个实例时不再复制。</summary>
	internal Dictionary<string, int> AnchorTable { get; }

	internal int ReusedBlockCount { get; }

	private static (List<MarkdownTextUnit> Units, int[] Starts) BuildAll(
		IReadOnlyList<MarkdownBlock> blocks, Dictionary<string, int> anchors)
	{
		var units = new List<MarkdownTextUnit>();
		var starts = new int[blocks.Count + 1];
		for (int index = 0; index < blocks.Count; index++)
		{
			starts[index] = units.Count;
			CollectAnchors(blocks[index], index, anchors);
			CollectTextUnits(blocks[index], "\n", index, units);
		}

		starts[blocks.Count] = units.Count;
		return (units, starts);
	}

	private static (List<MarkdownTextUnit> Units, int[] Starts) BuildIncremental(
		MarkdownDocument previous,
		IReadOnlyList<MarkdownBlock> blocks,
		int reusedBlockCount)
	{
		int prefixUnits = previous._unitStarts[reusedBlockCount];
		var units = new List<MarkdownTextUnit>(prefixUnits + Math.Max(0, blocks.Count - reusedBlockCount));
		for (int index = 0; index < prefixUnits; index++)
		{
			units.Add(previous.TextUnits[index]);
		}

		var starts = new int[blocks.Count + 1];
		Array.Copy(previous._unitStarts, starts, reusedBlockCount + 1);
		for (int index = reusedBlockCount; index < blocks.Count; index++)
		{
			starts[index] = units.Count;
			CollectTextUnits(blocks[index], "\n", index, units);
		}

		starts[blocks.Count] = units.Count;
		return (units, starts);
	}

	/// 锚点表只在片尾真的带了锚点（标题等）时才重建：流式增长的一整段普通段落按引用共享上一版。
	private static Dictionary<string, int> RebuildAnchors(
		MarkdownDocument previous,
		IReadOnlyList<MarkdownBlock> blocks,
		int reusedBlockCount)
	{
		var anchors = new Dictionary<string, int>(previous.AnchorTable, StringComparer.OrdinalIgnoreCase);
		// Drop anchors contributed by the replaced suffix before adding the freshly parsed tail.
		// Only values owned by those top-level blocks may be removed: an anchor duplicated in an
		// unchanged earlier block must keep its first definition.
		for (int index = reusedBlockCount; index < previous.Blocks.Count; index++)
		{
			RemoveAnchors(previous.Blocks[index], index, anchors);
		}
		for (int index = reusedBlockCount; index < blocks.Count; index++)
		{
			CollectAnchors(blocks[index], index, anchors);
		}

		return anchors;
	}

	internal static void RemoveAnchors(MarkdownBlock block, int topIndex, Dictionary<string, int> anchors)
	{
		Remove(block.Anchor);
		foreach (string alias in block.Aliases)
		{
			Remove(alias);
		}

		foreach (MarkdownBlock child in block.Children)
		{
			RemoveAnchors(child, topIndex, anchors);
		}

		void Remove(string? anchor)
		{
			if (!string.IsNullOrEmpty(anchor) &&
				anchors.TryGetValue(anchor, out int owner) && owner == topIndex)
			{
				anchors.Remove(anchor);
			}
		}
	}

	internal int SourceLength { get; }

	internal StringBuilder? SourceBuilder => _sourceBuilder;

	internal bool TailRepaired { get; set; }

	public string Source => _sourceText ?? _sourceBuilder?.ToString() ?? string.Empty;

	public IReadOnlyList<MarkdownBlock> Blocks { get; }

	public bool HasLinkReferenceDefinitions { get; init; }
	public MarkdownOptions Options { get; }
	public IReadOnlyDictionary<string, int> AnchorBlocks => AnchorTable;
	public IReadOnlyList<MarkdownTextUnit> TextUnits { get; }

	public int GetTextUnit(MarkdownBlock block) => block.TextUnitIndex;

	public static string VisualText(MarkdownRun run) => run.Image && run.Text.Length == 0 ? "\uFFFC" : run.Text;

	public static string GetListMarkerText(MarkdownBlock item)
		=> item.Children.FirstOrDefault()?.Runs.Any(static run => run.TaskChecked.HasValue) == true
			? string.Empty
			: string.IsNullOrWhiteSpace(item.Marker) ? "•" : item.Marker;

	private static void CollectTextUnits(MarkdownBlock block, string separator, int topIndex, List<MarkdownTextUnit> units)
	{
		switch (block.Kind)
		{
			case MarkdownBlockKind.Paragraph:
			case MarkdownBlockKind.Heading:
			case MarkdownBlockKind.Code:
				bool hasLinks = block.Runs.Any(static run => VisualText(run).Length > 0 && run.Url != null);
				// 块自己记住下标：每个块只产出一个文本单元，前缀块的下标在追加路径上不变，
				// 因此不需要每帧为整份文档重建一张 块→下标 的字典。
				block.TextUnitIndex = units.Count;
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

	internal static void CollectAnchors(MarkdownBlock block, int topIndex, Dictionary<string, int> anchors)
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
