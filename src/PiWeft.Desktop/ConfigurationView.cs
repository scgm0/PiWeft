using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

internal sealed partial class ConfigurationView
{
	private readonly ConfigurationStore _store;
	private readonly Action<ConfigFile> _saved;
	private readonly Func<IReadOnlyList<Model>>? _models;
	private readonly StackPanel _body = new();
	private readonly ScrollViewer _scroll = new();
	private readonly TextBlock _notice = new();
	private readonly TextBlock _path = new();
	private readonly TextBlock _dirtyLabel = new TextBlock().Text(Strings.ConfigReady);
	private readonly ComboBox _scope = new();
	private ConfigurationDocument? _document;
	private JsonObject _root = new();
	private ConfigFile _file = ConfigFile.Models;
	private bool _project;
	private bool _source;
	private bool _dirty;
	private bool _syncing;
	private bool _revealed;
	private bool _authBusy;
	private Action? _saveForm;
	/// 下拉实例在页面级别复用：换页或重读时跟着释放，免得留下一个还挂在窗口上的原生弹层。
	private ToolPickerMenu? _toolMenu;
	/// 仅供 <c>--ui-smoke config-tools</c>：把「添加工具」下拉摆出来供截图；离开 Agent 页后在 Build 里清掉。
	private Action? _showToolMenu;

