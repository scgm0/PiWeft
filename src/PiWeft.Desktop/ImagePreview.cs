using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed record PreviewPayload(string Base64, string MimeType);

internal static class ChatImages
{
	private sealed class CachedSource
	{
		private WeakReference<ImageSource>? _source;
		public ImageSource Get(string data)
		{
			if (_source?.TryGetTarget(out var source) == true) return source;
			source = ImageSource.FromBytes(Convert.FromBase64String(data));
			_source = new WeakReference<ImageSource>(source);
			return source;
		}
	}
	private static readonly ConditionalWeakTable<string, CachedSource> Sources = new();

	private static readonly ConditionalWeakTable<ImageSource, PreviewPayload> Payloads = new();

	internal static ImageSource Source(string data) => Sources.GetOrCreateValue(data).Get(data);

	internal static ImageSource Source(ChatImage image)
	{
		var source = Source(image.Data);
		Payloads.AddOrUpdate(source, new PreviewPayload(image.Data, image.MimeType));
		return source;
	}

	internal static PreviewPayload? PayloadOf(ImageSource source) =>
		Payloads.TryGetValue(source, out var payload) ? payload : null;

	public static UIElement Create(ChatImage image, Action<ImageSource>? preview = null)
	{
		try
		{
			var source = Source(image);
			var thumbnail = new Image().Source(source).MaxWidth(AppTheme.MessageMaxWidth).MaxHeight(360);
			return preview is null ? thumbnail : new Button().Ghost().Padding(0).Cursor(CursorType.Hand)
				.HorizontalAlignment(HorizontalAlignment.Left).MaxWidth(AppTheme.MessageMaxWidth)
				.Content(thumbnail).HoverTip(Strings.ImagePreviewOpen).OnClick(() => preview(source));
		}
		catch (FormatException)
		{
			return new TextBlock().Text(Strings.BrokenImage).FontSize(AppTheme.FontSizeCaption).Foreground(AppTheme.Error);
		}
	}

	/// 用户消息的紧凑缩略图（PiDeck 同款 64–88 DIP 方块）：历史里的大图不再把整条气泡撑满，
	/// 点击仍打开全尺寸预览。图片按 UniformToFill 居中裁切，透明底用内嵌表面托住。
	public static UIElement CreateThumbnail(ChatImage image, Action<ImageSource>? preview = null, double size = 88)
	{
		try
		{
			var source = Source(image);
			var frame = new Border()
				.Width(size).Height(size)
				.CornerRadius(AppTheme.RadiusSm)
				.ClipToBounds(true)
				.WithTheme((theme, border) => border.Background = AppTheme.For(theme).CardBackgroundAlt)
				.Child(new Image().Source(source).StretchMode(Stretch.UniformToFill));

			return preview is null ? frame : new Button().Ghost().Padding(0).CornerRadius(AppTheme.RadiusSm)
				.Cursor(CursorType.Hand).Content(frame).HoverTip(Strings.ImagePreviewOpen)
				.OnClick(() => preview(source));
		}
		catch (FormatException)
		{
			return new TextBlock().Text(Strings.BrokenImage).FontSize(AppTheme.FontSizeCaption).Foreground(AppTheme.Error);
		}
	}
}

internal sealed class ImagePreview
{
	private readonly Image _image = new();
	private readonly Button _close;
	private readonly Button _copy;
	private readonly StackPanel _actions;
	private Window? _window;
	private UIElement? _returnFocus;
	private PreviewPayload? _payload;
	private readonly OverlayFade _fade;

	public ImagePreview()
	{
		// 预览遮罩始终是深色：按钮用代码表面的浅色前景与低透明度白色悬停，
		// 不能用浅色主题的 Ghost 前景/底色，否则按钮会消失在遮罩里。
		// 遮罩永远是深色，所以这里取深色配色的前景，不跟着浅色主题变暗。
		_close = AppIcons.Button("x", Strings.ImagePreviewClose, Hide, tint: AppPalette.Dark.CodeText)
			.StyleName(AppStyles.CodeButton).Padding(Thickness.Zero);
		_copy = AppIcons.Button("copy", Strings.CopyImage, CopyImage, tint: AppPalette.Dark.CodeText)
			.StyleName(AppStyles.CodeButton).Padding(Thickness.Zero);
		_actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.HorizontalAlignment(HorizontalAlignment.Right).VerticalAlignment(VerticalAlignment.Top)
			.Margin(new Thickness(AppTheme.Space3)).Children(_copy, _close);
		View = new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0xE0, 0, 0, 0)).BorderThickness(0).CornerRadius(0)
				.Cursor(CursorType.Arrow).Focusable(false).OnClick(Hide),
			_image.Margin(new Thickness(48)).HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center).OnMouseDown(args => args.Handled = true),
			_actions)
			.OnKeyDown(key => { if (key.Key == Key.Escape) { key.Handled = true; Hide(); } });
		_fade = new OverlayFade((FrameworkElement)View);
	}

	public UIElement View { get; }
	public bool IsOpen => View.IsVisible;

	public Action<string, bool>? Status { get; set; }

	public void Show(UIElement owner, ImageSource source)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (_window is not null && !ReferenceEquals(_window, window)) { _fade.Cancel(); CloseNow(); }
		if (IsOpen)
		{
			if (_fade.IsClosing) _fade.Reopen();
			_image.Source(source);
			_payload = ChatImages.PayloadOf(source);
			_copy.IsVisible(_payload is not null);
			_close.Focus();
			return;
		}

		_window = window;
		_image.Source(source);
		_payload = ChatImages.PayloadOf(source);
		// 没有原始编码数据（例如外部传入的图片源）时不给一个注定失败的按钮。
		_copy.IsVisible(_payload is not null);
		TransientOverlay.Open(window, View, Hide);
		if (!IsOpen) _returnFocus = window.FocusManager.FocusedElement;
		View.IsVisible(true);
		_fade.Open();
		_close.Focus();
	}

	public void Hide()
	{
		if (!IsOpen || _fade.IsClosing) return;
		_fade.Close(CloseNow);
	}

	private void CloseNow()
	{
		View.IsVisible(false);
		if (_window is { } window) TransientOverlay.Remove(window, View);
		_window = null;
		_image.Source = null;
		_payload = null;
		_returnFocus?.Focus();
		_returnFocus = null;
	}

	private void CopyImage()
	{
		if (_payload is not { } payload) return;
		if (ClipboardImages.Service is not { } service)
		{
			Status?.Invoke(Strings.ClipboardUnavailable, false);
			return;
		}

		_ = CopyImageAsync(service, payload);
	}

	private async Task CopyImageAsync(IClipboardImages service, PreviewPayload payload)
	{
		bool copied;
		try
		{
			copied = await service.TrySetImageAsync(payload.MimeType, Convert.FromBase64String(payload.Base64)).ConfigureAwait(true);
		}
		catch (FormatException)
		{
			copied = false;
		}

		if (!IsOpen) return;
		Status?.Invoke(copied ? Strings.Copied : Strings.ClipboardUnavailable, copied);
	}
}
