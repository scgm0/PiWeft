using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>提交图 swimlane 算法：线性、分叉/合并与宽度上限。</summary>
public sealed class GitCommitGraphTests
{
	private static GitCommitEntry Commit(string hash, params string[] parents) => new(
		hash,
		"author",
		"author@example.com",
		DateTimeOffset.UnixEpoch,
		parents,
		[],
		hash);

	[Fact]
	public void LinearHistoryKeepsOneLane()
	{
		var rows = GitCommitGraph.Build([
			Commit("c", "b"),
			Commit("b", "a"),
			Commit("a"),
		]);

		Assert.All(rows, row => Assert.Equal(0, row.NodeIndex));
		Assert.Equal(["b"], rows[0].Output.Select(static node => node.Id));
		Assert.Equal(["a"], rows[1].Output.Select(static node => node.Id));
		Assert.Empty(rows[2].Output);
	}

	[Fact]
	public void MergeIntroducesASecondLaneAndClosesIt()
	{
		var rows = GitCommitGraph.Build([
			Commit("merge", "main-parent", "feature-parent"),
			Commit("main-parent", "base"),
			Commit("feature-parent", "base"),
			Commit("base"),
		]);

		Assert.Equal(2, rows[0].Output.Count);
		Assert.Contains(rows[0].Output, static node => node.Id == "main-parent");
		Assert.Contains(rows[0].Output, static node => node.Id == "feature-parent");
		Assert.NotEqual(rows[0].Output[0].Color, rows[0].Output[1].Color);
		Assert.Equal(["base", "feature-parent"], rows[1].Output.Select(static node => node.Id));
		Assert.Equal(["base", "base"], rows[2].Output.Select(static node => node.Id));
		Assert.Empty(rows[3].Output);
	}

	[Fact]
	public void WidthIsCappedAtEightVisibleLanes()
	{
		var row = new GitGraphRow(
			Commit("head"),
			Enumerable.Range(0, 12).Select(index => new GitGraphNode($"lane-{index}", index)).ToArray(),
			Enumerable.Range(0, 12).Select(index => new GitGraphNode($"lane-{index}", index)).ToArray(),
			0);

		Assert.Equal((GitCommitGraph.MaxVisibleLanes + 1) * GitCommitGraph.LaneWidth, GitCommitGraph.Width(row));
	}
}
