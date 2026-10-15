using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.Desktop;

/// <summary>
/// D-03（部分）：命令 Dock。在项目目录里运行 shell 命令并流式显示输出，支持取消。
/// <para>
/// 这不是完整 PTY：每次执行都是一条独立命令（<c>bash -lc</c> / PowerShell），没有交互式提示、
/// 全屏 TUI 或常驻会话，因此不会引入平台原生代码与额外体积。输出按字符预算有界，避免长命令撑爆内存。
/// </para>
/// </summary>
public sealed class TerminalDock : IDisposable
{
	/// <summary>保留的输出上限（字符），超出后从头部丢弃。</summary>
	private const int MaxOutputChars = 200_000;

	/// <summary>两次界面刷新之间的最小间隔，避免高频输出逐行重排。</summary>
	private const long PaintIntervalMs = 40;

	private readonly Func<string> _cwd;
	private readonly StringBuilder _buffer = new();
	private readonly Lock _gate = new();
	private readonly TextBlock _output = new();
	private readonly TextBox _command = new();
	private readonly TextBlock _status = new();
	private readonly Button _run;
	private readonly Button _cancel;
	private readonly ScrollViewer _scroll;

	private CancellationTokenSource? _cancellation;
	private bool _running;
	private long _lastPaint;

	public TerminalDock(Func<string> cwd)
	{
		_cwd = cwd;

		_output.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap);
		_status.FontSize(AppTheme.FontSizeMicro).SubtleText();

		_scroll = new ScrollViewer()
			.SmoothScrolling()
			.Content(_output)
			.AutoVerticalScroll()
			.NoHorizontalScroll();

		_command
			.Placeholder(Strings.TerminalPlaceholder)
			.CornerRadius(AppTheme.RadiusMd)
			.StyleName(AppStyles.TextInputLine)
			.OnKeyDown(key =>
			{
				if (key.Key == Key.Enter)
				{
					key.Handled = true;
					_ = RunAsync();
				}
			});

		_run = AppIcons.Button("play", Strings.TerminalRun, () => _ = RunAsync());
		_cancel = AppIcons.Button("x", Strings.TerminalCancel, Cancel);
		_cancel.IsEnabled = false;

		View = new Border()
			.StyleName(AppStyles.Window)
			.Child(new DockPanel()
				.LastChildFill()
				.Children(
					new StackPanel()
						.Spacing(AppTheme.Space2)
						.Padding(new Thickness(AppTheme.Space3))
						.Children(
							new TextBlock().Text(Strings.Terminal).FontSize(18).FontWeight(FontWeight.Bold).PrimaryText(),
							_status,
							new StackPanel()
								.Orientation(Orientation.Horizontal)
								.Spacing(AppTheme.Space2)
								.Children(_run, _cancel))
						.DockTop(),
					new StackPanel()
						.Spacing(AppTheme.Space2)
						.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space3))
						.Children(_command)
						.DockBottom(),
					_scroll.Margin(new Thickness(AppTheme.Space3))));

		AppendLine(Strings.TerminalHint, force: true);
	}

	public UIElement View { get; }

	/// <summary>执行输入框里的命令；运行期间忽略重复触发。</summary>
	public async Task RunAsync()
	{
		if (_running || _disposed)
		{
			return;
		}

		var command = _command.Text?.Trim();
		if (command is null or { Length: 0 })
		{
			return;
		}

		_command.Text = string.Empty;
		_running = true;
		_run.IsEnabled = false;
		_cancel.IsEnabled = true;
		AppendLine($"$ {command}", force: true);

		var cancellation = new CancellationTokenSource();
		_cancellation = cancellation;

		try
		{
			var result = await ShellRunner
				.RunAsync(command, _cwd(), timeoutSeconds: null, line => AppendLine(line), cancellation.Token)
				.ConfigureAwait(false);

			AppendLine(result.Cancelled ? Strings.TerminalCancelled : Strings.TerminalExit(result.ExitCode), force: true);
		}
		catch (OperationCanceledException)
		{
			AppendLine(Strings.TerminalCancelled, force: true);
		}
		catch (Exception ex)
		{
			AppendLine(Strings.TerminalFailed(ex.Message), force: true);
		}
		finally
		{
			_running = false;
			_cancellation = null;
			Dispatch(() =>
			{
				_run.IsEnabled = true;
				_cancel.IsEnabled = false;
			});
		}
	}

	/// <summary>取消正在运行的命令（杀掉整棵进程树）。</summary>
	public void Cancel() => _cancellation?.Cancel();

	private bool _disposed;

	public void Dispose()
	{
		_disposed = true;
		_cancellation?.Cancel();
		_cancellation?.Dispose();
		_cancellation = null;
	}

	private void AppendLine(string? line, bool force = false)
	{
		if (line is null)
		{
			return;
		}

		lock (_gate)
		{
			_buffer.Append(line).Append('\n');
			if (_buffer.Length > MaxOutputChars)
			{
				_buffer.Remove(0, _buffer.Length - MaxOutputChars);
			}
		}

		var now = Environment.TickCount64;
		if (!force && now - _lastPaint < PaintIntervalMs)
		{
			return;
		}

		_lastPaint = now;
		Dispatch(Paint);
	}

	private void Paint()
	{
		if (_disposed)
		{
			return;
		}

		string text;
		lock (_gate)
		{
			text = _buffer.ToString();
		}

		_output.Text(text);
		SmoothScroll.For(_scroll).ScrollToEnd();
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
}
