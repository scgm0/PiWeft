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
	public const string Sidebar = "pi-sidebar";
	public const string Panel = "pi-panel";
	/// <summary>右侧功能面板的页签条：与侧栏同色，底部一条发丝线，与主区页签条区分。</summary>
	public const string RightPanelHeader = "pi-right-panel-header";
	public const string Card = "pi-card";
	public const string CardMuted = "pi-card-muted";
	/// <summary>浮层卡片（上下文/余额这类浮在内容之上的面板）：比 Card 多一条可见结构线，配合阴影使用。</summary>
	public const string PopupCard = "pi-popup-card";

	/// <summary>右侧面板外壳：与左侧栏同色，窗框两侧对称。</summary>
	public const string RightPanel = "pi-right-panel";

	/// <summary>会话底部的终端面板：主区底色，顶部一条发丝线把终端与对话分开。</summary>
	public const string TerminalDock = "pi-terminal-dock";

	public const string UserMessage = "pi-message-user";
	public const string ToolMessage = "pi-message-tool";
	public const string ToolError = "pi-message-tool-error";
	public const string Code = "pi-code";
	public const string Terminal = "pi-terminal";
	public const string Composer = "pi-composer";
	public const string SelectedRow = "pi-row-selected";

	// 弹窗（设置 / 配置管理）：窗口级浮层里的整块界面。
	public const string Dialog = "pi-dialog";
	public const string DialogHeader = "pi-dialog-header";
	public const string DialogNav = "pi-dialog-nav";
	public const string DialogContent = "pi-dialog-content";

	/// <summary>设置卡片的分区标题条：底色与卡片同色，只有一条底发丝线。</summary>
	public const string CardHeader = "pi-card-header";

	/// <summary>分段切换（系统设置 / 配置管理）：轨道容器。</summary>
	public const string SegmentTrack = "pi-segment-track";
	public const string SegmentButton = "pi-segment-button";
	public const string SegmentButtonActive = "pi-segment-button-active";

	// 窗框（自定义标题栏）
	public const string TitleBarSidebar = "pi-title-bar-sidebar";
	public const string TitleBarMain = "pi-title-bar-main";
	public const string TitleBarThread = "pi-title-bar-thread";
	public const string TitleBarSeparator = "pi-title-bar-separator";
	public const string TitleBarButton = "pi-title-bar-button";
	public const string TitleBarIconButton = "pi-title-bar-icon";

	/// <summary>标题栏里的开关型图标按钮（右侧面板开关）处于打开状态时的常驻浮起。</summary>
	public const string TitleBarIconButtonActive = "pi-title-bar-icon-active";
	public const string TitleBarCloseButton = "pi-title-bar-close";
	public const string TitleBarTab = "pi-title-bar-tab";
	public const string TitleBarTabActive = "pi-title-bar-tab-active";
	public const string TitleBarTabClose = "pi-title-bar-tab-close";
	public const string WindowFrame = "pi-window-frame";

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
	public const string CodeButton = "pi-button-code";
	public const string GhostButton = "pi-button-ghost";
	public const string SelectedButton = "pi-button-selected";

	// 文本角色（记在 TextBlock.Tag 上，换主题时遍历刷新）
	public const string RolePrimary = "primary";
	public const string RoleMuted = "muted";
	public const string RoleSubtle = "subtle";
	public const string RoleFaint = "faint";
	public const string RoleAccent = "accent";
	public const string RoleOnAccent = "on-accent";
	public const string RoleInfo = "info";
	public const string RoleThinking = "thinking";
	public const string RoleError = "error";
	public const string RoleSuccess = "success";

	/// <summary>按角色取当前主题下的文本颜色。</summary>
	public static Color ColorForRole(string role) => role switch
	{
		RolePrimary => AppTheme.TextPrimary,
		RoleMuted => AppTheme.TextMuted,
		RoleSubtle => AppTheme.TextSubtle,
		RoleFaint => AppTheme.TextFaint,
		RoleAccent => AppTheme.Accent,
		RoleOnAccent => AppTheme.TextOnAccent,
		RoleInfo => AppTheme.Info,
		RoleThinking => AppTheme.Thinking,
		RoleError => AppTheme.Error,
		RoleSuccess => AppTheme.Success,
		_ => AppTheme.TextPrimary,
	};

	/// <summary>
	/// 换肤后重建文本视图，并显式失效排布。
	/// </summary>
	/// <remarks>
	/// MewUI 0.22.1 的 <c>MultiLineTextBox</c> 在换肤重建视图时只调用 <c>InvalidateMeasure</c>；
	/// 若控件此前已被标成测量失效（换肤广播通常如此），这次调用会提前返回，排布标记不会被重新置位。
	/// 外层横向 ScrollViewer 以无限宽测量时，新视图的视口宽度仍停在 0，<c>TextViewLayout</c>
	/// 不会物化任何行，代码块/差异文本就只剩背景。这里补一次排布失效，让 ArrangeContent
	/// 把真实宽度写回视口后再绘制。
	/// </remarks>
	public static void RefreshTextView(MultiLineTextBox view)
	{
		view.InvalidateTextView();
		view.InvalidateArrange();
	}

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
			.With(Sidebar, () => Fill(typeof(Border), p => p.SidebarBackground))
			.With(Panel, () => Fill(typeof(Border), p => p.CardBackground))
			.With(RightPanelHeader, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).SidebarBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Border.NonUniformBorderThicknessProperty, new Thickness(0, 0, 0, 1)),
				]
			})
			.With(Card, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusMd),
				],
			})
			.With(CardMuted, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackgroundAlt)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
				],
			})
			.With(PopupCard, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusMd),
				],
			})
			.With(Composer, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusLg),
				],
			})
			// 用户气泡：内嵌表面 + 一条发丝线，和 PiDeck 一样靠边界与色阶区分，不铺大面积纯色。
			.With(UserMessage, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackgroundAlt)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusMd),
				],
			})
			// 工具调用条：无描边的色调条（图标 + 命令摘要 + 状态），展开后内容再进卡片。
			.With(ToolMessage, () => Fill(typeof(Border), p => p.CardBackground))
			.With(ToolError, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error)),
				],
			})
			.With(Code, () => Fill(typeof(Border), p => p.CodeBackground))
			.With(Terminal, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TerminalBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TerminalBorder)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusMd),
				],
			})
			.With(SelectedRow, () => Fill(typeof(Border), p => p.AccentSoft))
			.With(RightPanel, () => Fill(typeof(Border), p => p.SidebarBackground))
			// 底部终端面板：与主区同色，顶部一条发丝线就是它与对话之间的分界。
			.With(TerminalDock, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).WindowBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Border.NonUniformBorderThicknessProperty, new Thickness(0, 1, 0, 0)),
				]
			})
			// 设置弹窗：浮起表面 + 一条结构线，圆角由调用方给到 Lg（弹窗是唯一的“大”表面）。
			.With(Dialog, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusLg),
					Setter.Create(Border.ClipToBoundsProperty, true),
				]
			})
			// 弹窗头尾都用发丝线分区：标题栏只有一条底边，不铺重背景。
			.With(DialogHeader, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Border.NonUniformBorderThicknessProperty, new Thickness(0, 0, 0, 1)),
				]
			})
			// 弹窗里的导航轨：与主区同属一块表面，但比内容区深一档，右侧一条结构线。
			.With(DialogNav, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).SidebarBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Border.NonUniformBorderThicknessProperty, new Thickness(0, 0, 1, 0)),
				]
			})
			.With(DialogContent, () => Fill(typeof(Border), p => p.WindowBackground))
			.With(CardHeader, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Border.NonUniformBorderThicknessProperty, new Thickness(0, 0, 0, 1)),
				]
			})
			// 分段切换：轨道用内嵌表面，活动段用卡片底色 + 结构线浮起。
			.With(SegmentTrack, () => new Style(typeof(Border))
			{
				Setters =
				[
					// 轨道压在导航轨上：深色用比侧栏稍亮的主区底色，浅色用比导航轨暗一档的
					// 结构色，让「轨道 < 悬停 < 活动」的亮度阶梯在两个主题里都成立。
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => t.IsDark
						? AppTheme.For(t).WindowBackground : AppTheme.For(t).Border)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => t.IsDark
						? AppTheme.For(t).BorderSubtle : AppTheme.For(t).Border)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusMd),
				]
			})
			.With(SegmentButton, () => new Style(typeof(Button))
			{
				BasedOn = ButtonInteraction,
				Transitions = [Transition.Create(Control.BackgroundProperty), Transition.Create(Control.ForegroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground.WithAlpha(0))),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextMuted)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						// 悬停色取「轨道与活动段之间」的一档：深色 #141414→#252525→#2b2b2b，
						// 浅色 #e0e0e0→#f0f0f0→#ffffff。不再和轨道同色，浅色也能看见反馈。
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackgroundAlt))],
					},
					new StateTrigger
					{
						Match = VisualStateFlags.Pressed | VisualStateFlags.Enabled,
						Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).AccentSoft))],
					},
				],
			})
			.With(SegmentButtonActive, () => new Style(typeof(Button))
			{
				BasedOn = ButtonInteraction,
				Transitions = [Transition.Create(Control.BackgroundProperty), Transition.Create(Control.ForegroundProperty)],
				Setters =
				[
					// 活动段要比轨道明显跳一档：深色用悬停灰、浅色用白底，各带一条结构线。
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => t.IsDark
						? AppTheme.For(t).HoverBackground : AppTheme.For(t).CardBackground)),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => t.IsDark
						? AppTheme.For(t).Border : AppTheme.For(t).BorderSubtle)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).AccentSoft))],
					},
				],
			})
			// 标题栏左右两段：左段与侧栏同色、右段与主区同色，拼成 PiDeck 式的连续窗框。
			.With(TitleBarSidebar, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).SidebarBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Border.NonUniformBorderThicknessProperty, new Thickness(0, 0, 0, 1)),
				],
			})
			.With(TitleBarMain, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).WindowBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).BorderSubtle)),
					Setter.Create(Border.NonUniformBorderThicknessProperty, new Thickness(0, 0, 0, 1)),
				],
			})
			// 无边框窗口没有系统外框：由应用补圆角、可见边界与裁剪；阴影由 ShadowDecorator 负责。
			.With(WindowFrame, () => new Style(typeof(Border))
			{
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).WindowBackground)),
					Setter.Create(Control.BorderBrushProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Border)),
					Setter.Create(Control.BorderThicknessProperty, 1.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusLg),
					Setter.Create(Border.ClipToBoundsProperty, true),
				],
			})
			.With(TitleBarThread, () => Fill(typeof(Border), p => p.Accent))
			.With(TitleBarSeparator, () => Fill(typeof(Border), p => p.BorderSubtle))
			// 标题栏图标的悬停块：小圆角，不撑满整行。
			.With(TitleBarIconButton, () => new Style(typeof(Button))
			{
				Transitions = [Transition.Create(Control.BackgroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground.WithAlpha(0))),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextMuted)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusXs),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground)),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
						],
					},
					new StateTrigger
					{
						Match = VisualStateFlags.Pressed | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).AccentSoft)),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
						],
					},
				],
			})
			// 开关型标题栏按钮的「已打开」态：与悬停同档的浮起，再加一条强调弱底，
			// 让用户一眼看出右侧面板是从这个键唤醒的、现在正开着。
			.With(TitleBarIconButtonActive, () => new Style(typeof(Button))
			{
				Transitions = [Transition.Create(Control.BackgroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).AccentSoft)),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusXs),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground)),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
						],
					},
				],
			})
			// 窗口按钮是窗框的一部分，不是正文按钮：直角、等高、悬停才浮起。
			.With(TitleBarButton, () => new Style(typeof(Button))
			{
				Transitions = [Transition.Create(Control.BackgroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground.WithAlpha(0))),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextMuted)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, 0.0),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground)),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
						],
					},
					new StateTrigger
					{
						Match = VisualStateFlags.Pressed | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).AccentSoft)),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
						],
					},
				],
			})
			// 关闭键是唯一的语义色窗口按钮：悬停只给极淡的错误色底，不抢正文注意力。
			.With(TitleBarCloseButton, () => new Style(typeof(Button))
			{
				Transitions = [Transition.Create(Control.BackgroundProperty), Transition.Create(Control.ForegroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error.WithAlpha(0))),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextMuted)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, 0.0),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error.WithAlpha(0x2e))),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error)),
						],
					},
					new StateTrigger
					{
						Match = VisualStateFlags.Pressed | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error.WithAlpha(0x4a))),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error)),
						],
					},
				],
			})
			// 页签：非活动态透明、活动态浮起；文字色由 Button 的前景继承给标签。
			.With(TitleBarTab, () => new Style(typeof(Button))
			{
				Transitions = [Transition.Create(Control.BackgroundProperty), Transition.Create(Control.ForegroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground.WithAlpha(0))),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextMuted)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground)),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
						],
					},
				],
			})
			.With(TitleBarTabActive, () => new Style(typeof(Button))
			{
				Transitions = [Transition.Create(Control.BackgroundProperty), Transition.Create(Control.ForegroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackgroundAlt)),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextPrimary)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground)),
						],
					},
				],
			})
			.With(TitleBarTabClose, () => new Style(typeof(Button))
			{
				Transitions = [Transition.Create(Control.BackgroundProperty), Transition.Create(Control.ForegroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error.WithAlpha(0))),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextSubtle)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusXs),
					Setter.Create(Control.PaddingProperty, new Thickness(0)),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error.WithAlpha(0x2e))),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Error)),
						],
					},
				],
			})
			.With(CardButton, () => CardStyle(typeof(Button), p => p.CardBackground))
			.With(CardMutedButton, () => CardStyle(typeof(Button), p => p.CardBackgroundAlt))
			.With(TextPrimary, () => Text(p => p.TextPrimary))
			.With(TextMuted, () => Text(p => p.TextMuted))
			.With(TextSubtle, () => Text(p => p.TextSubtle))
			.With(TextFaint, () => Text(p => p.TextFaint))
			.With(AccentText, () => Text(p => p.Accent))
			.With(OnAccentText, () => Text(p => p.TextOnAccent))
			// 深色代码表面（工具输出/diff、图片预览遮罩）上的按钮：前景始终取 CodeText，
			// 悬停是白色低透明度，不跟随浅色主题的 Ghost 底。
			.With(CodeButton, () => new Style(typeof(Button))
			{
				Transitions = [Transition.Create(Control.BackgroundProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CodeText.WithAlpha(0))),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CodeText)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CodeText.WithAlpha(0x1a))),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CodeText)),
						],
					},
					new StateTrigger
					{
						Match = VisualStateFlags.Pressed | VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CodeText.WithAlpha(0x2e))),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CodeText)),
						],
					},
				],
			})
			.With(PrimaryButton, () => new Style(typeof(Button))
			{
				BasedOn = ButtonInteraction,
				// 主按钮的强调色在浅色是近黑、深色是近白，用透明度做悬停/按下反馈，
				// 两个主题的反馈方向一致，也不必为两种底色各配一套颜色。
				Transitions = [Transition.Create(UIElement.OpacityProperty)],
				Setters =
				[
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).Accent)),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextOnAccent)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
					// 常态不透明度写进样式：悬停/按下触发退出后能可靠回到 1。
					Setter.Create(UIElement.OpacityProperty, 1.0),
				],
				Triggers =
				[
					new StateTrigger
					{
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters = [Setter.Create(UIElement.OpacityProperty, 0.9)],
					},
					new StateTrigger
					{
						Match = VisualStateFlags.Pressed | VisualStateFlags.Enabled,
						Setters = [Setter.Create(UIElement.OpacityProperty, 0.78)],
					},
					// 主按钮没有“未保存”时可点：禁用态整体变灰，不保留醒目的强调白。
					// 字色取 TextSubtle 而不是 TextFaint：底色已经是 HoverBackground，Faint 在深色
					// 主题下只剩 1.5:1 的对比，“保存”会真的读不出来；Subtle 仍然明确弱于可用态。
					new StateTrigger
					{
						Exclude = VisualStateFlags.Enabled,
						Setters =
						[
							Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground)),
							Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextSubtle)),
						],
					},
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
					// 底色用「悬停色的零 alpha」而不是 Transparent：黑色全透明到浅灰的插值会经过深灰，
					// 悬停进出时肉眼看到闪一下（MewUI 的 flat 按钮样式也是这么处理的）。
					Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).HoverBackground.WithAlpha(0))),
					Setter.Create(Control.ForegroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).TextMuted)),
					Setter.Create(Control.BorderThicknessProperty, 0.0),
					Setter.Create(Control.CornerRadiusProperty, AppTheme.RadiusSm),
				],
				Triggers =
				[
					new StateTrigger
					{
						// 悬停色取「轨道与活动段之间」的一档：深色 #141414→#252525→#2b2b2b，
						// 浅色 #e0e0e0→#f0f0f0→#ffffff。不再和轨道同色，浅色也能看见反馈。
						Match = VisualStateFlags.Hot | VisualStateFlags.Enabled,
						Setters = [Setter.Create(Control.BackgroundProperty, (Func<Theme, Color>)(t => AppTheme.For(t).CardBackgroundAlt))],
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
