using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 窗口级提示条的呈现。框架自带的 <c>Window.ShowToast</c> 把卡片放在窗口高度 80% 处，
/// 正好压住会话输入卡（错误提示弹出来时连占位提示都读不到）；这里改成标题栏下方的顶部横幅，
/// 最多三条、点穿、自动淡出。外观沿用 PiWeft 的卡片：CardBackground + 发丝线 + Md 圆角。
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
		_stack.VerticalAlignment = VerticalAlignment.Top;
		_stack.IsHitTestVisible = false;
		_stack.Margin = TopInset(window);
	}

	internal int VisibleCount => _cards.Count;

	internal IReadOnlyList<string> Texts => [.. _cards.Select(static card => card.Text)];

	public void Show(string text, ToastKind kind)
	{
		text = text.Trim();
		if (text.Length == 0)
		{
			return;
		}

		// 窗口最大化/还原会改变标题栏与阴影的让位，每次显示重新算一遍。
		_stack.Margin = TopInset(_window);

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
		}

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
			.MaxWidth(MaxWidth)
			.Padding(new Thickness(AppTheme.Space3, 6, AppTheme.Space3, 6))
			.HorizontalAlignment(HorizontalAlignment.Center)
			.WithTheme((theme, border) =>
			{
				border.Background = AppTheme.For(theme).CardBackground;
				border.BorderBrush = AppTheme.For(theme).Border;
				border.BorderThickness = 1;
				border.CornerRadius = AppTheme.RadiusMd;
			})
			.Child(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					AppIcons.Icon(icon, role, 14),
					label));

		var view = new ShadowDecorator()
			.BlurRadius(14)
			.OffsetY(3)
			.CornerRadius(AppTheme.RadiusMd)
			.WithTheme((theme, shadow) => shadow.ShadowColor = theme.IsDark
				? new Color(0xB4, 0x00, 0x00, 0x00)
				: new Color(0x3C, 0x00, 0x00, 0x00))
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

	/// 标题栏下方的安全区：窗口级浮层从标题栏下面开始，不遮页签、面板键与窗口按钮。
	private static Thickness TopInset(Window window)
	{
		var inset = window is MainWindow main
			? MainWindow.OverlayInsetFor(main.WindowState)
			: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		return new Thickness(inset.Left, inset.Top + AppTheme.Space2, inset.Right, inset.Bottom);
	}

	private sealed class Card(string text, ToastKind kind, ShadowDecorator view)
	{
		public string Text { get; } = text;

		public ToastKind Kind { get; } = kind;

		public ShadowDecorator View { get; } = view;

		public OverlayFade Fade { get; } = new(view);

		public DispatcherTimer? Timer { get; set; }

		public Action OnTick { get; set; } = static () => { };
	}
}
