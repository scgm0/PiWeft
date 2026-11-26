using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 文件行尾的增删数：解析 <c>git diff --numstat -z</c>，以及 GitHub 式五格比例的取整规则。
public class GitDiffStatTests
{
	[Fact]
	public void ParsesRegularBinaryAndRenamedEntries()
	{
		var stats = GitDiffStatsReader.Parse(
			"2\t1\ta.txt\0" +
			"-\t-\tbin.dat\0" +
			"0\t0\t\0old.txt\0new.txt\0" +
			"5\t0\tdir/b.txt\0");

		Assert.Equal(new GitDiffStat(2, 1), stats["a.txt"]);
		Assert.True(stats["bin.dat"].Binary);
		Assert.Equal(0, stats["bin.dat"].Total);
		// 改名按目标路径列出，和状态列表里的那一行对得上。
		Assert.Equal(new GitDiffStat(0, 0), stats["new.txt"]);
		Assert.False(stats.ContainsKey("old.txt"));
		Assert.Equal(new GitDiffStat(5, 0), stats["dir/b.txt"]);
	}

	[Fact]
	public void EmptyOutputHasNoEntries()
	{
		Assert.Empty(GitDiffStatsReader.Parse(string.Empty));
	}

	[Theory]
	// 两侧都向下取整，剩下的格子留空：纯新增 5 格全绿，混合改动按比例分。
	[InlineData(19, 1, 4, 0)]
	[InlineData(61, 59, 2, 2)]
	[InlineData(91, 0, 5, 0)]
	[InlineData(1, 201, 0, 4)]
	[InlineData(1, 0, 5, 0)]
	[InlineData(0, 3, 0, 5)]
	[InlineData(0, 0, 0, 0)]
	public void BlocksSplitLikeGitHub(int added, int removed, int expectedAdded, int expectedRemoved)
	{
		var (blocksAdded, blocksRemoved) = new GitDiffStat(added, removed).Blocks();

		Assert.Equal(expectedAdded, blocksAdded);
		Assert.Equal(expectedRemoved, blocksRemoved);
		Assert.InRange(blocksAdded + blocksRemoved, 0, DiffStatBlocks.BlockCount);
	}

	[Fact]
	public void RealRenameEntriesCarryTheirStatsUnderTheTargetPath()
	{
		// 解析器的字段顺序依赖真的 --numstat -z 输出：改名条目是 `a\tr\t` + NUL + 原路径 + NUL + 目标路径。
		using var repo = new TempRepo();
		repo.Write("old.cs", "a\nb\n");
		repo.Git("add", "--all");
		repo.Git("commit", "-qm", "base");
		repo.Git("mv", "old.cs", "new.cs");
		repo.Write("new.cs", "a\nb\nc\n");
		repo.Git("commit", "-qam", "rename and extend");

		var hash = repo.Git("rev-parse", "HEAD").Trim();
		var detail = GitHistoryReader.Detail(repo.Root, hash);

		Assert.NotNull(detail);
		var renamed = Assert.Single(detail!.Files);
		Assert.Equal("new.cs", renamed.Path);
		Assert.Equal(new GitDiffStat(1, 0), renamed.DiffStat);
		// 合计也从 numstat 加：rename 只新增一行。
		Assert.Equal(1, detail.Insertions);
		Assert.Equal(0, detail.Deletions);
	}

	private sealed class TempRepo : IDisposable
	{
		public TempRepo()
		{
			Root = Path.Combine(Path.GetTempPath(), "piweft-numstat-" + Guid.NewGuid().ToString("N")[..8]);
			Directory.CreateDirectory(Root);
			Git("init", "-q", "-b", "main");
			Git("config", "user.email", "test@example.com");
			Git("config", "user.name", "PiWeft Test");
		}

		public string Root { get; }

		public void Write(string name, string content) => File.WriteAllText(Path.Combine(Root, name), content);

		public string Git(params string[] arguments)
		{
			var startInfo = new ProcessStartInfo("git")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				WorkingDirectory = Root,
			};
			foreach (var argument in arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}

			using var process = Process.Start(startInfo)!;
			var stdout = process.StandardOutput.ReadToEnd();
			var stderr = process.StandardError.ReadToEnd();
			process.WaitForExit(15_000);
			Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
			return stdout;
		}

		public void Dispose()
		{
			if (Directory.Exists(Root))
			{
				Directory.Delete(Root, recursive: true);
			}
		}
	}

	[Fact]
	public void RowsLookTheirStatsUpOnTheRightSide()
	{
		var stats = new GitChangeStats(
			new Dictionary<string, GitDiffStat>(StringComparer.Ordinal) { ["both.cs"] = new(1, 0) },
			new Dictionary<string, GitDiffStat>(StringComparer.Ordinal) { ["both.cs"] = new(0, 7) });
		var status = new GitStatus
		{
			IsRepository = true,
			Branch = "main",
			Changes = [new GitChange("M", "M", "both.cs")],
		};

		var rows = GitChangeList.Build(status, collapsedDirectories: null, stats);
		var files = rows.Where(static row => row.Kind == GitChangeRowKind.File).ToList();

		Assert.Equal(2, files.Count);
		Assert.Equal(new GitDiffStat(1, 0), files.Single(static row => row.Staged).Stat);
		Assert.Equal(new GitDiffStat(0, 7), files.Single(static row => !row.Staged).Stat);
	}
}
