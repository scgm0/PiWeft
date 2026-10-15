using System.Runtime.CompilerServices;
using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Svg;

namespace PiWeft.Desktop;

/// <summary>
/// 界面图标：直接用 Lucide 图标库（ISC 许可，PiDeck 用的就是同一套 lucide-react）的原始 SVG，
/// 以 <c>EmbeddedResource</c> 打进程序集，渲染交给 <c>Aprillz.MewUI.Svg</c> 的 <see cref="SvgImageSource"/>。
/// </summary>
/// <remarks>
/// Lucide 是 stroke 风格（根元素 <c>stroke="currentColor"</c>），而 <see cref="SvgImageSource.Tint"/>
/// 只替换 fill。因此这里在加载时把 <c>currentColor</c> 换成当前主题的实际颜色再交给渲染器：
/// 同一个 SVG 跟随深浅主题换色，不必为每个主题准备两份文件，也避免用字体缺覆盖的符号导致笔画粗细不一致。
/// </remarks>
public static class AppIcons
{
	private const string ResourcePrefix = "PiWeft.Desktop.Resources.icons.";

	private static readonly Dictionary<(string Name, uint Tint), SvgImageSource> Cache = [];

	/// <summary>已创建的图标控件，主题变化时统一换色（弱引用，界面重建后自动回收）。</summary>
	private static readonly List<WeakReference<Image>> Live = [];
	private static int _trackedSinceSweep;

	/// <summary>图标的标准显示尺寸（Lucide 的画布是 24×24）。</summary>
	public const double Size = 16;

	/// <summary>顶栏动作的图标略大一号，远看也认得出。</summary>
	public const double ToolbarSize = 20;

	/// <summary>按名称取一个已着色的图标，颜色用于缓存键。</summary>
	public static IImageSource Get(string name, Color? tint = null)
	{
		var color = tint ?? AppTheme.TextMuted;
		var key = (name, Pack(color));

		lock (Cache)
		{
			if (Cache.TryGetValue(key, out var cached))
			{
				return cached;
			}

			// 24px 画布缩到 16–20px 显示时，Lucide 默认的 2 号线会显得纤细，这里略加粗。
			var markup = ReadMarkup(name)
				.Replace("currentColor", ToHex(color), StringComparison.Ordinal)
				.Replace("stroke-width=\"2\"", "stroke-width=\"2.25\"", StringComparison.Ordinal);
			var source = SvgImageSource.FromString(markup);
			Cache[key] = source;
			return source;
		}
	}

	/// <summary>图标按钮：只放一个矢量图标，配中文提示。</summary>
	public static Button Button(string icon, string tooltip, Action onClick, Color? tint = null, double size = Size)
	{
		var image = new Image().Source(Get(icon, tint)).Width(size).Height(size)
			.HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center);
		Track(image, icon, tint);

		return new Button()
			.Content(image)
			.OnClick(onClick)
			.Ghost()
			.HoverTip(tooltip);
	}

	/// <summary>带文字的主按钮（例如「新建会话」）。</summary>
	public static Button PrimaryButton(string icon, string text, Action onClick)
	{
		var image = new Image().Source(Get(icon, AppTheme.TextOnAccent)).Width(14).Height(14)
			.VerticalAlignment(VerticalAlignment.Center);
		Track(image, icon, null, onAccent: true);

		return new Button()
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.VerticalAlignment(VerticalAlignment.Center)
				.Spacing(6)
				.Children(
					image,
					new TextBlock().Text(text).FontSize(AppTheme.FontSizeControl).OnAccentText()
						.VerticalAlignment(VerticalAlignment.Center)))
			.OnClick(onClick)
			.StyleName(AppStyles.PrimaryButton);
	}

	/// <summary>Icon and label for navigation and secondary commands, with theme-aware normal text.</summary>
	public static Button TextButton(string icon, string text, Action onClick)
	{
		var image = new Image().Source(Get(icon)).Width(14).Height(14)
			.VerticalAlignment(VerticalAlignment.Center);
		Track(image, icon, null);
		return new Button().Ghost().Content(new StackPanel().Orientation(Orientation.Horizontal)
			.VerticalAlignment(VerticalAlignment.Center).Spacing(6)
			.Children(image, new TextBlock().Text(text).FontSize(AppTheme.FontSizeControl).PrimaryText()
				.VerticalAlignment(VerticalAlignment.Center))).OnClick(onClick);
	}

	/// <summary>主题变化后给所有已存在的图标换色。</summary>
	public static void RefreshAll()
	{
		lock (Cache)
		{
			for (var index = Live.Count - 1; index >= 0; index--)
			{
				if (!Live[index].TryGetTarget(out var image))
				{
					Live.RemoveAt(index);
					continue;
				}

				if (Info.TryGetValue(image, out var info))
				{
					image.Source(Get(info.Icon, info.OnAccent ? AppTheme.TextOnAccent : info.Tint));
				}
			}
		}
	}

	// 元数据不能强引用 Image，否则弱引用列表里的旧消息图标永远不会被回收。
	private static readonly ConditionalWeakTable<Image, IconInfo> Info = new();

	private sealed record IconInfo(string Icon, Color? Tint, bool OnAccent);

	private static void Track(Image image, string icon, Color? tint, bool onAccent = false)
	{
		lock (Cache)
		{
			Info.Add(image, new IconInfo(icon, tint, onAccent));
			if (++_trackedSinceSweep >= 128)
			{
				_trackedSinceSweep = 0;
				Live.RemoveAll(static item => !item.TryGetTarget(out _));
			}

			Live.Add(new WeakReference<Image>(image));
		}
	}

	private static string ReadMarkup(string name)
	{
		var assembly = Assembly.GetExecutingAssembly();
		using var stream = assembly.GetManifestResourceStream(ResourcePrefix + name + ".svg")
			?? throw new InvalidOperationException($"内嵌图标缺失：{name}.svg");

		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	private static uint Pack(Color color) => (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);

	/// <summary>图标用到的颜色以十六进制写回 SVG。</summary>
	private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
