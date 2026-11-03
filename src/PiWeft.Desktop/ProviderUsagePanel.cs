using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// <summary>
/// 用量查询配置弹窗（对齐 PiDeck 的 UsageProbeConfigDialog）：
/// 启用开关 + 预设模板 pills + 模板字段 + 超时/间隔 + 测试（带排查明细）+ 保存。
/// 无字段级自定义：旧版 probes 数组仍可读；声明式模板只保留 PiDeck 的四个。
/// </summary>
internal sealed class ProviderUsagePanel(Func<Model> current, Func<string> cwd)
{
	private readonly StackPanel _body = new();
	private readonly TextBlock _heading = new();
	private readonly TextBlock _status = new();
	private readonly TextBlock _testStatus = new();
	private readonly TextBlock _detail = new();
	private ScrollViewer _detailHost = null!;
	private readonly TextBlock _hint = new();
	private readonly TextBlock _notice = new();
	private readonly WrapPanel _pills = new();
	private readonly StackPanel _baseUrlGroup = new();
	private readonly StackPanel _generalGroup = new();
	private readonly StackPanel _newApiGroup = new();
	private readonly StackPanel _cookieGroup = new();
	private readonly ToggleSwitch _enabled = new();
	private readonly TextBox _baseUrl = new();
	private readonly TextBox _apiKey = new();
	private readonly PasswordBox _newApiToken = new();
	private readonly TextBox _userId = new();
	private readonly PasswordBox _cookieValue = new();
	private readonly TextBox _cookiePath = new();
	private readonly TextBox _valuePath = new();
	private readonly TextBox _currencyPath = new();
	private readonly TextBox _timeout = new();
	private readonly TextBox _interval = new();
	private Button _testButton = null!;
	private Button _saveButton = null!;
	private readonly Dictionary<string, Button> _templateButtons = new(StringComparer.Ordinal);
	private string _template = string.Empty;
	private Window? _window;
	private CancellationTokenSource? _abort;
	private int _generation;
	private ConfigurationStore? _store;
	private ConfigurationDocument? _document;
	private Model? _model;
	private UsageProbeProviderConfig? _config;
	private UsageProbeRecognition? _recognized;
	private UsageProbeCandidate? _legacy;
	private UsageProbeDocument? _loaded;
	private UIElement? _view;

	public UIElement View => _view ??= BuildView();
	public event Action? Closed;
	public event Action? Saved;
	public event Action<ProviderUsageResult>? ResultReady;

