using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// <summary>Balance section in session statistics. Queries only while open; stale responses cannot change providers.</summary>
internal sealed class ProviderUsageSummary
{
	private readonly Func<Model> _current;
	private readonly Func<string> _cwd;
	private readonly Func<Model, string, CancellationToken, Task<ProviderBalance>> _query;
	private readonly TextBlock _status = new();
	private readonly TextBlock _value = new();
	private readonly TextBlock _time = new();
	private readonly Button _refresh;
	private readonly Button _configure;
	private CancellationTokenSource? _abort;
	private (string Provider, string BaseUrl, string Cwd)? _key;
	private ProviderBalance? _balance;
	private DateTimeOffset _updated;
	private bool _open;
	private int _generation;

	public ProviderUsageSummary(Func<Model> current, Func<string> cwd, Action configure,
		Func<Model, string, CancellationToken, Task<ProviderBalance>>? query = null)
	{
		_current = current;
		_cwd = cwd;
		_query = query ?? Query;
		_refresh = AppIcons.Button("refresh-cw", Strings.UsageRefresh, () => _ = RefreshAsync());
		_configure = AppIcons.Button("settings", Strings.UsageConfigure, configure);
		View = new Border().WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(new Thickness(0, 1, 0, 0))
			.Padding(new Thickness(0, AppTheme.Space2, 0, 0)).Child(new StackPanel().Spacing(4).Children(
				new DockPanel().LastChildFill().Children(_refresh.DockRight(), _configure.DockRight(),
					new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
						new TextBlock().Text(Strings.ProviderUsage).FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Bold).SubtleText(),
						_time.FontSize(AppTheme.FontSizeMicro).SubtleText())),
				_status.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap),
				_value.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Bold).PrimaryText()
					.TextWrapping(TextWrapping.Wrap).IsVisible(false)));
		_value.WithTheme((theme, text) => text.Foreground = _balance?.Remaining is < 0 ? AppTheme.For(theme).Error : AppTheme.For(theme).Success);
		_value.Tag = null;
	}

	public Border View { get; }

	public void Show()
	{
		var model = _current();
		var key = (model.Provider, model.BaseUrl, _cwd());
		if (_open && _key == key) return;
		_open = true;
		if (_key != key)
		{
			Cancel();
			_key = key;
			_balance = null;
			_updated = default;
			_value.Text(string.Empty).IsVisible(false);
			_time.Text(string.Empty);
		}
		if (_abort is null && (_balance is null || DateTimeOffset.UtcNow - _updated >= TimeSpan.FromMinutes(1)))
			_ = RefreshAsync();
	}

	public void Hide() { _open = false; Cancel(); }
	public void Invalidate() { _balance = null; _updated = default; }

	public async Task RefreshAsync()
	{
		if (!_open) return;
		Cancel();
		var model = _current();
		var cwd = _cwd();
		_key = (model.Provider, model.BaseUrl, cwd);
		var version = _generation;
		var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		_abort = cancellation;
		_refresh.IsEnabled = false;
		_status.Text(Strings.UsageLoading);
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		ProviderBalance? balance = null;
		string? error = null;
		try { balance = await Task.Run(() => _query(model, cwd, cancellation.Token)).ConfigureAwait(false); }
		catch (OperationCanceledException) { error = Strings.UsageTimeout; }
		catch (Exception ex) { error = ErrorText(ex); }
		void Complete()
		{
			cancellation.Dispose();
			if (!_open || version != _generation) return;
			_abort = null;
			_refresh.IsEnabled = true;
			if (balance is not null)
			{
				_balance = balance;
				_updated = DateTimeOffset.UtcNow;
				_status.Text(model.Provider);
				_value.Text(balance.Display()).IsVisible(true);
				_value.Foreground(balance.Remaining is < 0 ? AppTheme.Error : AppTheme.Success);
				_time.Text(_updated.ToLocalTime().ToString("HH:mm:ss"));
			}
			else _status.Text(error ?? Strings.UsageUnavailable);
		}
		if (dispatcher is null || dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	private void Cancel()
	{
		_generation++;
		_abort?.Cancel();
		_abort = null;
		_refresh.IsEnabled = true;
	}

	private static Task<ProviderBalance> Query(Model model, string cwd, CancellationToken cancellation)
	{
		var root = new ConfigurationStore(SessionPaths.GetAgentDir(), cwd).Read(ConfigFile.Usage).Parse();
		var spec = ProviderUsageService.ReadSpec(model, root);
		return new ProviderUsageService(ProviderUsageService.Client).QueryAsync(model, spec,
			ProviderUsageService.ResolveKey(model, cwd), cancellation);
	}

	internal static string ErrorText(Exception error) => error switch
	{
		InvalidOperationException ex when ex.Message == Strings.UsageMissingKey || ex.Message == Strings.UsageUnavailable ||
			ex.Message == Strings.UsageResponseTooLarge || ex.Message.StartsWith(Strings.UsageHttpPrefix, StringComparison.Ordinal) => ex.Message,
		ArgumentException or System.Text.Json.JsonException or System.IO.InvalidDataException => Strings.UsageInvalidConfig,
		_ => Strings.UsageUnavailable,
	};
}
