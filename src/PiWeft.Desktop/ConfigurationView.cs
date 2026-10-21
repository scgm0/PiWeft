using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

/// <summary>Owns one configuration draft and scope. Theme changes never discard the draft.</summary>
internal sealed partial class ConfigurationView
{
	private readonly ConfigurationStore _store;
	private readonly Action<ConfigFile> _saved;
	private readonly StackPanel _body = new();
	/// <summary>只承载「这个文件本身坏了」这类持续状态；操作反馈走 toast。</summary>
	private readonly TextBlock _notice = new();
	private readonly TextBlock _path = new();
	private readonly TextBlock _dirtyLabel = new();
	private readonly ComboBox _scope = new();
	private ConfigurationDocument? _document;
	private JsonObject _root = new();
	private ConfigFile _file = ConfigFile.Models;
	private bool _project;
	private bool _source;
	private bool _dirty;
	private bool _syncing;
	private bool _revealed;
	private Action? _saveForm;

	public ConfigurationView(string agentDir, string cwd, Action<ConfigFile> saved)
	{
		_store = new(agentDir, cwd);
		_saved = saved;
		_scope.ItemsSource(ItemsView.Create(new[] { Strings.ConfigGlobal, Strings.ConfigProject })).Width(150).SelectedIndex(0);
		_scope.SelectionChanged += _ =>
		{
			if (_syncing) return;
			var requested = _scope.SelectedIndex == 1;
			SyncScope();
			_ = SwitchScopeAsync(requested);
		};
		View = new DockPanel().LastChildFill().Children(
			new StackPanel().Spacing(AppTheme.Space2).Children(
				new DockPanel().LastChildFill().Children(
					new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(
						_scope,
						AppIcons.Button("refresh-cw", Strings.ConfigReload, () => _ = ReloadAsync()),
						AppIcons.Button("folder-open", Strings.ConfigOpenFile, OpenFile)).DockRight(),
					_dirtyLabel.FontSize(AppTheme.FontSizeCaption).AccentText()).Margin(new Thickness(0, 0, 0, 4)),
				_path.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap),
				_notice.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap)
			).DockTop(),
			new ScrollViewer().SmoothScrolling().Content(_body.Spacing(AppTheme.Space3)).AutoVerticalScroll().NoHorizontalScroll());
		Load();
	}

	public UIElement View { get; }
	public bool IsDirty => _dirty;
	/// <summary>当前表单是否可提交（源文件模式在「显示源文件」之前不可保存）。</summary>
	public bool CanSave => _saveForm is not null;
	/// <summary>脏状态或可保存性变化；设置弹窗据此刷新头部的保存按钮。</summary>
	public event Action? StateChanged;
	internal void SaveDraft() => Guard(() => _saveForm?.Invoke());
	public void Discard() => Load();
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

	public Task<bool> CanDiscardAsync() => _dirty ? MessageBox.ConfirmAsync(Strings.ConfigDiscard) : Task.FromResult(true);

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
		_saveForm = null;
		_body.Clear();
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

	/// <summary><paramref name="notify"/> 为 false 时由调用方自己给出一条更具体的提示。</summary>
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
		_dirty = value;
		_dirtyLabel.Text(value ? Strings.ConfigDirty : Strings.ConfigReady);
		StateChanged?.Invoke();
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
		var files = new[] { ConfigFile.Models, ConfigFile.Auth, ConfigFile.Settings, ConfigFile.Mcp };
		var selector = new ComboBox().ItemsSource(ItemsView.Create(new[] { "models.json", "auth.json", "settings.json", "mcp.json" }))
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
	private static StackPanel Field(string title, UIElement input) => new StackPanel().Spacing(4).Children(Label(title), input);
	/// <summary>字段说明：比正文小一号的弱化文案，换行不撑破卡片。</summary>
	private static TextBlock Hint(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);
	/// <summary>工具 chip：等宽名字 + 移除按钮；颜色跟随主题，不写死色值。</summary>
	private static UIElement ToolChip(string name, Action remove) => new Border()
		.CornerRadius(AppTheme.RadiusSm).BorderThickness(1)
		.Padding(new Thickness(8, 1, 2, 1))
		.WithTheme((theme, border) =>
		{
			var palette = AppTheme.For(theme);
			border.Background = palette.CardBackgroundAlt;
			border.BorderBrush = palette.BorderSubtle;
		})
		.Child(new StackPanel().Orientation(Orientation.Horizontal).Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center).Children(
				new TextBlock().Text(name).FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont)
					.PrimaryText().VerticalAlignment(VerticalAlignment.Center),
				AppIcons.Button("x", Strings.ConfigRemoveTool(name), remove, size: 10)));
	private static Button ActionButton(string title, Action action) => new Button().Content(Label(title)).OnClick(action);

	private static Element FormCard(string title, string hint, params UIElement[] fields) => new Border()
		.StyleName(AppStyles.Card).Padding(new Thickness(AppTheme.Space4)).Child(
			new StackPanel().Spacing(AppTheme.Space3).Children(
				Label(title).FontWeight(FontWeight.Bold),
				new TextBlock().Text(hint).FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap))
			.Children(fields));

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
