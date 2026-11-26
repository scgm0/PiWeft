using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 仓库签名是 Git 面板自动刷新的开关：只 stat 引用与索引，不启动 git 子进程。
/// 这里钉住三件事：非仓库返回 null、提交会改签名、工作树（.git 是文件）也能解析。
public sealed class GitSignatureTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitsig-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _worktree = Path.Combine(Path.GetTempPath(), "piweft-gitsig-copy-" + Guid.NewGuid().ToString("N")[..8]);

	public GitSignatureTests()
	{
		Directory.CreateDirectory(_root);
		Run("init", "-q", "-b", "main");
		Run("config", "user.email", "test@example.com");
		Run("config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
		Run("add", "--all");
		Run("commit", "-qm", "base");
	}

	public void Dispose()
	{
		foreach (var directory in new[] { _worktree, _root })
		{
			try
			{
				if (Directory.Exists(directory))
				{
					Directory.Delete(directory, recursive: true);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 临时目录删不掉不影响断言结论。
			}
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void PlainDirectoryHasNoSignature()
	{
		var directory = Path.Combine(Path.GetTempPath(), "piweft-gitsig-plain-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(directory);
		try
		{
			Assert.Null(GitSignature.Read(directory));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void CommittingChangesTheSignature()
	{
		var before = GitSignature.Read(_root);
		Assert.NotNull(before);

		File.WriteAllText(Path.Combine(_root, "a.txt"), "two\n");
		Run("commit", "-qam", "change");

		Assert.NotEqual(before, GitSignature.Read(_root));
	}

	[Fact]
	public void StagingOnlyMovesTheIndexPart()
	{
		// 引用那一半没变，面板就不必为一次暂存重读提交图。
		var before = GitSignature.Read(_root)!.Value;
		File.WriteAllText(Path.Combine(_root, "new.txt"), "hello\n");
		Run("add", "new.txt");

		var after = GitSignature.Read(_root)!.Value;
		Assert.Equal(before.Refs, after.Refs);
		Assert.NotEqual(before.Full, after.Full);
	}

	[Fact]
	public void UnstagedEditsAloneDoNotChangeTheSignature()
	{
		// 工作区文件改动不进签名：那要遍历整个目录。它由回合结束、窗口激活与手动刷新兜住。
		var before = GitSignature.Read(_root);

		File.WriteAllText(Path.Combine(_root, "a.txt"), "edited\n");

		Assert.Equal(before, GitSignature.Read(_root));
	}

	[Fact]
	public void SubdirectoryCwdUsesTheContainingRepository()
	{
		var child = Path.Combine(_root, "src", "deep");
		Directory.CreateDirectory(child);

		var signature = GitSignature.Read(child);

		Assert.NotNull(signature);
		File.WriteAllText(Path.Combine(_root, "a.txt"), "changed\n");
		Run("add", "a.txt");
		Assert.NotEqual(signature, GitSignature.Read(child));
	}

	[Fact]
	public void WorktreePointingAtASharedGitDirStillHasASignature()
	{
		Assert.True(GitWriter.WorktreeAdd(_root, _worktree, "piweft/sig", "HEAD").Ok);

		var before = GitSignature.Read(_worktree);
		Assert.NotNull(before);

		// 工作树的 HEAD 是私有的，分支与公共引用在主仓库的 commondir 下：两处都要看。
		File.WriteAllText(Path.Combine(_worktree, "a.txt"), "worktree\n");
		RunIn(_worktree, "commit", "-qam", "worktree change");

		Assert.NotEqual(before, GitSignature.Read(_worktree));
	}

	private void Run(params string[] arguments) => RunIn(_root, arguments);

	private static void RunIn(string cwd, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = cwd,
		};

		startInfo.Environment["LC_ALL"] = "C";
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		var stderr = process.StandardError.ReadToEndAsync();
		process.StandardOutput.ReadToEnd();
		process.WaitForExit(30_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr.GetAwaiter().GetResult()}");
	}
}
