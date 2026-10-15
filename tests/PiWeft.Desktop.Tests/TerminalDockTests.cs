using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-03（部分）：命令 Dock 能运行命令并流式显示输出。</summary>
[Collection("mewui-controls")]
public sealed class TerminalDockTests
{
	private static string Shell => Path.GetTempPath();

	[Fact]
	public void ShowsTheTitleAndNonInteractiveHint()
	{
		using var dock = new TerminalDock(() => Shell);

		Assert.Contains(Find<TextBlock>(dock.View), block => block.Text == Strings.Terminal);
		Assert.Contains(Find<TextBlock>(dock.View), block => block.Text.Contains(Strings.TerminalHint, StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunsACommandAndReportsTheExitCode()
	{
		using var dock = new TerminalDock(() => Shell);
		var command = Find<TextBox>(dock.View).Single();
		command.Text = "echo hello-from-dock";

		await dock.RunAsync();

		Assert.Contains(Find<TextBlock>(dock.View), block => block.Text.Contains("hello-from-dock", StringComparison.Ordinal));
		Assert.Contains(Find<TextBlock>(dock.View), block => block.Text.Contains("[退出码 0]", StringComparison.Ordinal));
	}

	[Fact]
	public async Task EmptyCommandDoesNothing()
	{
		using var dock = new TerminalDock(() => Shell);
		await dock.RunAsync();

		Assert.DoesNotContain(Find<TextBlock>(dock.View), block => block.Text.Contains("退出码", StringComparison.Ordinal));
	}

	[Fact]
	public void ViewAttachesToAStyleSheetWithoutTargetMismatch()
	{
		using var dock = new TerminalDock(() => Shell);

		// 复现崩溃路径：视图挂到带样式表的根上会触发整棵子树的样式解析，
		// 单行 TextBox 套 MultiLineTextBox 的命名样式会在这里抛 InvalidOperationException。
		var root = new Border().StyleSheet(AppStyles.Create()).Child(dock.View);
		dock.View.Measure(new Size(600, 400));

		var command = Find<TextBox>(root).Single();
		Assert.NotEqual(default, command.BorderBrush);
		GC.KeepAlive(root);
	}
}
