using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 按钮与卡片的外观约定，参考同类桌面客户端的层级：主操作醒目、次要操作安静、面板统一圆角与留白。
/// 放在一处，避免每个界面各自拼颜色。
public static class UiStyles
{
	public static Button Primary(this Button button) => button
		.StyleName(AppStyles.PrimaryButton)
		.Padding(new Thickness(14, 6, 14, 6))
		.MinHeight(AppTheme.ControlHeightMd);

	public static Button Ghost(this Button button) => button
		.StyleName(AppStyles.GhostButton)
		.Padding(new Thickness(8, 4, 8, 4))
		.MinHeight(AppTheme.ControlHeightSm);

	public static Button Icon(this Button button) => button
		.Ghost()
		.Padding(new Thickness(6, 2, 6, 2));

	public static Border Card(this Border border) => border.StyleName(AppStyles.Card);

	public static TextBlock Caption(this TextBlock block) => block
		.FontSize(AppTheme.FontSizeMicro)
		.FontWeight(FontWeight.Bold)
		.SubtleText();

	// TextBlock 不是 Control，用不了 StyleName；这里把角色记在 Tag 上，
	// 换主题时由 AppStyles.ApplyTextColors 遍历整棵树重新上色。

	public static TextBlock PrimaryText(this TextBlock block) => block.Role(AppStyles.RolePrimary);

	public static TextBlock MutedText(this TextBlock block) => block.Role(AppStyles.RoleMuted);

	public static TextBlock SubtleText(this TextBlock block) => block.Role(AppStyles.RoleSubtle);

	public static TextBlock FaintText(this TextBlock block) => block.Role(AppStyles.RoleFaint);

	public static TextBlock AccentText(this TextBlock block) => block.Role(AppStyles.RoleAccent);

	public static TextBlock OnAccentText(this TextBlock block) => block.Role(AppStyles.RoleOnAccent);

	public static TextBlock InfoText(this TextBlock block) => block.Role(AppStyles.RoleInfo);

	public static TextBlock ThinkingText(this TextBlock block) => block.Role(AppStyles.RoleThinking);

	public static TextBlock ErrorText(this TextBlock block) => block.Role(AppStyles.RoleError);

	internal static TextBlock Role(this TextBlock block, string role)
	{
		block.Tag = role;
		return block.Foreground(AppStyles.ColorForRole(role));
	}

	public static TextBlock Muted(this TextBlock block) => block
		.FontSize(AppTheme.FontSizeCaption)
		.MutedText()
		.TextWrapping(TextWrapping.Wrap);
}
