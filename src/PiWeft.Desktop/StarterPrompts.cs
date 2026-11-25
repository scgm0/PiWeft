using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 空会话与起始页共用的三枚起步建议。点击只把提示词填进输入框，不替用户发送：
/// 用户还能改措辞，也不会因为点错就花掉一次模型调用。
internal static class StarterPrompts
{
	internal static readonly (string Icon, string Label, string Prompt)[] Items =
	[
		("folder-open", Strings.StarterProjectLabel, Strings.StarterProjectPrompt),
		("file-diff", Strings.StarterChangesLabel, Strings.StarterChangesPrompt),
		("list-checks", Strings.StarterTestsLabel, Strings.StarterTestsPrompt),
	];

	public static CenteredWrapPanel Build(Action<string> pick)
	{
		var row = new CenteredWrapPanel
		{
			Spacing = AppTheme.Space2,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		foreach (var (icon, label, prompt) in Items)
		{
			var content = new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(6)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					AppIcons.Icon(icon, AppStyles.RoleSubtle, 14),
					new TextBlock().Text(label).FontSize(AppTheme.FontSizeCaption).MutedText());
			row.Children(new Button()
				.StyleName(AppStyles.CardMutedButton)
				.Padding(new Thickness(AppTheme.Space2, 6, AppTheme.Space3, 6))
				.Content(content)
				.HoverTip(prompt)
				.OnClick(() => pick(prompt)));
		}

		return row;
	}
}
