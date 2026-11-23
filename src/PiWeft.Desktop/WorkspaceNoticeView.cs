using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 跨会话占用提示：同一项目里另一个会话在跑或改过文件时，在本会话输入框上方显示一行。
/// 不做成卡片：一行文字加一个跳转键，不参与卡片的展开与滚动。
internal sealed class WorkspaceNoticeView
{
	private readonly Border _view;
	private readonly TextBlock _text;
	private readonly Button _open;
	private Action? _openAction;

	public WorkspaceNoticeView()
	{
		_text = new TextBlock()
			.FontSize(AppTheme.FontSizeMicro)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		_open = AppIcons.TextButton("arrow-right", Strings.WorkspaceNoticeOpen, Open);
		_open.IsVisible(false);
		_view = new Border()
			.StyleName(AppStyles.CardMuted)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space1, AppTheme.Space1))
			.Child(new Grid()
				.Columns("Auto,*,Auto")
				.Spacing(AppTheme.Space1)
				.Children(
					AppIcons.Icon("activity", AppStyles.RoleInfo, 13).Column(0),
					_text.Column(1),
					_open.Column(2)))
			.IsVisible(false);
	}

	public Border View => _view;

	public void Set(string? text, Action? open)
	{
		var value = text ?? string.Empty;
		var visible = value.Length > 0;
		if (string.Equals(_text.Text, value, StringComparison.Ordinal) && _view.IsVisible == visible)
		{
			// 文本没变就只换跳转目标：每次侧栏重建都写文本会让输入区反复失效。
			_openAction = open;
			return;
		}

		_openAction = open;
		_text.Text(value);
		_view.IsVisible(visible);
		_open.IsVisible(visible && open is not null);
	}

	private void Open() => _openAction?.Invoke();
}
