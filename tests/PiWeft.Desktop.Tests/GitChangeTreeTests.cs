using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>变更树的紧凑链、目录优先排序与折叠语义。</summary>
public sealed class GitChangeTreeTests
{
	private static GitChange Change(string path, string index = " ", string workTree = "M") =>
		new(index, workTree, path);

	[Fact]
	public void CompactChainMergesSingleChildDirectories()
	{
		var rows = GitChangeTree.Build([Change("src/main/java/com/One.cs")]);

		var directory = Assert.Single(rows, static row => row.IsDirectory);
		Assert.Equal("src.main.java.com", directory.Label);
		Assert.Equal(0, directory.Depth);
		Assert.Single(directory.Changes);

		var file = Assert.Single(rows, static row => !row.IsDirectory);
		Assert.Equal("One.cs", file.Label);
		Assert.Equal(1, file.Depth);
	}

	[Fact]
	public void DirectoriesSortBeforeFilesAndKeepRootFilesAtDepthZero()
	{
		var rows = GitChangeTree.Build([
			Change("zeta.txt"),
			Change("src/One.cs"),
			Change("alpha.txt"),
			Change("docs/Guide.md"),
		]);

		Assert.Equal(
			[GitTreeRowKind.Directory, GitTreeRowKind.File],
			rows.Take(2).Select(static row => row.Kind));
		Assert.Equal("docs", rows[0].Label);
		Assert.Equal("Guide.md", rows[1].Label);
		Assert.Equal("src", rows[2].Label);
		Assert.Equal("One.cs", rows[3].Label);
		Assert.Equal("alpha.txt", rows[4].Label);
		Assert.Equal("zeta.txt", rows[5].Label);
		Assert.Equal(0, rows[4].Depth);
	}

	[Fact]
	public void CollapsedDirectoryKeepsItsSubtreeResources()
	{
		var collapsed = new HashSet<string>(StringComparer.Ordinal) { "src" };
		var rows = GitChangeTree.Build([
			Change("src/Feature/One.cs"),
			Change("src/Two.cs"),
		], collapsed);

		var directory = Assert.Single(rows);
		Assert.True(directory.IsDirectory);
		Assert.Equal("src", directory.Label);
		Assert.Equal(2, directory.Changes.Count);
	}

	[Fact]
	public void AllDirectoryPathsReturnsEveryFoldableChain()
	{
		var paths = GitChangeTree.AllDirectoryPaths([
			Change("src/Feature/One.cs"),
			Change("docs/Guide.md"),
			Change("root.txt"),
		]);

		Assert.Contains("src/Feature", paths);
		Assert.Contains("docs", paths);
		Assert.DoesNotContain("root.txt", paths);
	}

	[Fact]
	public void WindowsSeparatorsAreNormalized()
	{
		var rows = GitChangeTree.Build([Change(@"src\Feature\One.cs")]);

		Assert.Equal("src.Feature", rows[0].Label);
		Assert.Equal("src/Feature/One.cs", rows[^1].Path);
		Assert.Equal("One.cs", rows[^1].Label);
	}
}
