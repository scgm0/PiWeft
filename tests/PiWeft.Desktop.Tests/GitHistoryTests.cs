using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>Git 历史解析与真实仓库上的 log/详情/分支比较。</summary>
public sealed class GitHistoryTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-git-history-" + Guid.NewGuid().ToString("N")[..8]);

	public GitHistoryTests()
	{
		Directory.CreateDirectory(_root);
		RunGit("init", "-q", "-b", "main");
		RunGit("config", "user.email", "test@example.com");
		RunGit("config", "user.name", "PiWeft Test");
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string RunGit(params string[] arguments) => RunGitOutput(_root, arguments);

	private static string RunGitOutput(string cwd, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = cwd,
		};

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		var output = process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit(15_000);
		return output.Trim();
	}

	private void Commit(string message, params (string Path, string Content)[] files)
	{
		foreach (var (path, content) in files)
		{
			var full = Path.Combine(_root, path);
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllText(full, content);
		}

		RunGit("add", "--all");
		RunGit("commit", "-q", "-m", message);
	}

	[Fact]
	public void ParseLogReadsFieldsParentsAndDecorations()
	{
		var output =
			"abc123\u001fAlice\u001falice@example.com\u001f1700000000\u001fparent1 parent2\u001fHEAD -> main, tag: v1\u001ffirst line\u001e\0" +
			"def456\u001fBob\u001fbob@example.com\u001f1700000100\u001fparent1\u001f\u001fsecond line\u001e\0";

		var commits = GitHistoryReader.ParseLog(output);

		Assert.Equal(2, commits.Count);
		Assert.Equal("abc123", commits[0].Hash);
		Assert.Equal("Alice", commits[0].AuthorName);
		Assert.Equal(["parent1", "parent2"], commits[0].Parents);
		Assert.Contains("HEAD -> main", commits[0].Refs);
		Assert.True(commits[0].IsHead);
		Assert.Equal("second line", commits[1].Subject);
		Assert.False(commits[1].IsHead);
	}

	[Fact]
	public void ParseNameStatusHandlesRenameAndTargetPaths()
	{
		var files = GitHistoryReader.ParseNameStatus("M\0a.txt\0R100\0old.txt\0new.txt\0A\0b.txt\0");

		Assert.Equal(3, files.Count);
		Assert.Equal("M", files[0].Letter);
		Assert.Equal("a.txt", files[0].Path);
		Assert.Equal("R", files[1].Letter);
		Assert.Equal("old.txt", files[1].OriginalPath);
		Assert.Equal("new.txt", files[1].Path);
		Assert.Equal("A", files[2].Letter);
	}

	[Fact]
	public void LogDetailAndDiffsReadARealRepository()
	{
		Commit("first", ("a.txt", "one\n"));
		Commit("second", ("a.txt", "one\ntwo\n"));

		var log = GitHistoryReader.Log(_root, 10);
		Assert.Null(log.Error);
		Assert.Equal(2, log.Commits.Count);
		Assert.Equal("second", log.Commits[0].Subject);
		Assert.Equal(2, GitHistoryReader.Count(_root));

		var detail = GitHistoryReader.Detail(_root, log.Commits[0].Hash);
		Assert.NotNull(detail);
		Assert.Equal("second", detail!.Commit.Subject);
		Assert.Equal("a.txt", Assert.Single(detail.Files).Path);
		Assert.Equal(1, detail.Insertions);

		var diff = GitHistoryReader.CommitFileDiff(
			_root, detail.Commit.Hash, detail.Commit.Parents.FirstOrDefault(), "a.txt");
		Assert.True(diff.Ok);
		Assert.Contains("+two", diff.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void CompareBranchesReportsFilesAndAheadBehind()
	{
		Commit("base", ("a.txt", "one\n"));
		RunGit("branch", "feature");
		RunGit("switch", "-q", "feature");
		Commit("feature", ("feature.txt", "feature\n"));
		RunGit("switch", "-q", "main");

		var comparison = GitHistoryReader.Compare(_root, "main", "feature");

		Assert.Null(comparison.Error);
		var file = Assert.Single(comparison.Files);
		Assert.Equal("feature.txt", file.Path);
		Assert.Equal(1, comparison.Ahead);
		Assert.Equal(0, comparison.Behind);

		var diff = GitHistoryReader.CompareFileDiff(
			_root, comparison.BaseHash, comparison.TargetHash, file.Path);
		Assert.True(diff.Ok);
		Assert.Contains("+feature", diff.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void CheckpointRefsAreExcludedFromAllBranchesHistory()
	{
		Commit("base", ("a.txt", "one\n"));
		var before = GitHistoryReader.Count(_root);
		Assert.NotNull(Checkpoints.Create(_root, "snapshot"));

		Assert.Equal(before, GitHistoryReader.Count(_root));
		Assert.Equal(before, GitHistoryReader.Log(_root, 50).Commits.Count);
	}
}
