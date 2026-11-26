using System.Diagnostics;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// Git 面板的自动刷新：仓库签名变了才重读状态。
/// 以前只能手动点刷新：子代理或在终端里 commit / 切分支之后，面板停在上一份快照。
[Collection("mewui-controls")]
public sealed class GitPanelRealtimeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-gitrt-" + Guid.NewGuid().ToString("N")[..8]);

	public GitPanelRealtimeTests()
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
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void DeletingTheRepositoryBringsThePanelBackToNotARepository()
	{
		using var view = new GitView(() => _root);
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		Assert.Equal(0, view.ChangeRowCountForTests);

		Directory.Delete(Path.Combine(_root, ".git"), recursive: true);

		Assert.True(view.RefreshIfSignatureChangedForTests());
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		Assert.Contains(
			Find<TextBlock>(view.View),
			static text => text.Text == Strings.GitNotRepository);
	}

	[Fact]
	public void SignaturePollPicksUpAnExternalStageWithoutAWindow()
	{
		using var view = new GitView(() => _root);
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		Assert.Equal(0, view.ChangeRowCountForTests);

		// 外部（终端或 bash 工具）暂存一个文件：签名里的 index 变了。
		File.WriteAllText(Path.Combine(_root, "new.txt"), "hello\n");
		Run("add", "new.txt");

		Assert.True(view.RefreshIfSignatureChangedForTests());
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		// 「已暂存」分节头 + 一行文件。
		Assert.Equal(2, view.ChangeRowCountForTests);

		// 签名没再变就不再重建：轮询空转不该反复重建列表。
		Assert.False(view.RefreshIfSignatureChangedForTests());
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
		Assert.True(
			process.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {stdout.GetAwaiter().GetResult()}{stderr.GetAwaiter().GetResult()}");
	}
}
