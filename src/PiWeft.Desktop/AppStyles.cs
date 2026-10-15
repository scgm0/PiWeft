using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 命名样式表：界面里长期存在的容器与文本用它上色，颜色由 <see cref="Setter.Create{T}"/> 从主题解析。
/// 因此切换主题时 MewUI 会重新解析样式并重绘，<b>不需要重建界面</b> —— 这正是官方示例的做法。
/// </summary>
/// <remarks>
/// 只给需要跟随主题的**颜色**定义样式；字号、内边距之类的静态值仍写在构建代码里，读起来更直观。
/// 消息气泡和工具卡片同样使用命名样式，换肤保留正文控件与 Markdown。
/// </remarks>
public static class AppStyles
{
	// 容器
	public const string Window = "pi-window";
	public const string FramedWindow = "pi-window-framed";
	public const string FramedSidebar = "pi-sidebar-framed";
	public const string TextInput = "pi-text-input";
	public const string TextInputLine = "pi-text-input-line";
	public const string TopBar = "pi-topbar";
	public const string Sidebar = "pi-sidebar";
	public const string Panel = "pi-panel";
	public const string Card = "pi-card";
	public const string CardMuted = "pi-card-muted";
	public const string UserMessage = "pi-message-user";
	public const string ToolMessage = "pi-message-tool";
	public const string ToolError = "pi-message-tool-error";
	public const string Code = "pi-code";
	public const string Composer = "pi-composer";
	public const string SelectedRow = "pi-row-selected";

	/// <summary>
	/// 与 <see cref="Card"/> 同色但目标是 <see cref="Button"/> 的版本。
	/// 样式是按控件类型校验的：把 Border 的样式套到 Button 上会在运行时抛异常，
	/// 所以可点击的卡片必须用这一组。
	/// </summary>
	public const string CardButton = "pi-button-card";

	public const string CardMutedButton = "pi-button-card-muted";

	// 文本
	public const string TextPrimary = "pi-text-primary";
	public const string TextMuted = "pi-text-muted";
	public const string TextSubtle = "pi-text-subtle";
	public const string TextFaint = "pi-text-faint";
	public const string AccentText = "pi-text-accent";
	public const string OnAccentText = "pi-text-on-accent";

	// 按钮
	public const string PrimaryButton = "pi-button-primary";
	public const string GhostButton = "pi-button-ghost";
	public const string SelectedButton = "pi-button-selected";

	// 文本角色（记在 TextBlock.Tag 上，换主题时遍历刷新）
	public const string RolePrimary = "primary";
	public const string RoleMuted = "muted";
	public const string RoleSubtle = "subtle";
	public const string RoleFaint = "faint";
	public const string RoleAccent = "accent";
	public const string RoleOnAccent = "on-accent";

	/// <summary>按角色取当前主题下的文本颜色。</summary>
	public static Color ColorForRole(string role) => role switch
	{
		RolePrimary => AppTheme.TextPrimary,
		RoleMuted => AppTheme.TextMuted,
		RoleSubtle => AppTheme.TextSubtle,
		RoleFaint => AppTheme.TextFaint,
		RoleAccent => AppTheme.Accent,
		RoleOnAccent => AppTheme.TextOnAccent,
		_ => AppTheme.TextPrimary,
	};

	/// <summary>
	/// 遍历子树，把带角色标记的文本重新上色。换主题时调用，比重建界面便宜得多
	/// （只改颜色，不重新创建控件、也不重新解析 Markdown）。
	/// </summary>
	public static void ApplyTextColors(Element root)
	{
		if (root is TextBlock text && text.Tag is string role)
		{
			text.Foreground(ColorForRole(role));
		}
		else if (root is MultiLineTextBox { IsReadOnly: true, Tag: string textRole } message)
		{
			message.Foreground(ColorForRole(textRole));
		}

		if (root is IVisualTreeHost host)
		{
			host.VisitChildren(child =>
			{
				ApplyTextColors(child);
				return true;
			});
		}
	}

