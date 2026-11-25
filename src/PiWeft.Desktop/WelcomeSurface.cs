using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;
using Aprillz.MewUI.Svg;

namespace PiWeft.Desktop;

/// 没有会话打开时的起始页：品牌标 + 一句标语 + 能直接开新会话的输入卡 + 三枚起步建议。
/// 输入卡沿用会话 composer 的表面与交互（Enter 开始、Shift+Enter 换行、起步建议只填草稿），
/// 落点由卡上的项目 chip 决定。MainWindow 负责目标目录、菜单与真正建会话。
internal sealed class WelcomeSurface
{
	private const string BrandResource = "PiWeft.Desktop.Resources.appicon.piweft.svg";

	private const double ColumnMaxWidth = 720;

	private const double BrandSize = 72;

	private const double InputHeight = 72;

	private readonly TextBlock _targetLabel = new();

	private readonly MultiLineTextBox _input;

	private readonly Grid _inputHost;

	private readonly TextBlock _model = new();

	private readonly Button _startButton;

	private TextBlock _hint;

	private bool _hintVisible = true;

	private bool _composing;

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
			.Height(InputHeight)
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

		_hint = ComposerHints.Build(Strings.WelcomePlaceholder);
		_inputHost = new Grid().Children(_input, _hint);

		_model
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center)
			.HoverTip(Strings.WelcomeModelTip);

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
					_inputHost,
					new Grid().Columns("*,Auto").Children(
						_model.Column(0),
						_startButton.Column(1))));

		var starters = StarterPrompts.Build(Prefill);

		var body = new TextBlock()
			.Text(Strings.WelcomeBody)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextAlignment(TextAlignment.Center)
			.TextWrapping(TextWrapping.Wrap)
			.MaxWidth(520)
			.HorizontalAlignment(HorizontalAlignment.Center);

		View = new CenteredWidthPanel()
		{
			MaxContentWidth = ColumnMaxWidth,
		}
			.VerticalAlignment(VerticalAlignment.Center)
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
	}

	public FrameworkElement View { get; }

	/// 项目 chip：MainWindow 在它上面挂目标目录的 ContextMenu。
	public Button TargetButton { get; }

	public Button StartButton => _startButton;

	public MultiLineTextBox Input => _input;

	public event Action<string>? Submitted;

	public event Action? TargetRequested;

	/// 显示新会话将落在哪个目录。路径放在悬停提示里，chip 上只留短名。
	public void SetTarget(string label, string cwd)
	{
		_targetLabel.Text(label);
		TargetButton.HoverTip(string.IsNullOrWhiteSpace(cwd)
			? Strings.WelcomeTargetTip
			: $"{Strings.WelcomeTargetTip}：{cwd}");
	}

	/// 底栏左侧的模型名。空串表示环境还没就绪，整段不占位。
	public void SetModel(string label)
	{
		_model.Text(label);
		_model.IsVisible(label.Length > 0);
	}

	public void Prefill(string text)
	{
		_input.Text = text;
		_input.Focus();
	}

	public void ClearDraft() => _input.Text = string.Empty;

	/// 测试与快捷入口共用的一次提交：空草稿不动作。
	public void Submit()
	{
		var text = _input.Text;
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		Submitted?.Invoke(text);
	}

	private void SyncHint() => SetHint(_input.Text.Length == 0 && !IsComposingNow());

	/// 显隐占位提示。复用同一节点时首次录制的光栅可能留在节点上（与会话 composer 同一个坑），
	/// 因此每次重新显示都换一枚新实例；隐藏则立即生效，不必等一拍。
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

		var fresh = ComposerHints.Build(Strings.WelcomePlaceholder);
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
}
