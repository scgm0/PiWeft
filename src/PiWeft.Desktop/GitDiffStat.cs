using System.Globalization;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// 单个文件的增删行数（来自 <c>git diff --numstat</c>）。
/// <see cref="Binary"/> 的条目没有行数：GitHub 也只给二进制文件一个占位，不显示数字。
internal readonly record struct GitDiffStat(int Added, int Removed, bool Binary = false)
{
	public int Total => Added + Removed;

	/// GitHub 式五格比例：两侧都向下取整，剩下的格子留空。
	/// 19/1 → 4 绿 + 1 空，61/59 → 2 绿 + 2 红 + 1 空。
	public (int Added, int Removed) Blocks()
	{
		var total = Total;
		if (total <= 0)
		{
			return (0, 0);
		}

		return (Added * DiffStatBlocks.BlockCount / total, Removed * DiffStatBlocks.BlockCount / total);
	}
}

/// 一次刷新读到的两份增删行数：暂存区（index 对 HEAD）与工作区（工作树对 index）。
/// 两侧分开算：部分暂存的文件在两节里的数字本来就不同。
internal sealed record GitChangeStats(
	IReadOnlyDictionary<string, GitDiffStat> Staged,
	IReadOnlyDictionary<string, GitDiffStat> Worktree)
{
	public static GitChangeStats Empty { get; } = new(
		new Dictionary<string, GitDiffStat>(StringComparer.Ordinal),
		new Dictionary<string, GitDiffStat>(StringComparer.Ordinal));

	/// 某个改动所在的那一侧的统计；未跟踪文件不在 numstat 里，返回 null。
	public GitDiffStat? For(string path, bool staged) =>
		(staged ? Staged : Worktree).TryGetValue(path, out var stat) ? stat : null;
}

/// 只读的增删行数：两次 <c>git diff --numstat -z</c>（工作区与暂存区）。
/// 逐文件跑 <c>git diff</c> 在改动上千条时要开上千个进程，这里一次拿全。
/// 没有改动的一侧不跑：常见场景是只有一堆未跟踪产物，两次 diff 都是白开进程。
internal static class GitDiffStatsReader
{
	private static readonly Dictionary<string, GitDiffStat> None = new(StringComparer.Ordinal);

	public static GitChangeStats Read(string cwd, bool staged, bool worktree) =>
		new(
			staged ? NumStat(cwd, staged: true) : None,
			worktree ? NumStat(cwd, staged: false) : None);

	/// 解析 <c>--numstat -z</c>：普通条目是 <c>added\tremoved\tpath\0</c>；
	/// 改名/复制条目的路径字段为空，后面跟原文与目标两个字段；二进制文件的行数是 <c>-</c>。
	/// 走 span 切分：2 万条改动时 string.Split 要额外分配几 MB 的字段子串。
	internal static Dictionary<string, GitDiffStat> Parse(string output)
	{
		var stats = new Dictionary<string, GitDiffStat>(StringComparer.Ordinal);
		var rest = output.AsSpan();
		while (rest.Length > 0)
		{
			var field = Take(ref rest);
			if (field.Length == 0)
			{
				continue;
			}

			var first = field.IndexOf('\t');
			if (first < 0)
			{
				continue;
			}

			var secondOffset = field[(first + 1)..].IndexOf('\t');
			if (secondOffset < 0)
			{
				continue;
			}

			var second = first + 1 + secondOffset;
			var addedText = field[..first];
			var removedText = field[(first + 1)..second];
			var path = field[(second + 1)..];
			if (path.Length == 0)
			{
				// 改名/复制：原路径与目标路径各占一个字段，列表按目标路径显示。
				_ = Take(ref rest);
				path = Take(ref rest);
			}

			if (path.Length == 0)
			{
				continue;
			}

			var binary = addedText.SequenceEqual("-") || removedText.SequenceEqual("-");
			var added = !binary && int.TryParse(addedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAdded)
				? parsedAdded
				: 0;
			var removed = !binary && int.TryParse(removedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemoved)
				? parsedRemoved
				: 0;
			stats[path.ToString()] = new GitDiffStat(added, removed, binary);
		}

		return stats;
	}

	/// 取出下一个 NUL 分隔字段；末尾没有分隔符时返回剩余全部。
	private static ReadOnlySpan<char> Take(ref ReadOnlySpan<char> rest)
	{
		var end = rest.IndexOf('\0');
		if (end < 0)
		{
			var tail = rest;
			rest = default;
			return tail;
		}

		var field = rest[..end];
		rest = rest[(end + 1)..];
		return field;
	}

	private static Dictionary<string, GitDiffStat> NumStat(string cwd, bool staged)
	{
		// --no-relative：用户开了 diff.relative 时路径是相对 cwd 的，和 status 的仓库根相对路径对不上。
		string[] arguments = staged
			? ["diff", "--no-relative", "--cached", "--numstat", "-z", "--no-color"]
			: ["diff", "--no-relative", "--numstat", "-z", "--no-color"];
		var (output, _, exitCode) = GitStatusReader.RunDetailed(cwd, arguments);
		return exitCode == 0 ? Parse(output) : [];
	}
}

/// 一行结尾的五格增删比例条：绿=新增、红=删除、空心=未占用的格子。
/// 自绘比五个 Border 便宜，也省掉每行一组控件；纯展示，不参与命中。
internal sealed class DiffStatBlocks : FrameworkElement
{
	internal const int BlockCount = 5;
	// 方块对齐 GitHub 的比例：8 DIP 圆角个小方块 + 2 DIP 间距。6 DIP 尖角在深色底部
	// 看上去像几个噪点，空心的那一格更像一个白框。
	private const double BlockSize = 8;
	private const double BlockGap = 2;
	private const double BlockRadius = 2;

	private int _added;
	private int _removed;

	public DiffStatBlocks()
	{
		IsHitTestVisible = false;
		VerticalAlignment = VerticalAlignment.Center;
	}

	public void Set(int added, int removed)
	{
		if (_added == added && _removed == removed)
		{
			return;
		}

		_added = added;
		_removed = removed;
		InvalidateVisual();
	}

	protected override Size MeasureContent(Size availableSize) =>
		new(BlockCount * BlockSize + (BlockCount - 1) * BlockGap, BlockSize);

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnRender(IGraphicsContext context)
	{
		var palette = AppTheme.For(Theme);
		var (added, removed) = new GitDiffStat(_added, _removed).Blocks();
		var top = Bounds.Y + (Bounds.Height - BlockSize) / 2;
		for (var index = 0; index < BlockCount; index++)
		{
			var block = new Rect(Bounds.X + index * (BlockSize + BlockGap), top, BlockSize, BlockSize);
			if (index < added)
			{
				context.FillRoundedRectangle(block, BlockRadius, BlockRadius, palette.Success);
			}
			else if (index < added + removed)
			{
				context.FillRoundedRectangle(block, BlockRadius, BlockRadius, palette.Error);
			}
			else
			{
				context.DrawRoundedRectangle(block, BlockRadius, BlockRadius, palette.TextFaint);
			}
		}
	}
}