	private static readonly Style ButtonInteraction = new(typeof(Button))
	{
		Setters = [Setter.Create<CursorType?>(UIElement.CursorProperty, CursorType.Hand)],
		Triggers =
		[
			new StateTrigger
			{
				Exclude = VisualStateFlags.Enabled,
				Setters = [Setter.Create<CursorType?>(UIElement.CursorProperty, CursorType.Arrow)],
			},
		],
	};

	/// <summary>构建样式表。挂在 Window 上，作用域覆盖其所有后代。</summary>
	public static StyleSheet Create()
	{
		return new StyleSheet()
			.With<Button>(ButtonInteraction)
			.With(Window, () => Fill(typeof(Border), p => p.WindowBackground))
			.With(FramedWindow, () => CardStyle(typeof(Border), p => p.WindowBackground))
			.With(FramedSidebar, () => CardStyle(typeof(Border), p => p.SidebarBackground))
			.With(TextInput, () => new Style(typeof(MultiLineTextBox))
			{
				Setters = [Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border))],
			})
			.With(TextInputLine, () => new Style(typeof(TextBox))
			{
				Setters = [Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border))],
			})
			.With(TopBar, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).SidebarBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
				],
			})
			.With(Sidebar, () => Fill(typeof(Border), p => p.SidebarBackground))
			.With(Panel, () => Fill(typeof(Border), p => p.CardBackground))
			.With(Card, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusLg),
				],
			})
			.With(CardMuted, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackgroundAlt)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
				],
			})
			.With(Composer, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusLg),
				],
			})
			.With(UserMessage, () => Fill(typeof(Border), p => p.CardBackgroundAlt))
			.With(ToolMessage, () => CardStyle(typeof(Border), p => p.CardBackground))
			.With(ToolError, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error)),
				],
			})
			.With(Code, () => Fill(typeof(Border), p => p.CodeBackground))
			.With(SelectedRow, () => Fill(typeof(Border), p => p.AccentSoft))
			.With(CardButton, () => CardStyle(typeof(Button), p => p.CardBackground))
			.With(CardMutedButton, () => CardStyle(typeof(Button), p => p.CardBackgroundAlt))
			.With(TextPrimary, () => Text(p => p.TextPrimary))
			.With(TextMuted, () => Text(p => p.TextMuted))
			.With(TextSubtle, () => Text(p => p.TextSubtle))
			.With(TextFaint, () => Text(p => p.TextFaint))
			.With(AccentText, () => Text(p => p.Accent))
			.With(OnAccentText, () => Text(p => p.TextOnAccent))
			.With(PrimaryButton, () => new Style(typeof(Button))
			{
				BasedOn = ButtonInteraction,
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Accent)),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextOnAccent)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
				],
			})
			.With(SelectedButton, () => new Style(typeof(Button))
			{
				BasedOn = ButtonInteraction,
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).AccentSoft)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
				],
			})
			.With(GhostButton, () => new Style(typeof(Button))
			{
				BasedOn = ButtonInteraction,
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Transparent)),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextMuted)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground))],
					},
					new StateTrigger
					{
						Match = VisualStateFlags.Pressed | VisualStateFlags.Enabled,
						Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).AccentSoft))],
					},
				],
			});
	}

	/// <summary>卡片外观：底色 + 细边框 + 大圆角，目标类型由调用方指定。</summary>
	private static Style CardStyle(Type target, Func<AppPalette, Color> pick) => new(target)
	{
		BasedOn = typeof(Button).IsAssignableFrom(target) ? ButtonInteraction : null,
		Setters =
		[
			Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => pick(AppTheme.For(t)))),
			Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border)),
			Setter.Create(Control.BorderThicknessProperty, 1.0),
			Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
		],
	};

	private static Style Fill(Type target, Func<AppPalette, Color> pick) => new(target)
	{
		Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => pick(AppTheme.For(t))))],
	};

	private static Style Text(Func<AppPalette, Color> pick) => new(typeof(TextBlock))
	{
		Setters = [Setter.Create(TextElement.ForegroundProperty, (Func<Theme, Color>)(t => pick(AppTheme.For(t))))],
	};
}
