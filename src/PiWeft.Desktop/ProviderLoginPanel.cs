using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 订阅登录面板：展示授权入口（URL / 设备码）、实时日志、需要用户输入的提示，并提供取消/重试。
/// 登录本身复用 <see cref="ProviderAuthService"/>（底层仍是 PiWeft.Ai.Auth 的 OAuth 流程）。
/// </summary>
internal sealed class ProviderLoginPanel
{
	private readonly AuthProvider _provider;
	private readonly string _agentDir;
	private readonly TextBlock _title = new();
	private readonly TextBlock _status = new();
	private readonly TextBlock _logText = new();
	private readonly TextBlock _authUrl = new();
	private readonly TextBlock _authCode = new();
	private readonly TextBlock _promptLabel = new();
	private readonly TextBox _promptInput = new();
	private readonly Border _authCard;
	private readonly Button _copyCode;
	private readonly Button _copyUrl;
	private readonly StackPanel _codeGroup = new();
	private readonly Border _promptCard;
	private readonly ScrollViewer _logScroll;
	private readonly Button _retry;
	private readonly Button _cancel;
	private readonly Button _done;
	private readonly object _logGate = new();
	private readonly StringBuilder _log = new();
	private readonly DispatcherTimer _pump;
	private Window? _window;
	private CancellationTokenSource? _abort;
	private TaskCompletionSource<string?>? _pending;
	private bool _logDirty;
	private bool _running;
	private bool _cancelRequested;
	private int _generation;
	private UIElement? _view;

