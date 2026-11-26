using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 设置页共用的版式零件。参考 PiDeck 的「去卡片化」设置行：分组仍是卡片，
/// 组内用发丝线分行的「标签 / 说明 + 右侧控件」，空态、徽标、键帽同套尺寸。
/// 安全门、提示音、模型目录这些独立面板也走这里，避免各自抄一份 Group/Row 后慢慢漂移。
internal static class SettingsUi
{
	/// 内容列宽：与设置弹窗的可用宽度对齐。
	public const double ContentWidth = 960;

	/// 右侧控件列的默认宽度：下拉、输入框统一到这里，行尾不参差。
	public const double ControlWidth = 260;

	/// <summary>带图标的分组卡；描述放在标题下面，数量、动作靠右。</summary>
	/// <param name="icon">Lucide 图标名。</param>
	/// <param name="title">分组标题。</param>
	/// <param name="content">组内容，通常由 <see cref="Rows"/> 组合。</param>
	/// <param name="description">标题下的一句说明；为空时不占行。</param>
	/// <param name="action">组头右端的动作（按钮/文本链接）。</param>
	/// <param name="meta">标题后的弱化计数，例如「24 项」。</param>
	/// <param name="bodyPadding">组内容的内边距；需要让行内按钮的悬停底与卡片边留出空隙时传入。</param>
	public static UIElement Group(
		string icon,
		string title,
		UIElement content,
		string? description = null,
		UIElement? action = null,
		string? meta = null,
		Thickness? bodyPadding = null)
	{
		var titleRow = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				AppIcons.Icon(icon, AppStyles.RoleSubtle, 14),
				new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption)
					.FontWeight(FontWeight.SemiBold).PrimaryText()
					.VerticalAlignment(VerticalAlignment.Center));
		if (meta is { Length: > 0 })
		{
			titleRow.Children(new TextBlock().Text(meta).FontSize(11).SubtleText()
				.VerticalAlignment(VerticalAlignment.Center));
		}

		var heading = new StackPanel().Spacing(2).Children(titleRow);
		if (description is { Length: > 0 }) heading.Children(Hint(description));

		// 组头默认只有标题；说明挂在标题下方而不是塞进正文，扫读时先看到分组再看到解释。
		var header = new DockPanel().LastChildFill();
		if (action is not null) header.Children(action.DockRight());
		header.Children(heading);
		var headerBorder = new Border().StyleName(AppStyles.CardHeader)
			.Padding(new Thickness(AppTheme.Space4, AppTheme.Space3, AppTheme.Space4, AppTheme.Space3))
			.Child(header).DockTop();
		var body = new Border()
			.Padding(bodyPadding ?? new Thickness(AppTheme.Space4, AppTheme.Space2, AppTheme.Space4, AppTheme.Space3))
			.Child(content);

		return new Border().StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusMd)
			.MaxWidth(ContentWidth)
			.ClipToBounds(true)
			.Child(new DockPanel().LastChildFill().Children(headerBorder, body));
	}

	/// <summary>把若干行叠起来；行与行之间只画一条结构发丝线。</summary>
	public static StackPanel Rows(IReadOnlyList<UIElement> rows)
	{
		var panel = new StackPanel();
		for (var index = 0; index < rows.Count; index++)
		{
			panel.Children(Line(rows[index], index > 0));
		}

		return panel;
	}

	/// <summary>单行：标签与说明在左，控件在右；<paramref name="stacked"/> 用于长文本框。</summary>
	/// <param name="title">行标题。</param>
	/// <param name="description">标题下的一句说明；为空时不占行。</param>
	/// <param name="control">右侧控件。</param>
	/// <param name="stacked">把控件放到标题下方并占满整行。</param>
	public static UIElement Row(
		string title,
		string? description,
		UIElement control,
		bool stacked = false)
	{
		var label = new StackPanel().Spacing(2);
		label.Children(new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption)
			.PrimaryText().TextWrapping(TextWrapping.Wrap));
		if (description is { Length: > 0 }) label.Children(Hint(description));

		if (stacked)
		{
			return new StackPanel().Spacing(AppTheme.Space2)
				.Padding(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2))
				.Children(label, control);
		}

		var grid = new Grid().Columns("*,auto");
		if (control is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;
		if (control is FrameworkElement aligned) aligned.HorizontalAlignment = HorizontalAlignment.Right;
		grid.Children(label.Column(0), control.Column(1));
		return new Border().Padding(new Thickness(0, AppTheme.Space2 + 2, 0, AppTheme.Space2 + 2)).Child(grid);
	}

	/// <summary>只有值的一行：右侧可放复制 / 打开等行尾动作。</summary>
	public static UIElement ValueRow(
		string label,
		string value,
		string? copy = null,
		string? openPath = null,
		bool mono = true,
		string? hint = null,
		Action<string>? copyAction = null,
		Action<string>? openAction = null)
	{
		var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center);
		if (copy is { Length: > 0 } && copyAction is not null)
		{
			actions.Children(IconAction("copy", Strings.SettingsCopyValue, () => copyAction(copy)));
		}

		if (openPath is { Length: > 0 } && openAction is not null)
		{
			var isDirectory = Directory.Exists(openPath);
			actions.Children(IconAction(
				isDirectory ? "folder-open" : "square-pen",
				isDirectory ? Strings.SettingsOpenFolder : Strings.ConfigOpenFile,
				() => openAction(openPath)));
		}

		var text = new TextBlock().Text(value.Length == 0 ? "—" : value)
			.FontSize(AppTheme.FontSizeCaption).PrimaryText().TextWrapping(TextWrapping.Wrap);
		if (mono) text.FontFamily(AppTheme.MonoFont);
		var tip = hint ?? (value.Length > 48 ? value : null);
		if (tip is not null) text.HoverTip(tip);

		var line = new DockPanel().LastChildFill();
		if (actions.Children.Count > 0) line.Children(actions.DockRight());
		line.Children(
			new TextBlock().Text(label).Width(116).FontSize(AppTheme.FontSizeCaption)
				.SubtleText().TextTrimming(TextTrimming.CharacterEllipsis).DockLeft(),
			text);
		return new Border().Padding(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2)).Child(line);
	}

	/// <summary>整行可点的外链：左侧行文案，右侧外链标记；点击交给系统浏览器。</summary>
	/// <param name="label">行文案。</param>
	/// <param name="url">目标地址，同时作悬停提示。</param>
	/// <param name="open">点击回调。</param>
	public static UIElement LinkRow(string label, string url, Action open)
	{
		var marker = AppIcons.Icon("external-link", AppStyles.RoleFaint, 12);
		marker.HorizontalAlignment(HorizontalAlignment.Right);
		return new Button().Ghost()
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			// 水平内边距给悬停底留出空隙；调用方把组内容的内边距同步减掉这一档，文字才能与值行对齐。
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space2, AppTheme.Space1))
			.Content(new Grid().Columns("*,auto").Spacing(AppTheme.Space2)
				.Children(
					new TextBlock().Text(label).FontSize(AppTheme.FontSizeCaption)
						.PrimaryText().VerticalAlignment(VerticalAlignment.Center)
						.TextTrimming(TextTrimming.CharacterEllipsis).Column(0),
					marker.Column(1)))
			.OnClick(open)
			.HoverTip(url);
	}

	/// <summary>二元开关行：标题在左，开关在右。</summary>
	public static UIElement SwitchRow(
		string title,
		string? description,
		bool value,
		Action<bool> changed,
		out ToggleSwitch toggle,
		bool enabled = true)
	{
		toggle = new ToggleSwitch().IsChecked(value).IsEnabled(enabled)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.VerticalAlignment(VerticalAlignment.Center);
		// 标题不在控件的 Content 里：自动化与测试都要能按名字找到这一行的开关。
		Aprillz.MewUI.Automation.AutomationProperties.SetName(toggle, title);
		toggle.OnCheckedChanged(changed);
		return Row(title, description, toggle);
	}

	/// <summary>字段标签 + 控件，用于配置表单的竖向排布。</summary>
	public static UIElement Field(string label, UIElement control, double labelMaxWidth = 0)
	{
		var caption = new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).SubtleText();
		if (labelMaxWidth > 0) caption.MaxWidth(labelMaxWidth).TextWrapping(TextWrapping.Wrap);
		return new StackPanel().Spacing(4).Children(caption, control);
	}

	/// <summary>说明文字：12px 次要色，换行。</summary>
	public static TextBlock Hint(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);

	/// <summary>空态：图标、一句标题、一句说明，可带一个动作。</summary>
	public static UIElement Empty(string icon, string title, string? description = null, UIElement? action = null)
	{
		var box = new StackPanel().Spacing(AppTheme.Space2)
			.Children(
				new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption).PrimaryText()
					.TextWrapping(TextWrapping.Wrap));
		if (description is { Length: > 0 }) box.Children(Hint(description));
		if (action is not null)
		{
			if (action is FrameworkElement element) element.HorizontalAlignment = HorizontalAlignment.Left;
			box.Children(new Border().Height(2));
			box.Children(action);
		}

		var glyph = new Border().Width(32).Height(32).CornerRadius(AppTheme.RadiusSm)
			.BorderThickness(1)
			.WithTheme(static (theme, border) =>
			{
				var palette = AppTheme.For(theme);
				border.Background = palette.CardBackgroundAlt;
				border.BorderBrush = palette.BorderSubtle;
			})
			.Child(AppIcons.Icon(icon, AppStyles.RoleSubtle, 15)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center));

		return new Grid().Columns("auto,*").Spacing(AppTheme.Space3)
			.Children(
				glyph.Column(0).VerticalAlignment(VerticalAlignment.Top)
					.Margin(new Thickness(0, 2, 0, 0)),
				box.Column(1));
	}

	/// <summary>弱化徽标：状态、来源、计数一类短文本，不用彩色药丸。</summary>
	public static Border Badge(string text, string? role = null)
	{
		var label = new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro);
		if (role is null) label.SubtleText(); else label.Role(role);
		return new Border().CornerRadius(AppTheme.RadiusXs)
			.Padding(new Thickness(6, 1, 6, 2)).Child(label)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.WithTheme(static (theme, border) => border.Background = AppTheme.For(theme).CardBackgroundAlt);
	}

	/// <summary>状态点 + 文本：运行态、扩展状态用同一套，不靠整行染色。</summary>
	/// <param name="role">语义色角色名，见 <see cref="AppStyles"/> 的 <c>Role*</c> 常量。</param>
	public static UIElement Status(string text, string? role = null)
	{
		var dot = new Border().Width(5).Height(5).CornerRadius(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = role switch
			{
				AppStyles.RoleSuccess => AppTheme.For(theme).Success,
				AppStyles.RoleWarning => AppTheme.For(theme).Warning,
				AppStyles.RoleError => AppTheme.For(theme).Error,
				_ => AppTheme.For(theme).TextSubtle,
			});
		return new StackPanel().Orientation(Orientation.Horizontal).Spacing(6)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(dot, new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro).MutedText());
	}

	/// <summary>键帽：快捷键表格里的按键，等宽、浅底、细边。</summary>
	public static Border KeyCap(string text)
	{
		return new Border().CornerRadius(AppTheme.RadiusXs)
			.BorderThickness(1)
			.Padding(new Thickness(6, 1, 6, 2))
			.Child(new TextBlock().Text(text).FontFamily(AppTheme.MonoFont)
				.FontSize(AppTheme.FontSizeMicro).PrimaryText())
			.WithTheme(static (theme, border) =>
			{
				border.BorderBrush = AppTheme.For(theme).BorderSubtle;
				border.Background = AppTheme.For(theme).CardBackgroundAlt;
			});
	}

	/// <summary>行尾纯图标动作；名字供自动化与悬停提示。</summary>
	public static Button IconAction(string icon, string tooltip, Action action)
	{
		var button = AppIcons.Button(icon, tooltip, action, size: 12)
			.MinWidth(AppTheme.Space6).MinHeight(AppTheme.Space6)
			.Padding(Thickness.Zero)
			.CornerRadius(AppTheme.RadiusXs);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(button, tooltip);
		return button;
	}

	/// <summary>给单行加一条上缘发丝线，供手工拼装的列表使用。</summary>
	public static Border DividerRow(UIElement content, bool topLine)
	{
		return Line(content, topLine);
	}

	/// <summary>提示条：错误行左缘一条 2 DIP 语义短线，正文仍用次要文字色。</summary>
	public static UIElement Alert(string text, string role = AppStyles.RoleWarning)
	{
		return new Border().CornerRadius(AppTheme.RadiusSm)
			.BorderThickness(new Thickness(2, 0, 0, 0))
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.Margin(new Thickness(0, AppTheme.Space3, 0, 0))
			.Child(new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro)
				.MutedText().TextWrapping(TextWrapping.Wrap))
			.WithTheme((theme, border) =>
			{
				var palette = AppTheme.For(theme);
				border.BorderBrush = role switch
				{
					AppStyles.RoleError => palette.Error,
					AppStyles.RoleSuccess => palette.Success,
					AppStyles.RoleInfo => palette.Info,
					_ => palette.Warning,
				};
				border.Background = palette.CardBackgroundAlt;
			});
	}

	private static Border Line(UIElement content, bool topLine)
	{
		// 行线用 Border 而不是 BorderSubtle：后者与卡片底只差几级灰，成排的 30 多行会糊成一片。
		var line = new Border().Child(content)
			.WithTheme(static (theme, border) => border.BorderBrush = AppTheme.For(theme).Border);
		if (topLine) line.BorderThickness(new Thickness(0, 1, 0, 0));
		return line;
	}
}
