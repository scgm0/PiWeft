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
	public void ShowsTheTitleWithoutDescriptiveStatus()
	{
		using var dock = new TerminalDock(() => ShellDirectory);

		Assert.Contains(Find<TextBlock>(dock.View), block => block.Text == Strings.Terminal);
		// 说明文本默认不占位：正常运行时只在标题下留出终端表面，失败/退出才显示诊断行。
		Assert.DoesNotContain(Find<TextBlock>(dock.View), block => block.Text.Contains("伪终端", StringComparison.Ordinal));
	}

	/// <summary>
	/// 在指定行的指定列写一段文本，再把光标停到指定列上。用绝对定位写，避开 shell 自己那行的
	/// 提示符，测试结果与机器上装的是什么 shell 无关。
	/// </summary>
	private static async Task WriteOnRowAsync(
		TerminalDock dock,
		int row,
		int column,
		string text,
		int cursorColumn)
	{
		// 先等 shell 打出提示符；之后不再输入，shell 不会自己重画这一行。
		await WaitForAsync(() => dock.ScreenText.Length > 0);
		dock.FeedForTest($"\u001b[{row + 1};{column + 1}H{text}\u001b[{row + 1};{cursorColumn + 1}H");
		dock.SetFocusForTest(true);
	}

	[Fact]
	public async Task CursorIsACellShapeRatherThanRecolouredText()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		var (cellWidth, cellHeight) = dock.CellSizeForTest;
		await WriteOnRowAsync(dock, 5, 0, "hello", 0);

		// 聚焦：整格实心块，走文本底下的那层 —— 块是「格子的背景」，不是把字符染色。
		Assert.True(dock.CursorVisibleForTest);
		Assert.True(dock.CursorSolidBlockForTest);
		Assert.False(dock.CursorHollowForTest);
		var cell = dock.CursorCellForTest;
		Assert.Equal(cellWidth, cell.Width, 6);
		Assert.Equal(cellHeight, cell.Height, 6);

		// 失焦：同一格改画一整圈空心框，走文本之上那层；格子与颜色都不变。
		dock.SetFocusForTest(false);
		Assert.True(dock.CursorVisibleForTest);
		Assert.False(dock.CursorSolidBlockForTest);
		Assert.True(dock.CursorHollowForTest);
		Assert.Equal(cell, dock.CursorCellForTest);
		// 描边就是光标色：失焦只从实心变空心，亮度不降。
		Assert.Equal(AppTheme.TerminalCursor, dock.CursorColorForTest);
	}

	[Fact]
	public async Task CursorCellStepsExactlyOneCellPerColumn()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		var (cellWidth, cellHeight) = dock.CellSizeForTest;
		await WriteOnRowAsync(dock, 6, 0, "abcdefghij", 1);
		var atOne = dock.CursorCellForTest;

		dock.FeedForTest("\u001b[7;5H");
		dock.SetFocusForTest(true);
		var atFour = dock.CursorCellForTest;

		// 光标格严格按列走：跨三列就是三个格宽。块不会再缩成「那个 run 自己的排布宽度」，
		// 那正是旧实现里光标看起来偏在字符左边的原因。
		Assert.Equal(atOne.X + (3 * cellWidth), atFour.X, 6);
		Assert.Equal(atOne.Y, atFour.Y, 6);

		// 换行：行位移正好一个行高，块贴在所属文本行上，不飘到上一行或下一行。
		dock.FeedForTest("\u001b[10;5H");
		dock.SetFocusForTest(true);
		Assert.Equal(atFour.Y + (3 * cellHeight), dock.CursorCellForTest.Y, 6);
		Assert.Equal(atFour.X, dock.CursorCellForTest.X, 6);
	}

	[Fact]
	public async Task EmptyRowCursorStaysAtItsOwnColumn()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		var (cellWidth, cellHeight) = dock.CellSizeForTest;
		await WriteOnRowAsync(dock, 9, 0, "", 12);

		// 行尾根本没有字形可量，旧实现补的那个 run 会把光标拽到行内容末尾 ——
		// 看起来就是光标整块偏左。现在直接按列号落在自己那一格。
		Assert.Equal(12, dock.Screen.CursorColumn);
		Assert.Equal(12 * cellWidth, dock.CursorCellForTest.X, 6);
		Assert.Equal(cellWidth, dock.CursorCellForTest.Width, 6);
		Assert.Equal(9 * cellHeight, dock.CursorCellForTest.Y, 6);
	}

	[Fact]
	public async Task WideClusterCursorCoversBothCells()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		var (cellWidth, _) = dock.CellSizeForTest;
		// 「中文」各占两格，光标落在「文」上：高亮至少两格宽，不能只盖住半个字。
		await WriteOnRowAsync(dock, 7, 0, "中文abc", 2);
		Assert.Equal(2, dock.Screen.CursorColumn);
		Assert.Equal(2 * cellWidth, dock.CursorCellForTest.Width, 6);

		// 宽字符的右半格属于同一簇：光标停在列 3 时，块仍从整簇的左沿开始、仍是两格宽。
		dock.FeedForTest("\u001b[8;4H");
		dock.SetFocusForTest(true);
		Assert.Equal(3, dock.Screen.CursorColumn);
		Assert.Equal(2 * cellWidth, dock.CursorCellForTest.Width, 6);
		Assert.Equal(dock.CursorCellForTest.X + (2 * cellWidth), dock.CursorCellForTest.Right, 6);
	}

	[Fact]
	public async Task EolAndColouredCellCursorsKeepAFullCell()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		var (cellWidth, _) = dock.CellSizeForTest;
		// 光标在行尾之后：这一格本来没有字符，仍要画出整格宽的光标，不能塌成零宽。
		await WriteOnRowAsync(dock, 8, 0, "abc", 3);
		Assert.Equal(cellWidth, dock.CursorCellForTest.Width, 6);

		// 光标压在自带背景色的单元格上（vim 高亮行、状态栏）：块按整格画，颜色是光标色。
		dock.FeedForTest("\u001b[10;1H\u001b[41mX\u001b[0m\u001b[10;1H");
		dock.SetFocusForTest(true);
		Assert.Equal(cellWidth, dock.CursorCellForTest.Width, 6);
		Assert.Equal(AppTheme.TerminalCursor, dock.CursorColorForTest);
	}

	[Fact]
	public async Task ThinShapesAreRoutedToTheLayerAboveTheText()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		await WriteOnRowAsync(dock, 11, 0, "abc", 1);

		// 实心块走文本底下，块下那个字符才能按底色反白；
		// 竖线/下划线不遮字，走文本之上，单元格自带背景色时也不会被盖掉。
		Assert.True(dock.CursorSolidBlockForTest);

		dock.FeedForTest("\u001b[6 q");
		dock.SetFocusForTest(true);
		Assert.True(dock.CursorVisibleForTest);
		Assert.False(dock.CursorSolidBlockForTest);
		Assert.False(dock.CursorHollowForTest);

		dock.FeedForTest("\u001b[4 q");
		dock.SetFocusForTest(true);
		Assert.True(dock.CursorVisibleForTest);
		Assert.False(dock.CursorSolidBlockForTest);

		// 失焦：无论应用要哪种形态，都只画一整圈空心框，颜色仍是光标色。
		dock.SetFocusForTest(false);
		Assert.True(dock.CursorHollowForTest);
		Assert.False(dock.CursorSolidBlockForTest);
		Assert.Equal(AppTheme.TerminalCursor, dock.CursorColorForTest);
	}


	[Fact]
	public async Task CursorTracksTheCellInsideALaidOutTerminal()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		var (cellWidth, cellHeight) = dock.CellSizeForTest;

		// 真实排布一次：光标格要贴在 arrange 之后的文本行上，而不是另算一套浮点位置。
		dock.View.Measure(new Size(600, 400));
		dock.View.Arrange(new Rect(0, 0, 600, 400));
		await WriteOnRowAsync(dock, 2, 0, "xy", 1);
		var cell = dock.CursorCellForTest;
		Assert.Equal(cellWidth, cell.Width, 6);
		Assert.Equal(cellHeight, cell.Height, 6);

		// 存的是「相对表面」的坐标，绝对坐标在渲染时按当前 arrange 结果现算；
		// 候选窗与光标必须落在同一格，否则输入法候选窗会飘到别处。
		var surface = dock.CursorSurfaceForTest;
		Assert.True(surface.Height > 0);
		Assert.Equal(surface.X + cell.X, dock.CursorRect.X, 6);
		Assert.Equal(surface.Y + cell.Y, dock.CursorRect.Y, 6);
		Assert.True(dock.CursorSitsOnItsRowForTest);
	}

	[Fact]
	public async Task CursorFollowsItsRowWhileThePanelIsDragged()
	{
		using var dock = new TerminalDock(() => ShellDirectory);
		await WriteOnRowAsync(dock, 2, 0, "xy", 1);
		dock.SetFocusForTest(true);

		dock.View.Measure(new Size(600, 400));
		dock.View.Arrange(new Rect(0, 0, 600, 400));
		Assert.True(dock.CursorSitsOnItsRowForTest);

		// 拖分隔条：面板尺寸一变再变。布局回调跑在子元素 arrange 之前，
		// 绝对坐标要是提前算好存下，光标就会停在旧行上（拉高时尤其明显）。
		// 现在只能现算：每安排一次，光标都得重新压在它所属的那一行上。
		foreach (var height in new[] { 560.0, 700.0, 460.0, 640.0, 500.0 })
		{
			dock.View.Measure(new Size(600, height));
			dock.View.Arrange(new Rect(0, 0, 600, height));
			Assert.True(dock.CursorSitsOnItsRowForTest, $"高度 {height} 时光标脱离了它所在的文本行");
		}
	}

	[Fact]
	public void FractionalWheelNotchesAccumulateIntoWholeLines()
	{
		// 触控板按分数刻度发滚轮；旧实现每个事件都滚 3 行，手指轻轻一划就会飞过整屏。
		double remainder = 0;
		Assert.Equal(0, TerminalDock.WheelLines(ref remainder, 0.2));
		Assert.Equal(1, TerminalDock.WheelLines(ref remainder, 0.2));
		Assert.Equal(0, TerminalDock.WheelLines(ref remainder, 0.2));
		Assert.Equal(1, TerminalDock.WheelLines(ref remainder, 0.2));
		Assert.Equal(1, TerminalDock.WheelLines(ref remainder, 0.2));
		Assert.Equal(0, remainder, 9);

		// 离散滚轮仍是一格三行，反向也对称。
		Assert.Equal(3, TerminalDock.WheelLines(ref remainder, 1));
		Assert.Equal(-3, TerminalDock.WheelLines(ref remainder, -1));
		Assert.Equal(0, remainder);
	}

	[Fact]
	public void PasteConfirmationOnlyTriggersOnLineBreaks()
	{
		Assert.False(TerminalDock.NeedsPasteConfirmation("ls -la"));
		Assert.True(TerminalDock.NeedsPasteConfirmation("a\nb"));
		Assert.True(TerminalDock.NeedsPasteConfirmation("a\rb"));
	}

	[Fact]
	public void ReverseVideoTextPaintsTheSwappedBackground()
	{
		using var dock = new TerminalDock(() => ShellDirectory);

		// SGR 7 + 默认前景/背景：交换后文字色就是终端底色，必须同时画出交换后的背景，
		// 否则字符会变成“底色叠底色”。zsh 的括号粘贴高亮正是这种形态。
		dock.FeedForTest("\u001b[2J\u001b[H\u001b[7mPASTED123");

		var line = Find<TextBlock>(dock.View).Single(block => block.Text.Contains("PASTED123", StringComparison.Ordinal));
		var run = line.Inlines.Single(inline => inline.Text.Contains("PASTED123", StringComparison.Ordinal));
		Assert.Equal(AppTheme.TerminalBackground, run.Foreground);
		Assert.Equal(AppTheme.TerminalForeground, run.Background);
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

		var panel = Find<Border>(root).First(border => border.StyleName == AppStyles.Terminal);
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
