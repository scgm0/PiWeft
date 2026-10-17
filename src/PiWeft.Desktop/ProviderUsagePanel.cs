using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// <summary>Per-tab balance query and configuration. Closing cancels the request and rejects late results.</summary>
internal sealed class ProviderUsagePanel(Func<Model> current, Func<string> cwd)
{
	private readonly StackPanel _body = new();
	private readonly TextBlock _result = new();
	private Button? _saveButton;
	private Action? _saveForm;
	private Window? _window;
	private CancellationTokenSource? _abort;
	private int _generation;
	private ConfigurationStore? _store;
	private ConfigurationDocument? _document;
	private Model? _model;
	private UsageQuerySpec? _spec;
	private UIElement? _view;
	public UIElement View => _view ??= BuildView();
	public event Action? Closed;
	public event Action? Saved;

	private UIElement BuildView()
	{
		_body.Margin(new Thickness(0, 0, AppTheme.Space3, 0));
		_saveButton = AppIcons.PrimaryButton("check", Strings.ConfigSave, () => _saveForm?.Invoke()).IsVisible(false);
		var details = new ScrollViewer().SmoothScrolling().Content(_body).AutoVerticalScroll().NoHorizontalScroll().MaxHeight(550);
		return new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0x60, 0x00, 0x00, 0x00)).Cursor(CursorType.Arrow).Focusable(false)
				.BorderThickness(0).CornerRadius(0).OnClick(Hide),
			new Border().Width(520).MaxWidth(520).HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(AppTheme.Space3)).StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd).Padding(new Thickness(AppTheme.Space3))
				.Child(new DockPanel().LastChildFill().Spacing(AppTheme.Space2).Children(
					new StackPanel().Spacing(AppTheme.Space2).Children(
						new DockPanel().LastChildFill().Children(AppIcons.Button("x", Strings.Cancel, Hide).DockRight(), Label(Strings.ProviderUsage)),
						_result.FontSize(AppTheme.FontSizeCaption).PrimaryText().TextWrapping(TextWrapping.Wrap)).DockTop(),
					_saveButton.DockBottom(), details)))
			.OnKeyDown(key => { if (key.Key == Key.Escape) { key.Handled = true; Hide(); } });
	}

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		Hide();
		_window = window;
		if (View is FrameworkElement element)
		{
			element.Margin = window is MainWindow ? MainWindow.OverlayInset : new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		}
		window.OverlayLayer.Add(View);
		View.IsVisible(true);
		try
		{
			_model = current();
			_store = new ConfigurationStore(SessionPaths.GetAgentDir(), cwd());
			_document = _store.Read(ConfigFile.Usage);
			_spec = ProviderUsageService.ReadSpec(_model, _document.Parse());
			BuildForm();
			_ = QueryAsync();
		}
		catch
		{
			ClearBody();
			_saveForm = null;
			_saveButton?.IsVisible(false);
			_result.Text(Strings.UsageInvalidConfig);
			_body.Children(ActionButton(Strings.UsageAutomatic, () => Save(null)),
				AppIcons.Button("folder-open", Strings.ConfigOpenFile, () =>
				{
					if (_document is { } document && File.Exists(document.Path)) Shell.OpenInEditor(document.Path);
				}));
		}
		AppStyles.ApplyTextColors(View);
	}

	private void BuildForm()
	{
		ClearBody();
		if (_model is null || _spec is null) return;
		_body.Children(Label(_model.Provider),
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(
				AppIcons.Button("refresh-cw", Strings.UsageRefresh, () => _ = QueryAsync()),
				ActionButton(Strings.UsageAutomatic, () => Save(null)),
				ActionButton(Strings.UsageNewApi, () => { _spec = ProviderUsageService.NewApi("", ""); BuildForm(); })));
		var path = Input(_spec.Path);
		var value = Input(_spec.ValuePath);
		var total = Input(_spec.TotalPath);
		var used = Input(_spec.UsedPath);
		var currency = Input(_spec.Currency);
		var currencyPath = Input(_spec.CurrencyPath);
		var scale = Input(_spec.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
		var user = Input(_spec.UserId);
		var token = new PasswordBox().Password(_spec.Token);
		var cookie = new PasswordBox().Password(_spec.Cookie);
		var root = new CheckBox().Content(Label(Strings.UsageRootPath)).IsChecked(_spec.RootPath);
		_body.Children(Field(Strings.UsageEndpoint, path), root,
			Field(Strings.UsageValuePath, value), Field(Strings.UsageTotalPath, total), Field(Strings.UsageUsedPath, used),
			Field(Strings.UsageCurrency, currency), Field(Strings.UsageCurrencyPath, currencyPath), Field(Strings.UsageScale, scale),
			Field(Strings.UsageToken, token), Field(Strings.UsageCookie, cookie), Field(Strings.UsageUserId, user));
		_saveForm = () =>
		{
			if (!decimal.TryParse(scale.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var divisor))
			{ _result.Text(Strings.UsageInvalidConfig); return; }
			Save(new UsageQuerySpec(path.Text.Trim(), value.Text.Trim(), currencyPath.Text.Trim(), currency.Text.Trim(),
				total.Text.Trim(), used.Text.Trim(), divisor, root.IsChecked == true, token.Password, cookie.Password, user.Text.Trim()));
		};
		_saveButton?.IsVisible(true);
		AppStyles.ApplyTextColors(View);
		path.Focus();
	}

	private void Save(UsageQuerySpec? spec)
	{
		try
		{
			if (_model is null || _document is null || _store is null) return;
			if (spec is not null) ProviderUsageService.Validate(_model, spec);
			var root = ProviderUsageService.SaveSpec(_document.Parse(), _model.Provider, spec);
			_document = _store.Save(_document, root.ToJsonString(ConfigurationStore.WriteOptions));
			Saved?.Invoke();
			_spec = spec ?? ProviderUsageService.BuiltIn(_model);
			BuildForm();
			_ = QueryAsync();
		}
		catch (ConfigurationConflictException) { _result.Text(Strings.ConfigConflict); }
		catch { _result.Text(Strings.UsageInvalidConfig); }
	}

	private async Task QueryAsync()
	{
		if (_model is not { } model || _spec is not { } spec) return;
		_abort?.Cancel();
		var version = ++_generation;
		var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		_abort = cancellation;
		_result.Text(Strings.UsageLoading);
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		var project = cwd();
		string result;
		try
		{
			result = await Task.Run(async () =>
			{
				var key = ProviderUsageService.ResolveKey(model, project);
				var balance = await new ProviderUsageService(ProviderUsageService.Client).QueryAsync(model, spec, key, cancellation.Token);
				return balance.Display() + $" · {DateTime.Now:HH:mm:ss}";
			});
		}
		catch (OperationCanceledException) { result = Strings.UsageTimeout; }
		catch (InvalidOperationException ex) { result = ex.Message is var message &&
			(message == Strings.UsageMissingKey || message == Strings.UsageUnavailable || message == Strings.UsageResponseTooLarge ||
			message.StartsWith(Strings.UsageHttpPrefix, StringComparison.Ordinal)) ? message : Strings.UsageUnavailable; }
		catch { result = Strings.UsageUnavailable; }
		void Complete()
		{
			cancellation.Dispose();
			if (version != _generation || !View.IsVisible) return;
			_abort = null;
			_result.Text(result);
		}
		if (dispatcher is null || dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	public void Hide()
	{
		_generation++;
		_abort?.Cancel();
		_abort = null;
		if (_view is null || !_view.IsVisible) return;
		_view.IsVisible(false);
		_window?.OverlayLayer.Remove(_view);
		_window = null;
		ClearBody();
		_model = null;
		_spec = null;
		_document = null;
		_saveForm = null;
		Closed?.Invoke();
	}

	private void ClearBody()
	{
		foreach (var child in _body.Children.ToArray()) UiLifetime.Release(child);
		_body.Clear();
	}

	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();
	private static TextBox Input(string text) => new TextBox().PrimarySelection().Text(text).MinWidth(240);
	private static Element Field(string title, UIElement input) => new StackPanel().Spacing(4).Children(Label(title), input);
	private static Button ActionButton(string title, Action action) => new Button().Content(Label(title)).OnClick(action);
}
