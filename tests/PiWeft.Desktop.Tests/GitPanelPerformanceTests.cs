using System.Diagnostics;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 改动列表按固定行高虚拟化：行数上千时只建视口内的行。
/// 曾经一次建出全部行，每行约 20 个控件，2 万条改动要 6 秒建树加 8 秒布局，面板打开就冻住。
[Collection("mewui-controls")]
public sealed class GitPanelPerformanceTests : IDisposable
{
	private const int ChangedFiles = 800;

	private readonly ITestOutputHelper _output;
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-git-perf-" + Guid.NewGuid().ToString("N")[..8]);

	public GitPanelPerformanceTests(ITestOutputHelper output)
	{
		_output = output;
		Directory.CreateDirectory(_root);
		RunGit("init", "-q", "-b", "main");
		RunGit("config", "user.email", "test@example.com");
		RunGit("config", "user.name", "PiWeft Test");
		File.WriteAllText(Path.Combine(_root, "app.cs"), "first\n");
		RunGit("add", "--all");
		RunGit("commit", "-qm", "base");

		// 未跟踪的构建产物是「改动上千条」最常见的来源。
		for (var index = 0; index < ChangedFiles; index++)
		{
			var directory = Path.Combine(_root, "src", $"mod{index % 12}", $"deep{index % 5}");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, $"file{index}.cs"), $"content {index}\n");
		}

		File.WriteAllText(Path.Combine(_root, "app.cs"), "first\nsecond\n");
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
	public void ManyChangedFilesRealizeOnlyTheViewport()
	{
		GC.Collect();
		var started = Stopwatch.StartNew();
		using var view = new GitView(() => _root);
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		var elapsed = started.Elapsed;

		_output.WriteLine(
			$"{ChangedFiles + 2} 行改动：{elapsed.TotalMilliseconds:F1} ms，" +
			$"已实例化 {view.RealizedChangeRowsForTests} 行，控件 {Find<Button>(view.View).Count()} 个");

		Assert.True(view.ChangeRowCountForTests >= ChangedFiles, $"行表只有 {view.ChangeRowCountForTests} 行");
		Assert.InRange(view.RealizedChangeRowsForTests, 1, 60);
	}

	[Fact]
	public void ScrollingReachesTheLastRowWithoutRealizingThemAll()
	{
		using var view = new GitView(() => _root);
		ConversationPerformanceTests.Layout(view.View, 360, 800);
		var last = view.ChangeRowCountForTests - 1;
		Assert.False(view.IsChangeRowRealizedForTests(last));

		view.ScrollChangesForTests(double.MaxValue);
		ConversationPerformanceTests.Layout(view.View, 360, 800);

		Assert.True(view.IsChangeRowRealizedForTests(last), "滚到底后最后一行没有实例化");
		Assert.InRange(view.RealizedChangeRowsForTests, 1, 60);
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
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)!;
		process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit(15_000);
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
	}
}
