using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;
using MiniPty;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>
/// D-03：命令 Dock。用 MiniPty 在项目目录里启动一个真正的伪终端（交互式 shell），
/// 输出交给 <see cref="TerminalScreen"/> 做 ANSI/VT 解析后按行渲染，键盘与 IME 输入直接写回 PTY。
/// <para>
/// 交互对齐 xterm.js 的常用部分：scrollback 与滚动条、鼠标拖选/双击选词/三击选行/Alt 区块选择、
/// Shift+滚轮本地滚动、应用鼠标上报（1000/1002/1003 + SGR 1006/1016 + urxvt 1015）、
/// bracketed paste（多行先确认）。支持缩放重排、DECSCUSR 光标形态与闪烁、DECSCNM 反色、
/// OSC 4/10/11/12 调色、OSC 8 超链接（Ctrl+点击）。少数边界序列按 xterm.js 近似处理。
/// </para>
/// </summary>
public sealed class TerminalDock : IDisposable, ITerminalInputSink
{
	private const int MinColumns = 20;
	private const int MaxColumns = 400;
	private const int MinRows = 5;
	private const int MaxRows = 200;

	/// <summary>两次重绘之间的最小间隔，避免高频输出逐个字符重排。</summary>
	private const long PaintIntervalMs = 40;

	/// <summary>
	/// 面板尺寸连续变化（拖分隔条、缩放窗口）时，PTY 尺寸延后这么久再发一次：
	/// 每个像素都发 SIGWINCH 会让 shell 逐帧重画提示符，看起来像终端内容在跳。
	/// </summary>
	private const int ResizeSettleMs = 120;

	/// <summary>为滚动条预留的宽度（控件 + 间距），列数计算时扣除。</summary>
	private const double ScrollBarReserve = 18;

	/// <summary>重绘时复用的文本 run 上限；终端整屏行数远低于这个数量。</summary>
	private const int MaxPooledRuns = 512;

	private readonly Func<string> _cwd;
	private readonly TerminalScreen _screen;
	private readonly StackPanel _lineHost = new();
	private readonly List<TextBlock> _lineViews = [];
	private readonly Stack<Run> _runPool = new();
	private readonly List<List<TerminalRun>> _lineRuns = [];
	private readonly List<StringBuilder> _lineBuilders = [];
	private readonly List<ulong> _lineSignatures = [];
	private readonly List<int> _cachedLineVersions = [];
	private readonly List<int> _cachedBufferRows = [];
	private readonly List<int> _cachedCursorColumns = [];
	private readonly List<bool> _lineDirty = [];
	private int _selectionVersion;
	private int _cachedSelectionVersion = -1;
	private int _cursorFocusVersion;
	private int _cachedCursorFocusVersion = -1;
	private TerminalCursorStyle _paintCursorStyle;
	private bool _paintReverseVideo;
	private uint?[] _paintPalette = new uint?[256];
	private uint? _paintDefaultForeground;
	private uint? _paintDefaultBackground;
	private uint? _paintCursorColor;
	private TerminalCursorStyle _cachedCursorStyle;
	private bool _cachedReverseVideo;
	private int _cachedColorVersion = -1;
	private Color _paintedPanelBackground = default;
	private readonly TextBlock _status = new();
	private readonly Border _panel;
	private readonly TerminalInputSurface _input;
	private readonly Border _imePreview;
	private readonly TextBlock _imeLabel = new();
	private bool _imeActive;

	/// <summary>文本行底下的光标层：只画块光标的实心矩形，块下那个字符仍由文本行绘制。</summary>
	private readonly TerminalCursorLayer _cursorUnder;

	/// <summary>文本行之上的光标层：竖线、底线，以及失焦时的空心框。</summary>
	private readonly TerminalCursorLayer _cursorOver;

	/// <summary>本帧算出的光标格（窗口坐标）；候选窗定位与测试都读它。</summary>
	private Rect _cursorCell;

	/// <summary>量光标格用的本行文本与样式 run（每帧复用，不分配）。</summary>
	private readonly StringBuilder _cursorText = new();
	private readonly List<GeometryStyleRun> _cursorStyles = [];
	private readonly List<Rect> _cursorBounds = [];
	private readonly ScrollBar _scrollBar;
	private readonly DispatcherTimer _timer;
	private readonly Lock _gate = new();

	/// <summary>拖拽面板高度时的 PTY 尺寸只在停下后发一次，避免 SIGWINCH 让 shell 逐帧重画。</summary>
	private DispatcherTimer? _resizeTimer;
	private PtySize? _pendingResize;

	private PtySession? _session;
	private CancellationTokenSource? _shellCancel;
	private Channel<string>? _inputQueue;
	private int _renderedVersion = -1;
	private int _columns = 80;
	private int _rows = 24;
	private double _cellWidth = 8;
	private double _lineHeight = 16;
	private bool _selecting;
	private bool _syncingScroll;
	private bool _disposed;
	private bool _exited;
	private bool _timerRunning;
	private int _idleTicks;
	private bool _blinkHidden;
	// 触控板按分数刻度发事件：按 3 行/刻度累积，不能每个事件都跳 3 行。
	private double _wheelRemainder;
	private bool _blinkDirty;
	private int _blinkTicks;
	private bool _inputFocused;

	// 多行粘贴确认条：贴在终端右下角，避免把整段脚本直接喂给 shell。
	private readonly TextBlock _pasteLabel = new();
	private Border? _pastePrompt;
	private string? _pendingPaste;
	private int _wakeQueued;
	private ContextMenu? _contextMenu;
	private string? _lastPrimaryText;
	private string _shellPath;
	private readonly TextBlock _shellLabel = new();
	private readonly TextBox _shellInput;
	private readonly Button _shellButton;

	/// <param name="cwd">当前会话的工作目录。</param>
	/// <param name="compact">
	/// 紧凑形态：供会话底部的终端面板使用，省掉大标题与多余留白，标题与 shell 动作挤在同一行。
	/// </param>
	/// <param name="onClose">提供时在标题行末尾追加一个收起键，由宿主决定面板的开关。</param>
	public TerminalDock(Func<string> cwd, bool compact = false, Action? onClose = null)
	{
		_cwd = cwd;
		_shellPath = DesktopPreference.Load().TerminalShell;
		_screen = new TerminalScreen(_columns, _rows)
		{
			Respond = SendText,
			ClipboardWrite = text => Dispatch(() => Clipboard?.TrySetText(text)),
		};
		_screen.SelectionChanged += OnSelectionChanged;
		MeasureCell();

		// 说明文本默认不占位：只有 shell 失败/退出这类诊断才显示一行。
		_status.FontSize(AppTheme.FontSizeMicro).SubtleText().IsVisible(false);

		_shellLabel.Text(TerminalShells.DisplayName(_shellPath)).FontSize(AppTheme.FontSizeCaption).PrimaryText();
		_shellButton = new Button().Ghost()
			.Content(_shellLabel)
			.HoverTip(Strings.TerminalShell)
			.OnClick(ShowShellMenu);

		_shellInput = new TextBox()
			.Placeholder(Strings.TerminalShellPrompt)
			.IsVisible(false)
			.OnKeyDown(key =>
			{
				if (key.Key == Key.Enter)
				{
					key.Handled = true;
					ApplyCustomShell();
				}
				else if (key.Key == Key.Escape)
				{
					key.Handled = true;
					HideShellInput();
				}
			});

		var clear = new Button().Ghost()
			.Content(new TextBlock().Text(Strings.TerminalClear).FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.OnClick(ClearScreen);
		var restart = AppIcons.Button("rotate-ccw", Strings.TerminalRestart, RestartShell);

		_lineHost.Spacing(0);

		_scrollBar = new ScrollBar
		{
			Orientation = Orientation.Vertical,
			Width = 10,
			Margin = new Thickness(AppTheme.Space2, 0, 0, 0),
			SmallChange = 1,
			LargeChange = 8,
			IsVisible = false,
		};
		_scrollBar.ValueChanged += OnScrollBarChanged;

		// 展开成面板形态时不再包一层卡片：无边框、无圆角、无外边距，深色表面直接铺满。
		_panel = new Border()
			.StyleName(AppStyles.Terminal)
			.BorderThickness(compact ? 0 : 1)
			.CornerRadius(compact ? 0 : AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space2));

		_input = new TerminalInputSurface(this);
		_input.GotFocus += () => SendFocus(focused: true);
		_input.LostFocus += () => SendFocus(focused: false);

		// IME 预编辑浮层：终端没有文本缓冲区，直接把组合中的文字画在光标格上。
		_imeLabel.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption);
		_imePreview = new Border()
			.Background(AppTheme.TerminalBackground)
			.IsVisible(false)
			.IsHitTestVisible(false)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.VerticalAlignment(VerticalAlignment.Top)
			.Child(_imeLabel);

