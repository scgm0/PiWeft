using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Security;

namespace PiWeft.Desktop;

/// 起始页输入卡的工具栏与会话 composer 同构：左群是 + 菜单、快捷消息、模式与安全档位，
/// 中群是模型与思考强度，右群是开始键。选择先收在窗口侧的草稿里，提交时由 MainWindow
/// 一次性写进新会话（见 <see cref="SessionTab.ApplyStartChoices"/>）。
internal sealed partial class WelcomeSurface
{
	private const double IconColumnWidth = 16;

	private readonly ComposerAddMenu _addMenu = new();

	private readonly ComposerPicker _composerPicker = new();

	private readonly ComposerQuickMessages _quickMessages = new();

	private readonly SecurityMenu _securityMenu = new();

	private readonly ThinkingPicker _thinkingPicker = new();

	private readonly List<(string Path, ImageContent Content)> _pendingImages = [];

	private Button _addButton = null!;

	private Button _quickMessagesButton = null!;

	private Button _modeChip = null!;

	private readonly TextBlock _modeChipLabel = new();

	private Image _goalModeIcon = null!;

	private Image _planModeIcon = null!;

	private Button _securityButton = null!;

	private Image _securityOffIcon = null!;

	private Image _securityAlertIcon = null!;

	private Image _securityCheckIcon = null!;

	private Image _securityCustomIcon = null!;

	private SecurityConfig _securityConfig = SecurityConfig.Off;

	private ToolSecurity _securityLevel = ToolSecurity.Off;

	private bool _securityHasOverride;

	private Button _modelButton = null!;

	private readonly TextBlock _modelLabel = new();

	private Button _thinkingButton = null!;

	private readonly TextBlock _thinkingLabel = new();

	private IReadOnlyList<Model> _models = [];

	private Model? _model;

	private string _thinking = ThinkingLevels.Off;

	private int _loadingAttachments;

	/// 当前目标目录的技能与提示词快照。打开选择器时才取，换目录后自然重新解析。
	public Func<ResourceSnapshot>? ResourceProvider { get; set; }

	public event Action<Model>? ModelPicked;

	public event Action<string>? ThinkingPicked;

	/// 选了哪个安全档位；null 表示跟随默认档位。
	public event Action<string?>? SecurityPicked;

	public event Action<string>? NotifyError;

	public event Action? QuickMessagesSettingsRequested;

	public void SetModels(IReadOnlyList<Model> models) => _models = models;

	/// 模型铭牌与会话同款：provider/model · 强度；模型或环境未就绪时两个按钮一起不占位。
	public void SetModel(Model? model, string thinking)
	{
		_model = model;
		var ready = model is not null;
		_modelButton.IsVisible(ready);
		_thinkingButton.IsVisible(ready);
		if (!ready)
		{
			_modelLabel.Text(string.Empty);
			_thinkingLabel.Text(string.Empty);
			return;
		}

		var name = model!.Name.Length > 0 ? model.Name : model.Id;
		_modelLabel.Text($"{model.Provider}/{name}");
		_modelButton.HoverTip($"{model.Provider}/{model.Id}\n{Strings.ModelCapabilities(model)}");
		SetThinking(thinking);
	}

	public void SetThinking(string level)
	{
		_thinking = level;
		_thinkingLabel.Text($"· {level}");
		_thinkingButton.HoverTip($"{Strings.ThinkingLevelTitle}：{Strings.ThinkingLevelName(level)}");
	}

	/// 安全档位：起始页还没有会话文件，先按默认档位或用户本次的选择显示。
	/// 总开关关闭时按钮压暗，悬停说明为什么不能选。
	public void SetSecurity(SecurityConfig config, ToolSecurity level, bool hasOverride)
	{
		_securityConfig = config;
		_securityLevel = level;
		_securityHasOverride = hasOverride;

		var icon = config.Enabled ? SecurityMenu.IconFor(level.Level) : "shield-off";
		_securityOffIcon.IsVisible(icon == "shield-off");
		_securityAlertIcon.IsVisible(icon == "shield-alert");
		_securityCheckIcon.IsVisible(icon == "shield-check");
		_securityCustomIcon.IsVisible(icon == "shield");
		_securityButton.Opacity = config.Enabled ? 1 : 0.6;
		_securityButton.HoverTip(!config.Enabled
			? Strings.SecurityDisabledHint
			: hasOverride
				? Strings.SecurityOverrideHint(level.Name, level.Description)
				: Strings.SecurityFollowHint(level.Name, level.Description));
	}

