using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
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
		_body.Children(new DockPanel().LastChildFill().Children(
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(
				AppIcons.Button("plus", Strings.ConfigAddProvider, () => EditProvider(null)),
				AppIcons.Button("folder-open", Strings.ConfigImport, () => _ = ImportAsync()),
				AppIcons.Button("copy", Strings.ConfigExport, () => _ = ExportAsync())).DockRight(),
			Label(Strings.ConfigProviderCount(providers.Count))));
		if (providers.Count == 0) _body.Children(Label(Strings.ConfigNoProviders));
		foreach (var (name, value) in providers)
		{
			if (value is not JsonObject provider) continue;
			var models = provider["models"] as JsonArray ?? new JsonArray();
			var details = new StackPanel().Spacing(AppTheme.Space2);
			details.Children(new DockPanel().LastChildFill().Children(
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(2).Children(
					AppIcons.Button("pencil", Strings.ConfigEditProvider, () => EditProvider(name)),
					AppIcons.Button("download", Strings.ConfigFetchModels, () => _ = FetchRemoteModelsAsync(name)),
					AppIcons.Button("copy", Strings.ConfigDuplicate, () => Guard(() => DuplicateProvider(name))),
					AppIcons.Button("trash", Strings.Delete, () => _ = DeleteAsync(name, () =>
					{
						_root["providers"]!.AsObject().Remove(name); SetDirty(true); BuildProviders();
					}))).DockRight(),
				Label($"{name} · {Strings.ConfigModelCount(models.Count)}").FontWeight(FontWeight.Bold)));
			details.Children(new TextBlock().Text($"{ConfigurationModels.Text(provider, "api")} · {SafeUrl(ConfigurationModels.Text(provider, "baseUrl"))}")
				.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap));
			var list = new StackPanel().Spacing(4);
			for (var index = 0; index < models.Count; index++)
			{
				var at = index;
				var model = models[index];
				var id = ConfigurationModels.Text(model, "id");
				list.Children(new DockPanel().LastChildFill().Children(
					AppIcons.Button("trash", Strings.Delete, () => _ = DeleteAsync(id, () =>
					{
						models.RemoveAt(at); SetDirty(true); BuildProviders();
					})).DockRight(),
					AppIcons.Button("pencil", Strings.ConfigEditModel, () => EditModel(name, at)).DockRight(),
					Label($"{id} · {ConfigurationModels.Number(model, "contextWindow", 0):N0} · {ConfigurationModels.Number(model, "maxTokens", 0):N0}")));
			}
			list.Children(AppIcons.Button("plus", Strings.ConfigAddModel, () => EditModel(name, null)).HorizontalAlignment(HorizontalAlignment.Left));
			details.Children(new Expander().Header(Label(Strings.ConfigModels)).Content(list));
			_body.Children(new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd).Padding(new Thickness(AppTheme.Space3)).Child(details));
		}
	}

	internal void EditProvider(string? oldName)
	{
		var provider = oldName is not null ? _root["providers"]?[oldName] : null;
		var name = Input(oldName ?? "").IsReadOnly(oldName is not null);
		var url = Input(ConfigurationModels.Text(provider, "baseUrl"));
		var api = new ComboBox().ItemsSource(ItemsView.Create(CliEnvironment.SupportedApis)).MinWidth(240);
		var selected = Array.IndexOf(CliEnvironment.SupportedApis, ConfigurationModels.Text(provider, "api"));
		api.SelectedIndex = Math.Max(0, selected);
		api.SelectionChanged += _ => SetDirty(true);
		var key = new PasswordBox().MinWidth(240);
		key.PasswordChanged += () => SetDirty(true);
		_body.Clear();
		_body.Children(Label(oldName is null ? Strings.ConfigAddProvider : Strings.ConfigEditProvider).FontWeight(FontWeight.Bold),
			Field(Strings.ConfigName, name), Field(Strings.ConfigBaseUrl, url), Field(Strings.ConfigApi, api),
			Field(Strings.ConfigKey + " · " + Strings.ConfigKeepKey, key),
			ActionButton(Strings.Cancel, () => _ = CancelFormAsync()));
		_saveForm = () =>
		{
			var next = ConfigurationModels.UpsertProvider(_root, oldName, name.Text.Trim(), url.Text, CliEnvironment.SupportedApis[Math.Max(0, api.SelectedIndex)], key.Password);
			Save(next);
			key.Password = string.Empty;
			SetDirty(false);
		};
	}

	private async Task CancelFormAsync()
	{
		if (await CanDiscardAsync()) Load();
	}

	internal void EditModel(string providerName, int? index)
	{
		var model = index is { } at ? _root["providers"]?[providerName]?["models"]?[at] : null;
		var id = Input(ConfigurationModels.Text(model, "id"));
		var name = Input(ConfigurationModels.Text(model, "name"));
		var context = Input(ConfigurationModels.Number(model, "contextWindow", 128000).ToString(CultureInfo.InvariantCulture));
		var max = Input(ConfigurationModels.Number(model, "maxTokens", 8192).ToString(CultureInfo.InvariantCulture));
		var reasoning = Check(Strings.ConfigReasoning, ConfigurationModels.Flag(model, "reasoning"));
		var vision = Check(Strings.ConfigVision, model?["input"] is JsonArray input && input.Any(value => value is JsonValue v && v.TryGetValue<string>(out var text) && text == "image"));
		var cost = model?["cost"] as JsonObject;
		var costInput = Input(ConfigurationModels.Decimal(cost, "input").ToString(CultureInfo.InvariantCulture));
		var costOutput = Input(ConfigurationModels.Decimal(cost, "output").ToString(CultureInfo.InvariantCulture));
		var costRead = Input(ConfigurationModels.Decimal(cost, "cacheRead").ToString(CultureInfo.InvariantCulture));
		var costWrite = Input(ConfigurationModels.Decimal(cost, "cacheWrite").ToString(CultureInfo.InvariantCulture));
		_body.Clear();
		_body.Children(Label($"{providerName} · {(index is null ? Strings.ConfigAddModel : Strings.ConfigEditModel)}").FontWeight(FontWeight.Bold),
			Field(Strings.ConfigModelId, id), Field(Strings.ConfigName, name),
			Field(Strings.ConfigContext, context), Field(Strings.ConfigMaxTokens, max), reasoning, vision,
			Field(Strings.ConfigCostInput, costInput), Field(Strings.ConfigCostOutput, costOutput),
			Field(Strings.ConfigCostCacheRead, costRead), Field(Strings.ConfigCostCacheWrite, costWrite),
			ActionButton(Strings.Cancel, () => _ = CancelFormAsync()));
		_saveForm = () =>
		{
			if (!int.TryParse(context.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var contextNumber) ||
				!int.TryParse(max.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var maxNumber)) throw new ArgumentException(Strings.ConfigPositiveNumber);
			if (!TryCost(costInput.Text, out var costIn) || !TryCost(costOutput.Text, out var costOut) ||
				!TryCost(costRead.Text, out var costCacheRead) || !TryCost(costWrite.Text, out var costCacheWrite)) throw new ArgumentException(Strings.ConfigCostInvalid);
			var next = ConfigurationModels.UpsertModel(_root, providerName, index, id.Text, name.Text, contextNumber, maxNumber,
				reasoning.IsChecked == true, vision.IsChecked == true,
				new ModelCostValues(costIn, costOut, costCacheRead, costCacheWrite));
			Save(next);
		};
	}

	/// <summary>价格必须是非负数字（空视为 0）。</summary>
	private static bool TryCost(string? text, out double value)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			value = 0;
			return true;
		}

		return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 && double.IsFinite(value);
	}

	/// <summary>按供应的 API 拉取远程模型目录，并把缺失的模型加入配置草稿。</summary>
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

	private void BuildAuth()
	{
		_saveForm = () => Save(_root);
		if (_root.Count == 0) _body.Children(Label(Strings.ConfigNoAuth));
		foreach (var (name, value) in _root)
		{
			_body.Children(new DockPanel().LastChildFill().Children(
				AppIcons.Button("trash", Strings.Delete, () => _ = DeleteAsync(name, () =>
				{
					_root.Remove(name); SetDirty(true); Build();
				})).DockRight(), Label(Strings.ConfigCredentialType(name, ConfigurationModels.Text(value, "type")))));
		}
		var provider = Input();
		var key = new PasswordBox();
		key.PasswordChanged += () => SetDirty(true);
		Action commit = () =>
		{
			Save(string.IsNullOrWhiteSpace(provider.Text) && key.Password.Length == 0
				? _root : ConfigurationModels.SetApiKey(_root, provider.Text.Trim(), key.Password));
			key.Password = string.Empty;
			SetDirty(false);
		};
		_saveForm = commit;
		_body.Children(FormCard(Strings.ConfigAuth, Strings.ConfigCredentialsHint,
			Field(Strings.ConfigProvider, provider), Field(Strings.ConfigKey, key),
			AppIcons.PrimaryButton("check", Strings.ConfigSetKey, () => Guard(commit)).HorizontalAlignment(HorizontalAlignment.Left)));
	}

	private void BuildAgentSettings()
	{
		var provider = Input(ConfigurationModels.Text(_root, "defaultProvider"));
		var model = Input(ConfigurationModels.Text(_root, "defaultModel"));
		var levels = new[] { "", "off", "minimal", "low", "medium", "high", "xhigh", "max" };
		var thinking = new ComboBox().ItemsSource(ItemsView.Create(new[] { Strings.ConfigInherit, "off", "minimal", "low", "medium", "high", "xhigh", "max" })).Width(180);
		thinking.SelectedIndex = Math.Max(0, Array.IndexOf(levels, ConfigurationModels.Text(_root, "defaultThinkingLevel")));
		var thinkingChanged = false;
		thinking.SelectionChanged += _ => { thinkingChanged = true; SetDirty(true); };
		var defaults = _project ? _store.Read(ConfigFile.Settings) : null;
		var global = defaults is { Error: null } ? defaults.Parse() : new JsonObject();
		var compact = Check(Strings.ConfigAutoCompaction, ReadEnabled(_root, "compaction", ReadEnabled(global, "compaction", true)));
		var retry = Check(Strings.ConfigAutoRetry, ReadEnabled(_root, "retry", ReadEnabled(global, "retry", true)));
		var retryMaxInput = Input(ReadInt(_root, "retry", "maxRetries", ReadInt(global, "retry", "maxRetries", 3))
			.ToString(CultureInfo.InvariantCulture));
		var retryDelayInput = Input(ReadInt(_root, "retry", "baseDelayMs", ReadInt(global, "retry", "baseDelayMs", 2000))
			.ToString(CultureInfo.InvariantCulture));
		var compactChanged = false;
		var retryChanged = false;
		var retryMaxChanged = false;
		var retryDelayChanged = false;
		compact.OnCheckedChanged(_ => compactChanged = true);
		retry.OnCheckedChanged(_ => retryChanged = true);
		retryMaxInput.OnTextChanged(_ => retryMaxChanged = true);
		retryDelayInput.OnTextChanged(_ => retryDelayChanged = true);

		// 工具与 Codemode：项目层以全局原始条目为基线回验，chips 显示的集合就是保存后运行时解析出的集合。
		var baseEntries = _project ? ConfigurationTools.ReadEntries(global) : null;
		var initialEntries = ConfigurationTools.ReadEntries(_root);
		IReadOnlyList<string>? entries = initialEntries;
		var chips = new WrapPanel().Spacing(6);
		var toolsStatus = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText()
			.VerticalAlignment(VerticalAlignment.Center);
		var disabledWarning = Hint(Strings.ConfigToolsAllDisabled).IsVisible(false)
			.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).Error);
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

		_body.Children(
			FormCard(Strings.ConfigDefaults, Strings.ConfigDefaultsHint,
				Field(Strings.ConfigProvider, provider), Field(Strings.ConfigModelId, model)),
			FormCard(Strings.ConfigBehavior, Strings.ConfigBehaviorHint,
				Field(Strings.Thinking, thinking), compact, retry,
				Field(Strings.ConfigRetryMaxRetries, retryMaxInput),
				Field(Strings.ConfigRetryBaseDelay, retryDelayInput),
				Hint(Strings.ConfigRetryHint)),
			FormCard(Strings.ConfigToolsTitle, Strings.ConfigToolsHint,
				new StackPanel().Spacing(AppTheme.Space2).Children(
					Label(Strings.ConfigDefaultTools),
					Hint(Strings.ConfigDefaultToolsHint),
					chips,
					new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
						.VerticalAlignment(VerticalAlignment.Center).Children(addTool, resetTools, disableTools, toolsStatus),
					disabledWarning),
				Field(Strings.ConfigCodemodeMode, mode),
				Hint(Strings.ConfigCodemodeModeHint),
				Field(Strings.ConfigCodemodeBudget, budgetInput),
				Hint(Strings.ConfigCodemodeBudgetHint)));
		RefreshTools();
		_saveForm = () =>
		{
			var next = _root.DeepClone().AsObject();
			SetOptional(next, "defaultProvider", provider.Text.Trim());
			SetOptional(next, "defaultModel", model.Text.Trim());
			if (thinkingChanged) SetOptional(next, "defaultThinkingLevel", levels[Math.Max(0, thinking.SelectedIndex)]);
			if (compactChanged) SetEnabled(next, "compaction", compact.IsChecked == true);
			if (retryChanged || retryMaxChanged || retryDelayChanged)
			{
				int? maxRetries = null;
				int? baseDelayMs = null;
				if (retryMaxChanged)
				{
					if (!int.TryParse(retryMaxInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 20)
						throw new ArgumentException(Strings.ConfigRetryMaxRetriesInvalid);
					maxRetries = value;
				}
				if (retryDelayChanged)
				{
					if (!int.TryParse(retryDelayInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 600_000)
						throw new ArgumentException(Strings.ConfigRetryBaseDelayInvalid);
					baseDelayMs = value;
				}
				// retry 里未建模的子键（例如上游的 retry.provider）必须保留。
				var section = next["retry"] as JsonObject ?? new JsonObject();
				if (retryChanged) section["enabled"] = retry.IsChecked == true;
				if (maxRetries is { } max) section["maxRetries"] = max;
				if (baseDelayMs is { } delay) section["baseDelayMs"] = delay;
				next["retry"] = section;
			}
			if (!ConfigurationTools.Same(initialEntries, entries))
			{
				if (entries is null) next.Remove("defaultTools");
				else
				{
					// 用数组构造器而不是 JsonArray.Add<T>：泛型 Add 在裁剪/AOT 下会触发 IL2026/IL3050。
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
			Save(next);
		};

		// 生效集合与运行时同一套解析：默认集、纯名字、+/- 增量、显式空选择。
		void RefreshTools()
		{
			chips.Clear();
			var effective = ConfigurationTools.ResolveInLayer(baseEntries, entries);
			foreach (var name in effective)
			{
				var tool = name;
				chips.Children(ToolChip(tool, () =>
				{
					entries = ConfigurationTools.SetEnabled(baseEntries, entries, tool, false);
					SetDirty(true);
					RefreshTools();
				}));
			}
			toolsStatus.Text(entries is not null
				? Strings.ConfigToolsEnabledCount(effective.Count)
				: _project ? Strings.ConfigInheritGlobal : Strings.ConfigToolsUsingDefault);
			disabledWarning.IsVisible(ConfigurationTools.DisablesAll(entries));
		}

		void ShowToolMenu()
		{
			HoverHints.Cancel(addTool);
			var check = new IconTemplate(size => new GlyphElement().Kind(GlyphKind.CheckMark).GlyphSize(4)
				.Foreground(AppTheme.Accent)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center));
			var menu = new ContextMenu();
			foreach (var name in ConfigurationTools.Catalog)
			{
				var tool = name;
				var enabled = ConfigurationTools.IsEnabled(baseEntries, entries, tool);
				var command = new Command($"pi.config.tool.{tool}", tool);
				addTool.Commands.Register(command, () =>
				{
					entries = ConfigurationTools.SetEnabled(baseEntries, entries, tool, !enabled);
					SetDirty(true);
					RefreshTools();
				});
				menu.Items.Add(new MenuItem(command) { Icon = enabled ? check : null });
			}
			menu.Show(addTool);
		}
	}

	private static bool ReadEnabled(JsonObject root, string key, bool fallback) =>
		root[key] is JsonObject obj && obj["enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : fallback;

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

	private static string SafeUrl(string raw) => Uri.TryCreate(raw, UriKind.Absolute, out var uri)
		? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path) : string.Empty;
}
