using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// D-04：远端同步工作流。用一个裸仓库当远端，跑真实的 fetch/pull/push 往返，
/// 不 mock git —— 这些操作的语义全在同 git 的行为里，打桩只会验证我们的假设。
/// </summary>
public class GitRemoteTests : IDisposable
{
	private readonly string _base = Path.Combine(Path.GetTempPath(), "piweft-gitremote-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _remote;
	private readonly string _work;

	public GitRemoteTests()
	{
		_remote = Path.Combine(_base, "remote.git");
		_work = Path.Combine(_base, "work");
		Directory.CreateDirectory(_base);
		Directory.CreateDirectory(_work);

		Run(_base, "init", "--bare", "-b", "main", _remote);
		Configure(_work);
		Run(_work, "remote", "add", "origin", _remote);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_base, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	private void Configure(string cwd)
	{
		Run(cwd, "init", "-b", "main");
		Run(cwd, "config", "user.email", "test@example.com");
		Run(cwd, "config", "user.name", "PiWeft Test");
		// 容器/CI 里没配全局 user.name 时提交会直接失败，本地仓库里钉死。
		Run(cwd, "config", "commit.gpgsign", "false");
	}

	private static (string Stdout, string Stderr, int ExitCode) Exec(string cwd, params string[] arguments)
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
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		process.WaitForExit(60_000);
		return (stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), process.ExitCode);
	}

	private static void Run(string cwd, params string[] arguments)
	{
		var (_, stderr, exitCode) = Exec(cwd, arguments);
		Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
	}

	private string Write(string name, string content)
	{
		var path = Path.Combine(_work, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return name;
	}

	private void CommitAll(string message)
	{
		Run(_work, "add", "--all");
		Run(_work, "commit", "-m", message);
	}

	private string HeadOf(string cwd) => Exec(cwd, "rev-parse", "HEAD").Stdout.Trim();

	// ---------------------------------------------------------------- 状态解析

	[Fact]
	public void StatusReportsUpstreamAndRemotes()
	{
		Write("a.txt", "hello");
		CommitAll("first");

		var beforePush = GitStatusReader.Read(_work);
		Assert.True(beforePush.HasRemotes);
		Assert.False(beforePush.HasUpstream);
		Assert.False(beforePush.Detached);
		Assert.Equal("main", beforePush.Branch);

		Assert.True(GitWriter.Push(_work).Ok);

		var afterPush = GitStatusReader.Read(_work);
		Assert.True(afterPush.HasUpstream);
		Assert.Equal(string.Empty, GitStatusReader.Upstream(_work).Branch[..0]);
		Assert.Equal("origin/main", GitStatusReader.Upstream(_work).Upstream);
	}

	[Fact]
	public void RepositoryWithoutRemotesSaysSo()
	{
		var lonely = Path.Combine(_base, "lonely");
		Directory.CreateDirectory(lonely);
		Configure(lonely);
		File.WriteAllText(Path.Combine(lonely, "a.txt"), "x");

		var status = GitStatusReader.Read(lonely);
		Assert.False(status.HasRemotes);

		var push = GitWriter.Push(lonely);
		Assert.False(push.Ok);
		Assert.Equal(Strings.GitNoRemote, push.Message);

		Assert.Equal(Strings.GitNoRemote, GitWriter.Pull(lonely).Message);
		Assert.Equal(Strings.GitNoRemote, GitWriter.Fetch(lonely).Message);
	}

	// ---------------------------------------------------------------- push

	[Fact]
	public void FirstPushCreatesUpstreamTracking()
	{
		Write("a.txt", "hello");
		CommitAll("first");

		var result = GitWriter.Push(_work);
		Assert.True(result.Ok);

		// 上游绑定成功：不需要用户自己跑 git push -u。
		var upstream = GitStatusReader.Upstream(_work);
		Assert.Equal("origin/main", upstream.Upstream);
		Assert.Equal(HeadOf(_work), HeadOf(_remote));
	}

	[Fact]
	public void PushSendsNewCommits()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Assert.True(GitWriter.Push(_work).Ok);

		Write("a.txt", "hello again");
		CommitAll("second");
		Assert.True(GitWriter.Push(_work).Ok);

		Assert.Equal(HeadOf(_work), HeadOf(_remote));
		Assert.Equal(0, GitStatusReader.Read(_work).Ahead);
	}

	[Fact]
	public void PushWithoutNewCommitsIsReportedAsUpToDate()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Assert.True(GitWriter.Push(_work).Ok);

