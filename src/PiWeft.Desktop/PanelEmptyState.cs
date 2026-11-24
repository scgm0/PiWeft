using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 功能面板（文件、检查点等）的空态：图标 + 标题 + 一句说明。
/// 右侧面板的空态原来只有一个 13px 的灰行，和“这里没有东西”的层级不匹配；
/// 统一成左侧面板/空会话同款的「图标在上、标题、说明」结构，暂停点才有明确落点。
internal sealed class PanelEmptyState
{
	private readonly Border _view;
	private readonly TextBlock _title;
	private readonly TextBlock _hint;

	public PanelEmptyState(string icon, bool centered = false)
	{
		_title = new TextBlock().FontSize(AppTheme.FontSizeCaption).MutedText()
			.TextWrapping(TextWrapping.Wrap);
		_hint = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText()
			.TextWrapping(TextWrapping.Wrap);
		if (centered)
		{
			_title.TextAlignment(TextAlignment.Center);
			_hint.TextAlignment(TextAlignment.Center);
		}

		_view = new Border()
			.Background(AppTheme.Transparent)
			.VerticalAlignment(VerticalAlignment.Top)
			.IsHitTestVisible(false)
			.IsVisible(false)
			.Child(new StackPanel()
				.Spacing(AppTheme.Space2)
				.HorizontalAlignment(centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch)
				.Children(
					AppIcons.Icon(icon, AppStyles.RoleSubtle, 18)
						.HorizontalAlignment(centered ? HorizontalAlignment.Center : HorizontalAlignment.Left),
					_title,
					_hint));
	}

	public FrameworkElement View => _view;

	public void Show(string title, string hint = "")
	{
		_title.Text(title);
		_hint.Text(hint);
		_hint.IsVisible(hint.Length > 0);
		_view.IsVisible(true);
	}

	public void Hide() => _view.IsVisible(false);
}
