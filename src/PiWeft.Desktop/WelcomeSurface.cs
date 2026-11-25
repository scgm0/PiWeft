using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;
using Aprillz.MewUI.Svg;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// 没有会话打开时的起始页：品牌标 + 一句标语 + 能直接开新会话的输入卡 + 三枚起步建议。
/// 输入卡与会话 composer 用同一套表面、尺寸与工具栏（模型、思考、+ 菜单、快捷消息、安全档位），
/// 只是把选择先收在草稿里，提交时一次性带进新会话。MainWindow 负责目标目录、菜单与建会话。
internal sealed partial class WelcomeSurface : IDisposable
{
	private const string BrandResource = "PiWeft.Desktop.Resources.appicon.piweft.svg";

	private const double BrandSize = 72;

	private readonly TextBlock _targetLabel = new();

	private readonly MultiLineTextBox _input;

	private readonly Grid _inputHost;

	private readonly Button _startButton;

	private readonly WrapPanel _attachmentBar = new();

	private readonly ModelPicker _modelPicker;

	private TextBlock _hint;

	private bool _hintVisible = true;

	private bool _composing;

	private bool _disposed;

	private ComposerMode _mode = ComposerMode.Normal;

	// 掉落解析相对路径用的目标目录；打开拖放时才读，未设置时 ComposerDrop 会静默丢掉相对路径。
	private string _cwd = string.Empty;

