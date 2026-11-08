using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Security;

namespace PiWeft.Desktop;

/// 会话底栏的安全档位菜单。对齐 PiDeck 的 SecurityLevelMenu：
/// 状态提示行在顶，档位按「盾形图标 + 名称 + 当前勾」排列，只有存在会话覆盖时才出现
/// 「跟随默认档位」。总开关关闭时菜单仍可打开，只把档位行置灰并说明原因。
internal sealed class SecurityMenu : IDisposable
{
	/// 当前档位那一行右侧的勾。测试靠这个 Tag 找到勾，不去猜图标顺序。
	internal const string SelectedCheckTag = "security-level-check";

	private const double IconTile = 24;

	private static readonly Thickness RowPadding = new(AppTheme.Space2, 6, AppTheme.Space2, 6);

	private readonly FadePopup _popup = new();

	private sealed record Options(
		bool Enabled,
		IReadOnlyList<ToolSecurity> Levels,
		string EffectiveLevelId,
		bool HasOverride,
		Action<string?> Select);

	public void Show(FrameworkElement anchor, bool enabled, IReadOnlyList<ToolSecurity> levels,
		string effectiveLevelId, bool hasOverride, Action<string?> select)
	{
		if (!Application.IsRunning)
		{
			return;
		}

		Close();
		if (anchor.FindVisualRoot() is not Window)
		{
			return;
		}

		_popup.Show(Build(new Options(enabled, levels, effectiveLevelId, hasOverride, select)),
			anchor, HoverHints.Anchor(anchor), PopupAnchorSide.Above);
	}

	public bool IsOpen => _popup.IsOpen;

	internal UIElement? ContentForTests => _popup.Content;

	internal UIElement BuildForTests(bool enabled, IReadOnlyList<ToolSecurity> levels,
		string effectiveLevelId, bool hasOverride, Action<string?> select) =>
		Build(new Options(enabled, levels, effectiveLevelId, hasOverride, select));

	/// 档位对应的盾形图标，对齐 PiDeck 的 securityLevelIcon：严格=警报盾，标准=对勾盾，
	/// 关闭=划掉盾，自定义档=普通盾。
	internal static string IconFor(string? levelId) => levelId switch
	{
		ToolSecurity.StrictLevel => "shield-alert",
		ToolSecurity.StandardLevel => "shield-check",
		ToolSecurity.OffLevel => "shield-off",
		_ => "shield",
	};

	public void Dispose()
	{
		Close();
		_popup.Dispose();
	}

	public void Close() => _popup.Close();

	private UIElement Build(Options options)
	{
		var list = new StackPanel().Spacing(1)
			.Children(new TextBlock()
				.Text(options.Enabled ? Strings.SecurityMenuHint : Strings.SecurityDisabledHint)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText()
				.TextWrapping(TextWrapping.Wrap)
				.Margin(new Thickness(AppTheme.Space2, 6, AppTheme.Space2, 6)));

		list.Children(Separator());

		foreach (var level in options.Levels)
		{
			list.Children(LevelRow(level, options));
		}

		if (options.Enabled && options.HasOverride)
		{
			list.Children(Separator());
			list.Children(FollowRow(options));
		}

		return new Border()
			.StyleName(AppStyles.PopupCard)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(6))
			// 提示行是整句话；给一个能让它排成两行的宽度，档位名不会被挤断。
			.MinWidth(300)
			.MaxWidth(360)
			.Child(list);
	}

	private Button LevelRow(ToolSecurity level, Options options)
	{
		var selected = string.Equals(level.Level, options.EffectiveLevelId, StringComparison.Ordinal);
		var icon = AppIcons.Icon(IconFor(level.Level), selected ? AppStyles.RolePrimary : AppStyles.RoleSubtle, 14);
		var tile = new Border()
			.Width(IconTile)
			.Height(IconTile)
			.CornerRadius(AppTheme.RadiusSm)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = selected
				? AppTheme.For(theme).AccentSoft
				: AppTheme.For(theme).CardBackgroundAlt)
			.Child(icon.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center));

		var check = AppIcons.Icon("check", AppStyles.RolePrimary, 14)
			.IsVisible(selected)
			.HorizontalAlignment(HorizontalAlignment.Right);
		check.Tag = SelectedCheckTag;

		var row = new Button()
			.Ghost()
			.Content(new Grid()
				.Columns(GridLength.Pixels(IconTile), GridLength.Stars(1), GridLength.Pixels(24))
				.Children(
					tile.Column(0),
					new TextBlock()
						.Text(level.Name)
						.FontSize(AppTheme.FontSizeControl)
						.FontWeight(FontWeight.Medium)
						.PrimaryText()
						.VerticalAlignment(VerticalAlignment.Center)
						.Margin(new Thickness(AppTheme.Space2, 0, 0, 0))
						.Column(1),
					new Grid().Width(24).Children(check).Column(2)))
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Padding(RowPadding)
			.CornerRadius(AppTheme.RadiusSm)
			.IsEnabled(options.Enabled)
			.HoverTip(level.Description)
			.OnClick(() =>
			{
				Close();
				options.Select(level.Level);
			});
		return row;
	}

	private Button FollowRow(Options options)
	{
		var tile = new Border()
			.Width(IconTile)
			.Height(IconTile)
			.CornerRadius(AppTheme.RadiusSm)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).CardBackgroundAlt)
			.Child(AppIcons.Icon("rotate-ccw", AppStyles.RoleSubtle, 14)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center));

		return new Button()
			.Ghost()
			.Content(new Grid()
				.Columns(GridLength.Pixels(IconTile), GridLength.Stars(1))
				.Children(
					tile.Column(0),
					new TextBlock()
						.Text(Strings.SecurityFollowDefault)
						.FontSize(AppTheme.FontSizeControl)
						.PrimaryText()
						.VerticalAlignment(VerticalAlignment.Center)
						.Margin(new Thickness(AppTheme.Space2, 0, 0, 0))
						.Column(1)))
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Padding(RowPadding)
			.CornerRadius(AppTheme.RadiusSm)
			.HoverTip(Strings.SecurityClearOverride)
			.OnClick(() =>
			{
				Close();
				options.Select(null);
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
