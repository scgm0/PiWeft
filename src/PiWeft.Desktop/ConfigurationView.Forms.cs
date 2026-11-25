using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using System.Globalization;
using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

internal sealed partial class ConfigurationView
{
	private void BuildProviders()
	{
		_body.Clear();
		_saveForm = () => Save(_root);
		var providers = _root["providers"] as JsonObject ?? new JsonObject();

		var add = AppIcons.PrimaryButton("plus", Strings.ConfigAddProvider, () => EditProvider(null));
		var import = AppIcons.TextButton("folder-open", Strings.ConfigImport, () => _ = ImportAsync(), strong: false);
		var export = AppIcons.TextButton("copy", Strings.ConfigExport, () => _ = ExportAsync(), strong: false);
		if (providers.Count == 0)
		{
			// 空态也要能导入现成配置，不能只留一个新建按钮。
			var emptyActions = new StackPanel().Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2).Children(add, import);
			_body.Children(SettingsUi.Empty("package", Strings.ConfigNoProviders,
				Strings.ConfigNoProvidersHint, emptyActions));
			return;
		}

		var toolbar = new DockPanel().LastChildFill().Children(
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(import, export, add)
				.DockRight(),
			new TextBlock().Text(Strings.ConfigProviderCount(providers.Count))
				.FontSize(AppTheme.FontSizeCaption).MutedText().VerticalAlignment(VerticalAlignment.Center));
		_body.Children(toolbar);

		foreach (var (name, value) in providers)
		{
			if (value is not JsonObject provider) continue;
			var models = provider["models"] as JsonArray ?? new JsonArray();
			var api = ConfigurationModels.Text(provider, "api");
			var url = SafeUrl(ConfigurationModels.Text(provider, "baseUrl"));

			var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					SettingsUi.IconAction("pencil", Strings.ConfigEditProvider, () => EditProvider(name)),
					SettingsUi.IconAction("download", Strings.ConfigFetchModels, () => _ = FetchRemoteModelsAsync(name)),
					SettingsUi.IconAction("copy", Strings.ConfigDuplicate, () => Guard(() => DuplicateProvider(name))),
					SettingsUi.IconAction("trash", Strings.Delete, () => _ = DeleteAsync(name, () =>
					{
						_root["providers"]!.AsObject().Remove(name); SetDirty(true); BuildProviders();
					})));

			var modelRows = new List<UIElement>();
			for (var index = 0; index < models.Count; index++)
			{
				var at = index;
				var model = models[index];
				var id = ConfigurationModels.Text(model, "id");
				var heading = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(new TextBlock().Text(id).FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeCaption).PrimaryText());
				if (ConfigurationModels.Flag(model, "reasoning")) heading.Children(SettingsUi.Badge(Strings.ConfigReasoning));
				if (model?["input"] is JsonArray input && input.Any(value => value is JsonValue v && v.TryGetValue<string>(out var text) && text == "image"))
				{
					heading.Children(SettingsUi.Badge(Strings.ConfigVision));
				}

