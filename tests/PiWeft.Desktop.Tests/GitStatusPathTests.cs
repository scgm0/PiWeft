using System.Diagnostics;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// D-04：真实仓库上的路径边界。中文、空格、emoji 文件名在 <c>git status</c> 的非 <c>-z</c> 输出里
/// 会被八进制转义并加引号，按固定列解析会得到一串不存在的路径 —— 于是「点暂存没反应」「点开文件说没有」。
/// 这里用真实 git 把这条钉住。
/// </summary>
public class GitStatusPathTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitpath-" + Guid.NewGuid().ToString("N")[..8]);

	public GitStatusPathTests()
	{
		Directory.CreateDirectory(_root);
		Run("init", "-q", "-b", "main");
		Run("config", "user.email", "test@example.com");
		Run("config", "user.name", "PiWeft Test");
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	private void Run(params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = _root,
		};

		startInfo.Environment["LC_ALL"] = "C";
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		process.WaitForExit(30_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr.GetAwaiter().GetResult()}");
		_ = stdout.GetAwaiter().GetResult();
	}

	private string Write(string name, string content)
	{
		var path = Path.Combine(_root, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return name;
	}

	[Theory]
	[InlineData("中文.txt")]
	[InlineData("带 空格.txt")]
	[InlineData("emoji-🚀.txt")]
	[InlineData("目录/子文件.txt")]
	[InlineData("引号\"与'撇号.txt")]
	public void NonAsciiPathsSurviveTheStatusParse(string name)
	{
		Write(name, "hello");

		var change = Assert.Single(GitStatusReader.Read(_root).Changes);
		Assert.Equal(name, change.Path);
		Assert.Equal("未跟踪", change.Label);

		// 路径能原样交回 git：暂存必须真的命中这个文件。
		Assert.True(GitWriter.Stage(_root, [change.Path]).Ok);
		var staged = Assert.Single(GitStatusReader.Read(_root).Changes);
		Assert.Equal(name, staged.Path);
		Assert.Equal("A", staged.IndexStatus);
	}

	[Fact]
	public void RenameWithNonAsciiPathsKeepsTheTargetPath()
	{
		Write("旧 名字.txt", "hello");
		Run("add", "--all");
		Run("commit", "-q", "-m", "baseline");
		Run("mv", "旧 名字.txt", "新 名字🚀.txt");

		var status = GitStatusReader.Read(_root);
		var change = Assert.Single(status.Changes);
		Assert.Equal("新 名字🚀.txt", change.Path);
		Assert.Equal("改名", change.Label);

		// 改名条目后面还有一个「原路径」字段；解析错位会让后续条目丢失。
		Write("附加.txt", "x");
		var both = GitStatusReader.Read(_root);
		Assert.Equal(2, both.Changes.Count);
		Assert.Contains(both.Changes, item => item.Path == "附加.txt");
	}

	[Fact]
	public void PathsWithNewlinesDoNotBreakTheRecordBoundary()
	{
		// 行式输出在这里会直接崩掉解析（一个路径被当成两条记录）；-z 才有明确边界。
		// 文件名里带换行在 Linux 上合法，Windows 上不允许。
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		const string name = "换行\n名字.txt";
		Write(name, "hello");

		var change = Assert.Single(GitStatusReader.Read(_root).Changes);
		Assert.Equal(name, change.Path);
	}

	[Fact]
	public void DetachedHeadIsNotOfferedAsABranch()
	{
		Write("a.txt", "hello");
		Run("add", "--all");
		Run("commit", "-q", "-m", "one");
		Run("checkout", "-q", "--detach", "HEAD");

		var status = GitStatusReader.Read(_root);
		Assert.True(status.Detached);
		Assert.Equal(string.Empty, status.Branch);

		// 分离头指针下推送会造出没预期的分支名，必须拒绝。
		Assert.False(GitWriter.Push(_root).Ok);
	}

	[Fact]
	public void FreshRepositoryHasABranchButNoCommits()
	{
		Write("a.txt", "hello");

		var status = GitStatusReader.Read(_root);
		Assert.Equal("main", status.Branch);
		Assert.False(status.Detached);
		Assert.False(status.HasUpstream);
		Assert.Single(status.Changes);
	}
}
