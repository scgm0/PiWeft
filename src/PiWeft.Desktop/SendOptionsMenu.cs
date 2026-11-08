using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 发送键右侧的投递方式菜单（PiDeck 的 Split Send 下拉同款）。
/// 每一项是「标题 + 一行灰色说明」两行，不用一整条长标签：
/// 投递方式是发送前最难猜的语义（加入当前回合还是排队到下一轮），
/// 只靠「— steer」这种尾缀看不出它何时生效。用 MewUI 的
/// <see cref="Popup"/> 托管自绘内容：边界翻转、外点关闭、Esc 都由框架负责，
/// 我们只负责列表本身。
/// 版面对齐 PiDeck 的下拉：图标列定宽，两行文字左缘对齐，行间留白，
/// 最后一项（中断）前用一条发丝线分组，说明过长省略、不换行。
internal sealed class SendOptionsMenu : IDisposable
{
	/// <param name="Icon">Lucide 图标名。</param>
	/// <param name="Title">主标题（动作本身）。</param>
	/// <param name="Description">副标题（协议名 + 生效时机）。</param>
	/// <param name="Run">选中后执行的发送动作。</param>
	/// <param name="Role">图标颜色角色；中断这类破坏性动作传 <see cref="AppStyles.RoleError"/>。</param>
	/// <param name="SeparatorBefore">与上面一项之间画一条分隔线（用于把中断单独分组）。</param>
	internal sealed record Item(
		string Icon,
		string Title,
		string Description,
		Action Run,
		string Role = AppStyles.RoleSubtle,
		bool SeparatorBefore = false);

	private const double IconColumnWidth = 22;

	private static readonly Thickness RowPadding = new(AppTheme.Space3 - 2, 7, AppTheme.Space3, 7);

	private readonly FadePopup _popup = new();

	public void Show(FrameworkElement anchor, IReadOnlyList<Item> items)
	{
		// 原生弹窗需要真实后端；无头（单元测试、诊断脚本未 Run）时静默降级，
		// 不把一次「点开菜单」升级成异常。
		if (!Application.IsRunning)
		{
			return;
		}

		// 先收掉上一次：标签被移出窗口时 anchor 可能已经找不到窗口，旧菜单不能留着。
		CloseInstantly();
		if (anchor.FindVisualRoot() is not Window)
		{
			return;
		}

		_popup.Show(Build(items), anchor, HoverHints.Anchor(anchor), PopupAnchorSide.Above);
	}

	public bool IsOpen => _popup.IsOpen;

	internal UIElement? ContentForTests => _popup.Content;

	/// 释放原生弹窗。Popup 持有自己的原生窗口资源，标签关闭时只 Close 不 Dispose 会泄漏；
	/// 每个会话标签各有一个菜单实例，反复开关标签就会累积。
	public void Dispose()
	{
		CloseInstantly();
		_popup.Dispose();
	}

	public void Close() => _popup.Close();

	/// 立即关掉，不做淡出：同一个入口马上要重开，或随标签页一起收起。
	public void CloseInstantly() => _popup.CloseInstantly();

	internal UIElement BuildForTests(IReadOnlyList<Item> items) => Build(items);

	private UIElement Build(IReadOnlyList<Item> items)
	{
		var list = new StackPanel().Spacing(1);
		foreach (var item in items)
		{
			if (item.SeparatorBefore)
			{
				list.Children(Separator());
			}

			list.Children(Row(item));
		}

		return new Border()
			.StyleName(AppStyles.PopupCard)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(6))
			// 宽度由内容决定，只给一个下限：说明文字一整行放得下，不会被挤成两行。
			.MinWidth(288)
			.Child(list);
	}

	private Button Row(Item item)
	{
		var captured = item;
		var icon = AppIcons.Icon(item.Icon, item.Role, 14)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.VerticalAlignment(VerticalAlignment.Top)
			.Margin(new Thickness(0, 2, 0, 0));
		var iconHost = new Grid().Width(IconColumnWidth)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.Children(icon);

		var title = new TextBlock()
			.Text(item.Title)
			.FontSize(AppTheme.FontSizeControl)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center);
		var description = new TextBlock()
			.Text(item.Description)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			// 说明是补充信息：过长时省略，换行会把菜单项撑成三行、失去对齐。
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var label = new StackPanel().Spacing(2).Children(title, description);

		var content = new Grid().Columns(GridLength.Pixels(IconColumnWidth), GridLength.Stars(1))
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(iconHost.Column(0), label.Column(1));

		return new Button()
			.Ghost()
			.Content(content)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Padding(RowPadding)
			.CornerRadius(AppTheme.RadiusSm)
			.OnClick(() =>
			{
				Close();
				captured.Run();
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