	public ProviderLoginPanel(AuthProvider provider, string agentDir)
	{
		_provider = provider;
		_agentDir = agentDir;
		_title.Text(Strings.AuthLoginTitleFor(provider.Name)).FontSize(AppTheme.FontSizeTitle)
			.FontWeight(FontWeight.Bold).PrimaryText().TextTrimming(TextTrimming.CharacterEllipsis);
		_status.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap);
		_logText.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);
		_authUrl.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);
		_authCode.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeTitle).FontWeight(FontWeight.Bold).PrimaryText();
		_copyCode = AppIcons.TextButton("copy", Strings.AuthCopyCode, () => Copy(_authCode.Text)).IsVisible(false);
		_copyUrl = AppIcons.TextButton("copy", Strings.AuthCopyLink, () => Copy(_authUrl.Text)).IsVisible(false);
		_authCard = new Border().StyleName(AppStyles.CardMuted).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space3)).IsVisible(false)
			.Child(new StackPanel().Spacing(6).Children(
				Label(Strings.AuthAuthorization).FontWeight(FontWeight.Bold),
				_authUrl,
				_codeGroup.Spacing(2).Children(
					Label(Strings.AuthDeviceCode).FontSize(AppTheme.FontSizeMicro).SubtleText(),
					_authCode),
				new WrapPanel().Spacing(4).Children(
					AppIcons.TextButton("external-link", Strings.AuthOpenBrowser, OpenBrowser),
					_copyUrl,
					_copyCode)));
		_logScroll = new ScrollViewer().AutoVerticalScroll().NoHorizontalScroll().MaxHeight(180)
			.Content(new Border().StyleName(AppStyles.CardMuted).CornerRadius(AppTheme.RadiusSm)
				.Padding(new Thickness(AppTheme.Space2)).Child(_logText));
		_promptLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText().TextWrapping(TextWrapping.Wrap);
		_promptInput.PrimarySelection().Placeholder(Strings.AuthPromptDefault)
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Enter) return;
				key.Handled = true;
				SubmitPrompt();
			});
		var promptSubmit = AppIcons.PrimaryButton("check", Strings.AuthPromptSubmit, SubmitPrompt);
		_promptCard = new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space3)).IsVisible(false)
			.Child(new StackPanel().Spacing(6).Children(
				Label(Strings.AuthPromptTitle).FontWeight(FontWeight.Bold),
				_promptLabel,
				_promptInput,
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
					promptSubmit,
					new TextBlock().Text(Strings.AuthPromptEmptyHint).FontSize(AppTheme.FontSizeMicro).SubtleText()
						.TextWrapping(TextWrapping.Wrap).VerticalAlignment(VerticalAlignment.Center))));
		_retry = AppIcons.TextButton("rotate-ccw", Strings.AuthRetry, StartLogin).IsVisible(false);
		_cancel = AppIcons.Button("x", Strings.AuthCancelLogin, CancelLogin);
		_done = AppIcons.PrimaryButton("check", Strings.AuthDone, Hide).IsVisible(false);
		_pump = new DispatcherTimer(TimeSpan.FromMilliseconds(120));
		_pump.Tick += Pump;
		var titleRow = new Grid().Columns("*,Auto").Children(
			_title.Column(0),
			AppIcons.Button("x", Strings.AuthClose, Hide).Column(1));
		var footer = new Grid().Columns("Auto,*,Auto").Children(
			_retry.Column(0),
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
				.HorizontalAlignment(HorizontalAlignment.Right).Children(_cancel, _done).Column(2));
		var body = new ScrollViewer().SmoothScrolling().AutoVerticalScroll().NoHorizontalScroll().MaxHeight(520)
			.Content(new StackPanel().Spacing(AppTheme.Space3).Children(
				new TextBlock().Text(_provider.Description).FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap),
				_status,
				_authCard,
				_promptCard,
				new StackPanel().Spacing(4).Children(
					Label(Strings.AuthLogTitle).FontSize(AppTheme.FontSizeMicro).FontWeight(FontWeight.Bold).SubtleText(),
					_logScroll)));
		var card = new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3)).Child(new DockPanel().LastChildFill()
				.Spacing(AppTheme.Space2).Children(
					new StackPanel().Spacing(4).Children(titleRow,
						new TextBlock().Text(Strings.AuthLoginSubtitle).FontSize(AppTheme.FontSizeMicro)
							.SubtleText().TextWrapping(TextWrapping.Wrap)).DockTop(),
					new Border().Height(1).DockTop()
						.WithTheme((theme, border) => border.Background = AppTheme.For(theme).BorderSubtle),
					footer.DockBottom(),
					body));
		_view = new Grid().IsVisible(false).Focusable(true).Children(
			new Button().Background(new Color(0x60, 0x00, 0x00, 0x00)).Cursor(CursorType.Arrow).Focusable(false)
				.BorderThickness(0).CornerRadius(0).OnClick(Hide),
			new AuthDialogSurface().MaxWidth(640).HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center).Margin(new Thickness(AppTheme.Space3)).Content(card))
			.OnKeyDown(key => { if (key.Key == Key.Escape) { key.Handled = true; Hide(); } });
	}

	public UIElement View => _view ?? throw new InvalidOperationException("ProviderLoginPanel is constructed eagerly.");
	public event Action<AuthProvider>? Completed;
	public event Action? Closed;

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		Hide();
		_window = window;
		if (View is FrameworkElement element)
		{
			element.Margin = window is MainWindow main
				? MainWindow.OverlayInsetFor(main.WindowState)
				: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		}
		window.OverlayLayer.Add(View);
		View.IsVisible(true);
		View.Focus();
		_pump.Start();
		StartLogin();
		AppStyles.ApplyTextColors(View);
	}

	private void StartLogin()
	{
		_abort?.Cancel();
		_pending?.TrySetCanceled();
		_pending = null;
		ClearLog();
		_authCard.IsVisible(false);
		_promptCard.IsVisible(false);
		_promptInput.Text = string.Empty;
		_retry.IsVisible(false);
		_done.IsVisible(false);
		_cancel.IsVisible(true);
		SetStatus(Strings.AuthLoginStarting);
		_running = true;
		_cancelRequested = false;
		var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
		_abort = cancellation;
		var output = new AuthLogWriter(Append);
		var input = new AuthPromptReader(PromptAsync);
		_ = RunAsync(input, output, cancellation.Token, ++_generation);
	}

	private async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken, int generation)
	{
		try
		{
			await ProviderAuthService.LoginAsync(_provider, _agentDir, input, output, cancellationToken);
			if (generation == _generation) Post(LoginSucceeded);
		}
		catch (OperationCanceledException)
		{
			if (generation == _generation) Post(_cancelRequested ? LoginCanceled : () => LoginFailed(Strings.AuthLoginTimeout));
		}
		catch (Exception exception)
		{
			if (generation == _generation) Post(() => LoginFailed(exception.Message));
		}
	}

	private void LoginSucceeded()
	{
		_running = false;
		SetStatus(Strings.AuthLoginSuccess, success: true);
		_authCard.IsVisible(false);
		_promptCard.IsVisible(false);
		_cancel.IsVisible(false);
		_done.IsVisible(true);
		_retry.IsVisible(false);
		Completed?.Invoke(_provider);
	}

	private void LoginCanceled()
	{
		_running = false;
		SetStatus(Strings.AuthLoginCanceled);
		_authCard.IsVisible(false);
		_promptCard.IsVisible(false);
		_cancel.IsVisible(false);
		_done.IsVisible(true);
		_retry.IsVisible(true);
	}

	private void LoginFailed(string message)
	{
		_running = false;
		SetStatus(Strings.AuthLoginFailed + "：" + message, error: true);
		_promptCard.IsVisible(false);
		_cancel.IsVisible(false);
		_done.IsVisible(true);
		_retry.IsVisible(true);
	}

	private void CancelLogin()
	{
		_cancelRequested = true;
		_abort?.Cancel();
		_pending?.TrySetCanceled();
	}

	private void Append(string chunk)
	{
		lock (_logGate)
		{
			_log.Append(chunk);
			if (_log.Length > 32_000) _log.Remove(0, _log.Length - 16_000);
			_logDirty = true;
		}
	}

	private void Pump()
	{
		if (!_logDirty) return;
		string text;
		lock (_logGate)
		{
			_logDirty = false;
			text = _log.ToString();
		}
		_logText.Text(text.Length == 0 ? Strings.AuthLogEmpty : text);
		var (url, code) = AuthFlowParser.Parse(text);
		if (url is not null && _authUrl.Text != url) _authUrl.Text(url);
		_authUrl.IsVisible(url is not null);
		_copyUrl.IsVisible(url is not null);
		if (code is not null && _authCode.Text != code) _authCode.Text(code);
		_authCode.IsVisible(code is not null);
		_codeGroup.IsVisible(code is not null);
		_copyCode.IsVisible(code is not null);
		if (_running && (url is not null || code is not null))
		{
			_authCard.IsVisible(true);
			if (_status.Text == Strings.AuthLoginStarting) SetStatus(Strings.AuthLoginRunning);
		}
		if (_running && text.Contains("登录成功", StringComparison.Ordinal)) SetStatus(Strings.AuthLoginRefreshing);
		Post(() =>
		{
			var maximum = SmoothScroll.For(_logScroll).MaxVerticalOffset;
			if (maximum > 0) _logScroll.SetScrollOffsets(0, maximum);
		});
	}

	private Task<string?> PromptAsync(CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
		var label = LastLine();
		Post(() =>
		{
			_pending = completion;
			_promptLabel.Text(label.Length > 0 ? label : Strings.AuthPromptDefault);
			_promptInput.Text = string.Empty;
			_promptCard.IsVisible(true);
			_promptInput.Focus();
		});
		return AwaitPromptAsync();
		async Task<string?> AwaitPromptAsync()
		{
			try { return await completion.Task.ConfigureAwait(false); }
			finally
			{
				registration.Dispose();
				Post(() =>
				{
					if (ReferenceEquals(_pending, completion)) _pending = null;
					_promptCard.IsVisible(false);
				});
			}
		}
	}

	private void SubmitPrompt()
	{
		var pending = _pending;
		_pending = null;
		_promptCard.IsVisible(false);
		pending?.TrySetResult(_promptInput.Text);
	}

	private string LastLine()
	{
		string text;
		lock (_logGate) text = _log.ToString();
		var trimmed = text.TrimEnd();
		var index = trimmed.LastIndexOf('\n');
		return (index >= 0 ? trimmed[(index + 1)..] : trimmed).Trim();
	}

	private void OpenBrowser()
	{
		if (_authUrl.Text is not { Length: > 0 } url) return;
		if (!Shell.Open(url)) Toasts.Error(View, Strings.AuthOpenBrowserFailed);
	}

	private void Copy(string text)
	{
		if (text.Length == 0) return;
		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		var copied = clipboard?.TrySetText(text) == true;
		Toasts.Show(View, copied ? Strings.Copied : Strings.ClipboardUnavailable,
			copied ? ToastKind.Success : ToastKind.Error);
	}

	private void SetStatus(string text, bool success = false, bool error = false)
	{
		_status.Text(text);
		_status.Role(error ? AppStyles.RoleError : success ? AppStyles.RoleSuccess : AppStyles.RoleMuted);
	}

	private void ClearLog()
	{
		lock (_logGate)
		{
			_log.Clear();
			_logDirty = true;
		}
		_logText.Text(Strings.AuthLogEmpty);
		_authUrl.Text = string.Empty;
		_authCode.Text = string.Empty;
	}

	private void Post(Action action)
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread) action();
		else dispatcher.BeginInvoke(action);
	}

	public void Hide()
	{
		_generation++;
		_running = false;
		_abort?.Cancel();
		_abort?.Dispose();
		_abort = null;
		_pending?.TrySetCanceled();
		_pending = null;
		_pump.Stop();
		if (_view is null || !_view.IsVisible) return;
		_view.IsVisible(false);
		_window?.OverlayLayer.Remove(_view);
		_window = null;
		ClearLog();
		Closed?.Invoke();
	}

	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();
}