		// 光标不再靠「给 run 铺背景 / 把字符染色加下划线」近似：形状由光标层按字格几何画。
		// 实心块是「格子的背景」，排在文本行底下，块下那个字符仍读得出来；
		// 竖线、底线与失焦的空心框不遮字，排在文本行之上，单元格自带背景色时也不会被盖掉。
		_cursorUnder = new TerminalCursorLayer();
		_cursorOver = new TerminalCursorLayer();

		var surface = new Grid().Children(_cursorUnder, _lineHost, _input, _cursorOver, _imePreview).Column(0);
		_pastePrompt = BuildPastePrompt();
		surface.Children(_pastePrompt);
		_panel.Child = new Grid()
			.Columns("*,Auto")
			.Children(surface, _scrollBar.Column(1));

		var actions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(_shellButton, restart, clear);

		if (onClose is not null)
		{
			actions.Children(AppIcons.Button("x", Strings.CloseTerminal, onClose));
		}

		// 紧凑形态给会话底部的终端面板用：标题行只有一行 —— 左边「终端」，右边 shell / 重启 / 清屏 / 收起；
		// 说明文本默认隐藏，只在 shell 失败/退出时显示一行省略的诊断。
		var header = compact
			? new StackPanel()
				.Spacing(AppTheme.Space1)
				.Padding(new Thickness(AppTheme.Space3, AppTheme.Space1, AppTheme.Space2, 0))
				.Children(
					new DockPanel()
						.LastChildFill()
						.Children(actions.DockRight(),
							new TextBlock()
								.Text(Strings.Terminal)
								.FontSize(AppTheme.FontSizeCaption)
								.MutedText()
								.VerticalAlignment(VerticalAlignment.Center)),
					_status.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis),
					_shellInput)
			: new StackPanel()
				.Spacing(AppTheme.Space2)
				.Padding(new Thickness(AppTheme.Space3))
				.Children(
					new TextBlock().Text(Strings.Terminal).FontSize(18).FontWeight(FontWeight.Bold).PrimaryText(),
					_status,
					actions,
					_shellInput);

		// 底色由宿主面板统一给（会话底部的终端面板）；终端表面自己保持深底。
		View = new Border()
			.Child(new DockPanel()
				.LastChildFill()
				.Children(
					header.DockTop(),
					_panel.Margin(compact
						? Thickness.Zero
						: new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space3))));

		_panel.OnLayoutSizeChanged(_ => SyncViewport());

		RebuildRows();
		StartShell();

		_timer = new DispatcherTimer(TimeSpan.FromMilliseconds(PaintIntervalMs));
		_timer.Tick += () =>
		{
			// 光标闪烁期间不能停表，其余时候连续几帧没变化就休眠。
			var blinking = UpdateBlink();
			if (PaintIfDirty())
			{
				_idleTicks = 0;
			}
			else if (!blinking && ++_idleTicks > 5)
			{
				// 空闲时不再每 40ms 唤醒 UI 线程。
				_timer.Stop();
				_timerRunning = false;
			}
		};
		_timer.Start();
		_timerRunning = true;
	}

	public UIElement View { get; }

	/// <summary>把键盘焦点交给终端输入层；自定义 shell 输入框打开时优先给它。</summary>
	public void FocusTerminal()
	{
		if (_disposed)
		{
			return;
		}

		if (_shellInput.IsVisible)
		{
			_shellInput.Focus();
			return;
		}

		_input.Focus();
	}

	/// <summary>主题切换：终端表面、光标、选区、链接与 ANSI 系统色都按新主题重绘。</summary>
	public void ApplyTheme()
	{
		if (_disposed)
		{
			return;
		}

		AppStyles.ApplyTextColors((Element)View);
		_imePreview.Background(AppTheme.TerminalBackground);
		foreach (var run in _imeLabel.Inlines)
		{
			run.Foreground = AppTheme.TerminalCursor;
		}

		_paintedPanelBackground = default;
		_renderedVersion = -1;
		_cachedColorVersion = -1;
		_cachedSelectionVersion = -1;
		_cachedCursorFocusVersion = -1;
		for (var index = 0; index < _lineSignatures.Count; index++)
		{
			_lineSignatures[index] = ulong.MaxValue;
		}

		PaintIfDirty(force: true);
	}

	/// <summary>窗口less 测试用：当前视口文本。</summary>
	internal string ScreenText
	{
		get
		{
			lock (_gate)
			{
				return _screen.GetText();
			}
		}
	}

	/// <summary>窗口less 测试用：在屏幕锁内建立一段选区，触发 PRIMARY 同步。</summary>
	internal void SelectForTest(int bufferRow, int column, int length)
	{
		lock (_gate)
		{
			_screen.BeginSelection(bufferRow, column);
			_screen.ExtendSelection(bufferRow, column + Math.Max(0, length - 1));
		}
	}

	/// <summary>窗口less 测试用：向屏幕喂一段输出并立即重绘。</summary>
	internal void FeedForTest(string text)
	{
		lock (_gate)
		{
			_screen.Feed(text.AsSpan());
		}

		PaintIfDirty(force: true);
	}

	/// <summary>窗口less 测试用：读取屏幕内部状态。</summary>
	internal TerminalScreen Screen
	{
		get
		{
			lock (_gate)
			{
				return _screen;
			}
		}
	}

	/// <summary>窗口less 测试用：模拟失焦/聚焦并立即重绘。</summary>
	internal void SetFocusForTest(bool focused)
	{
		SendFocus(focused);
		PaintIfDirty(force: true);
	}

	/// <summary>窗口less 测试用：光标当前是否画得出来（含闪烁的隐藏相位）。</summary>
	internal bool CursorVisibleForTest => _cursorUnder.CursorVisible || _cursorOver.CursorVisible;

	/// <summary>窗口less 测试用：失焦光标是否走「一圈空心框」画法。</summary>
	internal bool CursorHollowForTest => _cursorOver.CursorVisible && !_cursorOver.CursorFocused;

	/// <summary>窗口less 测试用：聚焦块光标是否走「整格实心」画法。</summary>
	internal bool CursorSolidBlockForTest => _cursorUnder.CursorVisible;

	/// <summary>窗口less 测试用：光标色（含 OSC 12 覆盖）。</summary>
	internal Color CursorColorForTest =>
		_cursorOver.CursorVisible ? _cursorOver.CursorColor : _cursorUnder.CursorColor;

	/// <summary>窗口less 测试用：当前光标格（窗口坐标）。</summary>
	internal Rect CursorCellForTest => _cursorCell;

	/// <summary>窗口less 测试用：当前字格尺寸。</summary>
	internal (double Width, double Height) CellSizeForTest => (_cellWidth, _lineHeight);

	// ------------------------------------------------------------------ PTY

	private PtyStartInfo CreateStartInfo()
	{
		var size = new PtySize(_columns, _rows);
		var shell = TerminalShells.Resolve(_shellPath);
		if (OperatingSystem.IsWindows())
		{
			return new PtyStartInfo
			{
				FileName = shell,
				WorkingDirectory = _cwd(),
				Size = size,
			};
		}

		return new PtyStartInfo
		{
			FileName = shell,
			Arguments = [.. TerminalShells.Arguments(shell)],
			WorkingDirectory = _cwd(),
			Size = size,
			TerminalName = "xterm-256color",
		};
	}

	/// <summary>命令 Dock 的 shell 选择：自动、检测到的 shell，或手填路径。</summary>
	private void ShowShellMenu()
	{
		var menu = new ContextMenu();
		var checkIcon = new IconTemplate(size => new GlyphElement()
			.Kind(GlyphKind.CheckMark)
			.GlyphSize(4)
			.Foreground(AppTheme.Accent)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center));

		// 命令必须注册在菜单的 placement target 上，否则菜单项点击找不到处理程序。
		var auto = new Command("pi.terminal.shell.auto", Strings.TerminalShellAuto);
		_shellButton.Commands.Register(auto, () => SelectShell(string.Empty));
		menu.Items.Add(new MenuItem(auto)
		{
			Icon = string.IsNullOrEmpty(_shellPath) ? checkIcon : null,
		});

		var current = TerminalShells.RealPath(TerminalShells.Resolve(_shellPath));
		foreach (var option in TerminalShells.Detect())
		{
			var path = option.Path;
			var command = new Command($"pi.terminal.shell.{path}", option.Name);
			_shellButton.Commands.Register(command, () => SelectShell(path));
			menu.Items.Add(new MenuItem(command)
			{
				Icon = string.Equals(TerminalShells.RealPath(path), current, StringComparison.Ordinal) ? checkIcon : null,
			});
		}

		menu.AddSeparator();
		var custom = new Command("pi.terminal.shell.custom", Strings.TerminalShellCustom);
		_shellButton.Commands.Register(custom, ShowShellInput);
		menu.Items.Add(new MenuItem(custom));
		menu.Show(_shellButton);
	}

	private void SelectShell(string path)
	{
		_shellPath = path;
		_shellLabel.Text(TerminalShells.DisplayName(path));
		try
		{
			DesktopPreference.Load().WithTerminalShell(path).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<TerminalDock>(), "Save terminal shell preference", ex);
		}

		HideShellInput();
		RestartShell();
	}

	private void ShowShellInput()
	{
		_shellInput.Text = _shellPath;
		_shellInput.IsVisible(true);
		_shellInput.Focus();
	}

	private void HideShellInput()
	{
		_shellInput.IsVisible(false);
		_input.Focus();
	}

	private void ApplyCustomShell()
	{
		var value = _shellInput.Text?.Trim() ?? string.Empty;
		if (value.Length == 0)
		{
			HideShellInput();
			return;
		}

		if (!TerminalShells.IsExecutable(value))
		{
			Status(Strings.TerminalShellInvalid);
			return;
		}

		SelectShell(value);
	}

	private void StartShell()
	{
		if (_disposed)
		{
			return;
		}

		StopShell();
		_exited = false;

		try
		{
			_session = Pty.Start(CreateStartInfo());
		}
		catch (Exception ex)
		{
			// 无人值守演练靠 stderr 判断 PTY 是否真的建起来了。
			Console.Error.WriteLine($"piweft-desktop: pty start failed: {ex.Message}");
			Status(Strings.TerminalFailed(ex.Message));
			return;
		}

		// 正常运行不显示说明文本：只有失败/退出才有诊断行。
		Status(string.Empty);
		_shellCancel = new CancellationTokenSource();
		_inputQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
		var session = _session;
		var queue = _inputQueue;
		_ = Task.Run(() => PumpAsync(session, _shellCancel.Token));
		_ = Task.Run(() => WriteLoopAsync(session, queue.Reader, _shellCancel.Token));
		_input.Focus();
	}

	/// <summary>把 PTY 输出按 UTF-8 增量解码后喂给屏幕；界面只在有变化时重绘。</summary>
	private async Task PumpAsync(PtySession session, CancellationToken cancellationToken)
	{
		var decoder = Encoding.UTF8.GetDecoder();
		var chars = ArrayPool<char>.Shared.Rent(8192);
		try
		{
			await foreach (var chunk in session.ReadOutputAsync(cancellationToken).ConfigureAwait(false))
			{
				var data = chunk.Data.Span;
				while (data.Length > 0)
				{
					decoder.Convert(data, chars, flush: false, out var used, out var produced, out _);
					data = data[used..];
					if (produced > 0)
					{
						lock (_gate)
						{
							_screen.Feed(chars.AsSpan(0, produced));
						}
					}

					if (used == 0 && produced == 0)
					{
						break;
					}
				}

				// 有新输出才唤醒渲染定时器；空闲时它已经停了。
				RequestWake();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Dispatch(() => Status(Strings.TerminalFailed(ex.Message)));
		}
		finally
		{
			ArrayPool<char>.Shared.Return(chars);
			Dispatch(() =>
			{
				if (_disposed || !ReferenceEquals(_session, session))
				{
					return;
				}

				_exited = true;
				Status(Strings.TerminalExited(session.ExitCode));
			});
		}
	}

	/// <summary>输入按到达顺序串行写入 PTY，避免高频击键乱序。</summary>
	private static async Task WriteLoopAsync(PtySession session, ChannelReader<string> reader, CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var text in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
			{
				var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
				try
				{
					var length = Encoding.UTF8.GetBytes(text, buffer);
					await session.WriteInputAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(buffer);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception)
		{
			// 会话退出后写入会失败；退出状态由读取循环汇报。
		}
	}

	private void StopShell()
	{
		// 旧的 shell 不再需要这组尺寸；新 shell 会直接用当前行列启动。
		_resizeTimer?.Stop();
		_pendingResize = null;
		_inputQueue?.Writer.TryComplete();
		_inputQueue = null;
		_shellCancel?.Cancel();
		_shellCancel?.Dispose();
		_shellCancel = null;

		var session = _session;
		_session = null;
		if (session is null)
		{
			return;
		}

		try
		{
			if (!session.HasExited)
			{
				session.Kill(PtySignal.Terminate);
			}
		}
		catch (InvalidOperationException)
		{
		}

		session.Dispose();
	}

	private void RestartShell()
	{
		lock (_gate)
		{
			// 重启是“换一个新终端”：活动屏与 scrollback 一起清掉，并回到贴底。
			_screen.Feed("\u001b[3J\u001b[H".AsSpan());
			_screen.ScrollToBottom();
			_screen.ClearSelection();
		}

		StartShell();
		PaintIfDirty(force: true);
	}

	private void ClearScreen()
	{
		lock (_gate)
		{
			_screen.Feed("\u001b[2J\u001b[H".AsSpan());
		}

		PaintIfDirty(force: true);
	}

	// ------------------------------------------------------------------ 键盘

	public void SendKey(KeyEventArgs key)
	{
		// 刚拖完高度就打字时，先把挂起的尺寸发出去，shell 回显才用正确宽度。
		FlushPtyResize();

		// 粘贴确认条打开时 Esc 只关掉它，不把 ESC 送进 shell。
		if (_pendingPaste is not null && key.Key == Key.Escape)
		{
			key.Handled = true;
			CancelPaste();
			return;
		}

		// 复制/粘贴优先用通用快捷键：有选区时 Ctrl/Cmd+C 复制，没有选区时 Ctrl+C 仍是中断信号。
		if (key.Key == Key.C)
		{
			bool hasSelection;
			lock (_gate)
			{
				hasSelection = _screen.HasSelection;
			}

			// 复制只跟平台主修饰键走（Windows/Linux Ctrl、macOS Cmd）。
			if (key.PrimaryKey && hasSelection)
			{
				key.Handled = true;
				CopySelection();
				return;
			}

			// 中断只认 Control：macOS 的 Cmd+C 不会误发中断，而 Ctrl+C 即使有选区也仍然中断。
			if (key.ControlKey && !key.MetaKey)
			{
				key.Handled = true;
				SendText("\u0003");
				return;
			}
		}

		if (key.Key == Key.V && key.PrimaryKey)
		{
			key.Handled = true;
			PasteRequested();
			return;
		}

		if (key.ShiftKey && key.Key is Key.PageUp or Key.PageDown)
		{
			key.Handled = true;
			ScrollView(key.Key == Key.PageUp ? _rows - 1 : -(_rows - 1));
			return;
		}

		if (key.ShiftKey && key.ControlKey && key.Key is Key.Home or Key.End)
		{
			key.Handled = true;
			ScrollView(key.Key == Key.Home ? int.MaxValue / 2 : int.MinValue / 2);
			return;
		}

		bool application;
		lock (_gate)
		{
			application = _screen.ApplicationCursorKeys;
		}

		if (TerminalKeys.Translate(key, application) is { } sequence)
		{
			key.Handled = true;
			SendText(sequence);
		}
	}

	public void SendText(string text)
	{
		if (_disposed || _exited || text.Length == 0)
		{
			return;
		}

		_inputQueue?.Writer.TryWrite(text);
	}

	// ------------------------------------------------------------------ 输入法

	public bool IsComposing => _imeActive;

	/// <summary>候选窗定位：当前光标格的窗口坐标；滚动查看历史时不给位置。</summary>
	public Rect CursorRect
	{
		get
		{
			// 本帧已经画出来的那一格：候选窗必须跟屏幕上的光标同一位置，
			// 而不是拿可能过期的行内容再量一次。
			var painted = _cursorCell;
			if (painted.Width > 0 && painted.Height > 0)
			{
				return painted;
			}

			int row;
			int column;
			lock (_gate)
			{
				if (_screen.ViewOffset != 0)
				{
					return default;
				}

				row = _screen.CursorRow;
				column = _screen.CursorColumn;
			}

			return CursorCellRect(row, column);
		}
	}

	public void CompositionStarted(string text) => UpdateComposition(text);

	public void CompositionUpdated(string text) => UpdateComposition(text);

	public void CompositionEnded()
	{
		if (!_imeActive)
		{
			return;
		}

		_imeActive = false;
		_imePreview.IsVisible(false);
		PaintIfDirty(force: true);
	}

	private void UpdateComposition(string text)
	{
		if (_disposed)
		{
			return;
		}

		_imeActive = true;
		_imeLabel.Inlines.Clear();
		if (text.Length == 0)
		{
			_imePreview.IsVisible(false);
			return;
		}

		_imeLabel.Inlines.Add(new Run(text)
		{
			Foreground = AppTheme.TerminalCursor,
			Decoration = TextDecoration.Underline,
		});
		PositionImePreview();
		_imePreview.IsVisible(true);
		PaintIfDirty(force: true);
	}

	private void PositionImePreview()
	{
		int row;
		int column;
		bool atBottom;
		lock (_gate)
		{
			row = _screen.CursorRow;
			column = _screen.CursorColumn;
			atBottom = _screen.ViewOffset == 0;
		}

		if (!atBottom)
		{
			_imePreview.IsVisible(false);
			return;
		}

		_imePreview.Margin(new Thickness(column * _cellWidth, row * _lineHeight, 0, 0));
	}

	private void CopySelection()
	{
		string text;
		lock (_gate)
		{
			text = _screen.GetSelectionText() ?? _screen.GetText();
		}

		if (text.Length > 0)
		{
			Clipboard?.TrySetText(text);
		}
	}

	private void PasteRequested()
	{
		if (Clipboard?.TryGetText(out var text) != true || text.Length == 0)
		{
			return;
		}

		RequestPaste(text);
	}

	/// <summary>多行内容先确认再送进 PTY：粘贴一段脚本很可能被 shell 直接执行。</summary>
	internal void RequestPaste(string text)
	{
		if (NeedsPasteConfirmation(text))
		{
			ShowPastePrompt(text);
			return;
		}

		SendPaste(text);
	}

	internal static bool NeedsPasteConfirmation(string text) =>
		text.AsSpan().IndexOfAny('\r', '\n') >= 0;

	private Border BuildPastePrompt()
	{
		_pasteLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText();
		var confirm = new Button().Ghost()
			.Focusable(false)
			.Content(new TextBlock().Text(Strings.TerminalPaste).FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.OnClick(ConfirmPaste);
		var cancel = new Button().Ghost()
			.Focusable(false)
			.Content(new TextBlock().Text(Strings.Cancel).FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.OnClick(CancelPaste);

		return new Border()
			.StyleName(AppStyles.Terminal)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2))
			.Margin(new Thickness(AppTheme.Space2))
			.HorizontalAlignment(HorizontalAlignment.Right)
			.VerticalAlignment(VerticalAlignment.Bottom)
			.IsVisible(false)
			.Child(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.Children(_pasteLabel, confirm, cancel));
	}

	private void ShowPastePrompt(string text)
	{
		_pendingPaste = text;
		var lines = text.AsSpan().Count('\n') + 1;
		_pasteLabel.Text(Strings.TerminalPasteConfirm(lines));
		if (_pastePrompt is not null)
		{
			_pastePrompt.IsVisible = true;
		}
	}

	private void ConfirmPaste()
	{
		var text = _pendingPaste;
		HidePastePrompt();
		if (text is not null)
		{
			SendPaste(text);
		}

		_input.Focus();
	}

	private void CancelPaste()
	{
		HidePastePrompt();
		_input.Focus();
	}

	private void HidePastePrompt()
	{
		_pendingPaste = null;
		if (_pastePrompt is not null)
		{
			_pastePrompt.IsVisible = false;
		}
	}

	/// <summary>中键粘贴：Linux 的 PRIMARY selection（选中即复制的那份）。</summary>
	private async Task PastePrimaryAsync()
	{
		if (PrimarySelectionText.Service is not { } service)
		{
			return;
		}

		var text = await service.ReadAsync().ConfigureAwait(false);
		if (!string.IsNullOrEmpty(text))
		{
			SendPaste(text);
		}
	}

	private void SendPaste(string text)
	{
		bool bracketed;
		lock (_gate)
		{
			bracketed = _screen.BracketedPaste;
		}

		SendText(bracketed ? $"\u001b[200~{text}\u001b[201~" : text);
	}

	/// <summary>选区变化后同步到 PRIMARY selection（Linux 的“选中即复制”）。</summary>
	private void OnSelectionChanged()
	{
		Interlocked.Increment(ref _selectionVersion);
		Dispatch(PublishPrimarySelection);
	}

	private void PublishPrimarySelection()
	{
		if (_disposed || PrimarySelectionText.Service is not { } service)
		{
			return;
		}

		string text;
		lock (_gate)
		{
			text = _screen.GetSelectionText() ?? string.Empty;
		}

		if (text == _lastPrimaryText)
		{
			return;
		}

		_lastPrimaryText = text;
		service.Publish(text);
	}

	/// <summary>打开 OSC 8 链接；只放行常见协议，避免终端里的输出触发任意程序。</summary>
	private static bool OpenLink(string uri)
	{
		if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
			parsed.Scheme is not ("http" or "https" or "mailto" or "file"))
		{
			return false;
		}

		try
		{
			Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
			return true;
		}
		catch (Exception)
		{
			// 没有默认浏览器时静默失败，不打断终端输入。
			return false;
		}
	}

	/// <summary>DECSET 1004：应用开启后把焦点变化告诉它（vim/neovim 用）。</summary>
	private void SendFocus(bool focused)
	{
		_inputFocused = focused;
		_blinkTicks = 0;
		// 焦点切换时实心块与空心框互换，立即重绘；失焦不保留半亮的闪烁相。
		_blinkHidden = false;
		_blinkDirty = true;
		Interlocked.Increment(ref _cursorFocusVersion);

		bool report;
		lock (_gate)
		{
			report = _screen.FocusReporting;
		}

		if (report)
		{
			SendText(focused ? "\u001b[I" : "\u001b[O");
		}

		Wake();
	}

	/// <summary>DECSCUSR 请求闪烁时按 ~480ms 翻转光标；返回定时器是否应保持运行。</summary>
	private bool UpdateBlink()
	{
		bool blink;
		lock (_gate)
		{
			blink = _screen.CursorBlink && _screen.CursorVisible;
		}

		if (!blink || !_inputFocused || _imeActive)
		{
			if (_blinkHidden)
			{
				_blinkHidden = false;
				_blinkDirty = true;
			}

			return false;
		}

		if (++_blinkTicks >= 12)
		{
			_blinkTicks = 0;
			_blinkHidden = !_blinkHidden;
			_blinkDirty = true;
		}

		return true;
	}

	private static IClipboardService? Clipboard =>
		Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;

	/// <summary>右键菜单：复制/粘贴/全选，以及清屏与重启 shell。</summary>
	private void ShowContextMenu()
	{
		_contextMenu ??= BuildContextMenu();
		_contextMenu.Show(_input);
	}

	private ContextMenu BuildContextMenu()
	{
		var menu = new ContextMenu();

		var copy = new Command("pi.terminal.copy", Strings.TerminalCopy);
		_input.Commands.Register(copy, CopySelection);
		var paste = new Command("pi.terminal.paste", Strings.TerminalPaste);
		_input.Commands.Register(paste, PasteRequested);
		var selectAll = new Command("pi.terminal.selectall", Strings.TerminalSelectAll);
		_input.Commands.Register(selectAll, SelectAllText);
		var clear = new Command("pi.terminal.clear", Strings.TerminalClear);
		_input.Commands.Register(clear, ClearScreen);
		var restart = new Command("pi.terminal.restart", Strings.TerminalRestart);
		_input.Commands.Register(restart, RestartShell);

		menu.Items.Add(new MenuItem(copy));
		menu.Items.Add(new MenuItem(paste));
		menu.Items.Add(new MenuItem(selectAll));
		menu.AddSeparator();
		menu.Items.Add(new MenuItem(clear));
		menu.Items.Add(new MenuItem(restart));
		return menu;
	}

	private void SelectAllText()
	{
		lock (_gate)
		{
			_screen.SelectAll();
		}

		PaintIfDirty(force: true);
	}

	// ------------------------------------------------------------------ 鼠标

	public void PointerDown(MouseEventArgs e)
	{
		var (row, column) = CellAt(e.GetPosition(_input));

		// Ctrl+点击 OSC 8 链接：先于鼠标上报判断，TUI 里也能打开。
		if (e.Button == MouseButton.Left && e.Modifiers.HasFlag(ModifierKeys.Control))
		{
			string? link;
			lock (_gate)
			{
				link = _screen.LinkAt(row, column);
			}

			if (link is not null && OpenLink(link))
			{
				return;
			}
		}

		if (ShouldReportMouse(e.Modifiers))
		{
			ReportMouse(ButtonCode(e.Button) + ModifierCode(e.Modifiers), column, row, release: false);
			return;
		}

		if (e.Button == MouseButton.Middle)
		{
			_ = PastePrimaryAsync();
			return;
		}

		if (e.Button == MouseButton.Right)
		{
			ShowContextMenu();
			return;
		}

		if (e.Button != MouseButton.Left)
		{
			return;
		}

		int bufferRow;
		lock (_gate)
		{
			bufferRow = _screen.ViewTop + row;
			if (e.ClickCount >= 3)
			{
				_screen.SelectLine(bufferRow);
			}
			else if (e.ClickCount == 2)
			{
				_screen.SelectWord(bufferRow, column);
			}
			else if (e.Modifiers.HasFlag(ModifierKeys.Shift) && _screen.HasSelection)
			{
				// Shift+点击：把选区终点移到点击处。
				_screen.ExtendSelection(bufferRow, column);
			}
			else
			{
				// Alt+拖拽：矩形（区块）选择。
				_screen.BeginSelection(bufferRow, column, e.Modifiers.HasFlag(ModifierKeys.Alt));
			}
		}

		_selecting = true;
		PaintIfDirty(force: true);
	}

	public void PointerMove(MouseEventArgs e)
	{
		var (row, column) = CellAt(e.GetPosition(_input));

		if (_selecting && e.LeftButton)
		{
			lock (_gate)
			{
				_screen.ExtendSelection(_screen.ViewTop + row, column);
			}

			PaintIfDirty(force: true);
			return;
		}

		TerminalMouseMode mode;
		lock (_gate)
		{
			mode = _screen.MouseMode;
		}

		if (!ShouldReportMouse(e.Modifiers) || mode is TerminalMouseMode.None or TerminalMouseMode.X10)
		{
			return;
		}

		if (mode == TerminalMouseMode.Button && !e.LeftButton && !e.MiddleButton && !e.RightButton)
		{
			return;
		}

		var code = 32 + (e.LeftButton ? 0 : e.MiddleButton ? 1 : e.RightButton ? 2 : 3);
		ReportMouse(code + ModifierCode(e.Modifiers), column, row, release: false);
	}

	public void PointerUp(MouseEventArgs e)
	{
		if (_selecting)
		{
			_selecting = false;
			return;
		}

		var (row, column) = CellAt(e.GetPosition(_input));
		if (ShouldReportMouse(e.Modifiers))
		{
			ReportMouse(3 + ModifierCode(e.Modifiers), column, row, release: true);
		}
	}

	public void Wheel(MouseWheelEventArgs e)
	{
		var (row, column) = CellAt(e.GetPosition(_input));
		if (ShouldReportMouse(e.Modifiers) && e.Delta.Y != 0)
		{
			ReportMouse(e.Delta.Y > 0 ? 64 : 65, column, row, release: false);
			return;
		}

		var lines = WheelLines(ref _wheelRemainder, e.Delta.Y);
		if (lines != 0) ScrollView(lines);
	}

	/// <summary>把分数滚轮刻度累积成整行；一格滚轮 = 3 行，与旧行为一致。</summary>
	internal static int WheelLines(ref double remainder, double notches)
	{
		if (!double.IsFinite(notches) || notches == 0) return 0;
		remainder += notches * 3.0;
		// 分数刻度累积会有 1e-16 级误差；先吸附到整行，否则反向滚动会少一行。
		var lines = (int)(remainder >= 0
			? Math.Floor(remainder + 1e-9)
			: Math.Ceiling(remainder - 1e-9));
		remainder -= lines;
		if (Math.Abs(remainder) < 1e-9) remainder = 0;
		return lines;
	}

	private bool ShouldReportMouse(ModifierKeys modifiers)
	{
		lock (_gate)
		{
			return _screen.MouseMode != TerminalMouseMode.None && (modifiers & ModifierKeys.Shift) == 0;
		}
	}

	private (int Row, int Column) CellAt(Point position)
	{
		var column = (int)(position.X / _cellWidth);
		var row = (int)(position.Y / _lineHeight);
		return (Math.Clamp(row, 0, _rows - 1), Math.Clamp(column, 0, _columns - 1));
	}

	private int ButtonCode(MouseButton button) => button switch
	{
		MouseButton.Left => 0,
		MouseButton.Middle => 1,
		MouseButton.Right => 2,
		_ => 3,
	};

	private static int ModifierCode(ModifierKeys modifiers)
	{
		var code = 0;
		if ((modifiers & ModifierKeys.Shift) != 0) code += 4;
		if ((modifiers & ModifierKeys.Alt) != 0) code += 8;
		if ((modifiers & ModifierKeys.Control) != 0) code += 16;
		return code;
	}

	/// <summary>按应用选择的编码写回：SGR（1006/1016）、urxvt 十进制（1015）或传统 X10 字节序列。</summary>
	private void ReportMouse(int code, int column, int row, bool release)
	{
		bool sgr;
		bool urxvt;
		bool pixels;
		lock (_gate)
		{
			sgr = _screen.SgrMouse;
			urxvt = _screen.UrxvtMouse;
			pixels = _screen.PixelMouse;
		}

		int x;
		int y;
		if (pixels)
		{
			// SGR-Pixels：坐标换成像素（1 起），按单元格左上角折算，避免 DIP 与设备像素的缩放歧义。
			x = Math.Max(1, (int)Math.Round(column * _cellWidth) + 1);
			y = Math.Max(1, (int)Math.Round(row * _lineHeight) + 1);
		}
		else
		{
			x = Math.Clamp(column + 1, 1, 223);
			y = Math.Clamp(row + 1, 1, 223);
		}

		// 像素协议只能用 SGR 编码；X10 的 223 上限对它不适用。
		SendText(sgr || pixels
			? $"\u001b[<{code};{x};{y}{(release ? 'm' : 'M')}"
			: urxvt
				? $"\u001b[{code};{x};{y}M"
				: $"\u001b[M{(char)(32 + code)}{(char)(32 + x)}{(char)(32 + y)}");
	}

	// ------------------------------------------------------------------ 滚动

	private void ScrollView(int lines) => ScrollView(lines, force: true);

	private void ScrollView(int lines, bool force)
	{
		bool changed;
		lock (_gate)
		{
			changed = _screen.ScrollView(lines);
		}

		if (changed || force)
		{
			PaintIfDirty(force: true);
		}
	}

	private void OnScrollBarChanged(double value)
	{
		if (_syncingScroll || _disposed)
		{
			return;
		}

		int target;
		lock (_gate)
		{
			target = _screen.MaxViewOffset - (int)Math.Round(value);
			_screen.ScrollView(target - _screen.ViewOffset);
		}

		PaintIfDirty(force: true);
	}

	// ------------------------------------------------------------------ 布局与渲染

	private void MeasureCell()
	{
		var probe = new TextBlock()
			.Text("MMMMMMMMMM")
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeCaption);
		probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		var size = probe.DesiredSize;
		_cellWidth = Math.Max(1, size.Width / 10);
		_lineHeight = Math.Max(1, size.Height);
	}

	/// <summary>按面板尺寸重新计算列/行，并同步 PTY 的窗口大小。</summary>
	private void SyncViewport()
	{
		if (_disposed)
		{
			return;
		}

		var border = _panel.BorderThickness * 2;
		var width = _panel.Bounds.Width - _panel.Padding.HorizontalThickness - border - ScrollBarReserve;
		var height = _panel.Bounds.Height - _panel.Padding.VerticalThickness - border;
		if (width <= 0 || height <= 0)
		{
			return;
		}

		var columns = Math.Clamp((int)(width / _cellWidth), MinColumns, MaxColumns);
		var rows = Math.Clamp((int)(height / _lineHeight), MinRows, MaxRows);
		if (columns == _columns && rows == _rows)
		{
			return;
		}

		_columns = columns;
		_rows = rows;
		lock (_gate)
		{
			_screen.Resize(columns, rows);
		}

		RebuildRows();
		QueuePtyResize(columns, rows);
		PaintIfDirty(force: true);
	}

	/// <summary>攒一次 PTY 尺寸，等面板尺寸停下来再发；连续的 resize 只保留最后一组行列。</summary>
	private void QueuePtyResize(int columns, int rows)
	{
		if (_session is null)
		{
			return;
		}

		_pendingResize = new PtySize(columns, rows);
		_resizeTimer ??= CreateResizeTimer();
		_resizeTimer.Stop();
		_resizeTimer.Start();
	}

	private DispatcherTimer CreateResizeTimer()
	{
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ResizeSettleMs));
		timer.Tick += FlushPtyResize;
		return timer;
	}

	/// <summary>把攒下的尺寸发给 PTY；没有挂起的尺寸时什么也不做。</summary>
	private void FlushPtyResize()
	{
		_resizeTimer?.Stop();
		var pending = _pendingResize;
		_pendingResize = null;
		var session = _session;
		if (pending is not { } size || session is null)
		{
			return;
		}

		try
		{
			session.Resize(size);
		}
		catch (InvalidOperationException)
		{
			// shell 已经退出：尺寸不再重要。
		}
	}

	private void RebuildRows()
	{
		while (_lineViews.Count < _rows)
		{
			var view = new TextBlock()
				.FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeCaption)
				.TextWrapping(TextWrapping.NoWrap)
				.TextTrimming(TextTrimming.None)
				.Height(_lineHeight)
				.PrimaryText();
			_lineViews.Add(view);
			_lineHost.Children(view);
			_lineSignatures.Add(ulong.MaxValue);
			_lineRuns.Add([]);
			_lineBuilders.Add(new StringBuilder());
			_cachedLineVersions.Add(-1);
			_cachedBufferRows.Add(-1);
			_cachedCursorColumns.Add(-2);
			_lineDirty.Add(true);
		}

		while (_lineViews.Count > _rows)
		{
			var view = _lineViews[^1];
			_lineViews.RemoveAt(_lineViews.Count - 1);
			_lineSignatures.RemoveAt(_lineSignatures.Count - 1);
			_lineRuns.RemoveAt(_lineRuns.Count - 1);
			_lineBuilders.RemoveAt(_lineBuilders.Count - 1);
			_cachedLineVersions.RemoveAt(_cachedLineVersions.Count - 1);
			_cachedBufferRows.RemoveAt(_cachedBufferRows.Count - 1);
			_cachedCursorColumns.RemoveAt(_cachedCursorColumns.Count - 1);
			_lineDirty.RemoveAt(_lineDirty.Count - 1);
			_lineHost.Remove(view);
		}

		_renderedVersion = -1;
	}

	private bool PaintIfDirty(bool force = false)
	{
		if (_disposed)
		{
			return false;
		}

		int version;
		lock (_gate)
		{
			version = _screen.Version;
		}

		if (!force && version == _renderedVersion && !_blinkDirty)
		{
			return false;
		}

		_blinkDirty = false;
		_renderedVersion = version;
		Paint();
		return true;
	}

	/// <summary>唤醒可能已休眠的渲染定时器；只在有变化时调用。</summary>
	private void Wake()
	{
		_idleTicks = 0;
		if (_timerRunning)
		{
			return;
		}

		_timer.Start();
		_timerRunning = true;
	}

	/// <summary>后台输出合并唤醒：一帧内多次输出只排一次 UI 调度。</summary>
	private void RequestWake()
	{
		if (Interlocked.Exchange(ref _wakeQueued, 1) != 0)
		{
			return;
		}

		Dispatch(() =>
		{
			Volatile.Write(ref _wakeQueued, 0);
			Wake();
		});
	}

	private void Paint()
	{
		var rows = _rows;
		var cursorRow = -1;
		var cursorColumn = -1;
		var cursorVisible = false;
		var maxOffset = 0;
		var viewOffset = 0;
		var imeActive = _imeActive;

		// 预编辑浮层跟着光标走；滚到历史时隐藏。
		if (imeActive)
		{
			PositionImePreview();
		}

		// 一次锁内取完所有行的 runs：每帧只进一次锁，后台输出不必和 30 次 lock 竞争。
		lock (_gate)
		{
			cursorVisible = _screen.CursorVisible && !imeActive && !_blinkHidden;
			if (cursorVisible)
			{
				cursorRow = _screen.CursorRow;
				cursorColumn = _screen.CursorColumn;
			}

			maxOffset = _screen.MaxViewOffset;
			viewOffset = _screen.ViewOffset;

			// 行级缓存：行内容、视口映射、光标所在行都没变就不重建 runs。
			var viewTop = _screen.ViewTop;
			var selectionVersion = Volatile.Read(ref _selectionVersion);
			var cursorFocusVersion = Volatile.Read(ref _cursorFocusVersion);
			_paintCursorStyle = _screen.CursorStyle;
			_paintReverseVideo = _screen.ReverseVideo;
			_paintPalette = _screen.Palette;
			_paintDefaultForeground = _screen.DefaultForegroundRgb;
			_paintDefaultBackground = _screen.DefaultBackgroundRgb;
			_paintCursorColor = _screen.CursorColorRgb;
			// OSC 10/11 查询时用当前主题色作答（主题可能被用户切换）。
			_screen.HostForeground = ToRgb(AppTheme.TerminalForeground);
			_screen.HostBackground = ToRgb(AppTheme.TerminalBackground);
			var colorVersion = _screen.ColorVersion;
			// DECSCUSR / DECSCNM / OSC 4/10/11/12 只改整屏渲染参数，不碰行内容，必须主动全量重绘一次。
			var renderChanged = _paintCursorStyle != _cachedCursorStyle ||
				_paintReverseVideo != _cachedReverseVideo ||
				colorVersion != _cachedColorVersion;
			_cachedCursorStyle = _paintCursorStyle;
			_cachedReverseVideo = _paintReverseVideo;
			_cachedColorVersion = colorVersion;
			var allDirty = selectionVersion != _cachedSelectionVersion ||
				cursorFocusVersion != _cachedCursorFocusVersion ||
				renderChanged;
			_cachedSelectionVersion = selectionVersion;
			_cachedCursorFocusVersion = cursorFocusVersion;
			if (renderChanged)
			{
				// OSC 4/10/11/12 或光标形态变化不进 run 结构，靠重置指纹强制重建。
				for (var index = 0; index < _lineSignatures.Count; index++)
				{
					_lineSignatures[index] = ulong.MaxValue;
				}
			}

			var panelBackground = _paintDefaultBackground is { } backgroundOverride
				? FromRgb(backgroundOverride)
				: AppTheme.TerminalBackground;
			if (!panelBackground.Equals(_paintedPanelBackground))
			{
				_paintedPanelBackground = panelBackground;
				_panel.Background(panelBackground);
			}

			for (var row = 0; row < rows && row < _lineViews.Count; row++)
			{
				var bufferRow = viewTop + row;
				var cursorInRow = row == cursorRow ? cursorColumn : -1;
				var lineVersion = _screen.LineVersion(bufferRow);
				var dirty = allDirty
					|| _cachedBufferRows[row] != bufferRow
					|| _cachedLineVersions[row] != lineVersion
					|| _cachedCursorColumns[row] != cursorInRow;

				_lineDirty[row] = dirty;
				if (!dirty)
				{
					continue;
				}

				// 光标那一格永远单独切一个 run（run.Cursor）：一个簇要么整格高亮、要么整格不碰，
				// 不会在组合符号中间断开。失焦时也要切：光标层要靠这个边界量出那一格在哪。
				_screen.GetRuns(row, _lineRuns[row], _lineBuilders[row], cursorInRow);
				_cachedBufferRows[row] = bufferRow;
				_cachedLineVersions[row] = lineVersion;
				_cachedCursorColumns[row] = cursorInRow;
			}
		}

		SyncScrollBar(maxOffset, viewOffset);

		// 光标交给光标层按字格几何画：聚焦是实心块/竖线/底线，失焦是一整圈空心框。
		// 颜色都取光标自身（含 OSC 12 覆盖）：失焦只是从实心变空心，不把光标调暗，也不改字符颜色。
		var cursorPaintColor = _paintCursorColor is { } cursorOverride
			? FromRgb(cursorOverride)
			: AppTheme.TerminalCursor;
		var cursorShown = cursorVisible && cursorRow >= 0;
		_cursorCell = cursorShown ? CursorCellRect(cursorRow, cursorColumn) : default;
		// 实心块走底层，其余形状（竖线 / 底线 / 失焦空心框）走上层：同一帧只有一层画得出来。
		var solidBlock = cursorShown && _inputFocused && _paintCursorStyle == TerminalCursorStyle.Block;
		_cursorUnder.SetCursor(
			_cursorCell,
			cursorPaintColor,
			TerminalCursorStyle.Block,
			focused: true,
			visible: solidBlock);
		_cursorOver.SetCursor(
			_cursorCell,
			cursorPaintColor,
			_paintCursorStyle,
			focused: _inputFocused,
			visible: cursorShown && !solidBlock);

		for (var row = 0; row < rows && row < _lineViews.Count; row++)
		{
			if (!_lineDirty[row])
			{
				continue;
			}

			var runs = _lineRuns[row];
			var focusedCursor = row == cursorRow && _inputFocused;
			var signature = Signature(runs, focusedCursor ? cursorColumn : -1, focusedCursor);
			if (signature == _lineSignatures[row])
			{
				continue;
			}

			_lineSignatures[row] = signature;
			var view = _lineViews[row];
			// Reuse the previous runs instead of allocating one per span on every repaint.
			foreach (var inline in view.Inlines)
			{
				if (inline is Run run && _runPool.Count < MaxPooledRuns)
				{
					_runPool.Push(run);
				}
			}

			view.Inlines.Clear();
			foreach (var run in runs)
			{
				// 光标格由屏幕自己切出来（run.Cursor）：一个簇永远整格高亮，不会在组合符号中间断开。
				// 只有聚焦才把块下那个字符按底色画；失焦时文本一个字也不动，空心框是纯几何。
				// 行尾之外的光标不再补一个 run：格子由光标层按列号画，补位只会把块拽到行内容末尾。
				AddRun(view, run, run.Text, run.Cursor && focusedCursor);
			}
		}
	}

	private void SyncScrollBar(int maxOffset, int viewOffset)
	{
		var visible = maxOffset > 0;
		if (_scrollBar.IsVisible != visible)
		{
			_scrollBar.IsVisible(visible);
		}

		if (!visible)
		{
			return;
		}

		_syncingScroll = true;
		try
		{
			// 只写有变化的属性：每帧无脑赋值会让滚动条反复失效重绘。
			if (Math.Abs(_scrollBar.Maximum - maxOffset) > 0.5)
			{
				_scrollBar.Maximum = maxOffset;
			}

			if (Math.Abs(_scrollBar.ViewportSize - _rows) > 0.5)
			{
				_scrollBar.ViewportSize = _rows;
			}

			var value = maxOffset - viewOffset;
			if (Math.Abs(_scrollBar.Value - value) > 0.5)
			{
				_scrollBar.Value = value;
			}
		}
		finally
		{
			_syncingScroll = false;
		}
	}

	/// <summary>
	/// 光标格矩形（窗口坐标）。横向跟着本行的真实排布量：回退字体的字宽、宽字符、粗体都会让
	/// 「列号 × 格宽」与字符实际位置对不上，只有按排布量，光标才压得住它所属那一格；
	/// 纵向用该行 arrange 后的实际位置，行高与文本行严格一致。量不到时退回格网数学。
	/// </summary>
	private Rect CursorCellRect(int viewportRow, int column)
	{
		var row = CursorRowBounds(viewportRow);
		if (TryBuildCursorRow(viewportRow, out var charIndex, out var charLength, out var cells) &&
			TryMeasureCluster(charIndex, charLength, out var measured))
		{
			// 横向跟着该行的真实排布量：回退字体、emoji 这类「字形宽度与占格数对不上」的簇，
			// 只有按排布量才不会画在邻居字符上。宽度不小于它占的格数，整字（含整格）都盖住。
			return new Rect(
				row.X + measured.X,
				row.Y,
				Math.Max(measured.Width, cells * _cellWidth),
				row.Height);
		}

		// 量不到排布（光标落在行尾空白里、那些格根本没有字形；或首帧还没排版）：按格网算。
		// 旧实现在这里给行尾光标补一个 run，于是块画在「最后一个有字形的格子」后面，
		// 光标越往右走块偏得越多 —— 看起来就是光标整块偏左。
		return new Rect(
			row.X + (column * _cellWidth),
			row.Y,
			cells * _cellWidth,
			row.Height);
	}

	/// <summary>该文本行 arrange 后的矩形；还没排布（首帧、窗口less 测试）时退回行号 × 行高。</summary>
	private Rect CursorRowBounds(int viewportRow)
	{
		if (viewportRow >= 0 && viewportRow < _lineViews.Count)
		{
			var arranged = _lineViews[viewportRow].Bounds;
			if (arranged.Height > 0)
			{
				return arranged;
			}
		}

		var surface = _input.Bounds;
		return new Rect(
			surface.X,
			surface.Y + (viewportRow * _lineHeight),
			surface.Width,
			_lineHeight);
	}

	/// <summary>
	/// 复制光标行的排布输入：文本与各格样式 run 都照 <see cref="AddRun"/> 的写法拼出来，
	/// 同时给出光标那一格在文本里的码元区间。行内量不到光标格时返回 false。
	/// </summary>
	private bool TryBuildCursorRow(int viewportRow, out int charIndex, out int charLength, out int cells)
	{
		charIndex = 0;
		charLength = 0;
		cells = 1;
		_cursorText.Clear();
		_cursorStyles.Clear();

		if (viewportRow < 0 || viewportRow >= _lineRuns.Count)
		{
			return false;
		}

		foreach (var run in _lineRuns[viewportRow])
		{
			if (run.Cursor && charLength == 0)
			{
				charIndex = _cursorText.Length;
				charLength = run.Text.Length;
				cells = Math.Max(1, run.Cells);
			}

			_cursorText.Append(run.Text);
			if (run.Bold || run.Italic)
			{
				_cursorStyles.Add(new GeometryStyleRun(
					_cursorText.Length - run.Text.Length,
					run.Text.Length,
					new TextRunStyle(AppTheme.MonoFont, AppTheme.FontSizeCaption)
					{
						Weight = run.Bold ? FontWeight.Bold : FontWeight.Normal,
						Italic = run.Italic,
					}));
			}
		}

		return charLength > 0;
	}

	/// <summary>把上面拼出的行交给文本引擎量一格的实际位置；内容一致时命中引擎缓存，不重复排版。</summary>
	private bool TryMeasureCluster(int charIndex, int charLength, out Rect bounds)
	{
		bounds = default;
		if (_cursorText.Length == 0 ||
			charIndex < 0 ||
			charIndex + charLength > _cursorText.Length) 
		{
			return false;
		}

		try
		{
			var layout = Application.DefaultGraphicsFactory.TextEngine.GetOrCreateLayout(
				new TextLayoutRequest
				{
					Text = _cursorText.ToString().AsMemory(),
					DefaultStyle = new TextRunStyle(AppTheme.MonoFont, AppTheme.FontSizeCaption),
					Runs = _cursorStyles.Count == 0 ? [] : [.. _cursorStyles],
					Paragraph = new TextParagraphStyle
					{
						Wrapping = TextWrapping.NoWrap,
						Trimming = TextTrimming.None,
					},
				},
				TextLayoutCachePolicy.Owner,
				_cursorOver);
			_cursorBounds.Clear();
			layout.GetRangeBounds(charIndex, charLength, _cursorBounds);
			if (_cursorBounds.Count == 0)
			{
				return false;
			}

			bounds = _cursorBounds[0];
			return bounds.Width > 0;
		}
		catch (Exception)
		{
			// 渲染后端未就绪（窗口less 测试）或排布异常：宁可用格网数学，也不让光标消失。
			return false;
		}
	}

	private Run RentRun(string text)
	{
		if (!_runPool.TryPop(out var element))
		{
			return new Run(text);
		}

		// Detached free list: reset every property AddRun may have set on a previous paint.
		element.Text = text;
		element.Foreground = null;
		element.Background = null;
		element.FontWeight = null;
		element.Italic = false;
		element.Decoration = TextDecoration.None;
		element.BaselineOffset = 0;
		return element;
	}

	private void AddRun(TextBlock view, TerminalRun run, string text, bool cursor = false)
	{
		if (text.Length == 0)
		{
			return;
		}

		// 默认前景/背景取终端主题色，OSC 10/11 可覆盖；DECSCNM 时两者互换，SGR 7 再按单元格换一次。
		var defaultForeground = _paintDefaultForeground is { } foregroundOverride
			? FromRgb(foregroundOverride)
			: AppTheme.TerminalForeground;
		var defaultBackground = _paintDefaultBackground is { } backgroundOverride
			? FromRgb(backgroundOverride)
			: AppTheme.TerminalBackground;
		if (_paintReverseVideo)
		{
			(defaultForeground, defaultBackground) = (defaultBackground, defaultForeground);
		}

		var explicitDefault = _paintDefaultForeground.HasValue || _paintDefaultBackground.HasValue;
		var foreground = Resolve(run.Foreground) ?? defaultForeground;
		var background = Resolve(run.Background) ?? defaultBackground;
		if (run.Reverse)
		{
			(foreground, background) = (background, foreground);
		}

		// 失焦时光标只是一圈空心框（由光标层画），字符保持原色，不做反白也不染色。
		var cursorFocused = cursor && _inputFocused;
		var element = RentRun(text);
		if (cursorFocused && _paintCursorStyle == TerminalCursorStyle.Block)
		{
			// 块状光标：整格的实心矩形由光标层画在文本底下；这里再给那一格铺一层同色，
			// 让本身就带背景色的单元格（vim 高亮行、状态栏）不会把块盖掉。
			// 块下的字符取该格自己的底色，反白后仍读得出来；竖线/下划线不遮字，字符一律不动色。
			element.Background = _paintCursorColor is { } blockOverride
				? FromRgb(blockOverride)
				: AppTheme.TerminalCursor;
			element.Foreground = background;
		}
		else
		{
			element.Foreground = foreground;
			if (run.Selected)
			{
				element.Background = AppTheme.TerminalSelection;
			}
			// SGR 7（反白）把默认前景换成了背景色：不写明背景时字符会被画成“底色叠底色”，
			// 例如 zsh 对括号粘贴高亮的输入看起来就像整段消失。反白行必须显式铺上交换后的背景。
			else if (_paintReverseVideo || explicitDefault || run.Reverse || run.Background.Kind != TerminalColorKind.Default)
			{
				element.Background = background;
			}

			if (run.Link && run.Foreground.Kind == TerminalColorKind.Default)
			{
				element.Foreground = AppTheme.TerminalLink;
			}
		}

		if (run.Bold)
		{
			element.FontWeight = FontWeight.Bold;
		}

		if (run.Italic)
		{
			element.Italic = true;
		}

		var decoration = TextDecoration.None;
		if (run.Underline || (run.Link && !cursor))
		{
			decoration |= TextDecoration.Underline;
		}

		if (run.Strikethrough)
		{
			decoration |= TextDecoration.Strikethrough;
		}

		if (decoration != TextDecoration.None)
		{
			element.Decoration = decoration;
		}

		if (!cursor && run.Dim && element.Foreground is { } dimmed)
		{
			element.Foreground = Color.FromRgb(
				(byte)(dimmed.R * 0.6),
				(byte)(dimmed.G * 0.6),
				(byte)(dimmed.B * 0.6));
		}

		if (!cursor && run.Hidden)
		{
			element.Foreground = element.Background ?? defaultBackground;
		}

		view.Inlines.Add(element);
	}

	/// <summary>默认色交给主题/OSC 覆盖，调色板（含 OSC 4 改写）与真彩色转成具体颜色。</summary>
	private Color? Resolve(TerminalColor color) => color.Kind switch
	{
		TerminalColorKind.Indexed => FromRgb(_paintPalette[color.Index] ?? TerminalPalette.Rgb(color.Index, AppTheme.IsLightTheme)),
		TerminalColorKind.Rgb => FromRgb(color.Rgb),
		_ => null,
	};

	private static Color FromRgb(uint rgb) => Color.FromRgb(
		(byte)(rgb >> 16),
		(byte)(rgb >> 8),
		(byte)rgb);

	private static uint ToRgb(Color color) => ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

	/// <summary>行内容指纹：用结构哈希代替字符串拼接，每帧每行零字符串分配。</summary>
	private static ulong Signature(List<TerminalRun> runs, int cursor, bool cursorFocused = false)
	{
		var hash = new HashCode();
		hash.Add(cursor);
		hash.Add(cursorFocused);
		foreach (var run in runs)
		{
			hash.Add(run.Column);
			hash.Add(run.Cells);
			hash.Add(run.Text);
			hash.Add(run.Foreground);
			hash.Add(run.Background);
			hash.Add(run.Bold);
			hash.Add(run.Italic);
			hash.Add(run.Underline);
			hash.Add(run.Strikethrough);
			hash.Add(run.Dim);
			hash.Add(run.Hidden);
			hash.Add(run.Reverse);
			hash.Add(run.Link);
			hash.Add(run.Selected);
			hash.Add(run.Cursor);
		}

		return (ulong)hash.ToHashCode();
	}

	private void Status(string text)
	{
		// 说明文本只在有诊断时占一行；正常运行时整行隐藏，终端可以铺满。
		_status.Text(text);
		_status.IsVisible(text.Length > 0);
		HoverHints.Set(_status, text.Length > 0 ? text : null);
	}

	private static void Dispatch(Action action)
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			action();
			return;
		}

		dispatcher.BeginInvoke(action);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_timer.Stop();
		_timerRunning = false;
		_runPool.Clear();
		try
		{
			// 光标格测量用的那份排布挂在光标层上，随宿主一起释放。
			Application.DefaultGraphicsFactory.TextEngine.ManagedCache.ReleaseOwner(_cursorOver);
		}
		catch (Exception)
		{
			// 后端已销毁：没有可释放的排布。
		}
		_resizeTimer?.Dispose();
		_resizeTimer = null;
		_scrollBar.ValueChanged -= OnScrollBarChanged;
		StopShell();
	}
}
