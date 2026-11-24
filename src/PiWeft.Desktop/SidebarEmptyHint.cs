using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 侧栏列表为空时的引导块：图标 + 标题 + 说明。
/// 整句放在一个 TextBlock 里时，侧栏约 230 DIP 的正文区会从逗号处折行，
/// 标点落到下一行行首；拆成标题与说明两行后每行都能独立读完。
internal sealed class SidebarEmptyHint
{
	private readonly Border _view;
	private readonly Image _icon;
	private readonly TextBlock _title;
	private readonly TextBlock _hint;

	public SidebarEmptyHint(string title, string hint, string icon)
	{
		_title = new TextBlock().FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.Medium).PrimaryText();
		_hint = new TextBlock().FontSize(AppTheme.FontSizeCaption).SubtleText()
			.TextWrapping(TextWrapping.Wrap);
		_icon = AppIcons.Icon(icon, AppStyles.RoleSubtle, 18)
			.HorizontalAlignment(HorizontalAlignment.Left);
		_view = new Border()
			.Background(AppTheme.Transparent)
			.Margin(new Thickness(10, AppTheme.Space3, 8, 0))
			.VerticalAlignment(VerticalAlignment.Top)
			.IsHitTestVisible(false)
			.IsVisible(false)
			.Child(new StackPanel()
				.Spacing(AppTheme.Space2)
				.Children(_icon, _title, _hint));
	}

	public UIElement View => _view;

	public bool IsVisible => _view.IsVisible;

	public void Show(string title, string hint)
	{
		_title.Text(title);
		_hint.Text(hint);
		_hint.IsVisible(hint.Length > 0);
		_view.IsVisible(true);
	}

	public void Hide() => _view.IsVisible(false);
}