				var meta = new TextBlock()
					.Text(Strings.ConfigModelMeta(
						ConfigurationModels.Number(model, "contextWindow", 0),
						ConfigurationModels.Number(model, "maxTokens", 0)))
					.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).FaintText();
				var body = new StackPanel().Spacing(2).Children(heading, meta);
				var ops = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(
						SettingsUi.IconAction("pencil", Strings.ConfigEditModel, () => EditModel(name, at)),
						SettingsUi.IconAction("trash", Strings.Delete, () => _ = DeleteAsync(id, () =>
						{
							models.RemoveAt(at); SetDirty(true); BuildProviders();
						})));
				modelRows.Add(new Grid().Columns("*,auto").Spacing(AppTheme.Space3)
					.Padding(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2))
					.Children(body.Column(0), ops.Column(1)));
			}

			modelRows.Add(AppIcons.TextButton("plus", Strings.ConfigAddModel,
				() => EditModel(name, null), strong: false));

			var modelList = SettingsUi.Rows(modelRows);
			var expander = new Expander()
				.Header(new TextBlock().Text(Strings.ConfigModels).FontSize(AppTheme.FontSizeCaption).PrimaryText())
				.Content(modelList);
			var content = new StackPanel().Spacing(AppTheme.Space2).Children(expander);
			_body.Children(SettingsUi.Group("package", name, content,
				description: url.Length == 0 ? api : $"{api} · {url}",
				action: actions,
				meta: Strings.ConfigModelCount(models.Count)));
		}
	}

	internal void EditProvider(string? oldName)
	{
		var provider = oldName is not null ? _root["providers"]?[oldName] : null;
		var name = Input(oldName ?? "").IsReadOnly(oldName is not null).Width(360)
			.Placeholder(Strings.ConfigProviderNamePlaceholder);
		var url = Input(ConfigurationModels.Text(provider, "baseUrl")).Width(360)
			.Placeholder(Strings.ConfigBaseUrlPlaceholder);
		var api = new ComboBox().ItemsSource(ItemsView.Create(CliEnvironment.SupportedApis)).Width(240);
		var selected = Array.IndexOf(CliEnvironment.SupportedApis, ConfigurationModels.Text(provider, "api"));
		api.SelectedIndex = Math.Max(0, selected);
		api.SelectionChanged += _ => SetDirty(true);
		var key = new PasswordBox().Width(360).Placeholder(Strings.ConfigKeyPlaceholder);
		key.PasswordChanged += () => SetDirty(true);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(name, Strings.ConfigName);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(url, Strings.ConfigBaseUrl);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(api, Strings.ConfigApi);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(key, Strings.ConfigKey);
		_body.Clear();
		_body.Children(
			SettingsUi.Group("package", Strings.ConfigProviderInfo, SettingsUi.Rows([
				SettingsUi.Row(Strings.ConfigName, null, name),
				SettingsUi.Row(Strings.ConfigBaseUrl, null, url),
				SettingsUi.Row(Strings.ConfigApi, null, api),
				SettingsUi.Row(Strings.ConfigKey, oldName is null ? Strings.ConfigNewKeyHint : Strings.ConfigKeepKey, key),
			]), description: oldName is null ? Strings.ConfigAddProvider : Strings.ConfigEditProvider),
			FormFooter());
		_saveForm = () =>
		{
			var next = ConfigurationModels.UpsertProvider(_root, oldName, name.Text.Trim(), url.Text, CliEnvironment.SupportedApis[Math.Max(0, api.SelectedIndex)], key.Password);
			Save(next);
			key.Password = string.Empty;
			SetDirty(false);
		};
	}

	/// <summary>表单底部的返回动作：保存仍在弹窗标题栏，取消只负责回到列表。</summary>
	private UIElement FormFooter() =>
		new Grid().Columns("*,auto").Children(
			AppIcons.TextButton("arrow-left", Strings.Cancel, () => _ = CancelFormAsync()).Column(1));

	private async Task CancelFormAsync()
	{
		if (await CanDiscardAsync()) Load();
	}

	internal void EditModel(string providerName, int? index)
	{
		var model = index is { } at ? _root["providers"]?[providerName]?["models"]?[at] : null;
		var id = Input(ConfigurationModels.Text(model, "id")).Width(360).Placeholder(Strings.ConfigModelIdPlaceholder);
		var name = Input(ConfigurationModels.Text(model, "name")).Width(360).Placeholder(Strings.ConfigModelNamePlaceholder);
		var context = Input(ConfigurationModels.Number(model, "contextWindow", 128000).ToString(CultureInfo.InvariantCulture)).Width(180);
		var max = Input(ConfigurationModels.Number(model, "maxTokens", 8192).ToString(CultureInfo.InvariantCulture)).Width(180);
		var reasoning = SettingsUi.SwitchRow(Strings.ConfigReasoning, Strings.ConfigReasoningHint,
			ConfigurationModels.Flag(model, "reasoning"), _ => SetDirty(true), out var reasoningToggle);
		var vision = SettingsUi.SwitchRow(Strings.ConfigVision, Strings.ConfigVisionHint,
			model?["input"] is JsonArray input && input.Any(value => value is JsonValue v && v.TryGetValue<string>(out var text) && text == "image"),
			_ => SetDirty(true), out var visionToggle);
		var cost = model?["cost"] as JsonObject;
		var costInput = Input(ConfigurationModels.Decimal(cost, "input").ToString(CultureInfo.InvariantCulture)).Width(180);
		var costOutput = Input(ConfigurationModels.Decimal(cost, "output").ToString(CultureInfo.InvariantCulture)).Width(180);
		var costRead = Input(ConfigurationModels.Decimal(cost, "cacheRead").ToString(CultureInfo.InvariantCulture)).Width(180);
		var costWrite = Input(ConfigurationModels.Decimal(cost, "cacheWrite").ToString(CultureInfo.InvariantCulture)).Width(180);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(id, Strings.ConfigModelId);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(name, Strings.ConfigName);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(context, Strings.ConfigContext);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(max, Strings.ConfigMaxTokens);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(costInput, Strings.ConfigCostInput);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(costOutput, Strings.ConfigCostOutput);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(costRead, Strings.ConfigCostCacheRead);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(costWrite, Strings.ConfigCostCacheWrite);
		_body.Clear();
		_body.Children(
			SettingsUi.Group("wrench", Strings.ConfigModelInfo, SettingsUi.Rows([
				SettingsUi.Row(Strings.ConfigModelId, Strings.ConfigModelInfoHint, id),
				SettingsUi.Row(Strings.ConfigName, Strings.ConfigModelNameHint, name),
				SettingsUi.Row(Strings.ConfigContext, Strings.ConfigContextHint, context),
				SettingsUi.Row(Strings.ConfigMaxTokens, Strings.ConfigMaxTokensHint, max),
				reasoning,
				vision,
			]), description: $"{providerName} · {(index is null ? Strings.ConfigAddModel : Strings.ConfigEditModel)}"),
			SettingsUi.Group("chart-column", Strings.ConfigPricing, SettingsUi.Rows([
				SettingsUi.Row(Strings.ConfigCostInput, null, costInput),
				SettingsUi.Row(Strings.ConfigCostOutput, null, costOutput),
				SettingsUi.Row(Strings.ConfigCostCacheRead, null, costRead),
				SettingsUi.Row(Strings.ConfigCostCacheWrite, null, costWrite),
			]), description: Strings.ConfigPricingHint),
			FormFooter());
		_saveForm = () =>
		{
			if (!int.TryParse(context.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var contextNumber) ||
				!int.TryParse(max.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var maxNumber)) throw new ArgumentException(Strings.ConfigPositiveNumber);
			if (!TryCost(costInput.Text, out var costIn) || !TryCost(costOutput.Text, out var costOut) ||
				!TryCost(costRead.Text, out var costCacheRead) || !TryCost(costWrite.Text, out var costCacheWrite)) throw new ArgumentException(Strings.ConfigCostInvalid);
			var next = ConfigurationModels.UpsertModel(_root, providerName, index, id.Text, name.Text, contextNumber, maxNumber,
				reasoningToggle.IsChecked == true, visionToggle.IsChecked == true,
				new ModelCostValues(costIn, costOut, costCacheRead, costCacheWrite));
			Save(next);
		};
	}

	private static bool TryCost(string? text, out double value)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			value = 0;
			return true;
		}

		return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 && double.IsFinite(value);
	}

	private async Task FetchRemoteModelsAsync(string provider)
	{
		if (_root["providers"]?[provider] is not JsonObject entry)
		{
			return;
		}

		var api = ConfigurationModels.Text(entry, "api");
		if (!RemoteModelCatalog.Supports(api))
		{
			Toasts.Error(View, Strings.ConfigFetchUnsupported(api));
			return;
		}

		Toasts.Show(View, Strings.ConfigFetchingModels);
		try
		{
			var result = await RemoteModelCatalog
				.FetchAsync(api, ConfigurationModels.Text(entry, "baseUrl"), ConfigurationModels.Text(entry, "apiKey"))
				.ConfigureAwait(true);

			if (!result.Ok)
			{
				Toasts.Error(View, Strings.ConfigFetchFailed(result.Error ?? string.Empty));
				return;
			}

			var (next, added) = ConfigurationModels.AddMissingModels(_root, provider, result.Models);
			if (added > 0)
			{
				// 保存本身不再提示，改成一条说明拉到了几个模型。
				Save(next, notify: false);
				Toasts.Success(View, Strings.ConfigFetchDone(added, result.Models.Count));
			}
			else
			{
				Toasts.Show(View, Strings.ConfigFetchNoNew);
			}
		}
		catch (Exception ex)
		{
			Toasts.Error(View, Strings.ConfigFetchFailed(ex.Message));
		}
	}

	private void DuplicateProvider(string name)
	{
		var providers = _root["providers"]!.AsObject();
		var next = name + "-copy";
		for (var suffix = 2; providers.ContainsKey(next); suffix++) next = name + "-copy-" + suffix;
		providers[next] = providers[name]!.DeepClone();
		SetDirty(true);
		BuildProviders();
	}

	private string _authQuery = string.Empty;
	private IReadOnlyList<ProviderAuthStatus> _authStatuses = [];
	private StackPanel? _authList;
	private TextBlock? _authCount;
	private UIElement? _authHeader;
	private TextBlock? _authEmpty;
	private readonly List<AuthRowEntry> _authRows = [];

	private sealed record AuthRowEntry(UIElement Row, UIElement Separator, string SearchText);

	private void BuildAuth()
	{
		// 认证页直接写 auth.json，不走「草稿 + 顶部保存」；顶部的保存仍留给源文件编辑。
		// RefreshAuthAsync 会直接调用本方法，必须自己清空，不能在旧列表下面再叠一份。
		_body.Clear();
		_saveForm = null;
		_authStatuses = ProviderAuthService.Statuses(_store.AgentDir, _store.Cwd);
		_authList = new StackPanel().Spacing(0);
		_authCount = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText();

		var search = new TextBox().PrimarySelection().StyleName(AppStyles.TextInputLine)
			.Height(AppTheme.ControlHeightSm).FontSize(AppTheme.FontSizeCaption)
			.Placeholder(Strings.AuthKeySearch).MinWidth(220).Text(_authQuery);
		search.OnTextChanged(_ =>
		{
			_authQuery = search.Text;
			FillAuthList();
		});
		var content = new StackPanel().Spacing(AppTheme.Space2).Children(
			search,
			_authCount,
			_authList);
		_body.Children(SettingsUi.Group("key-round", Strings.AuthSubscriptionTitle, content,
			description: Strings.AuthSubscriptionHint,
			action: AppIcons.Button("refresh-cw", Strings.ConfigReload, () => Load())));

		// 行只建一次；搜索只切换可见性。42 行 × 每键重建整棵树是认证页最明显的性能雷点。
		_authRows.Clear();
		_authHeader = AuthHeader();
		_authList.Children(_authHeader);
		foreach (var status in _authStatuses)
		{
			var row = AuthRow(status);
			var separator = new Border().Height(1).WithTheme((theme, border) => border.Background = AppTheme.For(theme).Border);
			_authList.Children(row, separator);
			_authRows.Add(new AuthRowEntry(row, separator, AuthSearchText(status)));
		}

		_authEmpty = Hint(string.Empty);
		_authEmpty.IsVisible(false);
		_authList.Children(_authEmpty);
		FillAuthList();
	}

	private void FillAuthList()
	{
		if (_authList is null || _authCount is null) return;
		var query = _authQuery.Trim();
		var visible = 0;
		foreach (var entry in _authRows)
		{
			var match = query.Length == 0 || entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
			if (entry.Row.IsVisible != match) entry.Row.IsVisible(match);
			if (entry.Separator.IsVisible != match) entry.Separator.IsVisible(match);
			if (match) visible++;
		}

		var countText = Strings.AuthKeyCount(visible);
		if (_authCount.Text != countText) _authCount.Text(countText);
		if (_authHeader is { } header && header.IsVisible != (visible > 0)) header.IsVisible(visible > 0);
		if (_authEmpty is not null)
		{
			var emptyText = query.Length == 0 ? Strings.AuthKeyCountNone : Strings.AuthKeyNoMatch;
			if (_authEmpty.Text != emptyText) _authEmpty.Text(emptyText);
			if (_authEmpty.IsVisible != (visible == 0)) _authEmpty.IsVisible(visible == 0);
		}
	}

	private static string AuthSearchText(ProviderAuthStatus status) => string.Join(' ',
		new[]
		{
			status.Id,
			status.Name,
			status.OAuth?.Description,
			status.OAuth is null ? null : "OAuth",
			status.Key?.Label,
			status.Key is null ? null : Strings.AuthKeyMethod,
			status.OAuthStateText,
			status.KeyStateText,
		}.Concat(status.Key?.EnvironmentNames ?? [])
		 .Concat(status.Key?.Options.SelectMany(static option =>
			new[] { option.Label, option.Description }.Concat(option.Fields.SelectMany(static field => new[] { field.Label, field.Name }))) ?? []));

	private static UIElement AuthHeader() => new Border().Padding(new Thickness(0, 0, 0, 4)).Child(
		new Grid().Columns("240,*,Auto").Children(
			Micro(Strings.ConfigProvider).Column(0),
			Micro(Strings.AuthColumnMethod).Column(1),
			Micro(Strings.AuthColumnActions).Column(2).HorizontalAlignment(HorizontalAlignment.Right)));

	private UIElement AuthRow(ProviderAuthStatus status)
	{
		var identity = new StackPanel().Spacing(2).Children(
			Label(status.Name).FontWeight(FontWeight.Bold),
			new TextBlock().Text(status.BuiltIn ? status.Id : status.Id + " · " + Strings.AuthKeyCustomProvider)
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).SubtleText()
				.TextWrapping(TextWrapping.Wrap));

		var methods = new StackPanel().Spacing(3);
		if (status.OAuth is not null || status.OAuthConfigured)
		{
			methods.Children(MethodState(Strings.AuthOAuthMethod, status.OAuthStateText, status.OAuthConfigured, status.OAuthExpired));
		}
		if (status.Key is not null || status.KeyConfigured)
		{
			methods.Children(MethodState(Strings.AuthKeyMethod, status.KeyStateText, status.KeyConfigured, error: status.KeyError));
		}
		if (status.AccountId is { Length: > 0 } account) methods.Children(Micro(Strings.AuthAccount(account)));
		if (status.EnterpriseUrl is { Length: > 0 } enterprise) methods.Children(Micro(Strings.AuthEnterprise(enterprise)));
		if (status.BaseUrl is { Length: > 0 } baseUrl) methods.Children(Micro(Strings.AuthBaseUrl(baseUrl)));

		var actions = new WrapPanel().Spacing(2).VerticalAlignment(VerticalAlignment.Center);
		if (status.CanLoginOAuth)
		{
			var login = AppIcons.TextButton("key-round",
				status.OAuthConfigured ? Strings.AuthRelogin : Strings.AuthLogin,
				() => StartAuthLogin(status.OAuth!));
			login.IsEnabled = !_authBusy;
			actions.Children(login);
		}
		if (status.CanSetKey)
		{
			var hasMainKey = status.StoredKey is { HasKey: true } && status.Key?.DefaultOption.HasMainKey == true;
			var key = AppIcons.TextButton("key-round",
				hasMainKey ? Strings.AuthKeyChange
					: status.KeyStored ? Strings.AuthKeyEdit
					: Strings.AuthKeySet,
				() => OpenKeyPanel(status));
			key.IsEnabled = !_authBusy;
			actions.Children(key);
		}
		if (status.CanRefresh)
		{
			var refresh = AppIcons.Button("refresh-cw", Strings.AuthRefresh, () => _ = RefreshAuthAsync(status.OAuth!));
			refresh.IsEnabled = !_authBusy;
			actions.Children(refresh);
		}
		if (status.CanForget)
		{
			var forget = AppIcons.Button("trash",
				status.OAuthConfigured ? Strings.AuthLogout : Strings.AuthKeyClear,
				() => _ = ForgetAsync(status));
			forget.IsEnabled = !_authBusy;
			actions.Children(forget);
		}
		if (status.DocsUrl.Length > 0)
		{
			actions.Children(AppIcons.Button("external-link", Strings.AuthDocs, () => Shell.Open(status.DocsUrl)));
		}

		return new Border().Padding(new Thickness(0, AppTheme.Space2)).Child(
			new Grid().Columns("240,*,Auto").Children(
				identity.Column(0).VerticalAlignment(VerticalAlignment.Top),
				methods.Column(1).VerticalAlignment(VerticalAlignment.Top),
				actions.Column(2).HorizontalAlignment(HorizontalAlignment.Right)));
	}

	private static TextBlock MethodState(string label, string state, bool configured, bool error)
	{
		var block = new TextBlock().Text(label + " · " + state).FontSize(AppTheme.FontSizeMicro)
			.TextWrapping(TextWrapping.Wrap);
		return block.Role(error
			? AppStyles.RoleError
			: configured ? AppStyles.RoleSuccess : AppStyles.RoleSubtle);
	}

	private static TextBlock Micro(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap);

	private void OpenKeyPanel(ProviderAuthStatus status)
	{
		if (!EnsureAuthEditable() || status.Key is null) return;
		var panel = new ProviderKeyPanel(status, _store.AgentDir, (_, forgotten) =>
		{
			Load();
			AuthChanged?.Invoke();
			Toasts.Success(View, forgotten ? Strings.AuthKeyCleared(status.Name) : Strings.AuthKeySaved(status.Name));
		});
		panel.Show(View);
	}

	private bool EnsureAuthEditable()
	{
		if (!_dirty) return true;
		Toasts.Error(View, Strings.AuthSaveFirst);
		return false;
	}

	internal void StartAuthLoginForSmoke(string providerId)
	{
		if (ProviderAuth.Find(providerId) is { } provider) StartAuthLogin(provider);
	}

	private void StartAuthLogin(AuthProvider provider) => _ = StartAuthLoginAsync(provider);

	private async Task StartAuthLoginAsync(AuthProvider provider)
	{
		if (!EnsureAuthEditable()) return;
		if (ProviderAuthService.HasStoredKey(provider.Id, _store.AgentDir) &&
			!await MessageBox.ConfirmAsync(Strings.AuthLoginOverwriteKey(provider.Name))) return;
		var panel = new ProviderLoginPanel(provider, _store.AgentDir);
		var completedLogin = false;
		panel.Completed += completed =>
		{
			if (_dirty) return;
			completedLogin = true;
			Load();
			AuthChanged?.Invoke();
			Toasts.Success(View, Strings.AuthLoginDone(completed.Name));
		};
		// 失败/取消也可能已经写了半个凭据：关闭面板时重读 auth.json，让后续保存不会撞 revision。
		// 成功时 Completed 已经重读过，这里不再重复构建整页。
		panel.Closed += () =>
		{
			if (!completedLogin && !_dirty) Load();
		};
		panel.Show(View);
	}

	private async Task ForgetAsync(ProviderAuthStatus status)
	{
		var confirmation = status.OAuthConfigured
			? Strings.AuthLogoutConfirm(status.Name)
			: Strings.AuthKeyClearConfirm(status.Name);
		if (!await MessageBox.ConfirmAsync(confirmation)) return;
		try
		{
			if (!ProviderAuthService.Logout(status.Id, _store.AgentDir))
			{
				Toasts.Error(View, Strings.AuthLogoutFailed);
				return;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or CodexAuthException)
		{
			Toasts.Error(View, Strings.ConfigFailed);
			return;
		}

		Load();
		AuthChanged?.Invoke();
		Toasts.Success(View, status.OAuthConfigured ? Strings.AuthLogoutDone : Strings.AuthKeyCleared(status.Name));
	}

	private async Task RefreshAuthAsync(AuthProvider provider)
	{
		if (!EnsureAuthEditable()) return;
		_authBusy = true;
		BuildAuth();
		try
		{
			var refreshed = await ProviderAuthService.RefreshAsync(provider, _store.AgentDir, CancellationToken.None);
			Toasts.Show(View, refreshed ? Strings.AuthRefreshDone : Strings.AuthRefreshFailed,
				refreshed ? ToastKind.Success : ToastKind.Error);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			Toasts.Error(View, Strings.AuthRefreshFailed);
		}
		finally
		{
			_authBusy = false;
			Load();
			AuthChanged?.Invoke();
		}
	}

	private void BuildAgentSettings()
	{
		var provider = Input(ConfigurationModels.Text(_root, "defaultProvider"));
		var model = Input(ConfigurationModels.Text(_root, "defaultModel"));
		var levels = new[] { "", "off", "minimal", "low", "medium", "high", "xhigh", "max" };
		var thinkingLabels = levels.Select(level => level.Length == 0 ? Strings.ConfigInherit : Strings.ThinkingLevelName(level)).ToArray();
		var thinking = new ComboBox().ItemsSource(ItemsView.Create(thinkingLabels)).Width(180);
		thinking.SelectedIndex = Math.Max(0, Array.IndexOf(levels, ConfigurationModels.Text(_root, "defaultThinkingLevel")));
		var thinkingChanged = false;
		thinking.SelectionChanged += _ => { thinkingChanged = true; SetDirty(true); };
		var defaults = _project ? _store.Read(ConfigFile.Settings) : null;
		var global = defaults is { Error: null } ? defaults.Parse() : new JsonObject();
		var compact = SettingsUi.SwitchRow(Strings.ConfigAutoCompaction, null,
			ReadEnabled(_root, "compaction", ReadEnabled(global, "compaction", true)),
			_ => SetDirty(true), out var compactSwitch);
		var retry = SettingsUi.SwitchRow(Strings.ConfigAutoRetry, null,
			ReadEnabled(_root, "retry", ReadEnabled(global, "retry", true)),
			_ => SetDirty(true), out var retrySwitch);
		var showNoticesChanged = false;
		var showNotices = SettingsUi.SwitchRow(Strings.ConfigShowCacheMissNotices, Strings.ConfigShowCacheMissNoticesHint,
			ReadFlag(_root, "showCacheMissNotices", ReadFlag(global, "showCacheMissNotices", false)),
			_ => { showNoticesChanged = true; SetDirty(true); }, out var showNoticesSwitch);
		var retryMaxInput = Input(ReadInt(_root, "retry", "maxRetries", ReadInt(global, "retry", "maxRetries", 3))
			.ToString(CultureInfo.InvariantCulture)).Width(160);
		var retryDelayInput = Input(ReadInt(_root, "retry", "baseDelayMs", ReadInt(global, "retry", "baseDelayMs", 2000))
			.ToString(CultureInfo.InvariantCulture)).Width(180);
		var compactChanged = false;
		var retryChanged = false;
		var retryMaxChanged = false;
		var retryDelayChanged = false;
		compactSwitch.OnCheckedChanged(_ => compactChanged = true);
		var retryEnabled = retrySwitch.IsChecked == true;
		retryMaxInput.IsEnabled(retryEnabled);
		retryDelayInput.IsEnabled(retryEnabled);
		retrySwitch.OnCheckedChanged(_ =>
		{
			retryChanged = true;
			retryMaxInput.IsEnabled(retrySwitch.IsChecked == true);
			retryDelayInput.IsEnabled(retrySwitch.IsChecked == true);
		});
		retryMaxInput.OnTextChanged(_ => retryMaxChanged = true);
		retryDelayInput.OnTextChanged(_ => retryDelayChanged = true);

		// 被 settings.extensions 关掉的内置扩展也进目录，菜单里压暗并说明原因（会话构造时仍会剔除它们）。
		var globalSettings = _store.Read(ConfigFile.Settings);
		var projectSettings = _store.Read(ConfigFile.Settings, project: true);
		var rules = new PiWeft.CodingAgent.Extensions.ExtensionRules(
			globalSettings.Error is null ? ExtensionSettings.ReadEntries(globalSettings.Parse()) : [],
			projectSettings.Error is null ? ExtensionSettings.ReadEntries(projectSettings.Parse()) : []);

		// 工具与 Codemode：项目层以全局原始条目为基线回验，chips 显示的集合就是保存后运行时解析出的集合。
		var baseEntries = _project ? ConfigurationTools.ReadEntries(global) : null;
		var initialEntries = ConfigurationTools.ReadEntries(_root);
		IReadOnlyList<string>? entries = initialEntries;
		var chips = new WrapPanel().Spacing(6);
		var toolsStatus = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText()
			.VerticalAlignment(VerticalAlignment.Center);
		var disabledWarning = Hint(Strings.ConfigToolsAllDisabled).IsVisible(false)
			.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).Error);
		var blockedNote = Hint(string.Empty).IsVisible(false);
		Button addTool = null!;
		addTool = AppIcons.TextButton("plus", Strings.ConfigToolAdd, () => ShowToolMenu());
		var resetTools = AppIcons.TextButton("rotate-ccw",
			_project ? Strings.ConfigInheritGlobal : Strings.ConfigToolsReset, () =>
			{
				entries = null;
				SetDirty(true);
				RefreshTools();
			});
		var disableTools = AppIcons.TextButton("x", Strings.ConfigToolsDisableAll, () =>
		{
			entries = [];
			SetDirty(true);
			RefreshTools();
		});

		// codemode 子设置：只回写真正改过的键，未知嵌套字段保持原样。
		var codemode = _root["codemode"] as JsonObject;
		var modeText = ConfigurationModels.Text(codemode, "mode");
		var budget = codemode?["inlineBudget"] is JsonValue budgetNode && budgetNode.TryGetValue<int>(out var budgetNumber)
			? budgetNumber : (int?)null;
		var mode = new ComboBox().ItemsSource(ItemsView.Create(new[] { Strings.ConfigInherit, "on", "only" })).Width(180);
		mode.SelectedIndex = modeText switch { "on" => 1, "only" => 2, _ => 0 };
		var modeChanged = false;
		mode.SelectionChanged += _ => { modeChanged = true; SetDirty(true); };
		var budgetInput = Input(budget?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
		var budgetChanged = false;
		budgetInput.OnTextChanged(_ => { budgetChanged = true; SetDirty(true); });

		// 子代理配额：只回写真正改过的键，subagents 里未建模的字段保持原样。
		var activeInput = Input(ReadInt(_root, "subagents", "maxActive", ReadInt(global, "subagents", "maxActive", 4))
			.ToString(CultureInfo.InvariantCulture));
		var spawnsInput = Input(ReadInt(_root, "subagents", "maxSpawns", ReadInt(global, "subagents", "maxSpawns", 32))
			.ToString(CultureInfo.InvariantCulture));
		var writersInput = Input(ReadInt(_root, "subagents", "maxWriters", ReadInt(global, "subagents", "maxWriters", 0))
			.ToString(CultureInfo.InvariantCulture));
		var runTimeoutInput = Input(ReadInt(_root, "subagents", "runTimeoutMinutes", ReadInt(global, "subagents", "runTimeoutMinutes", 30))
			.ToString(CultureInfo.InvariantCulture));
		var maxTokensInput = Input(ReadInt(_root, "subagents", "maxTokens", ReadInt(global, "subagents", "maxTokens", 0))
			.ToString(CultureInfo.InvariantCulture));
		var verifyTimeoutInput = Input(ReadInt(_root, "subagents", "verifyTimeoutSeconds",
			ReadInt(global, "subagents", "verifyTimeoutSeconds", 600)).ToString(CultureInfo.InvariantCulture));
		var goalRoundsInput = Input(ReadInt(_root, "goal", "maxRounds",
			ReadInt(global, "goal", "maxRounds", GoalMode.DefaultMaxRounds)).ToString(CultureInfo.InvariantCulture));
		var goalTokensInput = Input(ReadInt(_root, "goal", "maxTokens",
			ReadInt(global, "goal", "maxTokens", 0)).ToString(CultureInfo.InvariantCulture));
		var openTabsChanged = false;
		var openTabs = SettingsUi.SwitchRow(Strings.ConfigSubagentsOpenTabs, Strings.ConfigSubagentsOpenTabsHint,
			ReadBool(_root, "subagents", "openTabs", ReadBool(global, "subagents", "openTabs", false)),
			_ => { openTabsChanged = true; SetDirty(true); }, out var openTabsSwitch);
		var teamChanged = false;
		var team = SettingsUi.SwitchRow(Strings.ConfigSubagentsTeam, Strings.ConfigSubagentsTeamHint,
			ReadBool(_root, "subagents", "team", ReadBool(global, "subagents", "team", false)),
			_ => { teamChanged = true; SetDirty(true); }, out var teamSwitch);
		var activeChanged = false;
		var spawnsChanged = false;
		var writersChanged = false;
		var runTimeoutChanged = false;
		var maxTokensChanged = false;
		var verifyTimeoutChanged = false;
		var goalRoundsChanged = false;
		var goalTokensChanged = false;
		activeInput.OnTextChanged(_ => { activeChanged = true; SetDirty(true); });
		spawnsInput.OnTextChanged(_ => { spawnsChanged = true; SetDirty(true); });
		writersInput.OnTextChanged(_ => { writersChanged = true; SetDirty(true); });
		runTimeoutInput.OnTextChanged(_ => { runTimeoutChanged = true; SetDirty(true); });
		maxTokensInput.OnTextChanged(_ => { maxTokensChanged = true; SetDirty(true); });
		verifyTimeoutInput.OnTextChanged(_ => { verifyTimeoutChanged = true; SetDirty(true); });
		goalRoundsInput.OnTextChanged(_ => { goalRoundsChanged = true; SetDirty(true); });
		goalTokensInput.OnTextChanged(_ => { goalTokensChanged = true; SetDirty(true); });

		provider.Width(320);
		model.Width(320);
		budgetInput.Width(180);
		activeInput.Width(160);
		spawnsInput.Width(160);
		writersInput.Width(160);
		runTimeoutInput.Width(160);
		maxTokensInput.Width(180);
		verifyTimeoutInput.Width(160);
		goalRoundsInput.Width(160);
		goalTokensInput.Width(180);
		var defaultFields = new List<UIElement>
		{
			Fields(Field(Strings.ConfigProvider, provider, 320), Field(Strings.ConfigModelId, model, 320)),
		};

		// 模型快捷选择用普通 ComboBox：它是原生弹层，不会占用窗口的 TransientOverlay 槽位。
		var chatModels = _models?.Invoke().Where(static item => item.Type == ModelType.Chat).ToList() ?? [];
		var modelOptions = new List<Model?> { null };
		modelOptions.AddRange(chatModels);
		var currentProvider = provider.Text.Trim();
		var currentModel = model.Text.Trim();
		if (currentProvider.Length > 0 && currentModel.Length > 0 &&
			!chatModels.Any(item => string.Equals(item.Provider, currentProvider, StringComparison.Ordinal) &&
				string.Equals(item.Id, currentModel, StringComparison.Ordinal)))
		{
			modelOptions.Add(new Model { Provider = currentProvider, Id = currentModel });
		}

		if (modelOptions.Count > 1)
		{
			var labels = modelOptions.Select(item => item is null
				? Strings.ConfigAutoSelect
				: item.Provider + "/" + item.Id).ToArray();
			var picker = new ComboBox().ItemsSource(ItemsView.Create(labels)).Width(420);
			var syncing = false;
			picker.SelectionChanged += _ =>
			{
				if (syncing) return;
				var selected = modelOptions[Math.Clamp(picker.SelectedIndex, 0, modelOptions.Count - 1)];
				syncing = true;
				provider.Text = selected?.Provider ?? string.Empty;
				model.Text = selected?.Id ?? string.Empty;
				syncing = false;
			};
			void SyncPicker()
			{
				if (syncing) return;
				var providerId = provider.Text.Trim();
				var modelId = model.Text.Trim();
				var index = modelOptions.FindIndex(item => item is null
					? providerId.Length == 0 && modelId.Length == 0
					: string.Equals(item.Provider, providerId, StringComparison.Ordinal) &&
						string.Equals(item.Id, modelId, StringComparison.Ordinal));
				if (picker.SelectedIndex == index) return;
				syncing = true;
				picker.SelectedIndex = index;
				syncing = false;
			}
			provider.OnTextChanged(_ => SyncPicker());
			model.OnTextChanged(_ => SyncPicker());
			SyncPicker();
			defaultFields.Clear();
			defaultFields.Add(SettingsUi.Field(Strings.ConfigPickModel, picker));
			defaultFields.Add(Fields(
				Field(Strings.ConfigProvider, provider, 320),
				Field(Strings.ConfigModelId, model, 320)));
		}

		var defaultsCard = FormCard("settings", Strings.ConfigDefaults, Strings.ConfigDefaultsHint, [.. defaultFields]);
		_body.Children(
			defaultsCard,
			FormCard("activity", Strings.ConfigBehavior, Strings.ConfigBehaviorHint,
				SubLabel(Strings.ConfigBehaviorModel),
				Field(Strings.Thinking, thinking),
				SubLabel(Strings.ConfigBehaviorAutomation),
				compact, retry,
				Fields(Field(Strings.ConfigRetryMaxRetries, retryMaxInput, 180),
					Field(Strings.ConfigRetryBaseDelay, retryDelayInput, 200)),
				Hint(Strings.ConfigRetryHint),
				SubLabel(Strings.ConfigBehaviorNotices),
				showNotices),
			FormCard("wrench", Strings.ConfigToolsTitle, Strings.ConfigToolsHint,
				SubLabel(Strings.ConfigToolsDefaults),
				Hint(Strings.ConfigDefaultToolsHint),
				chips,
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
					.VerticalAlignment(VerticalAlignment.Center).Children(addTool, resetTools, disableTools, toolsStatus),
				disabledWarning,
				blockedNote,
				SubLabel(Strings.ConfigToolsCodemode),
				Fields(Field(Strings.ConfigCodemodeMode, mode, 180),
					Field(Strings.ConfigCodemodeBudget, budgetInput, 180)),
				Hint(Strings.ConfigCodemodeModeHint),
				Hint(Strings.ConfigCodemodeBudgetHint)),
			FormCard("sparkles", Strings.ConfigSubagentsTitle, Strings.ConfigSubagentsHint,
				SubLabel(Strings.ConfigSubagentsConcurrency),
				Fields(Field(Strings.ConfigSubagentsMaxActive, activeInput, 180),
					Field(Strings.ConfigSubagentsMaxSpawns, spawnsInput, 180),
					Field(Strings.ConfigSubagentsMaxWriters, writersInput, 180)),
				Hint(Strings.ConfigSubagentsConcurrencyHint),
				SubLabel(Strings.ConfigSubagentsRuntime),
				Fields(Field(Strings.ConfigSubagentsRunTimeout, runTimeoutInput, 220),
					Field(Strings.ConfigSubagentsMaxTokens, maxTokensInput, 220),
					Field(Strings.ConfigSubagentsVerifyTimeout, verifyTimeoutInput, 220)),
				Hint(Strings.ConfigSubagentsRuntimeHint),
				SubLabel(Strings.ConfigSubagentsUi),
				openTabs,
				team),
			FormCard("target", Strings.ConfigGoalTitle, Strings.ConfigGoalHint,
				SubLabel(Strings.ConfigGoalBudget),
				Fields(
					Field(Strings.ConfigGoalMaxRounds, goalRoundsInput, 180),
					Field(Strings.ConfigGoalMaxTokens, goalTokensInput, 220)),
				Hint(Strings.ConfigGoalMaxRoundsHint),
				Hint(Strings.ConfigGoalMaxTokensHint)));
		RefreshTools();
		_saveForm = () =>
		{
			var next = _root.DeepClone().AsObject();
			SetOptional(next, "defaultProvider", provider.Text.Trim());
			SetOptional(next, "defaultModel", model.Text.Trim());
			if (thinkingChanged) SetOptional(next, "defaultThinkingLevel", levels[Math.Max(0, thinking.SelectedIndex)]);
			if (showNoticesChanged) next["showCacheMissNotices"] = showNoticesSwitch.IsChecked == true;
			if (compactChanged) SetEnabled(next, "compaction", compactSwitch.IsChecked == true);
			var retryOn = retrySwitch.IsChecked == true;
			// 自动重试关闭时，次数/延迟不参与保存与校验；否则先输错再取消勾选会卡在禁用输入上。
			if (retryChanged || (retryOn && (retryMaxChanged || retryDelayChanged)))
			{
				int? maxRetries = null;
				int? baseDelayMs = null;
				if (retryOn && retryMaxChanged)
				{
					if (!int.TryParse(retryMaxInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 20)
						throw new ArgumentException(Strings.ConfigRetryMaxRetriesInvalid);
					maxRetries = value;
				}
				if (retryOn && retryDelayChanged)
				{
					if (!int.TryParse(retryDelayInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 600_000)
						throw new ArgumentException(Strings.ConfigRetryBaseDelayInvalid);
					baseDelayMs = value;
				}
				// retry 里未建模的子键（例如上游的 retry.provider）必须保留。
				var section = next["retry"] as JsonObject ?? new JsonObject();
				if (retryChanged) section["enabled"] = retrySwitch.IsChecked == true;
				if (maxRetries is { } max) section["maxRetries"] = max;
				if (baseDelayMs is { } delay) section["baseDelayMs"] = delay;
				next["retry"] = section;
			}
			if (!ConfigurationTools.Same(initialEntries, entries))
			{
				if (entries is null) next.Remove("defaultTools");
				else
				{
					// 用数组构造器，不用 JsonArray.Add<T>：泛型 Add 在裁剪/AOT 下会触发 IL2026/IL3050。
					var nodes = new JsonNode?[entries.Count];
					for (var index = 0; index < entries.Count; index++) nodes[index] = JsonValue.Create(entries[index]);
					next["defaultTools"] = new JsonArray(nodes);
				}
			}
			if (modeChanged || budgetChanged)
			{
				int? parsedBudget = null;
				if (budgetChanged)
				{
					var raw = budgetInput.Text.Trim();
					if (raw.Length > 0)
					{
						if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
							throw new ArgumentException(Strings.ConfigCodemodeBudgetInvalid);
						parsedBudget = value;
					}
				}
				var patched = ConfigurationTools.PatchCodemode(_root["codemode"],
					modeChanged ? mode.SelectedIndex switch { 1 => "on", 2 => "only", _ => null } : null,
					parsedBudget, modeChanged, budgetChanged);
				if (patched is null) next.Remove("codemode"); else next["codemode"] = patched;
			}
			if (goalRoundsChanged || goalTokensChanged)
			{
				var section = next["goal"] as JsonObject ?? new JsonObject();
				if (goalRoundsChanged)
				{
					section["maxRounds"] = ParseLimit(goalRoundsInput, 1, 512, Strings.ConfigGoalMaxRoundsInvalid);
				}

				if (goalTokensChanged)
				{
					section["maxTokens"] = ParseLimit(goalTokensInput, 0, 10_000_000, Strings.ConfigGoalMaxTokensInvalid);
				}

				next["goal"] = section;
			}
			if (activeChanged || spawnsChanged || writersChanged || runTimeoutChanged || maxTokensChanged || verifyTimeoutChanged || openTabsChanged || teamChanged)
			{
				var section = next["subagents"] as JsonObject ?? new JsonObject();
				if (activeChanged) section["maxActive"] = ParseSubagentLimit(activeInput, 1, 32, Strings.ConfigSubagentsMaxActive);
				if (spawnsChanged) section["maxSpawns"] = ParseSubagentLimit(spawnsInput, 1, 512, Strings.ConfigSubagentsMaxSpawns);
				if (writersChanged) section["maxWriters"] = ParseSubagentLimit(writersInput, 0, 16, Strings.ConfigSubagentsMaxWriters);
				if (runTimeoutChanged) section["runTimeoutMinutes"] = ParseSubagentLimit(runTimeoutInput, 1, 1440, Strings.ConfigSubagentsRunTimeout);
				if (maxTokensChanged) section["maxTokens"] = ParseSubagentLimit(maxTokensInput, 0, 10_000_000, Strings.ConfigSubagentsMaxTokens);
				if (verifyTimeoutChanged) section["verifyTimeoutSeconds"] = ParseSubagentLimit(verifyTimeoutInput, 1, 86_400, Strings.ConfigSubagentsVerifyTimeout);
				if (openTabsChanged) section["openTabs"] = openTabsSwitch.IsChecked == true;
				if (teamChanged) section["team"] = teamSwitch.IsChecked == true;
				next["subagents"] = section;
			}
			Save(next);
		};

		// 生效集合与运行时同一套解析：默认集、纯名字、+/- 增量、显式空选择。
		void RefreshTools()
		{
			chips.Clear();
			var effective = ConfigurationTools.ResolveInLayer(baseEntries, entries);
			var blocked = ConfigurationTools.Unavailable(rules);
			foreach (var name in effective)
			{
				var tool = name;
				chips.Children(ToolChip(tool, () =>
				{
					entries = ConfigurationTools.SetEnabled(baseEntries, entries, tool, false);
					SetDirty(true);
					RefreshTools();
				}, disabled: blocked.Contains(tool)));
			}

			// 计数只算真会加载的工具；被扩展开关挡下的单独写一行原因，
			// 否则「已启用」列表里出现一个运行时不存在的工具，开关看起来像坏了。
			var active = effective.Count(name => !blocked.Contains(name));
			toolsStatus.Text(entries is not null
				? Strings.ConfigToolsEnabledCount(active)
				: _project ? Strings.ConfigInheritGlobal : Strings.ConfigToolsUsingDefault);
			var blockedNames = effective.Where(blocked.Contains).ToArray();
			blockedNote.Text(blockedNames.Length == 0 ? string.Empty : Strings.ConfigToolsBlockedNote(string.Join("、", blockedNames)));
			blockedNote.IsVisible(blockedNames.Length > 0);
			disabledWarning.IsVisible(ConfigurationTools.DisablesAll(entries));
		}

		void ShowToolMenu()
		{
			HoverHints.Cancel(addTool);
			_toolMenu ??= new ToolPickerMenu();
			_toolMenu.Show(addTool, new ToolPickerMenu.Options(
				ConfigurationTools.Catalog,
				ConfigurationTools.Unavailable(rules),
				name => ConfigurationTools.IsEnabled(baseEntries, entries, name),
				name => entries?.Contains("-" + name, StringComparer.Ordinal) == true,
				name =>
				{
					var enabled = ConfigurationTools.IsEnabled(baseEntries, entries, name);
					entries = ConfigurationTools.SetEnabled(baseEntries, entries, name, !enabled);
					SetDirty(true);
					RefreshTools();
				}));
		}

		_showToolMenu = ShowToolMenu;
	}

	/// 仅供 <c>--ui-smoke config-tools</c>：先把工具卡滚进视口，下一帧再摆出「添加工具」下拉。
	/// 刚建出来的控件没有 Bounds，直接开会按 (0,0) 摆放，截图只能看到弹层挂在窗口外。
	internal void OpenToolMenuForSmoke()
	{
		var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250));
		var step = 0;
		timer.Tick += () =>
		{
			step++;
			if (step == 1)
			{
				_scroll.SetScrollOffsets(0, double.MaxValue);
				return;
			}

			timer.Stop();
			timer.Dispose();
			_showToolMenu?.Invoke();
			Console.WriteLine("PiWeft: ui smoke 已打开工具下拉");
		};
		timer.Start();
	}

	private static bool ReadEnabled(JsonObject root, string key, bool fallback) =>
		root[key] is JsonObject obj && obj["enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : fallback;

	private static bool ReadFlag(JsonObject root, string key, bool fallback) =>
		root[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

	private static bool ReadBool(JsonObject root, string sectionKey, string key, bool fallback) =>
		root[sectionKey] is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

	private static int ReadInt(JsonObject root, string sectionKey, string key, int fallback) =>
		root[sectionKey] is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

	private static void SetEnabled(JsonObject root, string key, bool enabled)
	{
		var section = root[key] as JsonObject ?? new JsonObject();
		if (root[key] is null) root[key] = section;
		section["enabled"] = enabled;
	}

	private static void SetOptional(JsonObject root, string key, string value)
	{
		if (value.Length == 0) root.Remove(key); else root[key] = value;
	}

	/// 解析子代理配额：超范围直接报错，不悄悄改成边界值；报错带上字段名。
	private static int ParseSubagentLimit(TextBox input, int min, int max, string field) =>
		ParseLimit(input, min, max, Strings.ConfigSubagentsInvalid(field));

	private static int ParseLimit(TextBox input, int min, int max, string error)
	{
		if (!int.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
			value < min || value > max)
		{
			throw new ArgumentException(error);
		}

		return value;
	}

	private static string SafeUrl(string raw) => Uri.TryCreate(raw, UriKind.Absolute, out var uri)
		? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path) : string.Empty;
}
