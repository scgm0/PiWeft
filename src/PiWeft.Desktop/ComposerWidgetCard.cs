using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 输入区上方的扩展卡：与输入框同宽同列，头部是高 36 的「图标 + 标题 + 进度 + 折叠键」，
/// 展开后放列表或扩展文本。对齐 PiDeck 的 composer widget frame：
/// 一卡一件事、没有内容时整体不占位，卡片之间用间距而不是分隔线区分。
/// </summary>
internal sealed class ComposerWidgetCard
{
	private readonly Button _header;
	private readonly GlyphElement _chevron;
	private readonly TextBlock _meta = new();
	private readonly StackPanel _body;
	private bool _expanded = true;

	public ComposerWidgetCard(string icon, string title)
	{
		_chevron = new GlyphElement()
			.Kind(GlyphKind.ChevronUp)
			.GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextSubtle);

		var iconImage = AppIcons.Icon(icon, AppStyles.RoleSubtle, 14)
			.Margin(new Thickness(0, 0, AppTheme.Space2 + 2, 0))
			.VerticalAlignment(VerticalAlignment.Center);
		var titleText = new TextBlock()
			.Text(title)
			.FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center);
		_meta.FontSize(AppTheme.FontSizeCaption)
			.SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space2 + 2, 0, AppTheme.Space2, 0));

		var headerContent = new Grid()
			.Columns("Auto,Auto,*,Auto")
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				iconImage.Column(0),
				titleText.Column(1),
				_meta.Column(2),
				_chevron.Column(3));

		_header = new Button()
			.Ghost()
			.Content(headerContent)
			.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space2 + 2, 0))
			.MinHeight(AppTheme.ControlHeightLg)
			.CornerRadius(0)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.OnClick(Toggle);

		_body = new StackPanel()
			.Spacing(AppTheme.Space1)
			.Margin(new Thickness(AppTheme.Space3, 0, AppTheme.Space3, AppTheme.Space3));

		// 宽度由宿主列统一给出（与输入卡同宽），卡片自己不再猜内容宽度：
		// 拉伸对齐 + MaxWidth 在 MewUI 里会得到内容宽或越过上限，二者都不对。
		View = new Border()
			.StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusMd)
			.ClipToBounds(true)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.IsVisible(false)
			.Child(new StackPanel().Children(_header, _body));
	}

	public Border View { get; }

	/// <summary>展开后的内容宿主；调用方只往里放内容。</summary>
	public StackPanel Body => _body;

	/// <summary>头部副标题（进度、数量这类元数据）。空字符串时整段不占位。</summary>
	public string Meta
	{
		get => _meta.Text;
		set
		{
			_meta.Text(value);
			_meta.IsVisible(value.Length > 0);
		}
	}

	/// <summary>默认展开；折叠只影响卡片自身，不改变会话数据。</summary>
	public bool IsExpanded
	{
		get => _expanded;
		set
		{
			if (_expanded == value) return;
			_expanded = value;
			_body.IsVisible(value);
			_chevron.Kind(value ? GlyphKind.ChevronUp : GlyphKind.ChevronDown);
		}
	}

	public void Show(bool visible) => View.IsVisible(visible);

	private void Toggle()
	{
		IsExpanded = !IsExpanded;
	}
}
