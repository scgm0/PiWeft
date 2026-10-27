using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// <summary>提交图里的一条 lane。</summary>
internal sealed record GitGraphNode(string Id, int Color);

/// <summary>一行提交的输入/输出 lane；算法与 PiDeck 的 swimlane 一致。</summary>
internal sealed record GitGraphRow(
	GitCommitEntry Commit,
	IReadOnlyList<GitGraphNode> Input,
	IReadOnlyList<GitGraphNode> Output,
	int NodeIndex);

/// <summary>把拓扑序提交折成连续的输入/输出 lane。</summary>
internal static class GitCommitGraph
{
	public const int LaneWidth = 14;
	public const int RowHeight = 28;
	public const int MaxVisibleLanes = 8;
	public const int ColorCount = 6;

	public static IReadOnlyList<GitGraphRow> Build(IReadOnlyList<GitCommitEntry> commits)
	{
		var rows = new List<GitGraphRow>(commits.Count);
		var previous = new List<GitGraphNode>();
		var colorIndex = -1;

		foreach (var commit in commits)
		{
			var input = new List<GitGraphNode>(previous);
			var inputIndex = input.FindIndex(node => string.Equals(node.Id, commit.Hash, StringComparison.Ordinal));
			var nodeIndex = inputIndex < 0 ? input.Count : inputIndex;
			var output = new List<GitGraphNode>();
			var firstParentAdded = false;

			foreach (var node in input)
			{
				if (string.Equals(node.Id, commit.Hash, StringComparison.Ordinal))
				{
					if (commit.Parents.Count > 0 && !firstParentAdded)
					{
						output.Add(new GitGraphNode(commit.Parents[0], node.Color));
						firstParentAdded = true;
					}

					continue;
				}

				output.Add(node);
			}

			for (var parentIndex = firstParentAdded ? 1 : 0; parentIndex < commit.Parents.Count; parentIndex++)
			{
				colorIndex = (colorIndex + 1) % ColorCount;
				output.Add(new GitGraphNode(commit.Parents[parentIndex], colorIndex));
			}

			rows.Add(new GitGraphRow(commit, input, output, nodeIndex));
			previous = output;
		}

		return rows;
	}

	public static int Width(GitGraphRow row)
	{
		var lanes = Math.Clamp(Math.Max(1, Math.Max(row.Input.Count, row.Output.Count)), 1, MaxVisibleLanes);
		return (lanes + 1) * LaneWidth;
	}
}

/// <summary>
/// 一行提交的 lane 绘制：输入 lane 到输出的连接、分叉/合并曲线与节点。
/// 只是数据可视化，不参与命中；点击交给外层提交行按钮。
/// </summary>
internal sealed class CommitGraphLanes : FrameworkElement
{
	private GitGraphRow? _row;
	private bool _current;

	public CommitGraphLanes()
	{
		IsHitTestVisible = false;
		VerticalAlignment = VerticalAlignment.Center;
	}

	public void SetRow(GitGraphRow row, bool current)
	{
		_row = row;
		_current = current;
		InvalidateMeasure();
		InvalidateVisual();
	}

	protected override Size MeasureContent(Size availableSize) =>
		_row is null ? new Size(GitCommitGraph.LaneWidth, GitCommitGraph.RowHeight)
			: new Size(GitCommitGraph.Width(_row), GitCommitGraph.RowHeight);

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnRender(IGraphicsContext context)
	{
		if (_row is not { } row)
		{
			return;
		}

		var palette = AppTheme.For(Theme);
		var originX = Bounds.X;
		var originY = Bounds.Y;
		var height = Bounds.Height;
		var bottom = originY + height;
		var middle = originY + height / 2;
		var commit = row.Commit;
		var nodeIndex = Math.Clamp(row.NodeIndex, 0, GitCommitGraph.MaxVisibleLanes - 1);
		var nodeX = X(nodeIndex);
		var inputIndex = row.Input.ToList().FindIndex(node => string.Equals(node.Id, commit.Hash, StringComparison.Ordinal));
		var nodeColorIndex = inputIndex >= 0
			? row.Input[inputIndex].Color
			: row.Output.Count > row.NodeIndex ? row.Output[row.NodeIndex].Color : 0;
		var nodeColor = LaneColor(palette, nodeColorIndex);
		var background = palette.SidebarBackground;

		double X(int lane) => originX + GitCommitGraph.LaneWidth * (lane + 1);

		int OutputLane(string id)
		{
			for (var index = row.Output.Count - 1; index >= 0; index--)
			{
				if (string.Equals(row.Output[index].Id, id, StringComparison.Ordinal))
				{
					return Math.Clamp(index, 0, GitCommitGraph.MaxVisibleLanes - 1);
				}
			}

			return -1;
		}

		// 输入 lane：同一 id 在输出里继续走；换道时画一条 S 曲线。
		for (var index = 0; index < row.Input.Count; index++)
		{
			var node = row.Input[index];
			if (string.Equals(node.Id, commit.Hash, StringComparison.Ordinal))
			{
				continue;
			}

			var from = Math.Clamp(index, 0, GitCommitGraph.MaxVisibleLanes - 1);
			var to = OutputLane(node.Id);
			if (to < 0)
			{
				context.DrawLine(new Point(X(from), originY), new Point(X(from), middle),
					LaneColor(palette, node.Color), 1.5);
				continue;
			}

			if (from == to)
			{
				context.DrawLine(new Point(X(from), originY), new Point(X(from), bottom),
					LaneColor(palette, node.Color), 1.5);
			}
			else
			{
				DrawCurve(context, X(from), originY, X(to), bottom, LaneColor(palette, node.Color));
			}
		}

		// 节点到父提交：第一父通常占原 lane，合并的额外父向右分叉。
		for (var parentIndex = 0; parentIndex < commit.Parents.Count; parentIndex++)
		{
			var to = OutputLane(commit.Parents[parentIndex]);
			if (to < 0)
			{
				continue;
			}

			if (parentIndex == 0 && to == nodeIndex)
			{
				context.DrawLine(new Point(nodeX, middle), new Point(nodeX, bottom), nodeColor, 1.5);
			}
			else
			{
				DrawCurve(context, nodeX, middle, X(to), bottom, LaneColor(palette, nodeColorIndex));
			}
		}

		if (commit.Parents.Count > 1)
		{
			context.DrawEllipse(new Rect(nodeX - 6, middle - 6, 12, 12), nodeColor, 1.5);
		}

		context.FillEllipse(new Rect(nodeX - 4.5, middle - 4.5, 9, 9), nodeColor);
		context.DrawEllipse(new Rect(nodeX - 4.5, middle - 4.5, 9, 9), background, 2);
		if (_current)
		{
			context.FillEllipse(new Rect(nodeX - 2, middle - 2, 4, 4), background);
		}
	}

	private static void DrawCurve(IGraphicsContext context, double x1, double y1, double x2, double y2, Color color)
	{
		var path = new PathGeometry();
		path.MoveTo(x1, y1);
		path.BezierTo(x1, (y1 + y2) / 2, x2, (y1 + y2) / 2, x2, y2);
		context.DrawPath(path, color, 1.5);
	}

	private static Color LaneColor(AppPalette palette, int index) => (index % GitCommitGraph.ColorCount) switch
	{
		0 => palette.TextMuted,
		1 => palette.Info,
		2 => palette.Success,
		3 => palette.Thinking,
		4 => palette.Error,
		_ => palette.TextSubtle,
	};
}
