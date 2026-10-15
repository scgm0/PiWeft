using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>缩略图和预览共用图片源；弱缓存不延长会话或大位图的生命周期。</summary>
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

	internal static ImageSource Source(string data) => Sources.GetOrCreateValue(data).Get(data);

	public static UIElement Create(ChatImage image, Action<ImageSource>? preview = null)
	{
		try
		{
			var source = Source(image.Data);
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
}

/// <summary>窗口级图片预览：适应窗口、遮罩关闭、Escape 关闭，隐藏时释放图片引用。</summary>
internal sealed class ImagePreview
{
	private readonly Image _image = new();
	private readonly Button _close;
	private Window? _window;
	private UIElement? _returnFocus;

	public ImagePreview()
	{
		_close = AppIcons.Button("x", Strings.ImagePreviewClose, Hide);
		View = new Grid().IsVisible(false).Children(
			new Button().Background(new Color(0xE0, 0, 0, 0)).BorderThickness(0).CornerRadius(0)
				.Cursor(CursorType.Arrow).Focusable(false).OnClick(Hide),
			_image.Margin(new Thickness(48)).HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center).OnMouseDown(args => args.Handled = true),
			_close.HorizontalAlignment(HorizontalAlignment.Right).VerticalAlignment(VerticalAlignment.Top)
				.Margin(new Thickness(AppTheme.Space3)))
			.OnKeyDown(key => { if (key.Key == Key.Escape) { key.Handled = true; Hide(); } });
	}

	public UIElement View { get; }
	public bool IsOpen => View.IsVisible;

	public void Show(UIElement owner, ImageSource source)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (_window is not null && !ReferenceEquals(_window, window)) Hide();
		_window = window;
		_image.Source(source);
		TransientOverlay.Open(window, View, Hide);
		if (!IsOpen) _returnFocus = window.FocusManager.FocusedElement;
		View.IsVisible(true);
		_close.Focus();
	}

	public void Hide()
	{
		if (!IsOpen) return;
		View.IsVisible(false);
		if (_window is { } window) TransientOverlay.Remove(window, View);
		_window = null;
		_image.Source = null;
		_returnFocus?.Focus();
		_returnFocus = null;
	}
}