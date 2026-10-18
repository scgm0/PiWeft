using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Themes;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>一个 MCP 服务器在界面上的状态。</summary>
public sealed record McpServerStatus(string Name, bool Connected, int ToolCount, string? Error);

/// <summary>设置面板要展示的一次快照。</summary>
public sealed record SettingsSnapshot
{
	public required string AgentDir { get; init; }

	public required string Cwd { get; init; }

	public required int ModelCount { get; init; }

	public required IReadOnlyList<string> Tools { get; init; }

	public required IReadOnlyList<string> Skills { get; init; }

	/// <summary>已解析的资源（技能、提示词模板、主题、包与诊断）。</summary>
	public ResourceSnapshot Resources { get; init; } = ResourceSnapshot.Empty;

	public required IReadOnlyList<McpServerStatus> McpServers { get; init; }

	/// <summary>当前会话的信息；没有打开会话时为 <c>null</c>。</summary>
	public CodingAgentSession? Session { get; init; }

	/// <summary>归档（<c>.trash</c>）里的会话。</summary>
	public IReadOnlyList<SessionListItem> Archived { get; init; } = [];
	public Func<IReadOnlyList<SessionListItem>>? LoadArchived { get; init; }
}

/// <summary>
/// 设置弹窗的内容：分类展示系统状态，并通过独立的配置视图管理模型、认证、Agent 与 MCP 文件。
/// 外壳（压暗背景、卡片、标题栏、保存/关闭）由 <see cref="SettingsDialog"/> 提供。
/// </summary>
public sealed class SettingsView
{
	private readonly Func<SettingsSnapshot> _snapshot;
	private readonly StackPanel _content = new();
	private readonly TextBlock _toast = new();
	private readonly StackPanel _navigation = new();
	private readonly Lazy<ConfigurationView> _configuration;
	private readonly Grid _main;
	private readonly TextBlock _sectionTitle = new();
	private readonly Action<ConfigFile> _saved;

	/// <summary>手动重建 MCP 连接；为空时不显示重连按钮。</summary>
	private readonly Func<Task>? _reconnectMcp;

	/// <summary>重新解析技能/提示词/主题；为空时不显示重载按钮。</summary>
	private readonly Func<Task>? _reloadResources;
	private readonly ScrollViewer _systemView;
	private Button? _activeNavigationButton;
	private bool _configMode;
	private string _section = "overview";

	/// <summary>当前快捷键表（可改键后刷新）。</summary>
	private ShortcutMap _shortcuts = new(DesktopPreference.Load().Shortcuts);

	/// <summary>分区或配置脏状态变化；宿主弹窗据此刷新头部的保存按钮。</summary>
	public event Action? StateChanged;

	public SettingsView(
		Func<SettingsSnapshot> snapshot,
		Action<ConfigFile>? saved = null,
		Func<Task>? reconnectMcp = null,
		Func<Task>? reloadResources = null)
	{
		_snapshot = snapshot;
		var initial = snapshot();
		_saved = saved ?? (_ => { });
		_reconnectMcp = reconnectMcp;
		_reloadResources = reloadResources;
		_configuration = new(() =>
		{
			var view = new ConfigurationView(initial.AgentDir, initial.Cwd, _saved);
			view.StateChanged += () => StateChanged?.Invoke();
			return view;
		});
		_toast.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap)
			.MaxWidth(420).VerticalAlignment(VerticalAlignment.Center).IsVisible(false);
		_content.Spacing(AppTheme.Space4).MaxWidth(960);
		_systemView = new ScrollViewer().SmoothScrolling().Content(_content).AutoVerticalScroll().NoHorizontalScroll();
		var main = _main = new Grid().Children(_systemView);

		// 弹窗左侧导航轨：顶部是「系统设置 / 配置管理」分段，下面是分区列表。
		_navigation.Spacing(2);
		var navigationColumn = new Border().StyleName(AppStyles.DialogNav).Width(196)
			.Padding(new Thickness(10, AppTheme.Space3, 10, AppTheme.Space3))
			.Child(new ScrollViewer().SmoothScrolling().Content(_navigation)
				.AutoVerticalScroll().NoHorizontalScroll());