	private UIElement BuildView()
	{
		_body.Spacing(AppTheme.Space3);
		_detail.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);
		_detailHost = new ScrollViewer().AutoVerticalScroll().NoHorizontalScroll().MaxHeight(150)
			.Content(new Border().StyleName(AppStyles.CardMuted).CornerRadius(AppTheme.RadiusSm)
				.Padding(new Thickness(8)).Child(_detail))
			.IsVisible(false);
		_notice.FontSize(AppTheme.FontSizeCaption).PrimaryText().TextWrapping(TextWrapping.Wrap).IsVisible(false);
		_status.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap);
		_testStatus.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap).IsVisible(false);
		_hint.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);
		_enabled.IsChecked(false);
		_pills.Spacing(6);
		_timeout.Text(UsageProbeStore.DefaultTimeoutSeconds.ToString());
		_interval.Text(UsageProbeStore.DefaultIntervalMinutes.ToString());

		_baseUrlGroup.Children(Field(Strings.UsageBaseUrl, Input(_baseUrl, Strings.UsageBaseUrlPlaceholder)));
		_generalGroup.Children(Field(Strings.UsageApiKey, Input(_apiKey, Strings.UsageApiKeyPlaceholder)));
		_newApiGroup.Children(
			Field(Strings.UsageAccessToken, _newApiToken.PrimarySelection().Placeholder(Strings.UsageAccessTokenPlaceholder)),
			Field(Strings.UsageNewApiUserId, Input(_userId, Strings.UsageNewApiUserIdPlaceholder)));
		_cookieGroup.Children(
			Field(Strings.UsageCookieLabel, _cookieValue.PrimarySelection().Placeholder(Strings.UsageCookiePlaceholder)),
			Field(Strings.UsageCookiePath, Input(_cookiePath, Strings.UsageCookiePathPlaceholder)),
			Field(Strings.UsageValuePath, Input(_valuePath, Strings.UsageValuePathPlaceholder)),
			Field(Strings.UsageCurrencyPath, Input(_currencyPath, Strings.UsageCurrencyPathPlaceholder)));

		_testButton = AppIcons.Button("play", Strings.UsageTest, () => _ = TestAsync());
		_saveButton = AppIcons.PrimaryButton("check", Strings.UsageSave, Save);
		var bottom = new Grid().Columns("Auto,*,Auto").Children(
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(
				AppIcons.Button("folder-open", Strings.UsageOpenFile, OpenFile), _testButton).Column(0),
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
				.HorizontalAlignment(HorizontalAlignment.Right).Children(
					new Button().Content(Label(Strings.UsageCancel)).OnClick(Hide),
					_saveButton).Column(2));

		var scroll = new ScrollViewer().SmoothScrolling().Content(_body)
			.AutoVerticalScroll().NoHorizontalScroll().MaxHeight(560);
		return new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0x60, 0x00, 0x00, 0x00)).Cursor(CursorType.Arrow).Focusable(false)
				.BorderThickness(0).CornerRadius(0).OnClick(Hide),
			new UsageDialogSurface().MaxWidth(560).HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center).Margin(new Thickness(AppTheme.Space3))
				.Content(new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
					.Padding(new Thickness(AppTheme.Space3))
					.Child(new DockPanel().LastChildFill().Spacing(AppTheme.Space2).Children(
						new StackPanel().Spacing(AppTheme.Space2).Children(
							new Grid().Columns("*,Auto").Children(
								_heading.Text(Strings.ProviderUsage).FontSize(AppTheme.FontSizeTitle)
									.FontWeight(FontWeight.Bold).PrimaryText().Column(0),
								AppIcons.Button("x", Strings.UsageClose, Hide).Column(1)),
							_status).DockTop(),
						new Border().Height(1).DockTop()
							.WithTheme((theme, border) => border.Background = AppTheme.For(theme).BorderSubtle),
						bottom.DockBottom(),
						new Border().Height(1).DockBottom()
							.WithTheme((theme, border) => border.Background = AppTheme.For(theme).BorderSubtle),
						scroll))))
			.OnKeyDown(key => { if (key.Key == Key.Escape) { key.Handled = true; Hide(); } });
	}

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
		try
		{
			_model = current();
			_heading.Text(Strings.ProviderUsage + " · " + _model.Provider);
			var agentDir = SessionPaths.GetAgentDir();
			_store = new ConfigurationStore(agentDir, cwd());
			_document = _store.Read(ConfigFile.Usage);
			_loaded = UsageProbeStore.Load(agentDir);
			_recognized = UsageProbeStore.Recognize(_model);
			var legacyErrors = new List<string>();
			_legacy = UsageProbeStore.LoadLegacyCandidate(agentDir, _model.Provider, legacyErrors);
			_config = _loaded.Providers.GetValueOrDefault(_model.Provider);
			// 旧版配置仍在生效（旧版原本就是自动查询）：弹窗按「已启用」展示并提示迁移。
			if (_config is null && _legacy is not null) _config = new UsageProbeProviderConfig(Enabled: true);
			_config ??= new UsageProbeProviderConfig();
			BuildForm();
			_detail.Text(string.Empty);
			_detailHost.IsVisible(false);
			_testStatus.IsVisible(false);
			if (_loaded.Errors.Count > 0) SetStatus(string.Join("\n", _loaded.Errors), error: true);
			else if (legacyErrors.Count > 0) SetStatus(string.Join("\n", legacyErrors), error: true);
			else if (_config is { Enabled: true }) _ = QueryAsync();
			else SetStatus(Strings.UsageDisabled);
		}
		catch (Exception)
		{
			ClearBody();
			_saveButton.IsVisible(false);
			SetStatus(Strings.UsageInvalidConfig, error: true);
			_body.Children(ActionButton(Strings.UsageOpenFile, OpenFile));
		}
		AppStyles.ApplyTextColors(View);
	}

	private void BuildForm()
	{
		ClearBody();
		if (_model is not { } model || _config is not { } config) return;
		_template = config.Template.Length > 0 ? config.Template : _recognized?.TemplateId ?? string.Empty;
		_enabled.IsChecked(config.Enabled);
		_baseUrl.Text(config.BaseUrl);
		_apiKey.Text(config.ApiKey);
		_newApiToken.Password = config.AccessToken;
		_userId.Text(config.UserId);
		_cookieValue.Password = config.Cookie;
		_cookiePath.Text(config.CookiePath);
		_valuePath.Text(config.ValuePath);
		_currencyPath.Text(config.CurrencyPath);
		_timeout.Text(config.TimeoutSecs.ToString());
		_interval.Text(config.IntervalMinutes.ToString());

		_templateButtons.Clear();
		_pills.Clear();
		_notice.IsVisible(false);
		_pills.Children(TemplatePill(string.Empty, Strings.UsageTemplateNone));
		if (_recognized is { } recognized)
			_pills.Children(TemplatePill(recognized.TemplateId, Strings.UsageTemplateBuiltin(recognized.Category)));
		else if (_template.Length > 0 && !UsageProbeStore.IsDeclarativeTemplate(_template))
			_pills.Children(TemplatePill(_template,
				Strings.UsageTemplateBuiltin(UsageProbes.TemplateCategories.GetValueOrDefault(_template, Strings.UsageCategoryBalance))));
		_pills.Children(
			TemplatePill("general", Strings.UsageTemplateGeneral),
			TemplatePill("newapi", Strings.UsageTemplateNewApi),
			TemplatePill("cookie", Strings.UsageTemplateCookie),
			TemplatePill("sub2api", Strings.UsageTemplateSub2Api));

		_body.Children(
			new Grid().Columns("*,Auto").Children(
				new TextBlock().Text(Strings.UsageEnable).FontSize(AppTheme.FontSizeControl).PrimaryText()
					.VerticalAlignment(VerticalAlignment.Center).Column(0),
				_enabled.VerticalAlignment(VerticalAlignment.Center).Column(1)),
			new StackPanel().Spacing(6).Children(
				Label(Strings.UsageTemplates).FontWeight(FontWeight.Bold),
				_pills,
				_hint),
			_notice,
			_baseUrlGroup,
			_generalGroup,
			_newApiGroup,
			_cookieGroup,
			new Grid().Columns("*,*").Spacing(12).Children(
				Field(Strings.UsageTimeoutSeconds, Input(_timeout, "10")).Column(0),
				Field(Strings.UsageIntervalMinutes, Input(_interval, "5")).Column(1)),
			new StackPanel().Spacing(6).Children(_testStatus, _detailHost));
		if (_legacy is not null && config.Template.Length == 0)
		{
			_notice.Text(Strings.UsageLegacyNotice).Role(AppStyles.RoleWarning);
			_notice.IsVisible(true);
		}
		_saveButton.IsVisible(_document?.Error is null);
		ApplyTemplateVisibility();
		AppStyles.ApplyTextColors(View);
	}

	private Button TemplatePill(string id, string title)
	{
		var button = new Button().Content(Label(title)).Height(26).Padding(new Thickness(10, 2, 10, 2))
			.StyleName(_template == id ? AppStyles.SelectedButton : AppStyles.GhostButton)
			.OnClick(() =>
			{
				_template = id;
				foreach (var (key, item) in _templateButtons)
					item.StyleName(key == _template ? AppStyles.SelectedButton : AppStyles.GhostButton);
				ApplyTemplateVisibility();
			});
		_templateButtons[id] = button;
		return button;
	}

	private void ApplyTemplateVisibility()
	{
		var declarative = UsageProbeStore.IsDeclarativeTemplate(_template);
		_baseUrlGroup.IsVisible(declarative);
		_generalGroup.IsVisible(_template == "general");
		_newApiGroup.IsVisible(_template == "newapi");
		_cookieGroup.IsVisible(_template == "cookie");
		_hint.Text(_template switch
		{
			"" => Strings.UsageHintNone,
			"general" => Strings.UsageHintGeneral,
			"newapi" => Strings.UsageHintNewApi,
			"cookie" => Strings.UsageHintCookie,
			"sub2api" => Strings.UsageHintSub2Api,
			_ => Strings.UsageHintBuiltin,
		});
	}

	/// <summary>读表单：只写当前模板相关的字段，切换模板不会把上一个模板的密钥写进配置。</summary>
	private UsageProbeProviderConfig ReadForm()
	{
		var template = UsageProbeStore.IsKnownTemplate(_template) ? _template : string.Empty;
		var config = new UsageProbeProviderConfig(
			Enabled: _enabled.IsChecked == true,
			Template: template,
			TimeoutSecs: int.TryParse(_timeout.Text.Trim(), out var timeout) ? timeout : UsageProbeStore.DefaultTimeoutSeconds,
			IntervalMinutes: int.TryParse(_interval.Text.Trim(), out var interval) ? interval : UsageProbeStore.DefaultIntervalMinutes);
		return template switch
		{
			"general" => config with { ApiKey = _apiKey.Text.Trim(), BaseUrl = _baseUrl.Text.Trim() },
			"newapi" => config with
			{
				AccessToken = _newApiToken.Password,
				UserId = _userId.Text.Trim(),
				BaseUrl = _baseUrl.Text.Trim(),
			},
			"cookie" => config with
			{
				Cookie = _cookieValue.Password,
				CookiePath = _cookiePath.Text.Trim(),
				ValuePath = _valuePath.Text.Trim(),
				CurrencyPath = _currencyPath.Text.Trim(),
				BaseUrl = _baseUrl.Text.Trim(),
			},
			"sub2api" => config with { BaseUrl = _baseUrl.Text.Trim() },
			// 自动识别没有表单字段，但配置里可能已有覆盖（旧版迁移或手写），保存时原样保留，
			// 避免「打开弹窗再保存」把可用的 key/地址清掉；内置模板则只写开关与频率。
			"" => config with { ApiKey = _config?.ApiKey ?? string.Empty, BaseUrl = _config?.BaseUrl ?? string.Empty },
			_ => config,
		};
	}

	private void Save()
	{
		try
		{
			if (_model is not { } model || _document is null || _store is null) return;
			var config = ReadForm();
			UsageProbeStore.Validate(config);
			if (UsageProbeStore.TemplateError(config) is { } templateError)
			{
				SetStatus(templateError, error: true);
				return;
			}
			var root = _document.Parse().DeepClone().AsObject();
			var providers = root["providers"] as JsonObject ?? new JsonObject();
			if (root["providers"] is null) root["providers"] = providers;
			providers[model.Provider] = UsageProbeStore.ToJson(config);
			_document = _store.Save(_document, root.ToJsonString(ConfigurationStore.WriteOptions));
			_config = config;
			_loaded = UsageProbeStore.Load(SessionPaths.GetAgentDir());
			Saved?.Invoke();
			Toasts.Success(View, Strings.UsageSavedToast);
			_testStatus.IsVisible(false);
			_detailHost.IsVisible(false);
			if (config.Enabled) _ = QueryAsync();
			else SetStatus(Strings.UsageDisabled);
		}
		catch (ConfigurationConflictException) { SetStatus(Strings.ConfigConflict, error: true); }
		catch (ArgumentException) { SetStatus(Strings.UsageInvalidConfig, error: true); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			SetStatus(Strings.ConfigFailed, error: true);
		}
	}

	private async Task TestAsync()
	{
		if (_model is not { } model) return;
		UsageProbeProviderConfig config;
		try
		{
			config = ReadForm();
			UsageProbeStore.Validate(config);
		}
		catch (ArgumentException) { SetStatus(Strings.UsageInvalidConfig, error: true); return; }
		_testButton.IsEnabled = false;
		_testStatus.Text(Strings.UsageTesting).IsVisible(true).Role(AppStyles.RoleMuted);
		_detailHost.IsVisible(false);
		var result = await RunAsync(token => ProviderUsageService.TestAsync(model, config, cwd(), token),
			TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSecs, 1, 300) + 5));
		_testButton.IsEnabled = true;
		if (result is null) return;
		if (result.Success)
		{
			_testStatus.Text(Strings.UsageTestSuccess + " · " + Summarize(result)).Role(AppStyles.RoleSuccess);
			ResultReady?.Invoke(result);
		}
		else
		{
			_testStatus.Text(Strings.UsageTestFailed + " · " + (result.Error ?? Strings.UsageUnavailable)).Role(AppStyles.RoleError);
			ShowDetail(result.Detail);
		}
	}

	private async Task QueryAsync()
	{
		if (_model is not { } model || _loaded is null) return;
		UsageProbeProviderConfig config;
		try { config = ReadForm(); }
		catch (ArgumentException) { SetStatus(Strings.UsageInvalidConfig, error: true); return; }
		var legacy = UsageProbeStore.LoadLegacyCandidate(SessionPaths.GetAgentDir(), model.Provider);
		SetStatus(Strings.UsageLoading);
		_detailHost.IsVisible(false);
		var result = await RunAsync(token => ProviderUsageService.QueryAsync(model, config, _loaded, legacy, cwd(), token),
			TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSecs, 1, 300) + 5));
		if (result is null) return;
		if (result.Success)
		{
			SetStatus(model.Provider + " · " + Summarize(result), success: true);
			ResultReady?.Invoke(result);
		}
		else if (result.Disabled) SetStatus(Strings.UsageDisabled);
		else
		{
			SetStatus(result.Error ?? Strings.UsageUnavailable, error: true);
			ShowDetail(result.Detail);
		}
	}

	private async Task<ProviderUsageResult?> RunAsync(Func<CancellationToken, Task<ProviderUsageResult>> action, TimeSpan timeout)
	{
		_abort?.Cancel();
		var version = ++_generation;
		var cancellation = new CancellationTokenSource(timeout);
		_abort = cancellation;
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		ProviderUsageResult result;
		try { result = await Task.Run(() => action(cancellation.Token), cancellation.Token); }
		catch (OperationCanceledException) { result = ProviderUsageResult.Failure(Strings.UsageTimeout); }
		catch (Exception ex) { result = ProviderUsageResult.Failure(ProviderUsageDisplay.ErrorText(ex)); }
		if (dispatcher is not null && !dispatcher.IsOnUIThread)
		{
			var completion = new TaskCompletionSource();
			dispatcher.BeginInvoke(() => completion.TrySetResult());
			await completion.Task;
		}
		cancellation.Dispose();
		// 迟到的结果不允许改已经关闭或重新查询的弹窗。
		if (version != _generation) return null;
		_abort = null;
		return result;
	}

	private void ShowDetail(string? detail)
	{
		_detail.Text(detail ?? string.Empty);
		_detailHost.IsVisible(!string.IsNullOrWhiteSpace(detail));
	}

	private void SetStatus(string text, bool error = false, bool success = false)
	{
		_status.Text(text);
		_status.Role(error ? AppStyles.RoleError : success ? AppStyles.RoleSuccess : AppStyles.RoleMuted);
	}

	private static string Summarize(ProviderUsageResult result)
	{
		var segments = ProviderUsageDisplay.Segments(result);
		var text = segments is null ? string.Empty : string.Join(" · ", segments.Select(segment => $"{segment.Label} {segment.Text}"));
		if (result.Booster is { } booster)
		{
			var boost = $"{Strings.UsageBoosterBalance} {ProviderUsageDisplay.Money(booster.Balance, booster.Currency)}";
			text = text.Length == 0 ? boost : text + " · " + boost;
		}
		return text.Length == 0 ? Strings.UsageTestSuccess : text;
	}

	private void OpenFile()
	{
		try
		{
			if (_document is not { } document) return;
			if (!File.Exists(document.Path))
			{
				// CreateNew 而不是 WriteAllText：并行创建/外部写入时不会把别人的内容覆盖掉。
				using var stream = new FileStream(document.Path, FileMode.CreateNew);
			}
			if (!Shell.OpenInEditor(document.Path)) Toasts.Error(View, Strings.ConfigFailed);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			Toasts.Error(View, Strings.ConfigFailed);
		}
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
		_heading.Text(Strings.ProviderUsage);
		_model = null;
		_config = null;
		_document = null;
		_loaded = null;
		_legacy = null;
		_recognized = null;
		Closed?.Invoke();
	}

	private void ClearBody()
	{
		foreach (var child in _body.Children.ToArray()) UiLifetime.Release(child);
		_body.Clear();
	}

	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();
	private static TextBox Input(TextBox box, string placeholder) =>
		box.PrimarySelection().Placeholder(placeholder).MinWidth(220);
	private static Element Field(string title, UIElement input) => new StackPanel().Spacing(4).Children(Label(title), input);
	private static Button ActionButton(string title, Action action) => new Button().Content(Label(title)).OnClick(action);
}

/// <summary>
/// 弹窗卡片的外框：Grid 覆盖层不会把 MaxWidth 当成测量约束（PiDeck 的 ModelSurface 同款问题），
/// 这里显式用「可用宽度与卡片宽度取小」测量，窄窗口下不会被裁掉，宽窗口下也不会拉通整屏。
/// </summary>
internal sealed class UsageDialogSurface : ContentControl
{
	protected override Size MeasureContent(Size availableSize)
	{
		var width = Math.Min(560, availableSize.Width);
		var measured = base.MeasureContent(new Size(width, availableSize.Height));
		return new Size(width, measured.Height);
	}
}
