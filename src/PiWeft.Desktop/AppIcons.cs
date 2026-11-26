using System.Runtime.CompilerServices;
using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Svg;

namespace PiWeft.Desktop;

/// 界面图标：直接用 Lucide 图标库（ISC 许可，PiDeck 用的就是同一套 lucide-react）的原始 SVG，
/// 以 <c>EmbeddedResource</c> 打进程序集，渲染交给 <c>Aprillz.MewUI.Svg</c> 的 <see cref="SvgImageSource"/>。
/// Lucide 是 stroke 风格（根元素 <c>stroke="currentColor"</c>），而 <see cref="SvgImageSource.Tint"/>
/// 只替换 fill。因此这里在加载时把 <c>currentColor</c> 换成当前主题的实际颜色再交给渲染器：
/// 同一个 SVG 跟随深浅主题换色，不必为每个主题准备两份文件，也避免用字体缺覆盖的符号导致笔画粗细不一致。
public static class AppIcons
{
	private const string ResourcePrefix = "PiWeft.Desktop.Resources.icons.";

	private const string BrandResource = "PiWeft.Desktop.Resources.appicon.piweft.svg";

	// 品牌标是与 exe 图标同一份矢量，与主题无关，解析一次就够；资源名只在这里写。
	private static readonly Lazy<IImageSource> BrandSource = new(() =>
		SvgImageSource.FromResource(typeof(AppIcons).Assembly, BrandResource));

	private static readonly Dictionary<(string Name, uint Tint), SvgImageSource> Cache = [];
	private static readonly Lock CacheGate = new();

	private static readonly List<WeakReference<Image>> Live = [];
	private static int _trackedSinceSweep;

	public const double Size = 16;

	public const double ToolbarSize = 20;

	/// 品牌标：侧栏标题栏与起始页共用的应用图标矢量。
	public static IImageSource Brand() => BrandSource.Value;

	public static IImageSource Get(string name, Color? tint = null)
	{
		var color = tint ?? AppTheme.TextMuted;
		var key = (name, Pack(color));

		lock (CacheGate)
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
					// 字色不写死：不设前景色，继承 Button 的 Foreground，让主按钮样式在可用 / 禁用两态
					// 自己切换（TextOnAccent ↔ TextFaint）。写死 OnAccentText 的话，禁用态
					// 会变成深色字压在深色底上，看上去像已经消失的按钮。
					new TextBlock().Text(text).FontSize(AppTheme.FontSizeControl)
						.VerticalAlignment(VerticalAlignment.Center)))
			.OnClick(onClick)
			.StyleName(AppStyles.PrimaryButton);
	}

	/// Icon and label for navigation and secondary commands, with theme-aware normal text.
	/// <paramref name="strong"/> 把图标也提亮到主文字色：侧栏顶部的「新建 / 搜索会话」这类主要入口用得上。
	public static Button TextButton(string icon, string text, Action onClick, bool strong = false)
	{
		// 只记「跟随主文字色」这个语义，不记具体颜色，否则换肤后图标停在旧主题的色值上。
		var image = new Image().Source(Get(icon, strong ? AppTheme.TextPrimary : null)).Width(14).Height(14)
			.VerticalAlignment(VerticalAlignment.Center);
		Track(image, icon, tint: null, role: strong ? AppStyles.RolePrimary : null);
		return new Button().Ghost().Content(new StackPanel().Orientation(Orientation.Horizontal)
			.VerticalAlignment(VerticalAlignment.Center).Spacing(6)
			.Children(image, new TextBlock().Text(text).FontSize(AppTheme.FontSizeControl).PrimaryText()
				.VerticalAlignment(VerticalAlignment.Center))).OnClick(onClick);
	}

	public static Image Icon(string icon, string role = AppStyles.RoleSubtle, double size = Size)
	{
		var image = new Image().Source(Get(icon, AppStyles.ColorForRole(role))).Width(size).Height(size)
			.VerticalAlignment(VerticalAlignment.Center);
		Track(image, icon, tint: null, role: role);
		return image;
	}

	/// 选择菜单里表示「当前项」的勾：仓库、分支与图筛选三处共用，颜色跟随强调色。
	internal static IconTemplate CheckTemplate() => new(static _ => new GlyphElement()
		.Kind(GlyphKind.CheckMark)
		.GlyphSize(4)
		.Foreground(AppTheme.Accent)
		.HorizontalAlignment(HorizontalAlignment.Center)
		.VerticalAlignment(VerticalAlignment.Center));

	/// 换掉一个受主题跟踪的图标的形状，角色与反色语义不变。
	/// 用于「已看」这类原地翻转的开关：不必为一次点击重建整段列表。
	internal static void SetIcon(Image image, string icon, string? role = null)
	{
		lock (CacheGate)
		{
			if (!Info.TryGetValue(image, out var info))
			{
				return;
			}

			// 换形状时也可以换角色：已看/未看是两种语义状态，不能沿用建控件时的颜色。
			var nextRole = role ?? info.Role;
			var tint = nextRole is { } named ? AppStyles.ColorForRole(named)
				: info.OnAccent ? AppTheme.TextOnAccent
				: info.Tint;
			image.Source(Get(icon, tint));
			Info.Remove(image);
			Info.Add(image, info with { Icon = icon, Role = nextRole });
		}
	}

	public static void RefreshAll()
	{
		lock (CacheGate)
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
					// 每次都按当前主题重新取色：角色优先（主文字/次要文字…），其次反色，最后才是记录的固定色。
					var tint = info.Role is { } role ? AppStyles.ColorForRole(role)
						: info.OnAccent ? AppTheme.TextOnAccent
						: info.Tint;
					image.Source(Get(info.Icon, tint));
				}
			}
		}
	}

	// 元数据不能强引用 Image，否则弱引用列表里的旧消息图标永远不会被回收。
	private static readonly ConditionalWeakTable<Image, IconInfo> Info = new();

	/// <see cref="Role"/> 是文本角色（主文字/次要文字…），换肤时按新主题重解析；
	/// <see cref="Tint"/> 则是构建时的固定色，只用于确需固定颜色的图标。
	private sealed record IconInfo(string Icon, Color? Tint, bool OnAccent, string? Role = null);

	private static void Track(Image image, string icon, Color? tint, bool onAccent = false, string? role = null)
	{
		lock (CacheGate)
		{
			Info.Add(image, new IconInfo(icon, tint, onAccent, role));
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

	private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