/// <summary>把 OAuth 流程的 TextWriter 输出送进面板日志（不落盘、不回显令牌）。</summary>
internal sealed class AuthLogWriter(Action<string> append) : TextWriter
{
	public override Encoding Encoding => Encoding.UTF8;

	public override void Write(char value) => append(value.ToString());

	public override void Write(string? value)
	{
		if (!string.IsNullOrEmpty(value)) append(value);
	}

	public override void WriteLine(string? value) => append((value ?? string.Empty) + "\n");

	public override void WriteLine() => append("\n");
}

/// <summary>把 OAuth 流程的 TextReader 输入接到面板的提问输入框。</summary>
internal sealed class AuthPromptReader(Func<CancellationToken, Task<string?>> read) : TextReader
{
	public override Task<string?> ReadLineAsync() => read(CancellationToken.None);

	public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
		new(read(cancellationToken));

	public override string? ReadLine() => read(CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>登录面板的外框：与用量弹窗同款，Grid 覆盖层不会把 MaxWidth 当成测量约束。</summary>
internal sealed class AuthDialogSurface : ContentControl
{
	protected override Size MeasureContent(Size availableSize)
	{
		var width = Math.Min(640, availableSize.Width);
		var measured = base.MeasureContent(new Size(width, availableSize.Height));
		return new Size(width, measured.Height);
	}
}
