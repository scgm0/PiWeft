using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 输入区左侧的「+」菜单：附件、技能、提示词与模式收进同一个入口，对齐 PiDeck 的加号菜单。
/// 前三项是一组动作，发丝线后是「模式」分组：目标 / 计划互斥，当前项打勾，再点一次回普通。
/// 用原生 <see cref="Popup"/> 托管列表：边界翻转、外点关闭、Esc 都由框架负责。
internal sealed class ComposerAddMenu : IDisposable
{
	/// 当前模式那一行右侧的勾。测试靠这个 Tag 找到勾，不去猜图标顺序。
	internal const string ModeCheckTag = "composer-mode-check";

	private const double IconColumnWidth = 22;

	private static readonly Thickness RowPadding = new(AppTheme.Space3 - 2, 6, AppTheme.Space3, 6);

	private readonly FadePopup _popup = new();

	/// 打开菜单需要的动作与当前模式。回调在菜单关闭后执行，动作可以安全地再开别的浮层。
	/// <param name="CanSelectMode">流式运行中计划/目标不能切换；附件与插入仍可用。</param>
	internal sealed record Options(
		ComposerMode Mode,
		Action Attach,
		Action PickSkill,
		Action PickPrompt,
		Action<ComposerMode> SelectMode,
		bool CanSelectMode = true);

	public void Show(FrameworkElement anchor, Options options)
	{
		// 与发送菜单同一约定：无头（单测、诊断脚本未 Run）时静默降级。
		if (!Application.IsRunning)
		{
			return;
		}

		Close();
		if (anchor.FindVisualRoot() is not Window)
		{
			return;
		}

		_popup.Show(Build(options), anchor, HoverHints.Anchor(anchor), PopupAnchorSide.Above);
	}

	public bool IsOpen => _popup.IsOpen;

	internal UIElement? ContentForTests => _popup.Content;

	internal UIElement BuildForTests(Options options) => Build(options);

	public void Dispose()
	{
		Close();
		_popup.Dispose();
	}

	public void Close() => _popup.Close();

	private UIElement Build(Options options)
	{
		var list = new StackPanel().Spacing(1)
			.Children(
				Row("paperclip", Strings.ComposerAddAttach, options.Attach),
				Row("sparkles", Strings.ComposerAddSkill, options.PickSkill),
				Row("file-text", Strings.ComposerAddPrompt, options.PickPrompt),
				Separator(),
				new TextBlock().Text(Strings.ComposerAddMode)
					.FontSize(AppTheme.FontSizeMicro)
					.SubtleText()
					.Margin(new Thickness(IconColumnWidth + 4, 2, AppTheme.Space3, 4)),
				ModeRow("target", Strings.ModeGoal, Strings.ModeGoalHint, ComposerMode.Goal, options),
				ModeRow("list-checks", Strings.ModePlan, Strings.ModePlanHint, ComposerMode.Plan, options));

		return new Border()
			.StyleName(AppStyles.PopupCard)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(6))
			// 中文菜单项比 PiDeck 的英文长，给一个下限让「上传附件」不被挤成两行。
			.MinWidth(208)
			.Child(list);
	}

	private Button Row(string icon, string title, Action run) => new Button()
		.Ghost()
		.Content(new Grid()
			.Columns(GridLength.Pixels(IconColumnWidth), GridLength.Stars(1))
			.Children(
				new Grid().Width(IconColumnWidth).Children(AppIcons.Icon(icon, AppStyles.RoleSubtle, 14)).Column(0),
				new TextBlock().Text(title)
					.FontSize(AppTheme.FontSizeControl)
					.PrimaryText()
					.VerticalAlignment(VerticalAlignment.Center)
					.Column(1)))
		.HorizontalAlignment(HorizontalAlignment.Stretch)
		.Padding(RowPadding)
		.CornerRadius(AppTheme.RadiusSm)
		.OnClick(() =>
		{
			Close();
			run();
		});

	private Button ModeRow(string icon, string title, string hint, ComposerMode mode, Options options)
	{
		var active = options.Mode == mode;
		var check = AppIcons.Icon("check", AppStyles.RolePrimary, 14).IsVisible(active);
		check.Tag = ModeCheckTag;
		return new Button()
			.Ghost()
			.Content(new Grid()
				.Columns(GridLength.Pixels(IconColumnWidth), GridLength.Stars(1), GridLength.Pixels(18))
				.Children(
					new Grid().Width(IconColumnWidth).Children(AppIcons.Icon(icon, AppStyles.RoleSubtle, 14)).Column(0),
					new TextBlock().Text(title)
						.FontSize(AppTheme.FontSizeControl)
						.PrimaryText()
						.VerticalAlignment(VerticalAlignment.Center)
						.Column(1),
					new Grid().Width(18).Children(check).Column(2)))
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Padding(RowPadding)
			.CornerRadius(AppTheme.RadiusSm)
			.IsEnabled(options.CanSelectMode)
			.HoverTip(hint)
			.OnClick(() =>
			{
				Close();
				options.SelectMode(active ? ComposerMode.Normal : mode);
			});
	}

	private static UIElement Separator()
	{
		var line = new Border()
			.Height(1)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Margin(new Thickness(0, 4, 0, 4))
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).BorderSubtle);
		return new Grid().Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space3, 0)).Children(line);
	}
}