	private UIElement BuildComposerToolbar()
	{
		_addButton = AppIcons.Button("layout-grid", Strings.ComposerAddTitle, ShowComposerAddMenu);
		_quickMessagesButton = AppIcons.Button("message-square", Strings.QuickMessagesTitle, ShowQuickMessages);
		_goalModeIcon = AppIcons.Icon("target", AppStyles.RoleSubtle, 12).IsVisible(false);
		_planModeIcon = AppIcons.Icon("list-checks", AppStyles.RoleSubtle, 12).IsVisible(false);
		_modeChipLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText();
		var modeChipClose = AppIcons.Icon("x", AppStyles.RoleSubtle, 11);
		_modeChip = new Button().Ghost()
			.Padding(new Thickness(6, 2, 5, 2))
			.MinHeight(AppTheme.ControlHeightMd)
			.CornerRadius(AppTheme.RadiusSm)
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(4)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new Grid().Width(14).Children(_goalModeIcon, _planModeIcon),
					_modeChipLabel,
					modeChipClose))
			.OnClick(() => SelectMode(ComposerMode.Normal))
			.IsVisible(false);

		_securityOffIcon = AppIcons.Icon("shield-off", AppStyles.RoleSubtle, 16).IsVisible(false);
		_securityAlertIcon = AppIcons.Icon("shield-alert", AppStyles.RoleSubtle, 16).IsVisible(false);
		_securityCheckIcon = AppIcons.Icon("shield-check", AppStyles.RoleSubtle, 16).IsVisible(false);
		_securityCustomIcon = AppIcons.Icon("shield", AppStyles.RoleSubtle, 16).IsVisible(false);
		_securityButton = new Button().Ghost()
			.Content(new Grid().Width(IconColumnWidth).Children(
				_securityOffIcon, _securityAlertIcon, _securityCheckIcon, _securityCustomIcon))
			.OnClick(ShowSecurityMenu);

		_modelLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText().TextTrimming(TextTrimming.CharacterEllipsis);
		_modelButton = new Button().Ghost().MaxWidth(248).MinHeight(AppTheme.ControlHeightMd)
			.Content(_modelLabel)
			.OnClick(ShowModelPicker)
			.IsVisible(false);

		// 思考强度与模型名同级：都是安静的 ghost 文本，点开才出现强度弹层。
		_thinkingLabel.FontSize(AppTheme.FontSizeCaption).PrimaryText();
		var thinkingChevron = new GlyphElement()
			.Kind(GlyphKind.ChevronDown)
			.GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center);
		_thinkingButton = new Button().Ghost().MinHeight(AppTheme.ControlHeightMd)
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(_thinkingLabel, thinkingChevron))
			.OnClick(ShowThinkingPicker)
			.IsVisible(false);

		var leftActions = new WrapPanel().Spacing(4).VerticalAlignment(VerticalAlignment.Center)
			.Children(_addButton, _quickMessagesButton, _modeChip, _securityButton);
		var centerActions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.Children(_modelButton, _thinkingButton);
		var rightActions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Children(_startButton);

		return new Grid().Columns("Auto,1*,Auto")
			.Children(leftActions.Column(0), centerActions.Column(1), rightActions.Column(2));
	}

	private void SelectMode(ComposerMode mode)
	{
		if (_disposed)
		{
			return;
		}

		_mode = mode == _mode ? ComposerMode.Normal : mode;
		UpdateModeChip();
	}

	internal void SetModeForTests(ComposerMode mode) => SelectMode(mode);

	private void UpdateModeChip()
	{
		var actual = _mode;
		_goalModeIcon.IsVisible(actual == ComposerMode.Goal);
		_planModeIcon.IsVisible(actual == ComposerMode.Plan);
		_modeChip.IsVisible(actual != ComposerMode.Normal);
		_modeChipLabel.Text(actual switch
		{
			ComposerMode.Goal => Strings.ModeGoal,
			ComposerMode.Plan => Strings.ModePlan,
			_ => string.Empty,
		});
		if (actual == ComposerMode.Goal)
		{
			_modeChip.HoverTip(Strings.ModeGoalExit);
		}
		else if (actual == ComposerMode.Plan)
		{
			_modeChip.HoverTip(Strings.ModePlanExit);
		}

		RefreshHintText();
	}

	private void ShowComposerAddMenu()
	{
		if (_disposed)
		{
			return;
		}

		HoverHints.Cancel(_addButton);
		_addMenu.Show(_addButton, new ComposerAddMenu.Options(
			_mode,
			() => _ = AttachFilesAsync(),
			ShowSkillPicker,
			ShowPromptPicker,
			SelectMode));
	}

	private void ShowSkillPicker()
	{
		if (_disposed)
		{
			return;
		}

		var skills = ResourceProvider?.Invoke().Skills ?? [];
		_composerPicker.Show(View, Strings.SkillPickerTitle, Strings.SkillPickerPlaceholder,
			Strings.SkillPickerEmpty, Strings.SkillPickerNoMatch,
			[.. skills.Select(static skill => new ComposerPicker.Item(
				"sparkles", $"skill:{skill.Name}", skill.Name, skill.Description, skill.Origin.Display))],
			InsertPickerCommand);
	}

	private void ShowPromptPicker()
	{
		if (_disposed)
		{
			return;
		}

		var templates = ResourceProvider?.Invoke().Templates ?? [];
		_composerPicker.Show(View, Strings.PromptPickerTitle, Strings.PromptPickerPlaceholder,
			Strings.PromptPickerEmpty, Strings.PromptPickerNoMatch,
			[.. templates.Select(static template => new ComposerPicker.Item(
				"file-text", template.Name, template.Name, template.Description ?? string.Empty, template.Origin.Display))],
			InsertPickerCommand);
	}

	private void InsertPickerCommand(ComposerPicker.Item item)
	{
		if (_disposed)
		{
			return;
		}

		_input.Text = ComposerPicker.AppendCommand(_input.Text, item.Token);
		_input.Select(_input.Text.Length, 0);
		_input.Focus();
	}

	private void ShowQuickMessages()
	{
		if (_disposed)
		{
			return;
		}

		if (_quickMessages.IsOpen)
		{
			_quickMessages.Hide();
			return;
		}

		HoverHints.Cancel(_quickMessagesButton);
		_quickMessages.Show(_quickMessagesButton, DesktopPreference.Load().QuickMessageList,
			_pendingImages.Count == 0 && _loadingAttachments == 0,
			InsertQuickMessage,
			SendQuickMessage,
			() => QuickMessagesSettingsRequested?.Invoke(),
			() => _input.Focus());
	}

	private void InsertQuickMessage(string text)
	{
		if (_disposed)
		{
			return;
		}

		_input.Text = QuickMessages.Append(_input.Text, text);
		_input.Select(_input.Text.Length, 0);
		_input.Focus();
	}

	/// 直发不经过草稿：与会话里的快捷消息同一个契约，有未发送附件时不可用。
	private void SendQuickMessage(string text)
	{
		if (_disposed || _pendingImages.Count > 0 || _loadingAttachments > 0)
		{
			return;
		}

		var message = text.Trim();
		if (message.Length == 0)
		{
			return;
		}

		Submitted?.Invoke(message);
	}

	private void ShowModelPicker()
	{
		if (_disposed || _model is null)
		{
			return;
		}

		_modelPicker.Show(View);
	}

	private void OnModelPicked(Model model)
	{
		if (_disposed)
		{
			return;
		}

		ModelPicked?.Invoke(model);
	}

	private void ShowThinkingPicker()
	{
		if (_disposed || _model is null)
		{
			return;
		}

		var levels = ThinkingLevelResolver.GetSupported(_model);
		if (levels.Count == 0)
		{
			return;
		}

		HoverHints.Cancel(_thinkingButton);
		_thinkingPicker.Show(
			_thinkingButton,
			levels,
			ThinkingLevelResolver.Clamp(_model, _thinking),
			OnThinkingPicked);
	}

	private void OnThinkingPicked(string level)
	{
		if (_disposed)
		{
			return;
		}

		ThinkingPicked?.Invoke(level);
	}

	private void ShowSecurityMenu()
	{
		if (_disposed)
		{
			return;
		}

		HoverHints.Cancel(_securityButton);
		_securityMenu.Show(
			_securityButton,
			_securityConfig.Enabled,
			_securityConfig.Levels,
			_securityLevel.Level,
			_securityHasOverride,
			levelId =>
			{
				SecurityPicked?.Invoke(levelId);
				_input.Focus();
			});
	}

	private async Task AttachFilesAsync()
	{
		string[]? files;
		try
		{
			files = await FileDialog.OpenFilesAsync(new OpenFileDialogOptions
			{
				Title = Strings.AttachFiles,
				Filters = [new FileFilter(Strings.AllFiles, "*.*")],
				Multiselect = true,
			});
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<WelcomeSurface>(), "Choose attachment", ex);
			NotifyError?.Invoke(Strings.AttachFailed(ex.Message));
			return;
		}

		if (files is null || _disposed)
		{
			return;
		}

		await AddFilesAsync(files);
	}

	/// 与会话 composer 的拖入/附件读取同一条规则：非图片插成 @ 引用，图片进附件条。
	private async Task AddFilesAsync(IReadOnlyList<string> paths)
	{
		var references = paths.Where(path => Directory.Exists(path) || !ComposerDrop.IsImage(path))
			.Select(ComposerDrop.Reference).ToArray();
		if (references.Length > 0)
		{
			var start = Math.Clamp(_input.SelectionStart, 0, _input.Text.Length);
			var length = Math.Clamp(_input.SelectionLength, 0, _input.Text.Length - start);
			var insertion = " " + string.Join(" ", references) + " ";
			_input.Text = _input.Text.Remove(start, length).Insert(start, insertion);
			_input.Select(start + insertion.Length, 0);
		}

		_loadingAttachments++;
		try
		{
			foreach (var path in paths.Where(path => File.Exists(path) && ComposerDrop.IsImage(path)))
			{
				try
				{
					var content = await Task.Run(() => ComposerDrop.ReadImageAsync(path)).ConfigureAwait(false);
					Dispatch(() => AddPendingImage(path, content));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
				{
					Dispatch(() => { if (!_disposed) NotifyError?.Invoke(Strings.AttachmentReadFailed); });
				}
			}
		}
		finally
		{
			Dispatch(() => _loadingAttachments--);
		}
	}

	private void AddPendingImage(string path, ImageContent content)
	{
		if (_disposed || _pendingImages.Any(item => item.Path == path))
		{
			return;
		}

		var total = _pendingImages.Sum(item => item.Content.Data.Length * 3L / 4);
		if (_pendingImages.Count >= ComposerDrop.MaxImages ||
			total + content.Data.Length * 3L / 4 > ComposerDrop.MaxTotalBytes)
		{
			NotifyError?.Invoke(Strings.AttachmentLimit);
			return;
		}

		_pendingImages.Add((path, content));
		RefreshAttachments();
	}

	private void RefreshAttachments()
	{
		_attachmentBar.Clear();
		if (_pendingImages.Count == 0)
		{
			_attachmentBar.IsVisible(false);
			return;
		}

		foreach (var (path, _) in _pendingImages)
		{
			var captured = path;
			var remove = AppIcons.Button("x", Strings.RemoveAttachment, () =>
			{
				_pendingImages.RemoveAll(item => item.Path == captured);
				RefreshAttachments();
			}, size: 12);
			var chip = new Border()
				.CornerRadius(AppTheme.RadiusXs)
				.Padding(new Thickness(6, 2, 2, 2))
				.WithTheme((theme, border) => border.Background = AppTheme.For(theme).CardBackgroundAlt)
				.Child(new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(2)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(
						new TextBlock().Text(Path.GetFileName(path))
							.FontSize(AppTheme.FontSizeMicro).MutedText()
							.VerticalAlignment(VerticalAlignment.Center),
						remove));
			_attachmentBar.Children(chip);
		}

		_attachmentBar.IsVisible(true);
	}

	private void DisposeComposerPopups()
	{
		_modelPicker.CloseInstantly();
		_thinkingPicker.Dispose();
		_addMenu.Dispose();
		_composerPicker.CloseInstantly();
		_quickMessages.Dispose();
		_securityMenu.Dispose();
	}
}
