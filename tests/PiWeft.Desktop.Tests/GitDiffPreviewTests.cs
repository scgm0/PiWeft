using System.Diagnostics;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 右侧 Git 面板不再内嵌 diff 卡片：点一行改动会把读出的补丁交给公共的文件差异浮层。
[Collection("mewui-controls")]
public sealed class GitDiffPreviewTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-git-diff-" + Guid.NewGuid().ToString("N")[..8]);

	public GitDiffPreviewTests()
	{
		Directory.CreateDirectory(_root);
		RunGit("init", "-b", "main");
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

	private void RunGit(params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = _root,
		};
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo)!;
		process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit(15_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
	}

	private void Write(string name, string content)
	{
		var path = Path.Combine(_root, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	[Fact]
	public void SelectingAChangeHandsThePatchToTheOverlay()
	{
		Write("app.cs", "first\nsecond\n");
		RunGit("add", "--all");
		RunGit("commit", "-qm", "base");
		Write("app.cs", "first\nchanged\n");

		using var view = new GitView(() => _root);
		view.SelectFirstChangeForSmoke();

		var content = view.LastDiffContentForTests;
		Assert.NotNull(content);
		Assert.Equal("app.cs", content.Title);
		Assert.Equal(Strings.GitDiff, content.Context);
		Assert.Equal(Path.Combine(_root, "app.cs"), content.Path);
		Assert.Equal(1, content.Added);
		Assert.Equal(1, content.Removed);
		Assert.Null(content.Message);
		Assert.True(content.CanOpenFile);
		Assert.Contains("+changed", content.Patch, StringComparison.Ordinal);
		Assert.Contains("-second", content.Patch, StringComparison.Ordinal);
	}

	[Fact]
	public void StartedFileDiffHasNoInlineCardInThePanel()
	{
		Write("new.txt", "hello\n");
		RunGit("add", "--all");
		RunGit("commit", "-qm", "base");
		Write("new.txt", "hello\nworld\n");

		using var view = new GitView(() => _root);
		view.SelectFirstChangeForSmoke();

		// 面板里不再有内嵌的补丁文本（提交输入框仍在，但它不含 diff 内容）；补丁只交给浮层。
		Assert.DoesNotContain(Find<MultiLineTextBox>(view.View),
			text => text.Text.Contains("+world", StringComparison.Ordinal) ||
				text.Text.Contains("--- ", StringComparison.Ordinal));
		var content = view.LastDiffContentForTests;
		Assert.NotNull(content);
		Assert.Contains("+world", content.Patch, StringComparison.Ordinal);
	}
}
