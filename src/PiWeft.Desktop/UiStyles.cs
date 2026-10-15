using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 按钮与卡片的外观约定，参考同类桌面客户端的层级：主操作醒目、次要操作安静、面板统一圆角与留白。
/// 放在一处，避免每个界面各自拼颜色。
/// </summary>
public static class UiStyles
{
	/// <summary>主按钮：强调色底、反色文字（颜色来自样式，换主题自动跟随）。</summary>
	public static Button Primary(this Button button) => button
		.StyleName(AppStyles.PrimaryButton)
		.Padding(new Thickness(14, 6, 14, 6))
		.MinHeight(AppTheme.ControlHeightMd);

	/// <summary>次要按钮：无边框、弱化文字，用于工具条。</summary>
	public static Button Ghost(this Button button) => button
		.StyleName(AppStyles.GhostButton)
		.Padding(new Thickness(8, 4, 8, 4))
		.MinHeight(AppTheme.ControlHeightSm);

	/// <summary>图标按钮：更小的内边距，只放一个图标。</summary>
	public static Button Icon(this Button button) => button
		.Ghost()
		.Padding(new Thickness(6, 2, 6, 2));

	/// <summary>普通卡片：颜色来自样式，换主题自动跟随。</summary>
	public static Border Card(this Border border) => border.StyleName(AppStyles.Card);

	/// <summary>小标题。</summary>
	public static TextBlock Caption(this TextBlock block) => block
		.FontSize(AppTheme.FontSizeMicro)
		.FontWeight(FontWeight.Bold)
		.SubtleText();

	// ---------------------------------------------------------------- 文本角色
	// TextBlock 不是 Control，用不了 StyleName；这里把角色记在 Tag 上，
	// 换主题时由 AppStyles.ApplyTextColors 遍历整棵树重新上色。

	public static TextBlock PrimaryText(this TextBlock block) => block.Role(AppStyles.RolePrimary);

	public static TextBlock MutedText(this TextBlock block) => block.Role(AppStyles.RoleMuted);

	public static TextBlock SubtleText(this TextBlock block) => block.Role(AppStyles.RoleSubtle);

	public static TextBlock FaintText(this TextBlock block) => block.Role(AppStyles.RoleFaint);

	public static TextBlock AccentText(this TextBlock block) => block.Role(AppStyles.RoleAccent);

	public static TextBlock OnAccentText(this TextBlock block) => block.Role(AppStyles.RoleOnAccent);

	private static TextBlock Role(this TextBlock block, string role)
	{
		block.Tag = role;
		return block.Foreground(AppStyles.ColorForRole(role));
	}

	/// <summary>正文说明文字（弱化色 + 自动换行）。</summary>
	public static TextBlock Muted(this TextBlock block) => block
		.FontSize(AppTheme.FontSizeCaption)
		.MutedText()
		.TextWrapping(TextWrapping.Wrap);
}
