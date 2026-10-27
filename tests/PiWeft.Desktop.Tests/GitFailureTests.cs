using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// D-04：git 原始输出的分类表。把 <c>fatal: ...</c> 翻成一句用户能照着做的话，
/// 是远端同步能不能用的关键 —— 「推送失败」四个字帮不了任何人。
/// </summary>
public class GitFailureTests
{
	[Theory]
	[InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", "GitAuthFailed")]
	[InlineData("remote: Permission denied (publickey).", "GitAuthFailed")]
	[InlineData("fatal: Authentication failed for 'https://example.com/x.git/'", "GitAuthFailed")]
	[InlineData("fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com", "GitNetworkFailed")]
	[InlineData("fatal: unable to access 'https://x/': Failed to connect to x port 443", "GitNetworkFailed")]
	[InlineData("! [rejected]        main -> main (non-fast-forward)", "GitPushRejected")]
	[InlineData("! [rejected]        main -> main (fetch first)", "GitPushRejected")]
	[InlineData("fatal: The current branch main has no upstream branch.", "GitNoUpstream")]
	[InlineData("fatal: 'upstream' does not appear to be a git repository", "GitUnknownRemote")]
	[InlineData("error: Your local changes to the following files would be overwritten by merge:", "GitLocalChangesBlock")]
	[InlineData("fatal: Not possible to fast-forward, aborting.", "GitNotFastForward")]
	[InlineData("CONFLICT (content): Merge conflict in a.txt", "GitConflict")]
	[InlineData("nothing to commit, working tree clean", "GitNothingToCommit")]
	[InlineData("Everything up-to-date", "GitUpToDate")]
	[InlineData("No local changes to save", "GitStashNothing")]
	[InlineData("You do not have the initial commit yet", "GitNoInitialCommit")]
	public void KnownFailuresMapToActionableMessages(string output, string expected)
	{
		Assert.Equal(Expected(expected), GitFailure.Classify(output));
	}

	[Fact]
	public void UnrecognisedOutputIsLeftToTheCaller()
	{
		Assert.Null(GitFailure.Classify("fatal: something we have never seen"));
		Assert.Null(GitFailure.Classify("   "));
	}

	/// <summary>
	/// 分类表按英文特征串写。git 的消息是本地化的，所以每条 git 子进程都必须钉住 C 语言环境 ——
	/// 漏掉一处，中文环境下就会「失败报成成功」或只吐半截输出（实测过：<c>git pull</c> 失败时
	/// 首行是「来自 /tmp/remote」）。
	/// </summary>
	[Fact]
	public void EveryGitChildProcessPinsTheLocale()
	{
		var writer = File.ReadAllText(SourcePath("src/PiWeft.Desktop/GitWriter.cs"));
		var status = File.ReadAllText(SourcePath("src/PiWeft.Desktop/GitStatus.cs"));

		// writer 的所有调用都经由 RunCapture 一处设置。
		Assert.Contains("GitStatusReader.ApplyLocale(startInfo)", writer, StringComparison.Ordinal);

		// status 的两个进程入口（Read 与 Run）都要调用。
		var localeCalls = status.Split("ApplyLocale(startInfo)").Length - 1;
		Assert.Equal(2, localeCalls);
		Assert.Contains("LC_ALL", status, StringComparison.Ordinal);
	}

	private static string Expected(string constant) => constant switch
	{
		"GitAuthFailed" => Strings.GitAuthFailed,
		"GitNetworkFailed" => Strings.GitNetworkFailed,
		"GitPushRejected" => Strings.GitPushRejected,
		"GitNoUpstream" => Strings.GitNoUpstream,
		"GitUnknownRemote" => Strings.GitUnknownRemote,
		"GitLocalChangesBlock" => Strings.GitLocalChangesBlock,
		"GitNotFastForward" => Strings.GitNotFastForward,
		"GitConflict" => Strings.GitConflict,
		"GitNothingToCommit" => Strings.GitNothingToCommit,
		"GitUpToDate" => Strings.GitUpToDate,
		"GitStashNothing" => Strings.GitStashNothing,
		"GitNoInitialCommit" => Strings.GitNoInitialCommit,
		_ => throw new ArgumentOutOfRangeException(nameof(constant), constant, "未登记的期望常量"),
	};

	private static string SourcePath(string relative)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, relative);
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new FileNotFoundException($"找不到源码文件 {relative}");
	}
}