	public ConfigurationView(string agentDir, string cwd, Action<ConfigFile> saved,
		Func<IReadOnlyList<Model>>? models = null)
	{
		_store = new(agentDir, cwd);
		_saved = saved;
		_models = models;
		_scope.ItemsSource(ItemsView.Create(new[] { Strings.ConfigGlobal, Strings.ConfigProject })).Width(150).SelectedIndex(0);
		_scope.SelectionChanged += _ =>
		{
			if (_syncing) return;
			var requested = _scope.SelectedIndex == 1;
			SyncScope();
			_ = SwitchScopeAsync(requested);
		};
		View = new DockPanel().LastChildFill().Children(
			new StackPanel().Spacing(AppTheme.Space2)
				.Padding(new Thickness(0, 0, AppTheme.Space4, 0))
				.Children(
					new DockPanel().LastChildFill().Children(
						new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(
							new TextBlock().Text(Strings.ConfigScope).FontSize(AppTheme.FontSizeCaption)
								.SubtleText().VerticalAlignment(VerticalAlignment.Center),
							_scope,
							AppIcons.Button("refresh-cw", Strings.ConfigReload, () => _ = ReloadAsync()),
							AppIcons.Button("folder-open", Strings.ConfigOpenFile, OpenFile)).DockRight(),
						_dirtyLabel.FontSize(AppTheme.FontSizeCaption).SubtleText()).Margin(new Thickness(0, 0, 0, 4)),
					new DockPanel().LastChildFill().Children(
						AppIcons.Button("copy", Strings.SettingsCopyValue, CopyPath).DockRight(),
						_path.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap)),
					_notice.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap)
				).DockTop(),
			// 与设置页内容区一致：右内边距放在滚动内容侧，滚动条贴弹窗右缘，
			// 行尾按钮不压在滚动条下面。
			_scroll.SmoothScrolling().Content(_body.Spacing(AppTheme.Space3))
				.Padding(new Thickness(0, 0, AppTheme.Space4, 0))
				.AutoVerticalScroll().NoHorizontalScroll());
		Load();
	}

	public UIElement View { get; }
	public bool IsDirty => _dirty;
	public bool CanSave => _saveForm is not null;
	public event Action? StateChanged;
	public event Action? AuthChanged;
	internal void SaveDraft() => Guard(() => _saveForm?.Invoke());

	/// <summary>收起挂在窗口浮层上的模型选择器等临时面板；表单重建或设置弹窗关闭时调用。</summary>
	internal void CloseTransientPanels()
	{
		_toolMenu?.CloseInstantly();
	}

	/// <summary>仅用于 <c>--ui-smoke config-dirty</c>：只标脏，不写盘。</summary>
	internal void MarkDirtyForSmoke() => SetDirty(true);
	public void Discard() => Load();

	/// <summary>弹窗重开时重读磁盘；有未保存草稿时保持现状。</summary>
	internal void ReloadFromDisk()
	{
		if (_dirty) return;
		Load();
	}
	public void ApplyTheme() => AppStyles.ApplyTextColors((Element)View);

	public async Task<bool> OpenAsync(ConfigFile file, bool source = false)
	{
		if (_file == file && _source == source) return true;
		if (!await CanDiscardAsync()) return false;
		_file = file;
		_source = source;
		if (file == ConfigFile.Auth) _project = false;
		Load();
		return true;
	}

	public Task<bool> CanDiscardAsync() => _dirty
		? MessageBox.ConfirmAsync(Strings.ConfigDiscardNamed(
			SettingsSections.Title(SettingsSections.SectionOf(_file))))
		: Task.FromResult(true);

	private async Task SwitchScopeAsync(bool project)
	{
		if (!await CanDiscardAsync()) return;
		_project = project && _file != ConfigFile.Auth;
		Load();
	}

	private async Task ReloadAsync()
	{
		if (await CanDiscardAsync()) Load();
	}

	private void SyncScope()
	{
		_syncing = true;
		_scope.SelectedIndex = _project ? 1 : 0;
		_scope.IsEnabled(_file != ConfigFile.Auth);
		_syncing = false;
	}

	private void Load()
	{
		Guard(() =>
		{
			_document = _store.Read(_file, _project);
			_root = _document.Error is null ? _document.Parse() : new JsonObject();
			_revealed = false;
			_path.Text(_document.Path);
			_notice.Text(_document.Error ?? string.Empty);
			SetDirty(false);
			SyncScope();
			Build();
		});
	}

	private void Build()
	{
		CloseTransientPanels();
		_saveForm = null;
		_showToolMenu = null;
		_toolMenu?.Dispose();
		_toolMenu = null;
		_body.Clear();
		// 切走认证页后释放整棵 42 行控件树与凭据快照，避免旧页面控件被 _authRows 长期持有。
		_authRows.Clear();
		_authStatuses = [];
		_authList = null;
		_authCount = null;
		_authHeader = null;
		_authEmpty = null;
		if (_source || _document?.Error is not null) { BuildSource(); return; }
		switch (_file)
		{
			case ConfigFile.Models: BuildProviders(); break;
			case ConfigFile.Auth: BuildAuth(); break;
			case ConfigFile.Settings: BuildAgentSettings(); break;
			case ConfigFile.Mcp: BuildMcp(); break;
		}
		StateChanged?.Invoke();
	}

	private void Save(JsonObject next, bool notify = true)
	{
		if (_document is null) return;
		_document = _store.Save(_document, next.ToJsonString(ConfigurationStore.WriteOptions));
		_root = _document.Parse();
		SetDirty(false);
		Build();
		if (notify) Toasts.Success(View, Strings.ConfigSaved);
		_saved(_file);
	}

	private void SetDirty(bool value)
	{
		// 表单每次键入都会调 SetDirty(true)：已经脏时不必再刷一次标题栏/导航点。
		if (_dirty == value) return;
		_dirty = value;
		_dirtyLabel.Text(value ? Strings.ConfigDirty : Strings.ConfigReady);
		if (value) _dirtyLabel.AccentText(); else _dirtyLabel.SubtleText();
		StateChanged?.Invoke();
	}

	private void CopyPath()
	{
		var text = _document?.Path ?? _path.Text;
		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		if (clipboard?.TrySetText(text) == true) Toasts.Success(View, Strings.Copied);
		else Toasts.Error(View, Strings.ClipboardUnavailable);
	}

	private void Guard(Action action)
	{
		try { action(); }
		catch (ConfigurationConflictException) { Toasts.Error(View, Strings.ConfigConflict); }
		catch (ArgumentException ex) { Toasts.Error(View, ex.Message); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			// Do not emit exceptions from JSON/credential operations: their messages can contain secrets.
			Toasts.Error(View, Strings.ConfigFailed);
		}
	}

	private void OpenFile() => Guard(() =>
	{
		if (_document is not null && File.Exists(_document.Path) && !Shell.OpenInEditor(_document.Path)) Toasts.Error(View, Strings.ConfigFailed);
	});

	private void BuildSource()
	{
		_saveForm = null;
		var files = new[] { ConfigFile.Models, ConfigFile.Auth, ConfigFile.Settings, ConfigFile.Mcp, ConfigFile.Usage };
		var selector = new ComboBox().ItemsSource(ItemsView.Create(new[] { "models.json", "auth.json", "settings.json", "mcp.json", "usage-probes.json" }))
			.Width(200).SelectedIndex(Array.IndexOf(files, _file));
		var selecting = false;
		selector.SelectionChanged += _ =>
		{
			if (selecting) return;
			var selected = selector.SelectedIndex;
			selecting = true;
			selector.SelectedIndex = Array.IndexOf(files, _file);
			selecting = false;
			if (selected >= 0) _ = OpenAsync(files[selected], source: true);
		};
		_body.Children(selector);
		var reveal = new CheckBox().Content(Label(Strings.ConfigShowSource)).IsChecked(_revealed);
		reveal.OnCheckedChanged(_ =>
		{
			if (_dirty) { reveal.IsChecked(true); return; }
			_revealed = reveal.IsChecked == true;
			_body.Clear();
			BuildSource();
		});
		_body.Children(reveal);
		if (!_revealed) { _saveForm = null; StateChanged?.Invoke(); return; }
		var editor = new MultiLineTextBox().PrimarySelection().StyleName(AppStyles.TextInput).FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeCaption).Height(420).Text(_document?.Raw is { Length: > 0 } raw ? raw : "{}")
			.OnTextChanged(_ => SetDirty(true));
		_body.Children(editor);
		_saveForm = () => Save(ConfigurationStore.ParseObject(editor.Text));
		StateChanged?.Invoke();
	}

	private TextBox Input(string value = "") => new TextBox().PrimarySelection().Text(value).MinWidth(160).OnTextChanged(_ => SetDirty(true));
	private CheckBox Check(string title, bool value) => new CheckBox().Content(Label(title)).IsChecked(value).OnCheckedChanged(_ => SetDirty(true));
	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();
	private static StackPanel Field(string title, UIElement input, double labelMaxWidth = 0)
	{
		var label = Label(title);
		if (labelMaxWidth > 0) label.MaxWidth(labelMaxWidth).TextWrapping(TextWrapping.Wrap);
		return new StackPanel().Spacing(4).Children(label, input);
	}

	private static WrapPanel Fields(params UIElement[] fields) =>
		new WrapPanel().Spacing(AppTheme.Space4).Children(fields);
	private static TextBlock Hint(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);

	/// 卡片内的分组小标题：同一张卡里把并发、运行预算和界面开关分开读。
	private static TextBlock SubLabel(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Medium).PrimaryText();
	/// 一枚工具 chip；<paramref name="disabled"/> 表示它被「扩展」里的内置开关挡下，
	/// 名字压暗并给出原因，× 仍可用（把已经失效的条目清掉）。
	private static UIElement ToolChip(string name, Action remove, bool disabled = false)
	{
		var label = new TextBlock().Text(name).FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont)
			.VerticalAlignment(VerticalAlignment.Center);
		if (disabled) label.FaintText(); else label.PrimaryText();

		var chip = new Border()
			.CornerRadius(AppTheme.RadiusSm).BorderThickness(1)
			.Padding(new Thickness(8, 1, 2, 1))
			.WithTheme((theme, border) =>
			{
				var palette = AppTheme.For(theme);
				border.Background = palette.CardBackgroundAlt;
				border.BorderBrush = disabled ? palette.Warning : palette.BorderSubtle;
			})
			.Child(new StackPanel().Orientation(Orientation.Horizontal).Spacing(2)
				.VerticalAlignment(VerticalAlignment.Center).Children(
					label,
					AppIcons.Button("x", Strings.ConfigRemoveTool(name), remove, size: 10)));
		return chip;
	}

	private static Button ActionButton(string title, Action action) => new Button().Content(Label(title)).OnClick(action);

	private static Element FormCard(string title, string hint, params UIElement[] fields) =>
		FormCard(null, title, hint, fields);

	/// 带图标的表单卡：设置页的每张卡先给一个可扫的图标，标题与说明再往下读。
	private static Element FormCard(string? icon, string title, string hint, params UIElement[] fields)
	{
		var heading = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center);
		if (icon is { Length: > 0 })
		{
			heading.Children(AppIcons.Icon(icon, AppStyles.RoleSubtle, 14));
		}

		heading.Children(Label(title).FontWeight(FontWeight.Bold));

		return new Border()
			.StyleName(AppStyles.Card).Padding(new Thickness(AppTheme.Space4)).Child(
				new StackPanel().Spacing(AppTheme.Space3).Children(
					heading,
					new TextBlock().Text(hint).FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap))
				.Children(fields));
	}

	private async Task DeleteAsync(string title, Action mutation)
	{
		if (await MessageBox.ConfirmAsync(Strings.ConfigDelete(title))) Guard(mutation);
	}

	private async Task ExportAsync()
	{
		try
		{
			var path = await FileDialog.SaveFileAsync(new SaveFileDialogOptions { Title = Strings.ConfigExport, FileName = "models.json", DefaultExtension = "json" });
			if (path is not null) File.WriteAllText(path, ConfigurationModels.Export(_root));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Toasts.Error(View, Strings.ConfigFailed); }
	}

	private async Task ImportAsync()
	{
		try
		{
			if (!await CanDiscardAsync()) return;
			var document = _document;
			var path = await FileDialog.OpenFileAsync(new OpenFileDialogOptions { Title = Strings.ConfigImport, Filters = [new FileFilter("JSON", "*.json")] });
			if (path is null || document is null || !ReferenceEquals(document, _document)) return;
			if (new FileInfo(path).Length > ConfigurationStore.MaxBytes) { Toasts.Error(View, Strings.ConfigTooLarge); return; }
			var imported = ConfigurationStore.ParseObject(File.ReadAllText(path));
			if (imported["providers"] is not JsonObject additions) throw new ArgumentException(Strings.ConfigProvidersRequired);
			var next = _root.DeepClone().AsObject();
			var providers = next["providers"] as JsonObject ?? new JsonObject();
			if (next["providers"] is null) next["providers"] = providers;
			// A transfer is an unsaved draft. Existing names must be resolved explicitly, never overwritten.
			foreach (var (name, provider) in additions)
			{
				if (providers.ContainsKey(name)) throw new ArgumentException(Strings.ConfigNameExists);
				providers[name] = provider?.DeepClone();
			}
			_root = next;
			SetDirty(true);
			BuildProviders();
		}
		catch (ArgumentException ex) { Toasts.Error(View, ex.Message); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Toasts.Error(View, Strings.ConfigFailed); }
	}
}
