using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 窗口级提示条的呈现。提示条停在窗口底部、水平居中，最多三条、点穿、自动淡出。
/// 外观沿用 PiWeft 的卡片：CardBackground + 发丝线 + Md 圆角；落影与原生弹层同一档，
/// 进出场也用弹层的 200ms / 120ms 曲线。
internal sealed class ToastHost : IOverlayService, IDisposable
{
	private const int MaxVisible = 3;
	private const double MaxWidth = 520;
	private const int MinHoldMs = 2200;
	private const int MaxHoldMs = 7000;

	private readonly Window _window;
	private readonly StackPanel _stack = new();
	private readonly List<Card> _cards = [];
	private bool _attached;

	public ToastHost(Window window)
	{
		_window = window;
		_stack.Spacing = AppTheme.Space2;
		_stack.HorizontalAlignment = HorizontalAlignment.Center;
		_stack.VerticalAlignment = VerticalAlignment.Bottom;
		_stack.IsHitTestVisible = false;
		_stack.Margin = BottomInset(window);
		// 已显示的提示条也要跟着走：最大化时弹出再还原，卡片不能停在旧的底边位置。
		window.WindowStateChanged += OnWindowStateChanged;
	}

	internal int VisibleCount => _cards.Count;

	internal IReadOnlyList<string> Texts => [.. _cards.Select(static card => card.Text)];

	internal VerticalAlignment StackVerticalAlignment => _stack.VerticalAlignment;

	internal Thickness StackMargin => _stack.Margin;

	public void Show(string text, ToastKind kind)
	{
		text = text.Trim();
		if (text.Length == 0)
		{
			return;
		}

		// 窗口最大化/还原会改变底部安全区，每次显示重新算一遍。
		_stack.Margin = BottomInset(_window);

		// 同一条消息连续出现（例如连按复制）不叠三张卡：只把最新那张的计时重新拉满。
		if (_cards.Count > 0 && _cards[^1] is { } last && last.Text == text && last.Kind == kind)
		{
			Restart(last);
			return;
		}

		var card = CreateCard(text, kind);
		_cards.Add(card);
		_stack.Add(card.View);
		if (!_attached)
		{
			_window.OverlayLayer.Add(_stack);
			_attached = true;
		}

		card.Fade.Open();
		Restart(card);

		while (_cards.Count > MaxVisible)
		{
			Dismiss(_cards[0], animate: false);
		}
	}

	internal void DismissAll()
	{
		foreach (var card in _cards.ToArray())
		{
			Dismiss(card, animate: false);
		}
	}

	private void OnWindowStateChanged(WindowState _) => _stack.Margin = BottomInset(_window);

	private void Restart(Card card)
	{
		card.Timer?.Stop();
		var timer = card.Timer ??= new DispatcherTimer();
		timer.Tick -= card.OnTick;
		timer.Tick += card.OnTick;
		timer.Interval = TimeSpan.FromMilliseconds(HoldFor(card.Text));
		timer.Start();
	}

	private static int HoldFor(string text) =>
		Math.Clamp(1500 + (text.Length * 45), MinHoldMs, MaxHoldMs);

	private void Dismiss(Card card, bool animate)
	{
		if (!_cards.Remove(card))
		{
			return;
		}

		card.Timer?.Stop();
		if (animate && Application.IsRunning)
		{
			card.Fade.Close(() => Detach(card));
			return;
		}

		Detach(card);
	}

	private void Detach(Card card)
	{
		card.Fade.Cancel();
		_stack.Remove(card.View);
		if (_cards.Count == 0 && _attached)
		{
			_window.OverlayLayer.Remove(_stack);
			_attached = false;
		}
	}

	public void Dispose()
	{
		foreach (var card in _cards)
		{
			card.Timer?.Stop();
			card.Fade.Cancel();
		}

		_window.WindowStateChanged -= OnWindowStateChanged;
		_cards.Clear();
		if (_attached)
		{
			_window.OverlayLayer.Remove(_stack);
			_attached = false;
		}
	}

	private Card CreateCard(string text, ToastKind kind)
	{
		var (icon, role) = kind switch
		{
			ToastKind.Success => ("circle-check", AppStyles.RoleSuccess),
			ToastKind.Error => ("triangle-alert", AppStyles.RoleError),
			_ => ("bell", AppStyles.RoleSubtle),
		};

		var label = new TextBlock()
			.Text(text)
			.FontSize(AppTheme.FontSizeCaption)
			.PrimaryText()
			.TextWrapping(TextWrapping.Wrap)
			.VerticalAlignment(VerticalAlignment.Center)
			// Toast 在窗口浮层里，不一定被 ApplyTextColors 扫到：自己跟主题。
			.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).TextPrimary);

		var body = new Border()
			.StyleName(AppStyles.PopupCard)
			.MaxWidth(MaxWidth)
			.Padding(new Thickness(AppTheme.Space3, 6, AppTheme.Space3, 6))
			.HorizontalAlignment(HorizontalAlignment.Center)
			.Child(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					AppIcons.Icon(icon, role, 14),
					label));

		var view = new ShadowDecorator()
			.BlurRadius(8)
			.OffsetY(4)
			.CornerRadius(AppTheme.RadiusMd)
			// 与 PopupChrome 的落影同参：8 模糊、下移 4，深色 50% / 浅色 25%。
			.WithTheme((theme, shadow) => shadow.ShadowColor = theme.IsDark
				? new Color(0x80, 0x00, 0x00, 0x00)
				: new Color(0x40, 0x00, 0x00, 0x00))
			.Child(body)
			.HorizontalAlignment(HorizontalAlignment.Center);

		var card = new Card(text, kind, view);
		card.OnTick = () =>
		{
			card.Timer?.Stop();
			Dismiss(card, animate: true);
		};
		return card;
	}

	/// 窗口底部的安全区：窗口浮层从底部阴影边起算，再抬离底边一点。
	/// 提示条本身是点穿的，压住输入卡时也不影响打字。
	private static Thickness BottomInset(Window window)
	{
		var inset = window is MainWindow main
			? MainWindow.OverlayInsetFor(main.WindowState)
			: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		return new Thickness(inset.Left, 0, inset.Right, inset.Bottom + AppTheme.Space2);
	}

	private sealed class Card(string text, ToastKind kind, ShadowDecorator view)
	{
		public string Text { get; } = text;

		public ToastKind Kind { get; } = kind;

		public ShadowDecorator View { get; } = view;

		public OverlayFade Fade { get; } = OverlayFade.PopupLike(view);

		public DispatcherTimer? Timer { get; set; }

		public Action OnTick { get; set; } = static () => { };
	}
}