		var again = GitWriter.Push(_work);
		Assert.True(again.Ok);
		Assert.Equal(Strings.GitUpToDate, again.Message);
	}

	[Fact]
	public void DivergedRemoteRejectsPushWithAnActionableMessage()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Assert.True(GitWriter.Push(_work).Ok);

		// 另一个克隆往远端推一个提交，本地随后也提交一个：两条历史分叉。
		var other = Path.Combine(_base, "other");
		Directory.CreateDirectory(other);
		Run(_base, "clone", _remote, other);
		Run(other, "config", "user.email", "test@example.com");
		Run(other, "config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(other, "b.txt"), "theirs");
		Run(other, "add", "--all");
		Run(other, "commit", "-m", "theirs");
		Run(other, "push");

		Write("a.txt", "mine");
		CommitAll("mine");

		var push = GitWriter.Push(_work);
		Assert.False(push.Ok);
		Assert.Equal(Strings.GitPushRejected, push.Message);
		// 分类提示之外必须留下原文，否则用户没法判断到底是哪条 ref 被拒。
		Assert.Contains("rejected", push.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	// ---------------------------------------------------------------- fetch / pull

	[Fact]
	public void FetchUpdatesRemoteTrackingWithoutTouchingTheWorkTree()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Assert.True(GitWriter.Push(_work).Ok);

		var other = Path.Combine(_base, "other");
		Directory.CreateDirectory(other);
		Run(_base, "clone", _remote, other);
		Run(other, "config", "user.email", "test@example.com");
		Run(other, "config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(other, "b.txt"), "theirs");
		Run(other, "add", "--all");
		Run(other, "commit", "-m", "theirs");
		Run(other, "push");

		Assert.True(GitWriter.Fetch(_work).Ok);

		// fetch 只动远端跟踪分支：工作区里不该出现 b.txt，behind 计数才该变化。
		Assert.False(File.Exists(Path.Combine(_work, "b.txt")));
		Assert.Equal(1, GitStatusReader.Read(_work).Behind);
	}

	[Fact]
	public void PullFastForwardsTheWorkTree()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Assert.True(GitWriter.Push(_work).Ok);

		var other = Path.Combine(_base, "other");
		Directory.CreateDirectory(other);
		Run(_base, "clone", _remote, other);
		Run(other, "config", "user.email", "test@example.com");
		Run(other, "config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(other, "b.txt"), "theirs");
		Run(other, "add", "--all");
		Run(other, "commit", "-m", "theirs");
		Run(other, "push");

		Assert.True(GitWriter.Pull(_work).Ok);
		Assert.True(File.Exists(Path.Combine(_work, "b.txt")));
		Assert.Equal(HeadOf(other), HeadOf(_work));
	}

	[Fact]
	public void PullRefusesWhenLocalChangesWouldBeOverwritten()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Assert.True(GitWriter.Push(_work).Ok);

		var other = Path.Combine(_base, "other");
		Directory.CreateDirectory(other);
		Run(_base, "clone", _remote, other);
		Run(other, "config", "user.email", "test@example.com");
		Run(other, "config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(other, "a.txt"), "theirs");
		Run(other, "add", "--all");
		Run(other, "commit", "-m", "theirs");
		Run(other, "push");

		// 本地把同一个文件改成未提交状态：拉取会覆盖它。
		File.WriteAllText(Path.Combine(_work, "a.txt"), "mine, uncommitted");

		var pull = GitWriter.Pull(_work);
		Assert.False(pull.Ok);
		Assert.Equal(Strings.GitLocalChangesBlock, pull.Message);
		Assert.Equal("mine, uncommitted", File.ReadAllText(Path.Combine(_work, "a.txt")));
	}

	[Fact]
	public void PullRefusesToCreateAMergeCommitWhenHistoryDiverged()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Assert.True(GitWriter.Push(_work).Ok);

		var other = Path.Combine(_base, "other");
		Directory.CreateDirectory(other);
		Run(_base, "clone", _remote, other);
		Run(other, "config", "user.email", "test@example.com");
		Run(other, "config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(other, "b.txt"), "theirs");
		Run(other, "add", "--all");
		Run(other, "commit", "-m", "theirs");
		Run(other, "push");

		Write("c.txt", "mine");
		CommitAll("mine");

		var pull = GitWriter.Pull(_work);
		Assert.False(pull.Ok);
		Assert.Equal(Strings.GitNotFastForward, pull.Message);

		// 关键：不能被自动 merge 出一个用户没预期、也没法从界面收拾的合并提交。
		var parents = Exec(_work, "rev-list", "--parents", "-n", "1", "HEAD").Stdout.Trim().Split(' ');
		Assert.Equal(2, parents.Length);
	}

	[Fact]
	public void PullWithoutUpstreamSaysSo()
	{
		Write("a.txt", "hello");
		CommitAll("first");

		var pull = GitWriter.Pull(_work);
		Assert.False(pull.Ok);
		Assert.Equal(Strings.GitNoUpstream, pull.Message);
	}

	// ---------------------------------------------------------------- stash

	[Fact]
	public void StashPushCleansTheWorkTreeIncludingUntrackedFiles()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Write("a.txt", "changed");
		Write("new.txt", "untracked");

		var stash = GitWriter.StashPush(_work);
		Assert.True(stash.Ok);

		var status = GitStatusReader.Read(_work);
		Assert.Empty(status.Changes);
		Assert.Single(GitWriter.Stashes(_work));

		Assert.True(GitWriter.StashPop(_work).Ok);
		Assert.Equal("changed", File.ReadAllText(Path.Combine(_work, "a.txt")));
		Assert.Equal("untracked", File.ReadAllText(Path.Combine(_work, "new.txt")));
		Assert.Empty(GitWriter.Stashes(_work));
	}

	[Fact]
	public void StashOnCleanWorkTreeReportsNothingToDo()
	{
		Write("a.txt", "hello");
		CommitAll("first");

		var stash = GitWriter.StashPush(_work);
		Assert.False(stash.Ok);
		Assert.Equal(Strings.GitStashNothing, stash.Message);
	}

	[Fact]
	public void StashDropRequiresAStashReference()
	{
		// 把任意字符串当 git 选项传进去是危险的，引用必须先过形态校验。
		Assert.False(GitWriter.StashDrop(_work, "--all").Ok);
		Assert.False(GitWriter.StashDrop(_work, "-x").Ok);
		Assert.False(GitWriter.StashDrop(_work, "main").Ok);
	}

	[Fact]
	public void StashDropRemovesTheEntry()
	{
		Write("a.txt", "hello");
		CommitAll("first");
		Write("a.txt", "changed");
		Assert.True(GitWriter.StashPush(_work).Ok);

		var entry = Assert.Single(GitWriter.Stashes(_work));
		Assert.StartsWith("stash@{0}", entry.Reference, StringComparison.Ordinal);

		Assert.True(GitWriter.StashDrop(_work, entry.Reference).Ok);
		Assert.Empty(GitWriter.Stashes(_work));
	}
}