	public WelcomeSurface()
	{
		var brand = new Image()
			.Source(SvgImageSource.FromResource(typeof(MainWindow).Assembly, BrandResource))
			.Width(BrandSize).Height(BrandSize)
			.HorizontalAlignment(HorizontalAlignment.Center);

		var slogan = new TextBlock()
			.Text(Strings.WelcomeSlogan)
			.FontSize(20)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.TextAlignment(TextAlignment.Center)
			.TextWrapping(TextWrapping.Wrap)
			.HorizontalAlignment(HorizontalAlignment.Center);

		_targetLabel
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.MaxWidth(360)
			.VerticalAlignment(VerticalAlignment.Center);

		// 起始页是新会话的落点选择：folder 图标与侧栏项目行一致，chevron 表示可以换目录。
		var targetButton = TargetButton = new Button().Ghost()
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					AppIcons.Icon("folder-open", AppStyles.RoleSubtle, 14),
					_targetLabel,
					new GlyphElement()
						.Kind(GlyphKind.ChevronDown)
						.GlyphSize(3)
						.VerticalAlignment(VerticalAlignment.Center)
						.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextSubtle)))
			.Padding(new Thickness(6, 2, 6, 2))
			.CornerRadius(AppTheme.RadiusSm)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.VerticalAlignment(VerticalAlignment.Center)
			.HoverTip(Strings.WelcomeTargetTip)
			.OnClick(() => TargetRequested?.Invoke());

		_startButton = AppIcons.PrimaryButton("arrow-up", Strings.WelcomeStart, Submit)
			.Height(AppTheme.ControlHeightMd)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, 0));

		_input = new MultiLineTextBox().PrimarySelection()
			.FontSize(AppTheme.FontSizeChat)
			.Placeholder(string.Empty)
			.Height(AppTheme.ComposerHeight)
			.CornerRadius(AppTheme.RadiusMd)
			.StyleName(AppStyles.TextInput)
			.BorderThickness(0)
			.Background(AppTheme.Transparent)
			.Padding(new Thickness(AppTheme.Space2))
			.OnTextChanged(_ => SyncHint())
			// 组合串画在输入框里：占位叠上去就是两层字。
			.OnTextCompositionStart(_ => CompositionStarted())
			.OnTextCompositionUpdate(_ => CompositionStarted())
			.OnTextCompositionEnd(_ => CompositionEnded())
			.OnKeyDown(key =>
			{
				// Enter 开始，Shift+Enter 换行，与会话 composer 同一套按键。
				if (key.Key == Key.Enter && !key.ShiftKey)
				{
					key.Handled = true;
					Submit();
				}
			});

		_hint = ComposerHints.Build(HintText());
		_inputHost = new Grid().Children(_input, _hint);
		_attachmentBar.Spacing(AppTheme.Space1).IsVisible(false);

		_modelPicker = new ModelPicker(() => _models, () => _model!, OnModelPicked);
		_modelPicker.Closed += () => _input.Focus();
		_composerPicker.Closed += () => _input.Focus();

		var card = new Border()
			.StyleName(AppStyles.Composer)
			.CornerRadius(AppTheme.RadiusLg)
			// 品牌块与输入卡之间多留一档：标与标语是一个视觉单元，卡是另一个。
			.Margin(new Thickness(0, AppTheme.Space2, 0, 0))
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space3, AppTheme.Space3, AppTheme.Space2))
			.Child(new StackPanel()
				.Spacing(AppTheme.Space2)
				.Children(
					targetButton,
					_attachmentBar,
					_inputHost,
					BuildComposerToolbar()));

		var starters = StarterPrompts.Build(Prefill);

		var body = new TextBlock()
			.Text(Strings.WelcomeBody)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextAlignment(TextAlignment.Center)
			.TextWrapping(TextWrapping.Wrap)
			.MaxWidth(520)
			.HorizontalAlignment(HorizontalAlignment.Center);

		var root = new CenteredWidthPanel()
		{
			MaxContentWidth = AppTheme.MessageMaxWidth + AppTheme.Space6,
		};
		// 与会话页一样整块接掉落：拖到品牌、卡片或起步建议上都算落进输入卡。
		root.AllowDrop()
			.OnDragEnter(OnFilesDragOver)
			.OnDragOver(OnFilesDragOver)
			.OnDrop(OnFilesDropped)
			.Children(new StackPanel()
				.Spacing(AppTheme.Space4)
				.Children(
					new StackPanel()
						.Spacing(AppTheme.Space2)
						.HorizontalAlignment(HorizontalAlignment.Center)
						.Children(brand, slogan),
					card,
					starters,
					body));
		View = root;
	}

	public FrameworkElement View { get; }

	/// 项目 chip：MainWindow 在它上面挂目标目录的浮层菜单。
	public Button TargetButton { get; }

	public Button StartButton => _startButton;

	public MultiLineTextBox Input => _input;

	public ComposerMode Mode => _mode;

	public Model? Model => _model;

	public string ThinkingLevel => _thinking;

	public IReadOnlyList<(string Path, ImageContent Content)> PendingImages => _pendingImages;

	public event Action<string>? Submitted;

	public event Action? TargetRequested;

	/// 显示新会话将落在哪个目录。路径放在悬停提示里，chip 上只留短名。
	public void SetTarget(string label, string cwd)
	{
		_cwd = cwd;
		_targetLabel.Text(label);
		TargetButton.HoverTip(string.IsNullOrWhiteSpace(cwd)
			? Strings.WelcomeTargetTip
			: $"{Strings.WelcomeTargetTip}：{cwd}");
	}

	public void Prefill(string text)
	{
		_input.Text = text;
		_input.Focus();
	}

	/// 提交成功后清空草稿：模式回到普通，图片与引用不留给下一次会话。
	public void ClearDraft()
	{
		_mode = ComposerMode.Normal;
		UpdateModeChip();
		_input.Text = string.Empty;
		_pendingImages.Clear();
		RefreshAttachments();
	}

	/// 测试与快捷入口共用的一次提交：空草稿且没有附件时不动作。
	public void Submit()
	{
		if (_disposed)
		{
			return;
		}

		if (_loadingAttachments > 0)
		{
			NotifyError?.Invoke(Strings.AttachmentLoading);
			return;
		}

		var text = _input.Text;
		if (string.IsNullOrWhiteSpace(text) && _pendingImages.Count == 0)
		{
			return;
		}

		Submitted?.Invoke(text);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		DisposeComposerPopups();
	}

	/// 占位提示跟着模式走：计划与目标各说这一句会被当成什么。
	private string HintText() => _mode switch
	{
		ComposerMode.Plan => Strings.ComposerPlaceholderFor(ComposerMode.Plan),
		ComposerMode.Goal => Strings.ComposerPlaceholderFor(ComposerMode.Goal),
		_ => Strings.WelcomePlaceholder,
	};

	private void SyncHint() => SetHint(_input.Text.Length == 0 && !IsComposingNow());

	/// 显隐占位提示。复用同一节点时首次录制的光栅可能留在节点上（与会话 composer 同一个坑），
	/// 因此每次重新显示都换一枚新实例；换文字时也换新实例。
	private void SetHint(bool visible)
	{
		if (visible == _hintVisible)
		{
			return;
		}

		_hintVisible = visible;
		if (!visible)
		{
			_hint.IsVisible(false);
			return;
		}

		var fresh = ComposerHints.Build(HintText());
		_inputHost.Remove(_hint);
		_inputHost.Add(fresh);
		_hint = fresh;
	}

	private void RefreshHintText()
	{
		if (!_hintVisible)
		{
			return;
		}

		var fresh = ComposerHints.Build(HintText());
		_inputHost.Remove(_hint);
		_inputHost.Add(fresh);
		_hint = fresh;
	}

	private void CompositionStarted()
	{
		_composing = true;
		if (_hintVisible)
		{
			_hintVisible = false;
			_hint.IsVisible(false);
		}
	}

	private void CompositionEnded()
	{
		_composing = false;
		SyncHint();
	}

	/// 以框架状态为准：事件可能迟到或丢，只看事件会让占位永久消失。
	private bool IsComposingNow() =>
		_input is ITextCompositionClient composition ? composition.IsComposing : _composing;

	/// MewUI 不装同步上下文：后台读附件后回 UI 线程走这里，与 SessionTab.Dispatch 同一套。
	private static void Dispatch(Action action)
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			action();
			return;
		}

		dispatcher.BeginInvoke(action);
	}
}
