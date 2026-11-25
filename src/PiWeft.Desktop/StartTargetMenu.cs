using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 起始页项目 chip 的目录菜单。用与「+」菜单同一套原生浮层与行样式：
/// 图标列定宽、当前目录打勾、添加项目用发丝线分组。系统 ContextMenu 的深浅外观由平台决定，
/// 而且悬停提示会盖在它上面，不适合这里。
internal sealed class StartTargetMenu : IDisposable
{
	internal const string CheckTag = "start-target-check";

	private const double IconColumnWidth = 22;

	private const double CheckColumnWidth = 18;

	private static readonly Thickness RowPadding = new(AppTheme.Space3 - 2, 6, AppTheme.Space3, 6);

	private readonly FadePopup _popup = new();

	internal sealed record Item(string Cwd, string Label);

	internal sealed record Options(
		IReadOnlyList<Item> Targets,
		string Current,
		Action<string> Pick,
		Action AddProject);

	public void Show(FrameworkElement anchor, Options options)
	{
		// 与「+」菜单同一约定：无头（单测、诊断脚本未 Run）时静默降级。
		if (!Application.IsRunning)
		{
			return;
		}

		CloseInstantly();
		if (anchor.FindVisualRoot() is not Window)
		{
			return;
		}

		// chip 的悬停提示属于 chip 却不归浮层管，会叠在刚打开的菜单上。
		HoverHints.Cancel(anchor);
		_popup.Show(Build(options), anchor, HoverHints.Anchor(anchor), PopupAnchorSide.Below);
	}

	public bool IsOpen => _popup.IsOpen;

	internal UIElement? ContentForTests => _popup.Content;

	internal UIElement BuildForTests(Options options) => Build(options);

	public void Dispose()
	{
		CloseInstantly();
		_popup.Dispose();
	}

	public void Close() => _popup.Close();

	public void CloseInstantly() => _popup.CloseInstantly();

	private UIElement Build(Options options)
	{
		var list = new StackPanel().Spacing(1);
		foreach (var item in options.Targets)
		{
			list.Children(TargetRow(item, options));
		}

		list.Children(Separator());
		list.Children(ActionRow("plus", Strings.WelcomeTargetAdd, options.AddProject));

		return new Border()
			.StyleName(AppStyles.PopupCard)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(6))
			.MinWidth(208)
			.Child(list);
	}

	private Button TargetRow(Item item, Options options)
	{
		var active = SessionGroups.SamePath(item.Cwd, options.Current);
		var check = AppIcons.Icon("check", AppStyles.RolePrimary, 14).IsVisible(active);
		check.Tag = CheckTag;
		var cwd = item.Cwd;
		return Row(
			"folder-open",
			item.Label,
			check,
			() =>
			{
				Close();
				options.Pick(cwd);
			});
	}

	private Button ActionRow(string icon, string title, Action run) =>
		Row(icon, title, check: null, () =>
		{
			Close();
			run();
		});

	private static Button Row(string icon, string title, Image? check, Action run)
	{
		var checkSlot = new Grid().Width(CheckColumnWidth);
		if (check is not null)
		{
			checkSlot.Children(check);
		}

		var columns = new Grid()
			.Columns(GridLength.Pixels(IconColumnWidth), GridLength.Stars(1), GridLength.Pixels(CheckColumnWidth))
			.Children(
				new Grid().Width(IconColumnWidth)
					.Children(AppIcons.Icon(icon, AppStyles.RoleSubtle, 14)).Column(0),
				new TextBlock().Text(title)
					.FontSize(AppTheme.FontSizeControl)
					.PrimaryText()
					.TextTrimming(TextTrimming.CharacterEllipsis)
					.VerticalAlignment(VerticalAlignment.Center)
					.Column(1),
				checkSlot.Column(2));

		return new Button()
			.Ghost()
			.Content(columns)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Padding(RowPadding)
			.CornerRadius(AppTheme.RadiusSm)
			.OnClick(run);
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
