using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-03：命令 Dock 用 MiniPty 起一个真伪终端，键盘输入直接进 shell。</summary>
[Collection("mewui-controls")]
public sealed class TerminalDockTests
{
	private static string ShellDirectory => Path.GetTempPath();

	[Fact]
	public void ShowsTheTitleAndPtyStatus()
	{
		using var dock = new TerminalDock(() => ShellDirectory);

		Assert.Contains(Find<TextBlock>(dock.View), block => block.Text == Strings.Terminal);
		Assert.Contains(Find<TextBlock>(dock.View), block => block.Text.Contains("伪终端", StringComparison.Ordinal));
	}

	[Fact]
	public void PasteConfirmationOnlyTriggersOnLineBreaks()
	{
		Assert.False(TerminalDock.NeedsPasteConfirmation("ls -la"));
		Assert.True(TerminalDock.NeedsPasteConfirmation("a\nb"));
		Assert.True(TerminalDock.NeedsPasteConfirmation("a\rb"));
	}

	[Fact]
	public async Task MultiLinePasteAsksBeforeFeedingTheShell()
	{
		using var dock = new TerminalDock(() => ShellDirectory);

		dock.RequestPaste("echo one\r\necho two");

		var label = Find<TextBlock>(dock.View)
			.Single(block => block.Text == Strings.TerminalPasteConfirm(2));
		var prompt = (Border)label.Parent!.Parent!;
		Assert.True(prompt.IsVisible);

		// 取消：不往 PTY 写任何东西。
		ImagePreviewTests.Click(Find<Button>(prompt).Single(button => HasText(button, Strings.Cancel)));
		Assert.False(prompt.IsVisible);

		dock.RequestPaste("echo three\r\n");
		Assert.True(prompt.IsVisible);
		ImagePreviewTests.Click(Find<Button>(prompt).Single(button => HasText(button, Strings.TerminalPaste)));
		Assert.False(prompt.IsVisible);

		await WaitForAsync(() => dock.ScreenText.Contains("echo three", StringComparison.Ordinal));
	}

	private static bool HasText(Button button, string text) =>
		Find<TextBlock>(button).Any(block => block.Text == text);

	[Fact]
	public async Task ShellOutputReachesTheScreenAndAcceptsInput()
	{
		using var dock = new TerminalDock(() => ShellDirectory);

		// bash -i 启动后会先打印提示符；输出经 PTY 流回屏幕。
		await WaitForAsync(() => dock.ScreenText.Length > 0);

		dock.SendText("echo terminal-pty-ok\r");
		await WaitForAsync(() => dock.ScreenText.Contains("terminal-pty-ok", StringComparison.Ordinal));
	}

	[Fact]
	public void ViewAttachesToAStyleSheetWithoutTargetMismatch()
	{
		using var dock = new TerminalDock(() => ShellDirectory);

		// 复现崩溃路径：视图挂到带样式表的根上会触发整棵子树的样式解析。
		var root = new Border().StyleSheet(AppStyles.Create()).Child(dock.View);
		dock.View.Measure(new Size(600, 400));
		dock.View.Arrange(new Rect(0, 0, 600, 400));

		var panel = Find<Border>(root).First(border => border.StyleName == AppStyles.Code);
		Assert.NotEqual(default, panel.Background);
		GC.KeepAlive(root);
	}

	[Fact]
	public async Task ImeCompositionShowsAndClearsThePreview()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		await WaitForAsync(() => dock.ScreenText.Length > 0);

		dock.CompositionStarted("ni");
		Assert.True(dock.IsComposing);

		dock.CompositionUpdated("你");
		Assert.True(dock.IsComposing);
		Assert.Contains(Find<TextBlock>(dock.View), block => block.Text == "你");

		dock.CompositionEnded();
		Assert.False(dock.IsComposing);
	}

	[Fact]
	public async Task SelectionIsPublishedToTheLinuxPrimarySelection()
	{
		var service = new FakePrimarySelection();
		PrimarySelectionText.Service = service;
		try
		{
			using var dock = new TerminalDock(() => ShellDirectory);
			await WaitForAsync(() => dock.ScreenText.Length > 0);

			dock.SelectForTest(0, 0, 3);
			await WaitForAsync(() => service.Published.Count > 0);
		}
		finally
		{
			PrimarySelectionText.Service = null;
		}
	}

	[Fact]
	public async Task CtrlCCopiesASelectionAndInterruptsWithoutOne()
	{
		var service = new FakePrimarySelection();
		PrimarySelectionText.Service = service;
		try
		{
			using var dock = new TerminalDock(() => ShellDirectory);
			await WaitForAsync(() => dock.ScreenText.Length > 0);

			// 无选区：Ctrl+C 仍是中断信号。
			var interrupt = new KeyEventArgs(Key.C, 0, ModifierKeys.Control);
			dock.SendKey(interrupt);
			Assert.True(interrupt.Handled);

			// 有选区：Ctrl+C 复制。
			dock.SelectForTest(0, 0, 3);
			var copy = new KeyEventArgs(Key.C, 0, ModifierKeys.Control);
			dock.SendKey(copy);
			Assert.True(copy.Handled);
			Assert.NotEmpty(service.Published);

			var paste = new KeyEventArgs(Key.V, 0, ModifierKeys.Control);
			dock.SendKey(paste);
			Assert.True(paste.Handled);
		}
		finally
		{
			PrimarySelectionText.Service = null;
		}
	}

	private sealed class FakePrimarySelection : IPrimarySelection
	{
		public List<string> Published { get; } = [];

		public Task PendingPublish => Task.CompletedTask;

		public void Publish(string text) => Published.Add(text);

		public Task<string?> ReadAsync() => Task.FromResult<string?>(null);
	}

	private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
	{
		var started = Environment.TickCount64;
		while (!condition())
		{
			if (Environment.TickCount64 - started > timeoutMs)
			{
				throw new TimeoutException("PTY 输出没有在预期时间内到达。");
			}

			await Task.Delay(25);
		}
	}
}
