using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Delayed tooltip using MewUI's Popup API so styling, measurement and native popup hosting stay framework-owned.</summary>
internal sealed class HoverHints : IOverlayService, IDisposable
{
	internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);
	private static readonly ConditionalWeakTable<FrameworkElement, Hint> Hints = new();
	private readonly Window _window;
	private readonly DispatcherTimer _timer = new(Delay);
	private FrameworkElement? _owner;
	private Popup? _popup;

	private HoverHints(Window window)
	{
		_window = window;
		_timer.Tick += Show;
		window.Deactivated += Cancel;
		window.Closed += Dispose;
		window.MouseDown += OnMouseDown;
		window.MouseWheel += OnWheel;
		window.KeyDown += OnKeyDown;
	}

	public static T Set<T>(T element, string? text) where T : FrameworkElement
	{
		var hint = Hints.GetValue(element, static owner => new Hint(owner));
		if (hint.Text == text) return element;
		hint.Text = text;
		CancelOwner(element);
		// 行内 chip 的提示是在鼠标移动中换文本的；鼠标已经在元素上时直接开始计时，
		// 否则要等下一次 MouseEnter 才出现，行内悬浮永远看不到。
		if (!string.IsNullOrEmpty(text) && element.IsMouseOver) Begin(element);
		return element;
	}

	internal static string? Text(FrameworkElement element) => Hints.TryGetValue(element, out var hint) ? hint.Text : null;
	internal static bool IsOpen(Window window) => window.OverlayLayer.GetService<HoverHints>()?._popup?.IsOpen == true;

	private static void Begin(FrameworkElement owner)
	{
		if (!Application.IsRunning || string.IsNullOrEmpty(Text(owner)) || owner.FindVisualRoot() is not Window window) return;
		var service = window.OverlayLayer.GetOrCreateService(_ => new HoverHints(window));
		service.Cancel();
		service._owner = owner;
		service._timer.Start();
	}

	public static void Cancel(Element element)
	{
		if (element.FindVisualRoot() is Window window) window.OverlayLayer.GetService<HoverHints>()?.Cancel();
	}

	private static void CancelOwner(Element owner)
	{
		if (owner.FindVisualRoot() is Window window && window.OverlayLayer.GetService<HoverHints>() is { } service &&
			ReferenceEquals(service._owner, owner)) service.Cancel();
	}

	private void Show()
	{
		_timer.Stop();
		if (_owner is not { IsMouseOver: true } owner || !ReferenceEquals(owner.FindVisualRoot(), _window) || string.IsNullOrEmpty(Text(owner)))
		{ Cancel(); return; }
		for (Element? ancestor = owner; ancestor is not null; ancestor = ancestor.Parent)
			if (ancestor is UIElement { IsVisible: false }) { Cancel(); return; }

		var popup = _popup ??= new Popup().IsHitTestVisible(false);
		if (popup.Content is { } oldContent) UiLifetime.Release(oldContent);
		popup.Content = new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
			.MaxWidth(420).Padding(new Thickness(10, 6, 10, 6)).IsHitTestVisible(false)
			.Child(new TextBlock().Text(Text(owner)!).FontSize(AppTheme.FontSizeCaption).PrimaryText().TextWrapping(TextWrapping.Wrap));
		popup.ShowAt(owner, Anchor(owner), PopupAnchorSide.Below);
	}

	// MewUI arranges descendants in window-absolute coordinates, including scrolled content.
	// Keep the whole owner rect so Popup can flip above the control near the screen edge.
	internal static Rect Anchor(Element owner) =>
		new(owner.Bounds.X, owner.Bounds.Y - 6, owner.Bounds.Width, owner.Bounds.Height + 12);

	private void Cancel()
	{
		_timer.Stop();
		_owner = null;
		_popup?.Close();
	}

	private void OnMouseDown(MouseEventArgs _) => Cancel();
	private void OnWheel(MouseWheelEventArgs _) => Cancel();
	private void OnKeyDown(KeyEventArgs _) => Cancel();

	public void Dispose()
	{
		Cancel();
		if (_popup?.Content is { } content) UiLifetime.Release(content);
		_popup?.Dispose();
		_popup = null;
		_timer.Dispose();
		_window.Deactivated -= Cancel;
		_window.Closed -= Dispose;
		_window.MouseDown -= OnMouseDown;
		_window.MouseWheel -= OnWheel;
		_window.KeyDown -= OnKeyDown;
	}

	private sealed class Hint
	{
		public string? Text;
		public Hint(FrameworkElement owner)
		{
			owner.MouseEnter += () => Begin(owner);
			owner.MouseLeave += () => CancelOwner(owner);
			owner.MouseDown += _ => Cancel(owner);
			owner.MouseWheel += _ => Cancel(owner);
			owner.KeyDown += _ => Cancel(owner);
		}
	}
}

internal static class HoverHintExtensions
{
	public static T HoverTip<T>(this T element, string? text) where T : FrameworkElement => HoverHints.Set(element, text);
}