		// 右侧内容区：分区标题固定在顶部，只有卡片列表滚动。
		var sectionHeader = new DockPanel().LastChildFill()
			.Margin(new Thickness(0, 0, 0, AppTheme.Space3))
			.Children(_toast.DockRight(), _sectionTitle.FontSize(AppTheme.FontSizeTitle)
				.FontWeight(FontWeight.SemiBold).PrimaryText().VerticalAlignment(VerticalAlignment.Center));
		var contentColumn = new Border().StyleName(AppStyles.DialogContent)
			.Padding(new Thickness(AppTheme.Space4 + 4, AppTheme.Space3, AppTheme.Space4, AppTheme.Space4))
			.Child(new DockPanel().LastChildFill().Children(sectionHeader.DockTop(), main));
		View = new Grid().Columns("auto,*")
			.Children(navigationColumn.Column(0), contentColumn.Column(1));
		BuildNavigation();
		Refresh();
	}

	public UIElement View { get; }

	public bool IsConfigMode => _configMode;

	/// <summary>当前表单是否可提交；源文件模式在勾选「显示源文件」之前不可保存。</summary>
	public bool CanSave => _configuration.IsValueCreated && _configuration.Value.CanSave;

	/// <summary>把保存动作交给配置草稿；没有打开配置分区时什么也不做。</summary>
	public void SaveDraft()
	{
		if (_configuration.IsValueCreated) _configuration.Value.SaveDraft();
	}

	/// <summary>把焦点放到当前分区，保证弹窗打开或切换分区后 Esc / 键盘导航仍然可达。</summary>
	public void FocusNavigation()
	{
		if (_activeNavigationButton is { } active)
		{
			active.Focus();
			return;
		}

		foreach (var child in _navigation.Children)
		{
			if (child is Button button)
			{
				button.Focus();
				return;
			}
		}
	}

	public void ApplyTheme()
	{
		if (_configuration.IsValueCreated) _configuration.Value.ApplyTheme();
		AppStyles.ApplyTextColors((Element)View);
	}

	public Task<bool> CanCloseAsync() => _configuration.IsValueCreated ? _configuration.Value.CanDiscardAsync() : Task.FromResult(true);
	public bool IsDirty => _configuration.IsValueCreated && _configuration.Value.IsDirty;

	public void ShowConfiguration() => _ = SwitchModeAsync(true);

	private async Task SwitchModeAsync(bool config)
	{
		if (_configMode == config) return;
		if (!config && !await _configuration.Value.CanDiscardAsync()) return;
		if (!config) _configuration.Value.Discard();
		else
		{
			if (!await _configuration.Value.OpenAsync(ConfigFile.Models)) return;
			if (!_main.Children.Contains(_configuration.Value.View)) _main.Children(_configuration.Value.View);
		}
		_configMode = config;
		_section = config ? "models" : "overview";
		_configuration.Value.View.IsVisible(config);
		_systemView.IsVisible(!config);
		BuildNavigation();
		Refresh();
		StateChanged?.Invoke();
		FocusNavigation();
	}

	private void BuildNavigation()
	{
		_navigation.Clear();
		_activeNavigationButton = null;

		// 分段切换：与 PiDeck 一样放在导航顶部，两个分区在同一块轨道里。
		_navigation.Children(new Border().StyleName(AppStyles.SegmentTrack)
			.Padding(new Thickness(3)).Margin(new Thickness(0, 0, 0, AppTheme.Space3))
			.Child(new Grid().Columns("*,*").Spacing(AppTheme.Space1).Children(
				ModeButton("settings", Strings.SystemSettings, !_configMode, () => _ = SwitchModeAsync(false)).Column(0),
				ModeButton("square-pen", Strings.ConfigTitle, _configMode, () => _ = SwitchModeAsync(true)).Column(1))));

		if (_configMode)
		{
			AddNavigationGroup(Strings.ConfigurationGroup, first: true);
			AddNavigationButton("models", "settings", Strings.ConfigModels);
			AddNavigationButton("auth", "star", Strings.ConfigAuth);
			AddNavigationButton("agent", "settings", Strings.ConfigSettings);
			AddNavigationButton("mcp", "terminal", Strings.SettingsMcp);
			AddNavigationButton("source", "square-pen", Strings.ConfigSource);
		}
		else
		{
			AddNavigationGroup(Strings.SettingsGroupGeneral, first: true);
			AddNavigationButton("overview", "settings", Strings.ConfigOverview);
			AddNavigationButton("appearance", "contrast", Strings.ConfigAppearance);
			AddNavigationGroup(Strings.SettingsGroupRuntime, first: false);
			AddNavigationButton("resources", "book-open", Strings.SettingsResources);
			AddNavigationButton("session", "message-square", Strings.SettingsSession);
			AddNavigationButton("shortcuts", "terminal", Strings.SettingsShortcuts);
			AddNavigationGroup(Strings.MaintenanceGroup, first: false);
			AddNavigationButton("archive", "trash", Strings.SettingsArchive);
			AddNavigationButton("logs", "folder-open", Strings.SettingsLogs);
			AddNavigationButton("about", "settings", Strings.SettingsAbout);
		}

		_sectionTitle.Text(SectionTitle(_section));
	}

	private static string SectionTitle(string id) => id switch
	{
		"models" => Strings.ConfigModels,
		"auth" => Strings.ConfigAuth,
		"agent" => Strings.ConfigSettings,
		"mcp" => Strings.SettingsMcp,
		"source" => Strings.ConfigSource,
		"appearance" => Strings.ConfigAppearance,
		"resources" => Strings.SettingsResources,
		"session" => Strings.SettingsSession,
		"archive" => Strings.SettingsArchive,
		"shortcuts" => Strings.SettingsShortcuts,
		"logs" => Strings.SettingsLogs,
		"about" => Strings.SettingsAbout,
		_ => Strings.ConfigOverview,
	};

	private void AddNavigationGroup(string title, bool first)
	{
		_navigation.Children(new TextBlock().Text(title)
			.FontSize(11).FontWeight(FontWeight.SemiBold).FaintText()
			.Margin(new Thickness(AppTheme.Space2, first ? 0 : AppTheme.Space3, 0, AppTheme.Space1)));
	}

	private void AddNavigationButton(string id, string icon, string title)
	{
		var active = id == _section;
		var label = new TextBlock().Text(title).FontSize(AppTheme.FontSizeControl);
		if (active) label.PrimaryText(); else label.MutedText();
		var button = new Button()
			.Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(AppIcons.Icon(icon, active ? AppStyles.RolePrimary : AppStyles.RoleSubtle, 15), label))
			.OnClick(() => _ = SelectSectionAsync(id))
			.StyleName(active ? AppStyles.SelectedButton : AppStyles.GhostButton)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.MinHeight(AppTheme.ControlHeightMd + 2)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0));
		if (active) _activeNavigationButton = button;
		_navigation.Children(button);
	}

	private static Button ModeButton(string icon, string text, bool active, Action onClick)
	{
		var label = new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption)
			.HorizontalAlignment(HorizontalAlignment.Center);
		if (active)
		{
			// 选中段加半粗，暗色下即使只看到明度差也能靠字重认出当前分区。
			label.PrimaryText().FontWeight(FontWeight.SemiBold);
		}
		else label.MutedText();
		return new Button()
			.Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6)
				.HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center)
				.Children(AppIcons.Icon(icon, active ? AppStyles.RolePrimary : AppStyles.RoleSubtle, 14), label))
			.OnClick(onClick)
			.StyleName(active ? AppStyles.SegmentButtonActive : AppStyles.SegmentButton)
			.Height(AppTheme.ControlHeightSm + 2)
			.HorizontalAlignment(HorizontalAlignment.Stretch);
	}

	internal async Task SelectSectionAsync(string section)
	{
		if (_section == section) return;
		if (_configMode)
		{
			var file = section switch { "auth" => ConfigFile.Auth, "agent" => ConfigFile.Settings, "mcp" => ConfigFile.Mcp, _ => ConfigFile.Models };
			if (!await _configuration.Value.OpenAsync(file, section == "source")) return;
		}
		_section = section;
		BuildNavigation();
		Refresh();
		StateChanged?.Invoke();
		FocusNavigation();
	}

	/// <summary>在面板顶部提示一句话（例如「已设为默认模型」）。</summary>
	private void Toast(string message)
	{
		_toast.Text(message);
		_toast.IsVisible(true);
	}

	/// <summary>
	/// 最近一次分区构建失败的原因；正常时为 <c>null</c>。
	/// 图标缺失一类的错误会记在这里并给出可见提示，而不是留下空白面板。
	/// </summary>
	internal string? LastSectionError { get; private set; }

	/// <summary>重新读取快照并重建面板。</summary>
	public void Refresh()
	{
		if (_configMode) return;
		var snapshot = _snapshot();
		_content.Clear();
		SmoothScroll.For(_systemView).Stop();
		_systemView.SetScrollOffsets(0, 0);
		LastSectionError = null;

		try
		{
			switch (_section)
			{
				case "appearance": _content.Children(AppearanceSection()); break;
				case "resources": _content.Children(ResourcesSection()); break;
				case "session": _content.Children(SessionSection(snapshot.Session)); break;
				case "archive": _content.Children(ArchiveSection(snapshot.LoadArchived?.Invoke() ?? snapshot.Archived)); break;
				case "shortcuts": _content.Children(ShortcutSection()); break;
				case "logs": _content.Children(LogSection()); break;
				case "about": _content.Children(AboutSection()); break;
				default:
					_content.Children(Section(Strings.SettingsEnvironment, [
						(Strings.SettingsAgentDir, snapshot.AgentDir), (Strings.SettingsCwd, snapshot.Cwd),
						(Strings.SettingsModels, snapshot.ModelCount.ToString()),
						(Strings.SettingsTools, snapshot.Tools.Count == 0 ? "—" : string.Join(", ", snapshot.Tools))]),
						Section(Strings.SettingsSkills, snapshot.Skills.Count == 0
							? [(Strings.SettingsNone, "")] : [.. snapshot.Skills.Select(name => (name, string.Empty))]),
						McpSection(snapshot.McpServers));
					break;
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
		{
			// 分区构建失败时保留一个可见的提示，避免整页空白。
			LastSectionError = ex.Message;
			AppLog.Failure(AppLog.For<SettingsView>(), $"Build settings section {_section}", ex);
			_content.Children(new TextBlock().Text(Strings.ConfigFailed)
				.FontSize(AppTheme.FontSizeControl).MutedText().TextWrapping(TextWrapping.Wrap));
		}
	}

	private Element AppearanceSection()
	{
		var snapshot = _snapshot();
		var entries = new List<(string Label, string Value, AppPalette Palette)>
		{
			(Strings.ThemeDark, DesktopPreference.DarkTheme, AppPalette.Dark),
			(Strings.ThemeLight, DesktopPreference.LightTheme, AppPalette.Light),
			(Strings.ThemeSystem, DesktopPreference.SystemTheme, AppPalette.Dark),
		};

		// 自定义主题：用户与项目 themes 目录下能解析的 pi 主题文件。
		foreach (var (name, error) in DesktopTheme.ListCustomThemes(snapshot.Cwd, snapshot.AgentDir))
		{
			if (error is not null)
			{
				// 无效主题只在资源侧提示，不进入主题选择器。
				continue;
			}

			var resolved = DesktopTheme.Resolve(name, snapshot.Cwd, snapshot.AgentDir);
			entries.Add((name, name, resolved.Light ?? resolved.Dark ?? AppPalette.Dark));
		}

		var current = DesktopPreference.Load().Theme;
		var picker = new ComboBox()
			.ItemsSource(ItemsView.Create(entries.Select(entry => entry.Label).ToArray()))
			.Width(220);
		picker.SelectedIndex = Math.Max(
			0,
			entries.FindIndex(entry => string.Equals(entry.Value, current, StringComparison.Ordinal)));

		// 换肤会触发浮层重解析，重建过程中 ComboBox 可能再次抛出 SelectionChanged；
		// 用重入保护避免同一轮里反复保存/应用主题。
		var applying = false;
		void ApplySelection()
		{
			if (applying || picker.SelectedIndex < 0 || picker.SelectedIndex >= entries.Count)
			{
				return;
			}

			applying = true;
			try
			{
				var value = entries[picker.SelectedIndex].Value;
				try
				{
					(DesktopPreference.Load() with { Theme = value }).Save();
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					return;
				}

				var resolved = DesktopTheme.Resolve(value, snapshot.Cwd, snapshot.AgentDir);
				DesktopTheme.Apply(resolved);
			}
			finally
			{
				applying = false;
			}
		}

		picker.SelectionChanged += _ => ApplySelection();
		var previews = new WrapPanel().Spacing(AppTheme.Space2);
		var buttons = new List<Button>();
		for (var index = 0; index < entries.Count; index++)
		{
			var selected = index;
			var palette = entries[index].Palette;
			var miniature = new Border().Background(palette.WindowBackground).BorderBrush(palette.Border)
				.BorderThickness(1).CornerRadius(AppTheme.RadiusXs).Height(54).Child(
					new DockPanel().LastChildFill().Children(
						new Border().Background(palette.SidebarBackground).Width(32).DockLeft(),
						new StackPanel().Spacing(6).Padding(new Thickness(10)).Children(
							new Border().Background(palette.TextMuted).Height(4).Width(72).HorizontalAlignment(HorizontalAlignment.Left),
							new Border().Background(palette.Accent).Height(5).Width(44).HorizontalAlignment(HorizontalAlignment.Left),
							new Border().Background(
								string.Equals(entries[selected].Value, DesktopPreference.SystemTheme, StringComparison.Ordinal)
									? AppPalette.Light.CardBackground
									: palette.CardBackgroundAlt).Height(6))));
			var button = new Button().Width(176).Padding(new Thickness(AppTheme.Space2))
				.Content(new StackPanel().Spacing(AppTheme.Space2).Children(miniature, LabelForPreview(entries[index].Label)))
				.OnClick(() => picker.SelectedIndex = selected)
				.StyleName(index == picker.SelectedIndex ? AppStyles.SelectedButton : AppStyles.CardButton);
			buttons.Add(button);
			previews.Children(button);
		}
		picker.SelectionChanged += _ =>
		{
			for (var index = 0; index < buttons.Count; index++)
				buttons[index].StyleName(index == picker.SelectedIndex ? AppStyles.SelectedButton : AppStyles.CardButton);
		};
		var sizes = new double[] { 14, 16, 18, 20, 22 };
		var widths = new double[] { 760, 1060, 1360 };
		var preference = DesktopPreference.Load();
		var font = new ComboBox().Width(180).ItemsSource(ItemsView.Create(sizes.Select(size => $"{size:0} px").ToArray()))
			.SelectedIndex(Math.Max(0, Array.IndexOf(sizes, preference.ChatFontSize)));
		var width = new ComboBox().Width(180).ItemsSource(ItemsView.Create(new[] { Strings.ReadingCompact, Strings.ReadingComfortable, Strings.ReadingWide }))
			.SelectedIndex(Math.Max(0, Array.IndexOf(widths, preference.ReadingWidth)));
		var sample = new TextBlock().Text(Strings.ReadingSample).FontSize(preference.ChatFontSize).PrimaryText().TextWrapping(TextWrapping.Wrap);
		void SaveReading()
		{
			if (font.SelectedIndex < 0 || width.SelectedIndex < 0) return;
			var next = DesktopPreference.Load() with { ChatFontSize = sizes[font.SelectedIndex], ReadingWidth = widths[width.SelectedIndex] };
			try { next.Save(); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Toast(Strings.ConfigFailed); return; }
			ReadingPreferences.Apply(next);
			sample.FontSize(next.ChatFontSize);
		}
		font.SelectionChanged += _ => SaveReading();
		width.SelectionChanged += _ => SaveReading();
		return new StackPanel().Spacing(AppTheme.Space4).Children(
			Group("palette", Strings.ConfigTheme, new StackPanel().Spacing(AppTheme.Space3).Children(previews,
				picker.HorizontalAlignment(HorizontalAlignment.Left))),
			Group("file-text", Strings.ReadingSettings, new StackPanel().Spacing(AppTheme.Space3).Children(
				new DockPanel().LastChildFill().Children(font.DockRight(), LabelForPreview(Strings.ChatFontSize)),
				new DockPanel().LastChildFill().Children(width.DockRight(), LabelForPreview(Strings.ReadingWidth)),
				sample)));
	}

	private static TextBlock LabelForPreview(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeControl).PrimaryText();

	/// <summary>资源管理：技能、提示词模板、主题与 pi 包的来源、路径与解析结果。</summary>
	private Element ResourcesSection()
	{
		var snapshot = _snapshot();
		var resources = snapshot.Resources;
		var panel = new StackPanel().Spacing(AppTheme.Space4);

		panel.Children(Group("book-open", Strings.SettingsSkills, new StackPanel().Spacing(AppTheme.Space3).Children(
			Hint(Strings.ResourcesSkillHint),
			ResourceList(resources.Skills.Select(skill => new ResourceRow(
				skill.Name, skill.Description, skill.Origin.Display, skill.FilePath))))));

		panel.Children(Group("file-text", Strings.ResourcesPrompts, new StackPanel().Spacing(AppTheme.Space3).Children(
			Hint(Strings.ResourcesPromptHint),
			ResourceList(resources.Templates.Select(template => new ResourceRow(
				template.Name, string.Empty, template.Origin.Display, template.FilePath))))));

		panel.Children(Group("palette", Strings.ResourcesThemes, new StackPanel().Spacing(AppTheme.Space3).Children(
			Hint(Strings.ResourcesThemeHint),
			ResourceList(resources.Themes.Select(theme => new ResourceRow(
				theme.Name, ThemeDetail(theme), theme.Origin.Display, theme.FilePath))))));

		panel.Children(Group("package", Strings.ResourcesPackages, new StackPanel().Spacing(AppTheme.Space3).Children(
			Hint(Strings.ResourcesPackageHint),
			ResourceList(resources.Packages.Select(package => new ResourceRow(
				package.Name,
				package.Source,
				package.Scope == ResourceScope.Project ? Strings.ConfigProject : Strings.ConfigGlobal,
				package.Root))))));

		if (resources.Diagnostics.Count > 0)
		{
			var diagnostics = new StackPanel().Spacing(2);
			foreach (var message in resources.Diagnostics)
			{
				diagnostics.Children(new TextBlock().Text(message)
					.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap));
			}

			panel.Children(Group("settings", Strings.ResourcesDiagnostics, diagnostics));
		}

		if (_reloadResources is not null)
		{
			panel.Children(AppIcons.TextButton("refresh-cw", Strings.ResourcesReload, () => _ = ReloadAsync()));
		}

		return panel;

		async Task ReloadAsync()
		{
			try
			{
				await _reloadResources();
				Toast(Strings.ResourcesReloaded);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
			{
				Toast(Strings.ConfigFailed);
			}
		}
	}

	/// <summary>资源列表的一行：名称、补充说明、来源与文件路径。</summary>
	private sealed record ResourceRow(string Name, string Detail, string Source, string Path);

	private Element ResourceList(IEnumerable<ResourceRow> rows)
	{
		var list = new StackPanel().Spacing(AppTheme.Space2);
		var empty = true;

		foreach (var row in rows)
		{
			empty = false;
			var header = new DockPanel().LastChildFill();
			header.Children(
				AppIcons.TextButton("square-pen", Strings.ResourcesOpen, () => OpenPath(row.Path)).DockRight(),
				new TextBlock().Text(row.Source).FontSize(AppTheme.FontSizeMicro).FaintText().DockRight(),
				new TextBlock().Text(row.Name).FontSize(AppTheme.FontSizeControl).PrimaryText());

			list.Children(new StackPanel().Spacing(2).Children(
				header,
				new TextBlock().Text(row.Detail.Length > 0 ? row.Detail : row.Path)
					.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap)));
		}

		if (empty)
		{
			list.Children(new TextBlock().Text(Strings.ResourcesEmpty)
				.FontSize(AppTheme.FontSizeCaption).MutedText());
		}

		return list;
	}

	private void OpenPath(string path)
	{
		try
		{
			if (!Shell.OpenInEditor(path))
			{
				Toast(Strings.ConfigFailed);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			Toast(Strings.ConfigFailed);
		}
	}

	private static string ThemeDetail(ThemeEntry theme) => theme.Theme is { } parsed
		? (parsed.Appearance == ThemeAppearance.Light ? Strings.ResourcesLight : Strings.ResourcesDark)
		: (theme.Error ?? Strings.ResourcesInvalid);

	private static TextBlock Hint(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap);

	private Element LogSection()
	{
		return Group("folder-open", Strings.SettingsLogs, new StackPanel().Spacing(AppTheme.Space3).Children(
			new TextBlock().Text(AppLog.DirectoryPath).FontSize(AppTheme.FontSizeCaption)
				.MutedText().TextWrapping(TextWrapping.Wrap),
			new DockPanel().LastChildFill().Children(
				AppIcons.Button("folder-open", Strings.OpenLogDirectory, OpenLogDirectory).DockRight(),
				AppIcons.Button("copy", Strings.CopyLogDirectory, () =>
				{
					var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
					Toast(clipboard?.TrySetText(AppLog.DirectoryPath) == true ? Strings.Copied : Strings.ClipboardUnavailable);
				}).DockRight(),
				new TextBlock().Text($"{Strings.LogLevel}：{AppLog.MinimumLevel}")
					.FontSize(AppTheme.FontSizeMicro).SubtleText())));
	}

	private void OpenLogDirectory()
	{
		try
		{
			Directory.CreateDirectory(AppLog.DirectoryPath);
			if (!Shell.Open(AppLog.DirectoryPath))
			{
				Toast(Strings.OpenFailed(AppLog.DirectoryPath));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<SettingsView>(), "Open log directory", ex);
			Toast(Strings.OpenFailed(AppLog.DirectoryPath));
		}
	}

	/// <summary>带图标的卡片标题；图标名取自内嵌的 Lucide 图标集。</summary>
	private static Element Group(string icon, string title, UIElement content) => new Border()
		.StyleName(AppStyles.Card)
		.CornerRadius(AppTheme.RadiusMd)
		.MaxWidth(960)
		.ClipToBounds(true)
		.Child(new DockPanel().LastChildFill().Children(
			new Border().StyleName(AppStyles.CardHeader)
				.Padding(new Thickness(AppTheme.Space4, AppTheme.Space3, AppTheme.Space4, AppTheme.Space3))
				.Child(new StackPanel().Orientation(Orientation.Horizontal)
					.VerticalAlignment(VerticalAlignment.Center).Spacing(AppTheme.Space2)
					.Children(
						AppIcons.Icon(icon, AppStyles.RoleSubtle, 14),
						new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption)
							.FontWeight(FontWeight.SemiBold).PrimaryText()
							.VerticalAlignment(VerticalAlignment.Center)))
				.DockTop(),
			new Border().Padding(new Thickness(AppTheme.Space4, AppTheme.Space3, AppTheme.Space4, AppTheme.Space4))
				.Child(content)));

	private static Element Section(string title, IReadOnlyList<(string Label, string Value)> rows)
	{
		var body = new StackPanel().Spacing(4);
		foreach (var (label, value) in rows)
		{
			body.Children(new DockPanel().LastChildFill().Margin(new Thickness(0, 4, 0, 4)).Children(
				new TextBlock().Text(label).Width(116).FontSize(AppTheme.FontSizeCaption).SubtleText().DockLeft(),
				new TextBlock().Text(value.Length == 0 ? "—" : value).TextWrapping(TextWrapping.Wrap)
					.FontSize(AppTheme.FontSizeCaption).PrimaryText().HoverTip(value)));
		}

		return Group("settings", title, body);
	}

	private Element McpSection(IReadOnlyList<McpServerStatus> servers)
	{
		var body = new StackPanel().Spacing(4);

		if (servers.Count == 0)
		{
			body.Children(new TextBlock()
				.Text(Strings.SettingsMcpNone)
				.TextWrapping(TextWrapping.Wrap)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText());
		}

		foreach (var server in servers)
		{
			var state = server.Connected
				? Strings.SettingsMcpTools(server.ToolCount)
				: Strings.SettingsMcpFailed(server.Error ?? string.Empty);

			body.Children(new TextBlock()
				.Text($"{server.Name}：{state}")
				.TextWrapping(TextWrapping.Wrap)
				.FontSize(AppTheme.FontSizeCaption)
				.Foreground(server.Connected ? AppTheme.Success : AppTheme.Error));
		}

		// 改了 mcp.json 或服务器掉线时，可以不改会话直接重建连接。
		if (_reconnectMcp is { } reconnect)
		{
			body.Children(AppIcons.TextButton("refresh-cw", Strings.SettingsMcpReconnect, () => _ = ReconnectAsync(reconnect))
				.HorizontalAlignment(HorizontalAlignment.Left));
		}

		return Group("terminal", Strings.SettingsMcp, body);
	}

	private async Task ReconnectAsync(Func<Task> reconnect)
	{
		_toast.Text(Strings.SettingsMcpReconnecting).IsVisible(true);
		try
		{
			await reconnect().ConfigureAwait(true);
			_toast.Text(Strings.SettingsMcpReconnected).IsVisible(true);
			Refresh();
		}
		catch (Exception ex)
		{
			_toast.Text($"{Strings.SettingsMcpReconnect}：{ex.Message}").IsVisible(true);
		}
	}

	/// <summary>
	/// 快捷键表：列出全部可绑定动作、当前绑定，并允许改键。改动写回 <c>desktop.json</c>，重启后生效
	/// （MewUI 的窗口级快捷键在启动时注册）。
	/// </summary>
	private Element ShortcutSection()
	{
		var body = new StackPanel().Spacing(4);

		// 改键时用内联输入框，避免依赖对话框；这里保存正在编辑的动作标识。
		body.Children(new TextBlock().Text(Strings.ShortcutsEditHint).FontSize(AppTheme.FontSizeMicro).FaintText());

		foreach (var (id, description) in new (string, string)[]
		{
			(ShortcutActions.Palette, Strings.ShortcutPalette),
			(ShortcutActions.Send, Strings.ShortcutSend),
			(ShortcutActions.SearchSessions, Strings.ShortcutSearch),
			(ShortcutActions.NewSession, Strings.ShortcutNew),
			(ShortcutActions.OpenProject, Strings.OpenProject),
			(ShortcutActions.Models, Strings.SelectModel),
			(ShortcutActions.CloseTab, Strings.ShortcutClose),
			(ShortcutActions.Settings, Strings.ShortcutSettingsPanel),
			(ShortcutActions.Git, Strings.ShortcutGitPanel),
			(ShortcutActions.Files, Strings.ShortcutFilesPanel),
			(ShortcutActions.Terminal, Strings.ShortcutTerminal),
			(ShortcutActions.ToggleTheme, Strings.ShortcutTheme),
			(ShortcutActions.Refresh, Strings.ShortcutRefresh),
			(ShortcutActions.Branches, Strings.ShortcutBranches),
		})
		{
			body.Children(ShortcutRow(id, description));
		}

		// 固定键位（不参与改键）单独说明。
		body.Children(new TextBlock()
			.Text($"Ctrl+1…9 · {Strings.ShortcutSwitch}　Esc · {Strings.ShortcutClosePalette}")
			.FontSize(AppTheme.FontSizeMicro)
			.FaintText());

		var conflicts = _shortcuts.Conflicts();
		if (conflicts.Count > 0)
		{
			var lines = string.Join(
				"；",
				conflicts.Select(conflict => Strings.ShortcutsConflictLine(
					DescribeAction(conflict.OtherId),
					DescribeAction(conflict.Id),
					conflict.Binding)));

			body.Children(new TextBlock()
				.Text(Strings.ShortcutsConflict + lines)
				.FontSize(AppTheme.FontSizeMicro)
				.AccentText());
		}

		if (_shortcuts.Ignored.Count > 0)
		{
			body.Children(new TextBlock()
				.Text(Strings.ShortcutsIgnored(string.Join("、", _shortcuts.Ignored)))
				.FontSize(AppTheme.FontSizeMicro)
				.AccentText());
		}

		return Group("terminal", Strings.SettingsShortcuts, body);
	}

	/// <summary>
	/// 一行快捷键。改键时进入「按下组合键」的捕获状态：捕获走窗口级按键处理，
	/// 因此不受输入法影响（输入法会吞掉合成出来的可打印字符）。
	/// </summary>
	private Element ShortcutRow(string id, string description)
	{
		if (CapturingId != id)
		{
			return new DockPanel()
				.LastChildFill()
				.Children(
					AppIcons.Button("pencil", Strings.ShortcutsEdit, () => BeginCapture(id), size: 12).DockRight(),
					AppIcons.Button("rotate-ccw", Strings.ShortcutsReset, () => SaveShortcut(id, text: null), size: 12).DockRight(),
					new TextBlock()
						.Text(_shortcuts.Describe(id))
						.FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeMicro)
						.AccentText()
						.Width(120)
						.DockRight(),
					new TextBlock().Text(description).FontSize(AppTheme.FontSizeCaption).MutedText());
		}

		return new DockPanel()
			.LastChildFill()
			.Children(
				AppIcons.Button("x", Strings.Cancel, CancelCapture, size: 12).DockRight(),
				new TextBlock()
					.Text(Strings.ShortcutsPress)
					.FontSize(AppTheme.FontSizeCaption)
					.AccentText()
					.DockRight(),
				new TextBlock().Text(description).FontSize(AppTheme.FontSizeCaption).MutedText());
	}

	/// <summary>当前正在等待用户按下组合键的动作标识。</summary>
	public string? CapturingId { get; private set; }

	/// <summary>进入捕获状态：下一个带修饰键（或功能键）的按键会成为新绑定。</summary>
	public void BeginCapture(string id)
	{
		CapturingId = id;
		_toast.IsVisible(false);
		Refresh();
	}

	/// <summary>取消捕获。</summary>
	public void CancelCapture()
	{
		CapturingId = null;
		Refresh();
	}

	/// <summary>宿主捕获到按键后回调：是否接受这个组合（不接受时按键继续按原来的绑定处理）。</summary>
	public bool CaptureBinding(Key key, ModifierKeys modifiers)
	{
		if (CapturingId is not { } id)
		{
			return false;
		}

		// 只按下 Ctrl/Shift/Alt 本身（Key.None）不算绑定，继续等主键 ——
		// 否则用户一按 Ctrl 就被记录成 "Ctrl" 这种无效组合。
		if (key == Key.None)
		{
			return false;
		}

		// 至少要带一个修饰键，或者是功能键 —— 否则会和普通打字冲突。
		var usable = key is >= Key.F1 and <= Key.F24 || (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
		if (!usable)
		{
			_toast.Text(Strings.ShortcutsNeedsModifier);
			_toast.IsVisible(true);
			return false;
		}

		var text = new ShortcutBinding(key, modifiers).ToString();
		CapturingId = null;
		SaveShortcut(id, text);
		return true;
	}

	/// <summary>保存一个改键：<paramref name="text"/> 为 null 表示恢复默认。</summary>
	private void SaveShortcut(string id, string? text)
	{
		var map = new ShortcutMap(DesktopPreference.Load().Shortcuts);

		if (text is null || text.Trim().Length == 0)
		{
			map.Reset(id);
		}
		else if (!map.Rebind(id, text))
		{
			Toast(Strings.ShortcutsInvalid);
			return;
		}

		DesktopPreference.Load().WithShortcuts(map.Overrides).Save();
		_shortcuts = map;
		_toast.Text(Strings.ShortcutsRestart);
		_toast.IsVisible(true);
		Refresh();
	}

	/// <summary>动作标识 → 中文说明（冲突提示里用）。</summary>
	private static string DescribeAction(string id) =>
		ShortcutMap.Definitions.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))?.Description ?? id;

	/// <summary>关于：版本、内嵌字体与依赖，排查问题时一眼可见。</summary>
	private static Element AboutSection()
	{
		var body = new StackPanel().Spacing(4);

		foreach (var (label, value) in new (string, string)[]
		{
			(Strings.AboutVersion, AppInfo.Version),
			(Strings.AboutUiFont, AppFonts.HasUiFont ? AppFonts.UiDisplayName : $"{AppFonts.UiDisplayName}（系统字体）"),
			(Strings.AboutMonoFont, AppFonts.HasMonoFont ? AppFonts.Mono : $"{AppFonts.Mono}（系统字体）"),
			(Strings.AboutFramework, AppInfo.Framework),
			(Strings.AboutIcons, AppInfo.Icons),
			(Strings.AboutRuntime, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
		})
		{
			body.Children(new TextBlock()
				.Text($"{label}：{value}")
				.TextWrapping(TextWrapping.Wrap)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText());
		}

		return Group("settings", Strings.SettingsAbout, body);
	}

	/// <summary>归档管理：恢复单个会话，或清空归档。</summary>
	private Element ArchiveSection(IReadOnlyList<SessionListItem> archived)
	{
		var body = new StackPanel().Spacing(AppTheme.Space2);

		body.Children(new TextBlock()
			.Text(archived.Count == 0 ? Strings.ArchiveEmpty : Strings.ArchiveCount(archived.Count))
			.TextWrapping(TextWrapping.Wrap)
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText());

		if (archived.Count > 0)
		{
			var purge = new Button()
				.Content(new TextBlock().Text(Strings.ArchivePurge).FontSize(AppTheme.FontSizeMicro))
				.OnClick(() => _ = PurgeAsync());

			body.Children(purge);
		}

		foreach (var item in archived.Take(20))
		{
			var captured = item;
			body.Children(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.Children(
					new Button()
						.Content(new TextBlock().Text(Strings.ArchiveRestore).FontSize(AppTheme.FontSizeMicro))
						.OnClick(() => Restore(captured)),
					new TextBlock()
						.Text($"{captured.Display} · {captured.Cwd}")
						.FontSize(AppTheme.FontSizeMicro)
						.SubtleText()));
		}

		return Group("trash", Strings.SettingsArchive, body);
	}

	private void Restore(SessionListItem item)
	{
		try
		{
			new SessionCatalog().Restore(item.Path);
			Refresh();
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException)
		{
			AppLog.Failure(AppLog.For<SettingsView>(), "Restore archived session", ex);
		}
	}

	private async Task PurgeAsync()
	{
		if (!await MessageBox.ConfirmAsync(Strings.ArchivePurgeConfirm))
		{
			return;
		}

		new SessionCatalog().PurgeDeleted();
		Refresh();
	}

	private Element SessionSection(CodingAgentSession? session)
	{
		var body = new StackPanel().Spacing(4);

		if (session is null)
		{
			body.Children(new TextBlock()
				.Text(Strings.NoOpenSession)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText());
		}
		else
		{
			var display = SessionStatisticsDisplay.Create(session);
			foreach (var (label, value) in new (string, string)[]
			{
				(Strings.SettingsSessionFile, session.Session.SessionFile ?? "—"),
				(Strings.SettingsSessionId, session.Session.SessionId),
				(Strings.SettingsModel, $"{session.Model.Provider}/{session.Model.Id}"),
				(Strings.Thinking, Strings.ThinkingLevelName(session.ThinkingLevel)),
				(Strings.SettingsEntries, session.Session.EntryCount.ToString()),
			})
			{
				body.Children(new TextBlock()
					.Text($"{label}：{value}")
					.TextWrapping(TextWrapping.Wrap)
					.FontSize(AppTheme.FontSizeCaption)
					.MutedText());
			}
			foreach (var row in display.Rows())
			{
				body.Children(new TextBlock().Text($"{row.Label}：{row.Value}").HoverTip(row.Hint)
					.TextWrapping(TextWrapping.Wrap).FontSize(AppTheme.FontSizeCaption).MutedText());
			}
		}

		if (session is not null)
		{
			var model = session.Model;
			body.Children(AppIcons.Button(
				"star",
				Strings.MakeDefaultModel,
				() =>
				{
					try
					{
						var snapshot = _snapshot();
						var store = new ConfigurationStore(snapshot.AgentDir, snapshot.Cwd);
						var document = store.Read(ConfigFile.Settings);
						var root = document.Parse();
						root["defaultProvider"] = model.Provider;
						root["defaultModel"] = model.Id;
						store.Save(document, root.ToJsonString());
						_saved(ConfigFile.Settings);
						Toast(Strings.DefaultModelSaved(model.Provider, model.Id));
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
					{
						Toast(ex is ConfigurationConflictException ? Strings.ConfigConflict : Strings.ConfigFailed);
					}
				},
				size: 13));
		}

		return Group("message-square", Strings.SettingsSession, body);
	}
}
