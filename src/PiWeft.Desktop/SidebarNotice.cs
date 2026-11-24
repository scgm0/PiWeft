using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 侧栏顶部的一条提示（配置重载、模型目录更新、启动失败）。带可点的关闭键：
/// 原来是一行纯文字，只能等下一次提示覆盖它，占用的高度一直收不回来。
/// 左缘一条 2 DIP 的信息色短线沿用「纬线」语言，和错误行的错误色短线同一套结构。
internal sealed class SidebarNotice
{
	private readonly Border _view;
	private readonly TextBlock _text;

	public SidebarNotice()
	{
		_text = new TextBlock()
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextWrapping(TextWrapping.Wrap)
			.VerticalAlignment(VerticalAlignment.Center);

		var close = AppIcons.Button("x", Strings.DismissNotice, Clear, size: 11)
			.VerticalAlignment(VerticalAlignment.Top);
		close.Padding(new Thickness(6, 6, 6, 6));
		close.MinHeight = 0;

		_view = new Border()
			.Background(AppTheme.Transparent)
			.Padding(new Thickness(AppTheme.Space2, 2, 0, 2))
			.IsVisible(false)
			.Child(new DockPanel().LastChildFill().Children(close.DockRight(), _text))
			.WithTheme((theme, border) =>
			{
				// 中性结构色：提示不是错误，不该用语义彩色抢注意力。
				border.BorderBrush = AppTheme.For(theme).Border;
				border.NonUniformBorderThickness = new Thickness(AppTheme.ThreadWidth, 0, 0, 0);
			});
	}

	public UIElement View => _view;

	/// 与旧 TextBlock 字段同名，调用点不必逐处改写：写入非空文本即显示，写入空串即收起。
	public SidebarNotice Text(string value)
	{
		_text.Text(value);
		_view.IsVisible(value.Length > 0);
		return this;
	}

	public void Clear() => Text(string.Empty);
}
