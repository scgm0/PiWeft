using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 「添加工具」下拉。按「内置工具 / 内置扩展工具」两段平铺整个目录，行是复选样式，
/// 点击只切换不关菜单：连续勾选是主路径，关一次就要重开一次太费事。
/// 被「扩展」关掉的内置扩展仍然列出，名字压暗并给出原因，藏起来只会让人以为目录里没这个工具。
/// 名字用 TextBlock 原样画：原生菜单会把 <c>_</c> 当助记符吃掉，ask_question 曾显示成 askquestion。
internal sealed class ToolPickerMenu : IDisposable
{
	/// 复选框里的勾。测试靠这个 Tag 找勾，不去猜图标顺序。
	internal const string CheckTag = "config-tool-check";

	private const double BoxSize = 16;

	// 勾列固定宽度：切换后文字变长变短不会推动名字列，弹层也不会因为内容重排而跳动。
	// 108 是「−ask_question」在 12px 等宽下的实测宽度，再窄就会向名字列溢。
	private const double MarkerWidth = 108;

	private static readonly Thickness RowPadding = new(AppTheme.Space2, 3, AppTheme.Space2, 3);

	private readonly FadePopup _popup = new();

	/// 打开菜单需要的当前状态。回调都读调用方的最新草稿，菜单自己不留配置副本。
	internal sealed record Options(
		IReadOnlyList<string> Catalog,
		IReadOnlySet<string> Blocked,
		Func<string, bool> IsEnabled,
		Func<string, bool> IsRemoved,
		Action<string> Toggle);

	public void Show(FrameworkElement anchor, Options options)
	{
		if (!Application.IsRunning)
		{
			return;
		}

		CloseInstantly();
		if (anchor.FindVisualRoot() is not Window)
		{
			return;
		}

		_popup.Show(Build(options), anchor, HoverHints.Anchor(anchor), PopupAnchorSide.Below);
	}

	public bool IsOpen => _popup.IsOpen;

	internal UIElement? ContentForTests => _popup.Content;

	internal UIElement BuildForTests(Options options) => Build(options);

	public void Dispose()
	{
		CloseInstantly();
		_popup.Dispose();
	}

	public void Close() => _popup.Close();

	public void CloseInstantly() => _popup.CloseInstantly();

	private UIElement Build(Options options)
	{
		var list = new StackPanel().Spacing(1);
		AddGroup(list, Strings.ConfigToolsGroupBuiltin, options, extension: false);
		AddGroup(list, Strings.ConfigToolsGroupExtension, options, extension: true);

		// 12 个工具整排下来不到 400px；上限给足，默认展开就能看到扩展工具那段，不用先滚动。
		// 屏幕矮或弹层被挤到窗口下方时再出滚动条。
		var scroll = new ScrollViewer().SmoothScrolling().Content(list)
			.AutoVerticalScroll().NoHorizontalScroll().MaxHeight(400);
		return new Border()
			.StyleName(AppStyles.PopupCard)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(6))
			.MinWidth(248)
			.MaxWidth(320)
			.Child(scroll);
	}

	private static void AddGroup(StackPanel list, string title, Options options, bool extension)
	{
		list.Children(new TextBlock()
			.Text(title)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.Margin(new Thickness(AppTheme.Space2 + 2, 6, AppTheme.Space2, 2)));
		foreach (var name in options.Catalog)
		{
			if (ConfigurationTools.IsExtension(name) == extension) list.Children(Row(name, options));
		}
	}

	private static Button Row(string name, Options options)
	{
		var enabled = options.IsEnabled(name);
		var blocked = options.Blocked.Contains(name);
		Theme? lastTheme = null;

		var check = AppIcons.Icon("check", AppStyles.RoleOnAccent, 10)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center)
			.IsVisible(enabled);
		check.Tag = CheckTag;

		var label = new TextBlock()
			.Text(name)
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeControl)
			.VerticalAlignment(VerticalAlignment.Center);
		var marker = new TextBlock()
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Right);

		var box = new Border()
			.Width(BoxSize)
			.Height(BoxSize)
			.CornerRadius(AppTheme.RadiusXs)
			.BorderThickness(1)
			.VerticalAlignment(VerticalAlignment.Center)
			.Child(check);

		void Paint(Theme theme)
		{
			var palette = AppTheme.For(theme);
			box.Background = enabled ? palette.Accent : palette.Transparent;
			// 未勾选的框用文本次级色描边：卡片底上的 Border 几乎看不见，勾选态读不出来。
			box.BorderBrush = enabled ? palette.Accent : palette.TextSubtle;
			label.Foreground = blocked ? palette.TextFaint : palette.TextPrimary;
		}

		// 打开期间换肤也要跟着走：回调里重画一次，切换勾选时再用最后一次的主题重画。
		box.WithTheme((theme, _) =>
		{
			lastTheme = theme;
			Paint(theme);
		});

		void Refresh()
		{
			check.IsVisible(enabled);
			if (lastTheme is { } theme) Paint(theme);
			marker.Text(options.IsRemoved(name) ? "−" + name : string.Empty);
		}

		Refresh();
		var row = new Button()
			.Ghost()
			.Content(new Grid()
				.Columns(GridLength.Pixels(BoxSize + AppTheme.Space2), GridLength.Stars(1), GridLength.Pixels(MarkerWidth))
				.Children(
					new Grid().Width(BoxSize + AppTheme.Space2).Children(box).Column(0),
					label.Column(1),
					marker.Column(2)))
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Padding(RowPadding)
			.MinHeight(26)
			.CornerRadius(AppTheme.RadiusSm)
			.OnClick(() =>
			{
				// 菜单不关：切换后原地重画这一行，勾与 −name 标记都跟着草稿走。
				options.Toggle(name);
				enabled = options.IsEnabled(name);
				Refresh();
			});
		if (blocked) row.HoverTip(Strings.ConfigToolDisabledByExtension);
		return row;
	}
}
